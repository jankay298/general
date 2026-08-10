"""Company fundamentals.

Two sources, tried in order: Alpha Vantage's ``OVERVIEW`` endpoint and
``yfinance``'s info dictionary. Both return a **current snapshot**, which matters
enormously:

* For **live trading** a snapshot is exactly right — it is what is knowable today.
* For a **backtest** a snapshot is poison. Applying today's ROE and P/E to 2017
  prices tells the strategy in 2017 which companies would turn out well, and the
  equity curve that comes out is fiction.

So snapshots are only used live. A backtest wants ``fundamentals_archive``: a CSV
with a ``date`` column, one row per symbol per reporting date, containing values
*as they were reported then*. Without that file the fundamental factors are
disabled for the backtest and their weight is redistributed — see
:class:`quantbot.strategy.composite.CompositeStrategy`.
"""

from __future__ import annotations

from pathlib import Path
from typing import Dict, List, Optional, Sequence

import numpy as np
import pandas as pd

from ..config import Config
from ..utils.cache import Cache
from ..utils.logging import get_logger

log = get_logger(__name__)

#: Canonical metric name -> the keys each source uses for it.
_FIELD_MAP: Dict[str, Sequence[str]] = {
    "sector": ("Sector", "sector"),
    "industry": ("Industry", "industry"),
    "market_cap": ("MarketCapitalization", "marketCap"),
    "pe": ("PERatio", "trailingPE"),
    "forward_pe": ("ForwardPE", "forwardPE"),
    "pb": ("PriceToBookRatio", "priceToBook"),
    "ps": ("PriceToSalesRatioTTM", "priceToSalesTrailing12Months"),
    "ev_ebitda": ("EVToEBITDA", "enterpriseToEbitda"),
    "roe": ("ReturnOnEquityTTM", "returnOnEquity"),
    "roa": ("ReturnOnAssetsTTM", "returnOnAssets"),
    "profit_margin": ("ProfitMargin", "profitMargins"),
    "operating_margin": ("OperatingMarginTTM", "operatingMargins"),
    "gross_profit": ("GrossProfitTTM", "grossProfits"),
    "revenue": ("RevenueTTM", "totalRevenue"),
    "revenue_growth": ("QuarterlyRevenueGrowthYOY", "revenueGrowth"),
    "earnings_growth": ("QuarterlyEarningsGrowthYOY", "earningsGrowth"),
    "debt_to_equity": ("DebtToEquity", "debtToEquity"),
    "dividend_yield": ("DividendYield", "dividendYield"),
    "beta": ("Beta", "beta"),
    "eps": ("EPS", "trailingEps"),
}

_NUMERIC = [k for k in _FIELD_MAP if k not in {"sector", "industry"}]


def _to_float(value) -> float:
    if value in (None, "", "None", "-", "NaN"):
        return float("nan")
    try:
        return float(value)
    except (TypeError, ValueError):
        return float("nan")


class FundamentalData:
    """Snapshot fundamentals for live use, plus point-in-time archives for backtests."""

    #: Give up on a source after this many symbols in a row come back empty. One
    #: miss is a delisted ticker; six in a row means the source is unreachable, and
    #: waiting for a network timeout per symbol turns a 20-symbol universe into a
    #: multi-minute stall on every live run.
    _FAILURE_LIMIT = 5

    def __init__(self, cfg: Config, cache: Cache) -> None:
        self.cfg = cfg
        self.cache = cache
        self._consecutive_failures = 0
        self._source_disabled = False

    # ------------------------------------------------------------- snapshot

    def snapshot(self, symbols: Sequence[str], ttl_hours: float = 24.0) -> pd.DataFrame:
        """Current fundamentals, one row per symbol. For live trading only."""
        rows: List[Dict] = []
        for symbol in symbols:
            raw = self.cache.json(
                "fundamentals", f"snapshot-{symbol}", lambda s=symbol: self._fetch(s), ttl_hours
            )
            if not raw:
                continue
            row: Dict[str, object] = {"symbol": symbol}
            for canonical, keys in _FIELD_MAP.items():
                value = next((raw[k] for k in keys if k in raw and raw[k] not in (None, "")), None)
                row[canonical] = value if canonical in {"sector", "industry"} else _to_float(value)
            rows.append(row)

        if not rows:
            log.warning("no fundamentals available for any symbol")
            return pd.DataFrame(columns=["symbol", *_FIELD_MAP])

        frame = pd.DataFrame(rows).set_index("symbol")
        log.info("fundamentals loaded for %d/%d symbols", len(frame), len(symbols))
        return derive_metrics(frame)

    def _fetch(self, symbol: str) -> Optional[dict]:
        if self._source_disabled:
            return None

        from .providers.alphavantage import AlphaVantageProvider

        payload = None
        av = AlphaVantageProvider(key_env=self.cfg.data.alphavantage_key_env)
        if av.available():
            payload = av.overview(symbol)

        if payload is None:
            try:
                import yfinance as yf

                info = yf.Ticker(symbol).get_info()
                if isinstance(info, dict) and info.get("symbol"):
                    payload = info
            except Exception as exc:
                log.debug("yfinance fundamentals failed for %s: %s", symbol, exc)

        if payload is None:
            self._consecutive_failures += 1
            if self._consecutive_failures >= self._FAILURE_LIMIT:
                self._source_disabled = True
                log.warning(
                    "no fundamentals source answered for %d symbols in a row — "
                    "skipping the rest of this run (value/quality/growth will be "
                    "dropped and their weight redistributed)",
                    self._consecutive_failures,
                )
            return None

        self._consecutive_failures = 0
        return payload

    # --------------------------------------------------------------- archive

    def load_archive(self, path: str | Path) -> pd.DataFrame:
        """Load point-in-time fundamentals for backtesting.

        Expects a long or wide CSV with ``date`` and ``symbol`` columns plus any of
        the canonical metric columns. The index is (date, symbol); dates are the
        *reporting/publication* dates, not the period end.
        """
        p = Path(path)
        if not p.is_file():
            raise FileNotFoundError(f"fundamentals archive not found: {p}")
        raw = pd.read_csv(p)
        cols = {str(c).strip().lower(): c for c in raw.columns}
        if "date" not in cols or "symbol" not in cols:
            raise ValueError(f"{p} needs 'date' and 'symbol' columns; found {list(raw.columns)}")

        raw = raw.rename(columns={cols["date"]: "date", cols["symbol"]: "symbol"})
        raw["date"] = pd.to_datetime(raw["date"], errors="coerce")
        raw = raw.dropna(subset=["date", "symbol"])
        for metric in _NUMERIC:
            if metric in raw.columns:
                raw[metric] = pd.to_numeric(raw[metric], errors="coerce")

        frame = raw.set_index(["date", "symbol"]).sort_index()
        log.info(
            "loaded point-in-time fundamentals: %d rows, %d symbols, %s to %s",
            len(frame),
            frame.index.get_level_values("symbol").nunique(),
            frame.index.get_level_values("date").min().date(),
            frame.index.get_level_values("date").max().date(),
        )
        return frame

    @staticmethod
    def as_of(archive: pd.DataFrame, date: pd.Timestamp, symbols: Sequence[str]) -> pd.DataFrame:
        """Latest archived row per symbol at or before *date*."""
        if archive.empty:
            return pd.DataFrame()
        dates = archive.index.get_level_values("date")
        window = archive[dates <= pd.Timestamp(date)]
        if window.empty:
            return pd.DataFrame()
        latest = window.reset_index().sort_values("date").groupby("symbol").tail(1)
        latest = latest.set_index("symbol").drop(columns=["date"])
        return derive_metrics(latest.reindex([s for s in symbols if s in latest.index]))


def derive_metrics(frame: pd.DataFrame) -> pd.DataFrame:
    """Add the yield-style inversions the scoring layer actually consumes.

    Ratios are inverted into yields (1/PE rather than PE) because yields stay
    well-behaved: a company with tiny or negative earnings has a P/E of 3000 or
    -12, and both wreck a cross-sectional z-score. Negative denominators are
    mapped to the worst-possible yield rather than silently dropped.
    """
    if frame.empty:
        return frame
    out = frame.copy()

    def yield_of(column: str) -> pd.Series:
        if column not in out.columns:
            return pd.Series(np.nan, index=out.index)
        values = pd.to_numeric(out[column], errors="coerce")
        inverted = 1.0 / values.where(values > 0)
        # A loss-making or negative-book company scores at the bottom, not as NaN.
        return inverted.where(values.isna() | (values > 0), -1.0)

    out["earnings_yield"] = yield_of("pe")
    out["book_yield"] = yield_of("pb")
    out["sales_yield"] = yield_of("ps")
    out["ebitda_yield"] = yield_of("ev_ebitda")

    if {"gross_profit", "revenue"} <= set(out.columns):
        revenue = pd.to_numeric(out["revenue"], errors="coerce")
        out["gross_margin"] = pd.to_numeric(out["gross_profit"], errors="coerce") / revenue.where(
            revenue > 0
        )
    if "debt_to_equity" in out.columns:
        de = pd.to_numeric(out["debt_to_equity"], errors="coerce")
        # yfinance reports D/E in percent, Alpha Vantage as a ratio; normalise.
        out["leverage"] = np.where(de > 10, de / 100.0, de)
    return out
