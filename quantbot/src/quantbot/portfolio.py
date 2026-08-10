"""Portfolio accounting, shared by the backtester and the live runner.

Deliberately boring double-entry bookkeeping: cash goes down when you buy, up
when you sell, and costs come out of cash. The backtest and the live loop use the
same object so that "what the simulation thinks I own" and "what the broker says
I own" are comparable quantities — which is what makes reconciliation possible.

Fractional shares are allowed by default. Set ``allow_fractional=False`` for
brokers or instruments that require whole shares; the resulting rounding is then
part of the simulation rather than a surprise on the first live order.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Dict, List, Optional

import numpy as np
import pandas as pd

from .utils.logging import get_logger

log = get_logger(__name__)


@dataclass
class Trade:
    """One executed fill."""

    date: pd.Timestamp
    symbol: str
    quantity: float          # signed: positive buys, negative sells
    price: float             # fill price, before costs
    cost: float              # commission + spread + impact, always positive
    reason: str = "rebalance"

    @property
    def notional(self) -> float:
        return abs(self.quantity) * self.price

    @property
    def cash_flow(self) -> float:
        """Effect on cash: negative when buying."""
        return -self.quantity * self.price - self.cost


@dataclass
class Position:
    symbol: str
    quantity: float = 0.0
    average_price: float = 0.0
    #: Realised P&L booked on this symbol so far.
    realised: float = 0.0

    def market_value(self, price: float) -> float:
        return self.quantity * price

    def unrealised(self, price: float) -> float:
        return (price - self.average_price) * self.quantity


class Portfolio:
    """Cash, positions and a trade blotter."""

    def __init__(self, initial_cash: float, allow_fractional: bool = True) -> None:
        self.initial_cash = float(initial_cash)
        self.cash = float(initial_cash)
        self.allow_fractional = allow_fractional
        self.positions: Dict[str, Position] = {}
        self.trades: List[Trade] = []

    # ------------------------------------------------------------------ state

    @property
    def quantities(self) -> pd.Series:
        return pd.Series(
            {s: p.quantity for s, p in self.positions.items() if p.quantity != 0.0},
            dtype=float,
        )

    def market_value(self, prices: pd.Series) -> float:
        total = 0.0
        for symbol, position in self.positions.items():
            price = prices.get(symbol)
            if price is not None and np.isfinite(price):
                total += position.market_value(price)
        return total

    def equity(self, prices: pd.Series) -> float:
        return self.cash + self.market_value(prices)

    def weights(self, prices: pd.Series) -> pd.Series:
        equity = self.equity(prices)
        if equity <= 0:
            return pd.Series(dtype=float)
        values = {}
        for symbol, position in self.positions.items():
            price = prices.get(symbol)
            if position.quantity != 0 and price is not None and np.isfinite(price):
                values[symbol] = position.market_value(price) / equity
        return pd.Series(values, dtype=float)

    def exposure(self, prices: pd.Series) -> Dict[str, float]:
        w = self.weights(prices)
        return {
            "gross": float(w.abs().sum()) if not w.empty else 0.0,
            "net": float(w.sum()) if not w.empty else 0.0,
            "long": float(w[w > 0].sum()) if not w.empty else 0.0,
            "short": float(w[w < 0].sum()) if not w.empty else 0.0,
            "cash": self.cash / self.equity(prices) if self.equity(prices) else 1.0,
        }

    # -------------------------------------------------------------- mutation

    def fill(
        self,
        date: pd.Timestamp,
        symbol: str,
        quantity: float,
        price: float,
        cost: float = 0.0,
        reason: str = "rebalance",
    ) -> Optional[Trade]:
        """Book a fill. Returns the recorded trade, or None if it was a no-op."""
        if quantity == 0 or not np.isfinite(price) or price <= 0:
            return None
        if not self.allow_fractional:
            quantity = float(np.trunc(quantity))
            if quantity == 0:
                return None

        position = self.positions.setdefault(symbol, Position(symbol))
        previous = position.quantity
        new_quantity = previous + quantity

        if previous == 0 or np.sign(previous) == np.sign(quantity):
            # Opening or adding: roll the average price forward.
            total_cost = position.average_price * previous + price * quantity
            position.average_price = total_cost / new_quantity if new_quantity != 0 else 0.0
        else:
            # Reducing or flipping: book realised P&L on the closed portion.
            closed = min(abs(quantity), abs(previous)) * np.sign(previous)
            position.realised += (price - position.average_price) * closed
            if abs(quantity) > abs(previous):
                position.average_price = price  # flipped through zero
            # Trimming a position leaves the average price untouched.

        position.quantity = new_quantity
        if abs(position.quantity) < 1e-9:
            position.quantity = 0.0
            position.average_price = 0.0

        trade = Trade(date=date, symbol=symbol, quantity=quantity, price=price,
                      cost=abs(cost), reason=reason)
        self.cash += trade.cash_flow
        self.trades.append(trade)
        return trade

    def charge(self, amount: float) -> None:
        """Deduct a non-trade cost (borrow fees, financing, data)."""
        self.cash -= abs(amount)

    # ------------------------------------------------------------- reporting

    def blotter(self) -> pd.DataFrame:
        if not self.trades:
            return pd.DataFrame(
                columns=["date", "symbol", "quantity", "price", "cost", "notional", "reason"]
            )
        return pd.DataFrame(
            [
                {
                    "date": t.date,
                    "symbol": t.symbol,
                    "quantity": t.quantity,
                    "price": t.price,
                    "cost": t.cost,
                    "notional": t.notional,
                    "reason": t.reason,
                }
                for t in self.trades
            ]
        )

    def realised_pnl(self) -> float:
        return sum(p.realised for p in self.positions.values())

    def total_costs(self) -> float:
        return sum(t.cost for t in self.trades)

    def __repr__(self) -> str:  # pragma: no cover - debugging aid
        held = sum(1 for p in self.positions.values() if p.quantity != 0)
        return f"<Portfolio cash={self.cash:,.0f} positions={held} trades={len(self.trades)}>"
