"""Macro regime detection.

The bot does not try to forecast the economy. It does something much more modest
and much more robust: it measures how stressed financial conditions are *right
now*, and changes how much risk it is willing to run accordingly.

The stress score is a rolling z-score composite of indicators that move fast
enough to be useful — implied volatility, high-yield credit spreads, the shape of
the yield curve, the dollar, the Sahm-rule unemployment gap, and the benchmark's
own trend. Positive means stressed.

Three implementation details do most of the work:

* **Rolling, causal z-scores.** Each component is standardised against its own
  trailing window, never the full sample. Standardising against the full history
  would leak 2020 into 2016.
* **Hysteresis.** Regimes are sticky: leaving one requires clearing the threshold
  by a margin. Without it the label flickers day to day and the portfolio pays
  transaction costs for the privilege.
* **Graceful degradation.** Any component that is missing is simply left out. With
  no macro data at all the regime is permanently ``neutral``, which is a defensible
  default rather than a crash.
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum
from typing import Dict, List, Optional

import numpy as np
import pandas as pd

from ..config import RegimeConfig
from ..utils.logging import get_logger

log = get_logger(__name__)


class Regime(str, Enum):
    RISK_ON = "risk_on"
    NEUTRAL = "neutral"
    RISK_OFF = "risk_off"


@dataclass
class RegimeSeries:
    """Per-date regime label, stress score and the exposure it implies."""

    score: pd.Series
    label: pd.Series
    gross_exposure: pd.Series
    components: pd.DataFrame
    config: RegimeConfig

    def at(self, date) -> Regime:
        """Regime on *date*, using the last known label at or before it."""
        if self.label.empty:
            return Regime.NEUTRAL
        window = self.label.loc[:date]
        return Regime(window.iloc[-1]) if len(window) else Regime.NEUTRAL

    def gross_at(self, date) -> float:
        if self.gross_exposure.empty:
            return float(self.config.gross_exposure.get("neutral", 0.7))
        window = self.gross_exposure.loc[:date]
        return float(window.iloc[-1]) if len(window) else 0.7

    def weights_at(self, date) -> Dict[str, float]:
        """Factor weights the regime on *date* prescribes."""
        regime = self.at(date)
        return dict(self.config.factor_weights.get(regime.value, {}))

    def summary(self) -> str:
        if self.label.empty:
            return "regime: unavailable (no macro data)"
        counts = self.label.value_counts(normalize=True)
        parts = [f"{name} {share:.0%}" for name, share in counts.items()]
        return f"regime: {self.label.iloc[-1]} (score {self.score.iloc[-1]:+.2f}) | history: " + ", ".join(parts)


def _rolling_z(series: pd.Series, window: int) -> pd.Series:
    """Causal z-score against a trailing window."""
    mean = series.rolling(window, min_periods=max(30, window // 8)).mean()
    std = series.rolling(window, min_periods=max(30, window // 8)).std(ddof=0)
    return ((series - mean) / std.replace(0.0, np.nan)).clip(-4, 4)


def classify_regime(
    macro: pd.DataFrame,
    cfg: RegimeConfig,
    benchmark: Optional[pd.Series] = None,
    index: Optional[pd.DatetimeIndex] = None,
) -> RegimeSeries:
    """Build the regime series from macro data and (optionally) benchmark prices."""
    index = index if index is not None else (macro.index if not macro.empty else None)
    if index is None or len(index) == 0:
        empty = pd.Series(dtype=float)
        return RegimeSeries(empty, pd.Series(dtype=object), empty, pd.DataFrame(), cfg)

    window = cfg.lookback_days
    components: Dict[str, pd.Series] = {}

    if not macro.empty:
        m = macro.reindex(index).ffill()

        if "vix" in m:
            components["vix_level"] = _rolling_z(m["vix"], window)
        if "vix_chg_21d" in m:
            components["vix_momentum"] = _rolling_z(m["vix_chg_21d"], window)
        if "hy_oas" in m:
            components["credit_level"] = _rolling_z(m["hy_oas"], window)
        if "hy_oas_chg_21d" in m:
            components["credit_momentum"] = _rolling_z(m["hy_oas_chg_21d"], window)
        if "curve_10y2y" in m:
            # An inverted curve is the stressed state, hence the sign flip.
            components["curve_inversion"] = -_rolling_z(m["curve_10y2y"], window)
        if "usd_index" in m:
            # A rapidly appreciating dollar tightens global financial conditions.
            components["dollar_surge"] = _rolling_z(m["usd_index"].pct_change(63), window)
        if "unemp_vs_min_12m" in m:
            components["labour_deterioration"] = _rolling_z(m["unemp_vs_min_12m"], window)
        if "nfci" in m:
            components["financial_conditions"] = _rolling_z(m["nfci"], window)

    if benchmark is not None and not benchmark.empty:
        bench = benchmark.reindex(index).ffill()
        trend = bench / bench.rolling(200, min_periods=100).mean() - 1.0
        components["benchmark_trend"] = -_rolling_z(trend, window)
        drawdown = bench / bench.cummax() - 1.0
        components["benchmark_drawdown"] = -_rolling_z(drawdown, window)
        realised = bench.pct_change(fill_method=None).rolling(21, min_periods=10).std()
        components["benchmark_vol"] = _rolling_z(realised, window)

    if not components:
        log.warning("no regime components available — holding the neutral regime")
        neutral_gross = float(cfg.gross_exposure.get("neutral", 0.7))
        return RegimeSeries(
            score=pd.Series(0.0, index=index),
            label=pd.Series(Regime.NEUTRAL.value, index=index),
            gross_exposure=pd.Series(neutral_gross, index=index),
            components=pd.DataFrame(index=index),
            config=cfg,
        )

    frame = pd.DataFrame(components, index=index)
    # Average the components that exist on each date rather than requiring all of
    # them — early history has fewer, and demanding a full set would blank it out.
    raw_score = frame.mean(axis=1, skipna=True)
    # A short EMA takes the daily jitter out without adding lag that matters.
    score = raw_score.ewm(span=5, adjust=False).mean().fillna(0.0)

    label = _label_with_hysteresis(score, cfg)
    gross = label.map(lambda r: float(cfg.gross_exposure.get(r, 0.7))).astype(float)

    log.info(
        "regime components: %s | current: %s (%.2f)",
        ", ".join(frame.columns),
        label.iloc[-1],
        score.iloc[-1],
    )
    return RegimeSeries(score=score, label=label, gross_exposure=gross, components=frame, config=cfg)


def _label_with_hysteresis(
    score: pd.Series, cfg: RegimeConfig, band: Optional[float] = None
) -> pd.Series:
    """Turn the stress score into sticky regime labels.

    Two independent brakes, because one is not enough:

    * **Hysteresis.** Entering a regime uses the configured threshold; leaving it
      requires clearing that threshold by *band*.
    * **Minimum dwell time.** Even having cleared the band, a regime is held for
      ``cfg.min_dwell_bars``. Without this, a score that wanders near a threshold
      for months produces a new "regime" every couple of weeks — each one
      rescaling the whole book. Financial conditions do not actually change that
      often; the label was tracking noise.
    """
    band = cfg.hysteresis_band if band is None else band
    labels: List[str] = []
    current = Regime.NEUTRAL.value
    dwell = cfg.min_dwell_bars

    for value in score.to_numpy():
        proposed = current
        if not np.isnan(value):
            if current == Regime.RISK_OFF.value:
                if value < cfg.risk_off_threshold - band:
                    proposed = (
                        Regime.RISK_ON.value
                        if value < cfg.risk_on_threshold
                        else Regime.NEUTRAL.value
                    )
            elif current == Regime.RISK_ON.value:
                if value > cfg.risk_on_threshold + band:
                    proposed = (
                        Regime.RISK_OFF.value
                        if value > cfg.risk_off_threshold
                        else Regime.NEUTRAL.value
                    )
            else:  # neutral
                if value > cfg.risk_off_threshold:
                    proposed = Regime.RISK_OFF.value
                elif value < cfg.risk_on_threshold:
                    proposed = Regime.RISK_ON.value

        if proposed != current and dwell >= cfg.min_dwell_bars:
            current = proposed
            dwell = 0
        else:
            dwell += 1
        labels.append(current)

    return pd.Series(labels, index=score.index, dtype=object)
