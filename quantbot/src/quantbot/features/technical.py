"""Technical indicators, computed on whole panels at once.

Every function here is *strictly causal*: the value at row ``t`` uses only rows
``<= t``. That is not a stylistic preference — a single centred window or a
forward-shifted column silently turns a backtest into a fortune teller, and the
resulting equity curve looks wonderful right up until it is traded.

Panels are DataFrames indexed by date with one column per symbol, so everything
vectorises across the universe.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Dict

import numpy as np
import pandas as pd

from ..config import FeatureConfig
from ..data.repository import MarketData

TRADING_DAYS = 252


# --------------------------------------------------------------------------- #
# Primitives
# --------------------------------------------------------------------------- #


def sma(panel: pd.DataFrame, window: int) -> pd.DataFrame:
    return panel.rolling(window, min_periods=max(2, window // 2)).mean()


def ema(panel: pd.DataFrame, span: int) -> pd.DataFrame:
    return panel.ewm(span=span, adjust=False, min_periods=max(2, span // 2)).mean()


def wilder(panel: pd.DataFrame, period: int) -> pd.DataFrame:
    """Wilder's smoothing — the averaging RSI, ATR and ADX are actually defined with."""
    return panel.ewm(alpha=1.0 / period, adjust=False, min_periods=period).mean()


def rsi(close: pd.DataFrame, period: int = 14) -> pd.DataFrame:
    delta = close.diff()
    gain = wilder(delta.clip(lower=0.0), period)
    loss = wilder((-delta).clip(lower=0.0), period)
    rs = gain / loss.replace(0.0, np.nan)
    out = 100.0 - 100.0 / (1.0 + rs)
    # loss == 0 means an unbroken run of up days: RSI is 100 by definition.
    return out.where(loss.notna() & (loss != 0), 100.0).where(gain.notna())


def true_range(high: pd.DataFrame, low: pd.DataFrame, close: pd.DataFrame) -> pd.DataFrame:
    prev_close = close.shift(1)
    hl = (high - low).abs()
    hc = (high - prev_close).abs()
    lc = (low - prev_close).abs()
    # Elementwise max of the three, keeping the panel's index and columns.
    return pd.DataFrame(
        np.fmax(np.fmax(hl.to_numpy(), hc.to_numpy()), lc.to_numpy()),
        index=hl.index,
        columns=hl.columns,
    )


def atr(high: pd.DataFrame, low: pd.DataFrame, close: pd.DataFrame, period: int = 14) -> pd.DataFrame:
    return wilder(true_range(high, low, close), period)


def realised_vol(returns: pd.DataFrame, window: int = 63) -> pd.DataFrame:
    """Annualised realised volatility."""
    return returns.rolling(window, min_periods=max(10, window // 3)).std() * np.sqrt(TRADING_DAYS)


def macd_histogram(close: pd.DataFrame, fast: int = 12, slow: int = 26, signal: int = 9) -> pd.DataFrame:
    line = ema(close, fast) - ema(close, slow)
    return line - ema(line, signal)


def donchian_position(close: pd.DataFrame, high: pd.DataFrame, low: pd.DataFrame, period: int = 55) -> pd.DataFrame:
    """Where the close sits inside its N-day range, mapped to [-1, +1].

    The channel is measured on bars strictly before today, so touching a new high
    today reads as +1 rather than being folded into its own maximum.
    """
    top = high.shift(1).rolling(period, min_periods=period // 2).max()
    bottom = low.shift(1).rolling(period, min_periods=period // 2).min()
    span = (top - bottom).replace(0.0, np.nan)
    return (2.0 * (close - bottom) / span - 1.0).clip(-1.5, 1.5)


def adx(high: pd.DataFrame, low: pd.DataFrame, close: pd.DataFrame, period: int = 14) -> pd.DataFrame:
    """Trend strength (not direction). High ADX = the trend factor deserves more trust."""
    up = high.diff()
    down = -low.diff()
    plus_dm = up.where((up > down) & (up > 0), 0.0)
    minus_dm = down.where((down > up) & (down > 0), 0.0)
    tr = wilder(true_range(high, low, close), period).replace(0.0, np.nan)
    plus_di = 100.0 * wilder(plus_dm, period) / tr
    minus_di = 100.0 * wilder(minus_dm, period) / tr
    dx = 100.0 * (plus_di - minus_di).abs() / (plus_di + minus_di).replace(0.0, np.nan)
    return wilder(dx, period)


def dollar_volume(close: pd.DataFrame, volume: pd.DataFrame, window: int = 21) -> pd.DataFrame:
    """Average traded notional — the liquidity filter and the impact model use it."""
    return (close * volume).rolling(window, min_periods=5).mean()


# --------------------------------------------------------------------------- #
# Bundle
# --------------------------------------------------------------------------- #


@dataclass
class TechnicalFeatures:
    """All technical panels for one universe, plus the raw signals built from them."""

    returns: pd.DataFrame
    vol: pd.DataFrame
    atr: pd.DataFrame
    rsi: pd.DataFrame
    adx: pd.DataFrame
    dollar_volume: pd.DataFrame
    #: Raw (not yet cross-sectionally standardised) factor signals.
    signals: Dict[str, pd.DataFrame] = None  # type: ignore[assignment]

    def latest(self, name: str) -> pd.Series:
        return self.signals[name].iloc[-1]


def compute_technicals(data: MarketData, cfg: FeatureConfig) -> TechnicalFeatures:
    """Turn OHLCV panels into the raw factor signals the strategy ranks on."""
    close, high, low, volume = data.close, data.high, data.low, data.volume
    returns = close.pct_change(fill_method=None)

    vol = realised_vol(returns, cfg.vol_lookback)
    atr_panel = atr(high, low, close, cfg.atr_period)
    rsi_panel = rsi(close, cfg.rsi_period)
    adx_panel = adx(high, low, close, cfg.atr_period)
    dv = dollar_volume(close, volume)

    fast = sma(close, cfg.trend_fast)
    slow = sma(close, cfg.trend_slow)

    # --- trend -----------------------------------------------------------
    # Distance from the long moving average, expressed in volatility units so a
    # quiet utility and a violent small cap are comparable numbers.
    daily_vol = (vol / np.sqrt(TRADING_DAYS)).replace(0.0, np.nan)
    trend_distance = (close / slow - 1.0) / daily_vol.clip(lower=1e-4)
    ma_cross = (fast / slow - 1.0) / daily_vol.clip(lower=1e-4)
    breakout = donchian_position(close, high, low, cfg.donchian_period)
    # ADX gates the trend factor: in a directionless tape, trend signals are noise.
    trend_confidence = (adx_panel.fillna(20.0) / 25.0).clip(0.3, 1.6)
    trend = (
        0.45 * trend_distance.clip(-4, 4)
        + 0.35 * ma_cross.clip(-4, 4)
        + 0.20 * breakout * 2.0
    ) * trend_confidence

    # --- momentum --------------------------------------------------------
    # 12-1: skip the most recent month, whose short-horizon reversal works against
    # the longer-horizon momentum effect.
    lookback, skip = cfg.momentum_lookback, cfg.momentum_skip
    momentum = close.shift(skip) / close.shift(lookback) - 1.0
    # Risk-adjust, otherwise the ranking is just a list of the most volatile names.
    momentum = momentum / vol.clip(lower=0.05)

    # --- short-term reversal --------------------------------------------
    reversal_raw = close / close.shift(cfg.reversal_lookback) - 1.0
    reversal = -reversal_raw / daily_vol.clip(lower=1e-4)
    # RSI's own overbought/oversold reading, rescaled to roughly [-1, 1].
    reversal = 0.7 * reversal.clip(-4, 4) + 0.3 * ((50.0 - rsi_panel) / 25.0)

    # --- low-volatility / defensive -------------------------------------
    low_vol = -vol

    signals = {
        "trend": trend,
        "momentum": momentum,
        "reversal": reversal,
        "low_vol": low_vol,
        "macd": macd_histogram(close) / close.clip(lower=1e-6),
        "breakout": breakout,
    }

    # A signal is only meaningful where the symbol has genuinely traded recently;
    # forward-filled holiday prices must not keep generating fresh conviction.
    stale = ~data.traded.rolling(5, min_periods=1).max().astype(bool)
    for name, panel in signals.items():
        signals[name] = panel.mask(stale)

    return TechnicalFeatures(
        returns=returns,
        vol=vol,
        atr=atr_panel,
        rsi=rsi_panel,
        adx=adx_panel,
        dollar_volume=dv,
        signals=signals,
    )
