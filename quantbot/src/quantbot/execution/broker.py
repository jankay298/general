"""The broker interface.

Everything the live loop needs from a venue, and nothing more. Keeping the
surface this small is what makes the paper broker a faithful stand-in for the
real one: if the runner can only ask these questions, then a run against the
simulator exercises the same code path as a run against production.

Positions are returned as **signed share counts**, not weights, because that is
what brokers actually report and converting in one place beats converting in
five.
"""

from __future__ import annotations

import abc
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import List, Optional, Sequence

import numpy as np
import pandas as pd


@dataclass
class Account:
    """Snapshot of the trading account."""

    equity: float
    cash: float
    buying_power: float
    currency: str = "USD"
    #: True when the broker has flagged the account (margin call, restriction).
    blocked: bool = False
    timestamp: Optional[datetime] = None

    def __post_init__(self) -> None:
        if self.timestamp is None:
            self.timestamp = datetime.now(timezone.utc)


@dataclass
class Order:
    """An order to send. Quantity is signed: positive buys, negative sells."""

    symbol: str
    quantity: float
    order_type: str = "market"       # market | limit
    limit_price: Optional[float] = None
    time_in_force: str = "day"
    client_order_id: Optional[str] = None
    reason: str = "rebalance"

    @property
    def side(self) -> str:
        return "buy" if self.quantity > 0 else "sell"

    def notional(self, price: float) -> float:
        return abs(self.quantity) * price

    def describe(self, price: Optional[float] = None) -> str:
        text = f"{self.side.upper():4} {abs(self.quantity):>12,.4f} {self.symbol:<8}"
        if price:
            text += f" @ ~{price:,.2f} = {self.notional(price):>12,.2f}"
        if self.order_type == "limit" and self.limit_price:
            text += f" (limit {self.limit_price:,.2f})"
        return text


@dataclass
class OrderResult:
    """What came back after submitting an order."""

    order: Order
    accepted: bool
    broker_order_id: Optional[str] = None
    filled_quantity: float = 0.0
    filled_price: Optional[float] = None
    status: str = "unknown"
    message: str = ""
    timestamp: Optional[datetime] = None

    def __post_init__(self) -> None:
        if self.timestamp is None:
            self.timestamp = datetime.now(timezone.utc)


class Broker(abc.ABC):
    """Minimal venue interface."""

    name: str = "base"
    #: True when orders sent here move real money.
    is_live: bool = False

    @classmethod
    @abc.abstractmethod
    def from_config(cls, cfg) -> "Broker":
        """Build from a :class:`quantbot.config.Config`."""

    @abc.abstractmethod
    def account(self) -> Account:
        ...

    @abc.abstractmethod
    def positions(self) -> pd.Series:
        """Signed share counts by symbol. Empty Series when flat."""

    @abc.abstractmethod
    def submit(self, order: Order, reference_price: Optional[float] = None) -> OrderResult:
        ...

    def submit_all(
        self, orders: Sequence[Order], prices: Optional[pd.Series] = None
    ) -> List[OrderResult]:
        results = []
        for order in orders:
            price = None if prices is None else prices.get(order.symbol)
            results.append(self.submit(order, price))
        return results

    def cancel_open_orders(self) -> int:
        """Cancel resting orders; returns how many were cancelled."""
        return 0

    def is_market_open(self) -> bool:
        """Whether the venue is currently accepting orders."""
        return True

    def health_check(self) -> tuple[bool, str]:
        """Confirm the broker is reachable and the account is usable."""
        try:
            account = self.account()
        except Exception as exc:
            return False, f"{self.name}: cannot read account ({exc})"
        if account.blocked:
            return False, f"{self.name}: account is blocked by the broker"
        if account.equity <= 0:
            return False, f"{self.name}: account equity is {account.equity}"
        return True, f"{self.name}: equity {account.equity:,.2f} {account.currency}"

    def __repr__(self) -> str:  # pragma: no cover - debugging aid
        return f"<{type(self).__name__} live={self.is_live}>"


def orders_from_weights(
    target_weights: pd.Series,
    current_positions: pd.Series,
    prices: pd.Series,
    equity: float,
    min_notional: float = 0.0,
    no_trade_band: float = 0.0,
    no_trade_band_relative: float = 0.0,
    allow_fractional: bool = True,
    max_order_pct_equity: float = 1.0,
) -> List[Order]:
    """Translate target weights into the orders that get you there.

    The same band logic the backtester uses, applied to live orders — otherwise
    the live loop would trade on drift the simulation deliberately ignored, and
    the two would diverge for reasons that have nothing to do with the market.
    """
    orders: List[Order] = []
    if equity <= 0:
        return orders

    symbols = set(target_weights.index) | set(current_positions.index)
    for symbol in sorted(symbols):
        price = prices.get(symbol)
        if price is None or not pd.notna(price) or price <= 0:
            continue

        target_weight = float(target_weights.get(symbol, 0.0))
        target_quantity = target_weight * equity / price
        held = float(current_positions.get(symbol, 0.0))
        delta = target_quantity - held

        if not allow_fractional:
            delta = float(int(delta))
        if delta == 0:
            continue

        is_exit = abs(target_quantity) < 1e-9
        notional = abs(delta) * price

        if not is_exit:
            band = max(no_trade_band * equity, no_trade_band_relative * abs(target_quantity * price))
            if notional < band or notional < min_notional:
                continue

        # Never let one order become a large fraction of the account, whatever the
        # weights say. A bad price feed produces exactly this shape of order.
        cap = max_order_pct_equity * equity
        if notional > cap:
            delta = np.sign(delta) * (cap / price)

        orders.append(Order(symbol=symbol, quantity=delta,
                            reason="exit" if is_exit else "rebalance"))
    return orders
