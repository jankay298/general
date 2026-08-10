"""Fundamental factor scores.

Turns a table of company metrics into the three classic style factors. Each
returns a raw Series per symbol; the cross-sectional layer standardises them.

The construction choices matter more than the factor names:

* **Value uses yields, not ratios.** A company with 2c of earnings has a P/E of
  900 and would dominate any z-score built on P/E; its earnings yield is a
  well-behaved 0.001. See :func:`quantbot.data.fundamentals.derive_metrics`.
* **Quality is an average of standardised parts, not a sum of raw ones.** ROE is
  a percentage, gross margin is a fraction, leverage is a multiple — adding them
  directly would just be a bet on whichever has the largest units.
* **Missing metrics reduce coverage, they do not zero the score.** A symbol
  scored on two of three quality metrics keeps a meaningful score; one scored on
  none gets NaN and is excluded from the ranking rather than ranked as average.
"""

from __future__ import annotations

from typing import Dict, Sequence

import numpy as np
import pandas as pd

from ..utils.logging import get_logger

log = get_logger(__name__)

#: metric -> (sign, weight). Sign is +1 when more is better.
_VALUE_PARTS = {
    "earnings_yield": (+1.0, 0.40),
    "book_yield": (+1.0, 0.20),
    "sales_yield": (+1.0, 0.15),
    "ebitda_yield": (+1.0, 0.25),
}

_QUALITY_PARTS = {
    "roe": (+1.0, 0.30),
    "profit_margin": (+1.0, 0.25),
    "operating_margin": (+1.0, 0.20),
    "gross_margin": (+1.0, 0.15),
    "leverage": (-1.0, 0.10),  # more debt is lower quality
}

_GROWTH_PARTS = {
    "revenue_growth": (+1.0, 0.55),
    "earnings_growth": (+1.0, 0.45),
}

#: A cell must be scored on at least this share of a factor's weight to count.
_MIN_COVERAGE = 0.4


def _robust_z(series: pd.Series, clip: float = 3.0) -> pd.Series:
    """Median/MAD standardisation — outlier-resistant, which matters here.

    One misreported ROE of 4000% would drag a mean/std z-score for the whole
    universe. The median and MAD barely notice it.
    """
    values = pd.to_numeric(series, errors="coerce")
    if values.notna().sum() < 3:
        return pd.Series(np.nan, index=series.index)
    median = values.median()
    mad = (values - median).abs().median()
    # 1.4826 rescales MAD to be comparable with a standard deviation for normal data.
    scale = 1.4826 * mad
    if not np.isfinite(scale) or scale == 0:
        scale = values.std(ddof=0)
    if not np.isfinite(scale) or scale == 0:
        return pd.Series(0.0, index=series.index).where(values.notna())
    return ((values - median) / scale).clip(-clip, clip)


def _composite(frame: pd.DataFrame, parts: Dict[str, tuple]) -> pd.Series:
    """Weighted average of standardised parts, renormalised by actual coverage."""
    total_weight = sum(w for _, w in parts.values())
    acc = pd.Series(0.0, index=frame.index)
    covered = pd.Series(0.0, index=frame.index)

    for metric, (sign, weight) in parts.items():
        if metric not in frame.columns:
            continue
        z = _robust_z(frame[metric]) * sign
        mask = z.notna()
        acc = acc.add(z.fillna(0.0) * weight, fill_value=0.0)
        covered = covered.add(mask.astype(float) * weight, fill_value=0.0)

    fraction = covered / total_weight
    out = acc / covered.replace(0.0, np.nan)
    return out.where(fraction >= _MIN_COVERAGE)


def fundamental_scores(frame: pd.DataFrame) -> Dict[str, pd.Series]:
    """Value, quality and growth scores from a per-symbol fundamentals table."""
    if frame is None or frame.empty:
        return {}

    scores = {
        "value": _composite(frame, _VALUE_PARTS),
        "quality": _composite(frame, _QUALITY_PARTS),
        "growth": _composite(frame, _GROWTH_PARTS),
    }
    for name, series in scores.items():
        coverage = series.notna().sum()
        log.debug("fundamental factor %s: %d/%d symbols scored", name, coverage, len(frame))
    return scores


def to_panel(
    scores: Dict[str, pd.Series], index: pd.DatetimeIndex, symbols: Sequence[str]
) -> Dict[str, pd.DataFrame]:
    """Broadcast a per-symbol score onto a date x symbol panel.

    Only correct for the *live* path, where the snapshot describes today. A
    backtest must build its panels from a point-in-time archive instead — see
    :meth:`quantbot.data.fundamentals.FundamentalData.as_of`.
    """
    panels: Dict[str, pd.DataFrame] = {}
    for name, series in scores.items():
        row = series.reindex(symbols)
        panels[name] = pd.DataFrame(
            np.tile(row.to_numpy(dtype=float), (len(index), 1)),
            index=index,
            columns=list(symbols),
        )
    return panels
