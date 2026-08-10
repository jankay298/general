"""The strategy interface.

A strategy is split into two phases on purpose:

``prepare(data)`` does all the panel-wide work once — indicators, standardisation,
regime classification. ``target_weights(date)`` then answers for a single day.

That split is what lets the *same* object drive a ten-year backtest (prepare once,
call per rebalance date) and a live session (prepare on today's data, call once).
If the two paths ran different code, the backtest would be measuring something
other than what actually trades, which is the most common way a strategy that
"worked in testing" loses money.
"""

from __future__ import annotations

import abc
import copy
from dataclasses import dataclass, field
from typing import Dict, List, Optional

import pandas as pd

from ..config import Config
from ..data.repository import MarketData


@dataclass
class StrategyDiagnostics:
    """Why the strategy did what it did on one date — for reports and live logs."""

    date: pd.Timestamp
    regime: str = "neutral"
    regime_score: float = 0.0
    gross_target: float = 0.0
    factor_weights: Dict[str, float] = field(default_factory=dict)
    active_factors: List[str] = field(default_factory=list)
    eligible: int = 0
    selected_long: List[str] = field(default_factory=list)
    selected_short: List[str] = field(default_factory=list)
    vol_scale: float = 1.0
    estimated_vol: float = 0.0
    notes: List[str] = field(default_factory=list)

    def summary(self) -> str:
        parts = [
            f"{pd.Timestamp(self.date).date()}",
            f"regime={self.regime}({self.regime_score:+.2f})",
            f"eligible={self.eligible}",
            f"long={len(self.selected_long)}",
        ]
        if self.selected_short:
            parts.append(f"short={len(self.selected_short)}")
        parts.append(f"vol_scale={self.vol_scale:.2f}")
        if self.estimated_vol:
            parts.append(f"est_vol={self.estimated_vol:.1%}")
        if self.notes:
            parts.append("| " + "; ".join(self.notes))
        return " ".join(parts)


class Strategy(abc.ABC):
    """Base class for anything that produces target weights."""

    name: str = "base"

    def __init__(self, cfg: Config) -> None:
        self.cfg = cfg
        self.data: Optional[MarketData] = None
        self._prepared = False
        self.last_diagnostics: Optional[StrategyDiagnostics] = None

    @abc.abstractmethod
    def prepare(self, data: MarketData) -> None:
        """Precompute everything that does not depend on a single date."""

    @abc.abstractmethod
    def target_weights(
        self, date: pd.Timestamp, current: Optional[pd.Series] = None
    ) -> pd.Series:
        """Target portfolio weights on *date*, as fractions of equity.

        Must only use information available at the close of *date*. The engine
        adds the execution lag; the strategy must not try to be clever about it.

        ``current`` is the book as it stands, so the strategy can apply hysteresis
        and avoid selling a name that has slipped one rank. Passing it is what
        lets the same code path serve the backtest and the live loop, where the
        current book comes from the broker rather than from a simulation.
        """

    def rebalance_dates(self, index: pd.DatetimeIndex) -> pd.DatetimeIndex:
        """Which bars this strategy wants to trade on."""
        rule = self.cfg.backtest.rebalance
        if rule in ("B", "D", "daily"):
            return index
        # Resample to the requested frequency and snap to the last actual trading
        # day at or before each period end — calendars have holidays, offsets don't.
        marks = pd.Series(index, index=index).resample(rule).last().dropna()
        return pd.DatetimeIndex(sorted(set(marks.to_numpy())))

    def with_config(self, cfg: Config) -> "Strategy":
        """A copy of this strategy running under a different config.

        Precomputed panels are *shared*, not recomputed. That is safe because
        ``prepare`` only reads price data, and its outputs are never mutated
        afterwards — but it is only valid when the new config leaves the
        prepare-time inputs (features, regime, universe, data) untouched. The
        walk-forward search relies on this: without it, a 9-point grid over 12
        folds recomputes the same indicator panels 108 times.
        """
        self.require_prepared()
        clone = copy.copy(self)
        clone.cfg = cfg
        clone.last_diagnostics = None
        # Any per-run state must start fresh, or one trial leaks into the next.
        if hasattr(clone, "_last_vol_scale"):
            clone._last_vol_scale = None
        return clone

    def require_prepared(self) -> None:
        if not self._prepared:
            raise RuntimeError(f"{type(self).__name__}.prepare() must be called first")

    def __repr__(self) -> str:  # pragma: no cover - debugging aid
        return f"<{type(self).__name__} name={self.name!r} prepared={self._prepared}>"
