"""Hard exposure limits and the drawdown kill switch.

Limits are applied *after* the strategy has spoken and are not negotiable. The
order matters: capping per-position weight changes the gross, so gross is
enforced last, and re-capping afterwards would undo it. Sector caps sit between
the two because they are the constraint most likely to bind on a factor book —
"buy cheap quality" often resolves to "buy the whole energy sector".
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Dict, List, Optional

import numpy as np
import pandas as pd

from ..config import RiskConfig
from ..utils.logging import get_logger

log = get_logger(__name__)


def apply_limits(
    weights: pd.Series,
    cfg: RiskConfig,
    sectors: Optional[Dict[str, str]] = None,
) -> pd.Series:
    """Enforce per-position, per-sector, gross and net exposure limits."""
    if weights.empty:
        return weights
    w = weights.astype(float).fillna(0.0)

    # 1. Per-position cap.
    w = w.clip(-cfg.max_weight, cfg.max_weight)

    # 2. Sector cap — scale down the offending sector, leave everything else alone.
    if sectors:
        labels = pd.Series({s: sectors.get(s, "_unknown") for s in w.index})
        for sector, members in labels.groupby(labels).groups.items():
            if sector == "_unknown":
                continue
            cols = [c for c in members if c in w.index]
            exposure = w[cols].abs().sum()
            if exposure > cfg.max_sector_weight and exposure > 0:
                w[cols] = w[cols] * (cfg.max_sector_weight / exposure)
                log.debug(
                    "sector %s trimmed from %.1f%% to %.1f%%",
                    sector,
                    exposure * 100,
                    cfg.max_sector_weight * 100,
                )

    # 3. Net exposure — shift the whole book rather than dropping names, so the
    #    relative bets the strategy expressed survive the adjustment.
    net = w.sum()
    if abs(net) > cfg.max_net and len(w) > 0:
        w = w - np.sign(net) * (abs(net) - cfg.max_net) / len(w)

    # 4. Gross exposure, enforced last so nothing after it can inflate the book.
    gross = w.abs().sum()
    if gross > cfg.max_gross and gross > 0:
        w = w * (cfg.max_gross / gross)

    return w


@dataclass
class DrawdownGuard:
    """Flattens the book when the equity curve falls too far from its high.

    This is the one risk control that overrides every signal. A model that is
    wrong in a way it was never trained for produces confident, consistent, and
    entirely incorrect positions; no amount of position sizing helps. Stopping
    and standing aside for a fixed cooldown does.

    The cooldown is deliberately measured in bars rather than "until the model
    looks better", because the model's own opinion is precisely what is in doubt.
    """

    threshold: float
    cooldown_bars: int = 10
    high_water_mark: float = 0.0
    triggered_at: Optional[pd.Timestamp] = None
    bars_since_trigger: int = 0
    #: (date, drawdown) for each firing — surfaced in the backtest report.
    events: List[tuple] = field(default_factory=list)

    @property
    def active(self) -> bool:
        """True while the guard is suppressing trading."""
        return self.triggered_at is not None

    def update(self, equity: float, date: Optional[pd.Timestamp] = None) -> bool:
        """Feed the latest equity; returns True if trading is currently blocked."""
        if equity <= 0:
            return self.active

        self.high_water_mark = max(self.high_water_mark, equity)

        if self.active:
            self.bars_since_trigger += 1
            if self.bars_since_trigger >= self.cooldown_bars:
                # Reset the high-water mark to where we actually are, so the guard
                # does not immediately re-fire against a peak we may never revisit.
                self.high_water_mark = equity
                self.triggered_at = None
                self.bars_since_trigger = 0
                log.info("drawdown guard released on %s", date)
            return self.active

        drawdown = equity / self.high_water_mark - 1.0
        if drawdown <= -abs(self.threshold):
            self.triggered_at = date
            self.bars_since_trigger = 0
            self.events.append((date, drawdown))
            log.warning(
                "drawdown guard fired on %s: %.1f%% below the high-water mark — "
                "flattening for %d bars",
                date,
                drawdown * 100,
                self.cooldown_bars,
            )
        return self.active

    def reset(self) -> None:
        self.high_water_mark = 0.0
        self.triggered_at = None
        self.bars_since_trigger = 0
        self.events.clear()


@dataclass
class TrailingStops:
    """Per-position trailing stops in ATR multiples.

    Kept separate from the strategy so a stop-out is recorded as a risk event
    rather than silently looking like a change of mind about the signal.
    """

    multiple: float
    #: symbol -> best price seen since entry.
    peaks: Dict[str, float] = field(default_factory=dict)
    stopped: Dict[str, pd.Timestamp] = field(default_factory=dict)

    def update(
        self,
        positions: pd.Series,
        prices: pd.Series,
        atr: pd.Series,
        date: Optional[pd.Timestamp] = None,
    ) -> List[str]:
        """Return the symbols whose trailing stop was hit on this bar."""
        if self.multiple <= 0 or positions.empty:
            return []

        hits: List[str] = []
        for symbol, quantity in positions.items():
            if quantity == 0:
                self.peaks.pop(symbol, None)
                continue
            price = prices.get(symbol)
            band = atr.get(symbol)
            if price is None or not np.isfinite(price) or band is None or not np.isfinite(band):
                continue

            long = quantity > 0
            peak = self.peaks.get(symbol)
            self.peaks[symbol] = max(peak, price) if long and peak is not None else (
                min(peak, price) if not long and peak is not None else price
            )
            peak = self.peaks[symbol]

            distance = band * self.multiple
            if (long and price <= peak - distance) or (not long and price >= peak + distance):
                hits.append(symbol)
                self.stopped[symbol] = date
                self.peaks.pop(symbol, None)

        if hits:
            log.info("trailing stop hit on %s: %s", date, ", ".join(hits))
        return hits

    def clear(self, symbol: str) -> None:
        self.peaks.pop(symbol, None)
