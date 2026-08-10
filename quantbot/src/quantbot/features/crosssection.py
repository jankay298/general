"""Cross-sectional standardisation.

Raw factor values are not comparable to each other: trend is measured in
volatility units, earnings yield in percent, sentiment in [-1, 1]. Before they can
be blended into one score they have to be put on a common scale *within each
date*, across the universe.

Two choices worth defending:

* **Winsorised z-scores, not raw z-scores.** One symbol with a corrupted price
  produces a 40-sigma outlier that would otherwise dominate the whole composite.
* **Rank-based fallback for small universes.** With eight symbols a z-score is
  mostly noise, so below a threshold we standardise ranks instead, which is
  distribution-free.
"""

from __future__ import annotations

from typing import Dict, Optional, Sequence

import numpy as np
import pandas as pd

#: Below this many valid observations on a date, z-scores are replaced by ranks.
_RANK_FALLBACK_BELOW = 12


def cross_sectional_zscore(
    panel: pd.DataFrame, clip: float = 3.0, min_observations: int = 3
) -> pd.DataFrame:
    """Standardise each row across symbols; rows with too little data become NaN."""
    if panel.empty:
        return panel

    valid = panel.notna().sum(axis=1)
    mean = panel.mean(axis=1)
    std = panel.std(axis=1, ddof=0)

    # A constant row has zero dispersion: everything is equally good, so score 0.
    z = panel.sub(mean, axis=0).div(std.replace(0.0, np.nan), axis=0)
    z = z.where(std.replace(0.0, np.nan).notna(), 0.0).where(panel.notna())
    z = z.clip(-clip, clip)

    small = valid < max(min_observations, _RANK_FALLBACK_BELOW)
    if small.any():
        ranked = cross_sectional_rank(panel, min_observations=min_observations)
        # Map a uniform rank in [0,1] onto roughly the same scale as a z-score.
        ranked = (ranked - 0.5) * 2.0 * clip / 1.5
        z = z.mask(small, ranked)

    return z.where(valid >= min_observations)


def cross_sectional_rank(panel: pd.DataFrame, min_observations: int = 3) -> pd.DataFrame:
    """Percentile rank within each date, in [0, 1]; ties share the average rank."""
    if panel.empty:
        return panel
    valid = panel.notna().sum(axis=1)
    ranked = panel.rank(axis=1, pct=True, na_option="keep")
    return ranked.where(valid >= min_observations)


def neutralise(panel: pd.DataFrame, groups: Dict[str, str]) -> pd.DataFrame:
    """Demean each row within its group (usually sector).

    Without this a "buy quality" signal quietly becomes "buy staples", and the
    portfolio's real bet is a sector bet nobody chose to make.
    """
    if panel.empty or not groups:
        return panel
    labels = pd.Series({sym: groups.get(sym, "_unknown") for sym in panel.columns})
    out = panel.copy()
    for group, members in labels.groupby(labels).groups.items():
        cols = [c for c in members if c in panel.columns]
        if len(cols) < 2:
            continue  # a one-member group would be demeaned to exactly zero
        block = panel[cols]
        out[cols] = block.sub(block.mean(axis=1), axis=0)
    return out


def blend(
    scores: Dict[str, pd.DataFrame],
    weights: Dict[str, float],
    min_weight_covered: float = 0.35,
) -> pd.DataFrame:
    """Weighted average of standardised factors, renormalised per cell.

    Factors are missing at different times for different symbols (a young listing
    has no 12-month momentum; a private-equity-owned name has no P/E). Rather than
    dropping those symbols, each cell is divided by the weight that was actually
    available there — so a symbol scored on 70% of the factor weight is comparable
    to one scored on 100%. Cells covering less than *min_weight_covered* of the
    total weight are dropped as too thin to trust.
    """
    usable = {name: w for name, w in weights.items() if w > 0 and name in scores}
    if not usable:
        raise ValueError("no factor has both a positive weight and computed scores")

    total = sum(usable.values())
    index = next(iter(scores.values())).index
    columns = next(iter(scores.values())).columns

    weighted = pd.DataFrame(0.0, index=index, columns=columns)
    covered = pd.DataFrame(0.0, index=index, columns=columns)
    for name, weight in usable.items():
        panel = scores[name].reindex(index=index, columns=columns)
        mask = panel.notna()
        weighted = weighted.add(panel.fillna(0.0) * weight, fill_value=0.0)
        covered = covered.add(mask.astype(float) * weight, fill_value=0.0)

    fraction = covered / total
    combined = weighted / covered.replace(0.0, np.nan)
    return combined.where(fraction >= min_weight_covered)


def winsorise(series: pd.Series, lower: float = 0.01, upper: float = 0.99) -> pd.Series:
    """Clip a series to its own quantiles — used before fitting anything to it."""
    if series.dropna().empty:
        return series
    lo, hi = series.quantile(lower), series.quantile(upper)
    return series.clip(lo, hi)


def latest_row(panel: pd.DataFrame, symbols: Optional[Sequence[str]] = None) -> pd.Series:
    """The last row of a panel as a Series, reindexed onto *symbols* if given."""
    if panel.empty:
        return pd.Series(dtype=float)
    row = panel.iloc[-1]
    return row.reindex(symbols) if symbols is not None else row
