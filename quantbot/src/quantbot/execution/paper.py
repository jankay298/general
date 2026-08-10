"""A local paper broker.

State lives in a JSON file, so a paper account survives restarts and accumulates
a real track record over weeks — which is the point. A paper run that resets
every time you launch the process tests nothing except that the code runs.

Fills are immediate at the reference price plus modelled slippage, using the same
:class:`~quantbot.backtest.costs.CostModel` as the backtester. That is optimistic
about queue position and pessimistic about nothing, so treat paper results as an
upper bound — but at least it is the *same* upper bound the backtest reported.
"""

from __future__ import annotations

import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Dict, Optional

import numpy as np
import pandas as pd

from ..backtest.costs import CostModel
from ..utils.logging import get_logger
from .broker import Account, Broker, Order, OrderResult

log = get_logger(__name__)


class PaperBroker(Broker):
    """Simulated venue with persistent state."""

    name = "paper"
    is_live = False

    def __init__(
        self,
        state_file: str | Path,
        initial_cash: float = 100_000.0,
        cost_model: Optional[CostModel] = None,
        allow_fractional: bool = True,
    ) -> None:
        self.state_file = Path(state_file)
        self.initial_cash = float(initial_cash)
        self.costs = cost_model or CostModel()
        self.allow_fractional = allow_fractional
        self._state = self._load()

    @classmethod
    def from_config(cls, cfg) -> "PaperBroker":
        return cls(
            state_file=cfg.resolve(cfg.execution.state_file),
            initial_cash=cfg.backtest.initial_capital,
            cost_model=CostModel.from_config(cfg.backtest.costs),
        )

    # ------------------------------------------------------------------ state

    def _load(self) -> Dict:
        if self.state_file.is_file():
            try:
                state = json.loads(self.state_file.read_text(encoding="utf-8"))
                state.setdefault("cash", self.initial_cash)
                state.setdefault("positions", {})
                state.setdefault("fills", [])
                state.setdefault("last_prices", {})
                log.info(
                    "paper account loaded from %s: cash %.2f, %d positions",
                    self.state_file, state["cash"], len(state["positions"]),
                )
                return state
            except Exception as exc:
                log.error(
                    "paper state at %s is unreadable (%s) — refusing to silently "
                    "start a fresh account over it; move or delete the file to reset",
                    self.state_file, exc,
                )
                raise
        log.info("starting a new paper account with %.2f", self.initial_cash)
        return {
            "cash": self.initial_cash,
            "positions": {},
            "fills": [],
            "last_prices": {},
            "opened_at": datetime.now(timezone.utc).isoformat(),
        }

    def save(self) -> None:
        self.state_file.parent.mkdir(parents=True, exist_ok=True)
        temp = self.state_file.with_suffix(".json.tmp")
        temp.write_text(json.dumps(self._state, indent=2, default=str), encoding="utf-8")
        temp.replace(self.state_file)  # atomic: a crash cannot corrupt the account

    def mark(self, prices: pd.Series) -> None:
        """Record the latest prices so equity can be computed between sessions."""
        for symbol, price in prices.dropna().items():
            if price > 0:
                self._state["last_prices"][str(symbol)] = float(price)

    # -------------------------------------------------------------- interface

    def account(self) -> Account:
        cash = float(self._state["cash"])
        prices = self._state.get("last_prices", {})
        market_value = sum(
            quantity * prices.get(symbol, 0.0)
            for symbol, quantity in self._state["positions"].items()
        )
        equity = cash + market_value
        return Account(
            equity=equity,
            cash=cash,
            # No margin in the paper account: you can spend the cash you have.
            buying_power=max(cash, 0.0),
            blocked=False,
        )

    def positions(self) -> pd.Series:
        held = {s: float(q) for s, q in self._state["positions"].items() if abs(float(q)) > 1e-9}
        return pd.Series(held, dtype=float)

    def submit(self, order: Order, reference_price: Optional[float] = None) -> OrderResult:
        price = reference_price or self._state["last_prices"].get(order.symbol)
        if price is None or not np.isfinite(price) or price <= 0:
            return OrderResult(
                order=order, accepted=False, status="rejected",
                message=f"no price available for {order.symbol}",
            )

        quantity = order.quantity
        if not self.allow_fractional:
            quantity = float(int(quantity))
            if quantity == 0:
                return OrderResult(order=order, accepted=False, status="rejected",
                                   message="rounds to zero shares")

        notional = abs(quantity) * price
        cost = self.costs.trade_cost(notional)
        # Slippage: you cross the spread, so buys fill a little high and sells low.
        fill_price = price * (1.0 + np.sign(quantity) * self.costs.spread_bps * 1e-4)
        cash_flow = -quantity * fill_price - cost

        if quantity > 0 and self._state["cash"] + cash_flow < 0:
            return OrderResult(
                order=order, accepted=False, status="rejected",
                message=f"insufficient cash: need {abs(cash_flow):,.2f}, "
                        f"have {self._state['cash']:,.2f}",
            )

        current = float(self._state["positions"].get(order.symbol, 0.0))
        new_quantity = current + quantity
        if abs(new_quantity) < 1e-9:
            self._state["positions"].pop(order.symbol, None)
        else:
            self._state["positions"][order.symbol] = new_quantity

        self._state["cash"] += cash_flow
        self._state["last_prices"][order.symbol] = float(price)
        self._state["fills"].append(
            {
                "timestamp": datetime.now(timezone.utc).isoformat(),
                "symbol": order.symbol,
                "quantity": quantity,
                "price": fill_price,
                "cost": cost,
                "reason": order.reason,
            }
        )
        self.save()

        return OrderResult(
            order=order, accepted=True, status="filled",
            broker_order_id=f"paper-{len(self._state['fills'])}",
            filled_quantity=quantity, filled_price=fill_price,
            message=f"filled {quantity:+,.4f} @ {fill_price:,.4f} (cost {cost:,.2f})",
        )

    # --------------------------------------------------------------- extras

    def fills(self) -> pd.DataFrame:
        records = self._state.get("fills", [])
        if not records:
            return pd.DataFrame(columns=["timestamp", "symbol", "quantity", "price", "cost", "reason"])
        frame = pd.DataFrame(records)
        frame["timestamp"] = pd.to_datetime(frame["timestamp"])
        return frame

    def reset(self) -> None:
        """Wipe the account back to its starting cash."""
        self._state = {
            "cash": self.initial_cash,
            "positions": {},
            "fills": [],
            "last_prices": {},
            "opened_at": datetime.now(timezone.utc).isoformat(),
        }
        self.save()
        log.warning("paper account reset to %.2f", self.initial_cash)
