"""Position sizing.

The strategy decides *what* to hold; this module decides *how much*. Two ideas
carry most of the weight:

**Inverse-volatility weighting.** Equal dollar weights are not equal risk weights.
A 5% position in a 60%-vol biotech contributes several times the portfolio risk of
a 5% position in a utility, so an equal-weight book is quietly a bet on whichever
names happen to be most volatile. Sizing by 1/vol equalises the *risk* each name
contributes.

**Volatility targeting.** Realised volatility is strongly autocorrelated — calm
markets tend to stay calm and turbulent ones to stay turbulent — so scaling gross
exposure by (target vol / recently estimated vol) is one of the few adjustments
that reliably improves risk-adjusted returns rather than just moving return and
risk together. The estimate uses a shrunk covariance matrix, because a sample
covariance over 60 days and 30 assets is mostly noise.
"""

from __future__ import annotations

from typing import Optional

import numpy as np
import pandas as pd

from ..utils.logging import get_logger

log = get_logger(__name__)

TRADING_DAYS = 252


def inverse_vol_weights(
    scores: pd.Series, vol: pd.Series, floor: float = 0.05, cap: float = 1.50
) -> pd.Series:
    """Combine conviction with 1/vol sizing.

    ``scores`` are non-negative conviction values. Volatility is clipped into
    [floor, cap] so a single suspiciously quiet series cannot claim an enormous
    position — the usual cause of a near-zero vol estimate is stale data, not a
    genuinely riskless asset.
    """
    if scores.empty:
        return scores
    v = vol.reindex(scores.index).astype(float)
    v = v.fillna(v.median() if v.notna().any() else 0.20).clip(floor, cap)
    raw = scores / v
    total = raw.sum()
    return raw / total if total > 0 else raw


def shrunk_covariance(returns: pd.DataFrame, shrinkage: float = 0.30) -> pd.DataFrame:
    """Sample covariance pulled toward a constant-correlation target.

    With more assets than observations the sample covariance is singular and its
    extreme eigenvalues are artefacts. Shrinking toward a structured target keeps
    the matrix invertible and the vol estimate stable.
    """
    clean = returns.dropna(axis=1, how="all")
    if clean.shape[1] == 0:
        return pd.DataFrame()
    sample = clean.cov()
    variances = np.diag(sample.to_numpy())
    std = np.sqrt(np.clip(variances, 1e-12, None))

    off_diagonal = sample.to_numpy()[~np.eye(len(sample), dtype=bool)]
    denominator = np.outer(std, std)[~np.eye(len(sample), dtype=bool)]
    with np.errstate(invalid="ignore", divide="ignore"):
        correlations = off_diagonal / denominator
    mean_correlation = float(np.nanmean(correlations)) if correlations.size else 0.0
    mean_correlation = float(np.clip(mean_correlation, -0.9, 0.9))

    target = np.outer(std, std) * mean_correlation
    np.fill_diagonal(target, variances)

    blended = (1.0 - shrinkage) * sample.to_numpy() + shrinkage * target
    return pd.DataFrame(blended, index=sample.index, columns=sample.columns)


def portfolio_volatility(
    weights: pd.Series, returns: pd.DataFrame, lookback: int = 63, shrinkage: float = 0.30
) -> float:
    """Annualised volatility forecast for a weight vector."""
    if weights.empty or returns.empty:
        return 0.0
    active = weights[weights.abs() > 1e-9]
    if active.empty:
        return 0.0

    columns = [c for c in active.index if c in returns.columns]
    if not columns:
        return 0.0

    window = returns[columns].tail(lookback).dropna(axis=1, how="all")
    if window.shape[0] < 10 or window.shape[1] == 0:
        return 0.0

    cov = shrunk_covariance(window, shrinkage)
    w = active.reindex(cov.columns).fillna(0.0).to_numpy()
    variance = float(w @ cov.to_numpy() @ w)
    if not np.isfinite(variance) or variance <= 0:
        return 0.0
    return float(np.sqrt(variance * TRADING_DAYS))


def scale_to_target_vol(
    weights: pd.Series,
    returns: pd.DataFrame,
    target_vol: float,
    lookback: int = 63,
    max_leverage: float = 1.0,
    min_scale: float = 0.10,
    previous_scale: Optional[float] = None,
    deadband: float = 0.0,
) -> tuple[pd.Series, float]:
    """Scale a weight vector toward a target annualised volatility.

    Returns the scaled weights and the multiplier applied. When volatility cannot
    be estimated (too little history) the weights pass through unchanged — a
    guess would be worse than leaving the strategy's own sizing alone.

    ``deadband`` suppresses small changes to the multiplier. The volatility
    estimate wanders slightly every day, and since the multiplier touches *every*
    position, acting on each wander rewrites the whole book for no change in view.
    """
    if weights.empty:
        return weights, 1.0

    estimated = portfolio_volatility(weights, returns, lookback)
    if estimated <= 0:
        log.debug("volatility estimate unavailable — leaving weights unscaled")
        return weights, 1.0

    scale = float(np.clip(target_vol / estimated, min_scale, max_leverage))

    if previous_scale is not None and previous_scale > 0 and deadband > 0:
        if abs(scale / previous_scale - 1.0) < deadband:
            scale = previous_scale
    log.debug(
        "vol targeting: estimated %.1f%%, target %.1f%%, scale %.2f",
        estimated * 100,
        target_vol * 100,
        scale,
    )
    return weights * scale, scale


def atr_stop_levels(
    entry_prices: pd.Series, atr: pd.Series, multiple: float, long: bool = True
) -> pd.Series:
    """Initial stop level per position, ``multiple`` ATRs away from entry."""
    if multiple <= 0:
        return pd.Series(np.nan, index=entry_prices.index)
    distance = atr.reindex(entry_prices.index) * multiple
    return entry_prices - distance if long else entry_prices + distance


def kelly_fraction(
    win_rate: float, win_loss_ratio: float, cap: float = 0.25
) -> float:
    """Kelly bet size, capped hard.

    Full Kelly is the growth-optimal bet only if you know the true edge exactly;
    you don't, and overestimating it is catastrophic rather than merely
    suboptimal. Practitioners run a fraction of Kelly for that reason, so this is
    capped at *cap* and returns 0 for a negative edge.
    """
    if not 0 < win_rate < 1 or win_loss_ratio <= 0:
        return 0.0
    fraction = win_rate - (1.0 - win_rate) / win_loss_ratio
    return float(np.clip(fraction, 0.0, cap))
