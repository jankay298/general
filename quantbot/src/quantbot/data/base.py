"""The contract every price provider has to satisfy.

A provider returns a DataFrame indexed by tz-naive midnight timestamps with the
columns in :data:`OHLCV_COLUMNS`. Prices are expected to be split- and
dividend-adjusted; where a source cannot do that, the provider says so via
:attr:`PriceProvider.adjusted` and the repository logs a warning, because
unadjusted prices manufacture fake gaps that a momentum model will happily trade.
"""

from __future__ import annotations

import abc
from typing import Optional

import numpy as np
import pandas as pd

from ..utils.logging import get_logger

log = get_logger(__name__)

OHLCV_COLUMNS = ["open", "high", "low", "close", "volume"]


class ProviderError(RuntimeError):
    """Raised for a provider-level failure that should fall through to the next one."""


class PriceProvider(abc.ABC):
    """Fetches daily bars for one symbol at a time."""

    name: str = "base"
    #: False if the source serves raw prices that ignore splits and dividends.
    adjusted: bool = True
    #: Roughly how many requests per minute the source tolerates. None = unmetered.
    rate_limit_per_minute: Optional[int] = None

    @abc.abstractmethod
    def fetch(self, symbol: str, start: str, end: Optional[str]) -> Optional[pd.DataFrame]:
        """Return normalised OHLCV bars, or ``None`` when the symbol is unavailable.

        Raise :class:`ProviderError` for transport problems (so the repository can
        try the next provider); return ``None`` for "this source simply does not
        know this symbol".
        """

    def available(self) -> bool:
        """Whether this provider can run at all (imports present, key configured)."""
        return True

    def __repr__(self) -> str:  # pragma: no cover - debugging aid
        return f"<{type(self).__name__} name={self.name!r}>"


def normalise_frame(df: pd.DataFrame, *, symbol: str = "?") -> Optional[pd.DataFrame]:
    """Coerce a provider's raw output into the canonical shape.

    Handles the usual annoyances: capitalised column names, yfinance's MultiIndex
    columns, string prices, tz-aware or unsorted indices, duplicate dates, and
    rows where the OHLC relationship is impossible.
    """
    if df is None or len(df) == 0:
        return None

    df = df.copy()

    # yfinance hands back a MultiIndex ("Close", "AAPL") when given a list.
    if isinstance(df.columns, pd.MultiIndex):
        df.columns = [str(c[0]) for c in df.columns]

    rename = {}
    for col in df.columns:
        key = str(col).strip().lower().replace(" ", "_")
        if key in {"adj_close", "adjclose", "adjusted_close", "5._adjusted_close"}:
            rename[col] = "adj_close"
        elif key.startswith(("1._", "2._", "3._", "4._", "6._")):
            rename[col] = key.split("_", 1)[1]  # Alpha Vantage's "1. open"
        else:
            rename[col] = key
    df = df.rename(columns=rename)

    # Prefer a genuinely adjusted close when the source provides both, and rescale
    # the rest of the bar by the same factor so OHLC stays internally consistent.
    if "adj_close" in df.columns and "close" in df.columns:
        close = pd.to_numeric(df["close"], errors="coerce")
        adj = pd.to_numeric(df["adj_close"], errors="coerce")
        factor = (adj / close.replace(0, np.nan)).replace([np.inf, -np.inf], np.nan).fillna(1.0)
        for col in ("open", "high", "low", "close"):
            if col in df.columns:
                df[col] = pd.to_numeric(df[col], errors="coerce") * factor
        df = df.drop(columns=["adj_close"])

    missing = [c for c in OHLCV_COLUMNS if c not in df.columns]
    if "close" in missing:
        log.debug("%s: provider frame has no close column (%s)", symbol, list(df.columns))
        return None
    for col in missing:
        # Volume is genuinely absent for some sources (FX, indices); OHLC can be
        # backfilled from close without distorting anything a daily model uses.
        df[col] = 0.0 if col == "volume" else df["close"]

    df = df[OHLCV_COLUMNS].apply(pd.to_numeric, errors="coerce")

    idx = pd.to_datetime(df.index, errors="coerce", utc=True)
    df.index = idx.tz_convert(None).normalize()
    df = df[df.index.notna()]

    df = df[~df.index.duplicated(keep="last")].sort_index()
    df = df.dropna(subset=["close"])
    df = df[df["close"] > 0]

    if df.empty:
        return None

    # Repair impossible bars rather than dropping them: a bad high/low would break
    # ATR and Donchian, but the close is usually still good.
    df["high"] = df[["high", "open", "close"]].max(axis=1)
    df["low"] = df[["low", "open", "close"]].min(axis=1)
    df["volume"] = df["volume"].fillna(0.0).clip(lower=0.0)

    return df
