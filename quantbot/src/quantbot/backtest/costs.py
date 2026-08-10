"""Transaction costs.

Costs are where most backtests die. A strategy that rebalances weekly across
forty names turns over a large multiple of the book every year, and at a
realistic 5–15 basis points round trip that is several percentage points of
annual return — often the entire apparent edge.

Three components, because they behave differently:

* **Commission** — proportional to notional. Small, predictable, easy.
* **Spread** — you buy at the ask and sell at the bid, so every round trip pays
  the spread whether or not the trade was a good idea.
* **Market impact** — the cost of *being* the trade. Modelled as
  ``coeff * sqrt(participation)``: the square-root law is the standard empirical
  form, and it matters because it is the only cost that grows superlinearly with
  size, which is what caps a strategy's capacity.

Ignoring impact is what makes a backtest look scalable when it is not.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Optional

import numpy as np

from ..config import CostConfig

BPS = 1e-4


@dataclass
class CostModel:
    """Per-trade cost estimate in currency units."""

    commission_bps: float = 1.0
    spread_bps: float = 2.0
    impact_coeff_bps: float = 10.0
    short_borrow_bps: float = 50.0
    #: Some brokers charge a flat fee per order regardless of size.
    per_order_fee: float = 0.0

    @classmethod
    def from_config(cls, cfg: CostConfig) -> "CostModel":
        return cls(
            commission_bps=cfg.commission_bps,
            spread_bps=cfg.spread_bps,
            impact_coeff_bps=cfg.impact_coeff_bps,
            short_borrow_bps=cfg.short_borrow_bps,
        )

    def trade_cost(self, notional: float, adv: Optional[float] = None) -> float:
        """Total cost of trading *notional* against an average daily volume *adv*."""
        notional = abs(float(notional))
        if notional <= 0:
            return 0.0

        cost = notional * (self.commission_bps + self.spread_bps) * BPS + self.per_order_fee

        if self.impact_coeff_bps > 0 and adv and adv > 0:
            participation = min(notional / adv, 1.0)
            cost += notional * self.impact_coeff_bps * BPS * np.sqrt(participation)
        return float(cost)

    def effective_price(
        self, price: float, quantity: float, adv: Optional[float] = None
    ) -> float:
        """Fill price after slippage — the same cost expressed per share.

        Useful when a broker reports fills net of slippage and you want the
        simulation's prices to be directly comparable.
        """
        if quantity == 0 or price <= 0:
            return price
        notional = abs(quantity) * price
        slippage = self.trade_cost(notional, adv) / abs(quantity)
        return price + np.sign(quantity) * slippage

    def borrow_cost(self, short_notional: float, days: float = 1.0) -> float:
        """Financing charge for holding a short position."""
        if short_notional >= 0 or self.short_borrow_bps <= 0:
            return 0.0
        return abs(short_notional) * self.short_borrow_bps * BPS * days / 252.0

    def round_trip_bps(self, participation: float = 0.0) -> float:
        """Total round-trip cost in basis points — the number worth quoting."""
        one_way = self.commission_bps + self.spread_bps
        if participation > 0:
            one_way += self.impact_coeff_bps * np.sqrt(min(participation, 1.0))
        return 2.0 * one_way
