"""The single place the rest of the bot gets data from.

:class:`PriceRepository` walks the configured provider chain per symbol and caches
whatever comes back. :class:`MarketData` assembles the result into aligned panels
(date x symbol) and carries the macro, news and fundamental context alongside.

Two alignment decisions worth knowing about:

* Symbols trade on different calendars (NYSE, XETRA, crypto 24/7). Panels use the
  union of all observed dates and forward-fill prices across a symbol's holidays,
  because a portfolio still has to be *valued* on a day one of its markets is
  shut. Returns on those days come out as zero, which is correct.
* A separate ``traded`` mask records which symbol genuinely printed a bar on which
  day. Execution consults it, so the bot never sends an order into a closed market
  at a forward-filled price.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Dict, List, Optional, Sequence

import pandas as pd

from ..config import Config
from ..utils.cache import Cache
from ..utils.logging import get_logger
from .base import OHLCV_COLUMNS, PriceProvider, ProviderError
from .fundamentals import FundamentalData
from .macro import MacroData
from .news import NewsData
from .providers import build_providers

log = get_logger(__name__)

#: How far a price may be carried across a symbol's holidays before we treat the
#: series as stale and stop valuing it.
_MAX_FFILL_DAYS = 5


class PriceRepository:
    """Fetch daily bars for a universe, with provider fallback and caching."""

    def __init__(
        self,
        cfg: Config,
        cache: Optional[Cache] = None,
        providers: Optional[Sequence[PriceProvider]] = None,
    ) -> None:
        self.cfg = cfg
        self.cache = cache or Cache(cfg.resolve(cfg.run.cache_dir), cfg.data.cache_ttl_hours)
        self.providers = list(providers) if providers is not None else build_providers(cfg)
        #: Which provider actually served each symbol — surfaced in reports.
        self.sources: Dict[str, str] = {}
        self._disabled: set[str] = set()

    def fetch(self, symbol: str, start: str, end: Optional[str]) -> Optional[pd.DataFrame]:
        """Bars for one symbol from the first provider that can serve them."""

        def loader() -> Optional[pd.DataFrame]:
            for provider in self.providers:
                if provider.name in self._disabled:
                    continue
                if not provider.available():
                    log.debug("provider %s unavailable — skipping", provider.name)
                    continue
                try:
                    frame = provider.fetch(symbol, start, end)
                except ProviderError as exc:
                    # A transport-level failure is about the provider, not the
                    # symbol: stop asking it anything else this run.
                    log.warning("provider %s disabled for this run: %s", provider.name, exc)
                    self._disabled.add(provider.name)
                    continue
                except Exception as exc:  # a broken provider must not kill the run
                    log.warning("provider %s raised on %s: %s", provider.name, symbol, exc)
                    continue

                if frame is None or frame.empty:
                    continue
                if not provider.adjusted:
                    log.warning(
                        "%s served %s unadjusted — splits and dividends will look like "
                        "real returns to the model",
                        provider.name,
                        symbol,
                    )
                self.sources[symbol] = provider.name
                log.debug("%s: %d bars from %s", symbol, len(frame), provider.name)
                return frame
            return None

        key = f"{symbol}-{start}-{end or 'latest'}"
        frame = self.cache.frame("prices", key, loader)
        if frame is not None and symbol not in self.sources:
            self.sources[symbol] = "cache"
        return frame

    def fetch_many(
        self, symbols: Sequence[str], start: str, end: Optional[str]
    ) -> Dict[str, pd.DataFrame]:
        out: Dict[str, pd.DataFrame] = {}
        for symbol in symbols:
            frame = self.fetch(symbol, start, end)
            if frame is None or frame.empty:
                log.warning("no price data for %s from any provider — dropping it", symbol)
                continue
            if len(frame) < self.cfg.data.min_history_bars:
                log.warning(
                    "%s has only %d bars (need %d) — dropping it",
                    symbol,
                    len(frame),
                    self.cfg.data.min_history_bars,
                )
                continue
            out[symbol] = frame
        if not out:
            raise RuntimeError(
                "no price data for any symbol. Check network access and "
                "data.price_providers; 'synthetic' always works offline."
            )
        return out


@dataclass
class MarketData:
    """Everything the strategy is allowed to look at, aligned on one calendar."""

    close: pd.DataFrame
    open: pd.DataFrame
    high: pd.DataFrame
    low: pd.DataFrame
    volume: pd.DataFrame
    #: True where the symbol actually printed a bar (i.e. its market was open).
    traded: pd.DataFrame
    macro: pd.DataFrame = field(default_factory=pd.DataFrame)
    benchmark: Optional[pd.Series] = None
    news: pd.DataFrame = field(default_factory=pd.DataFrame)
    fundamentals: pd.DataFrame = field(default_factory=pd.DataFrame)
    #: Point-in-time fundamentals indexed by (date, symbol); empty when unavailable.
    fundamentals_archive: pd.DataFrame = field(default_factory=pd.DataFrame)
    sources: Dict[str, str] = field(default_factory=dict)

    # ---------------------------------------------------------------- helpers

    @property
    def symbols(self) -> List[str]:
        return list(self.close.columns)

    @property
    def dates(self) -> pd.DatetimeIndex:
        return self.close.index

    def returns(self) -> pd.DataFrame:
        return self.close.pct_change(fill_method=None).fillna(0.0)

    def slice(self, start=None, end=None) -> "MarketData":
        """A view restricted to a date range, for walk-forward windows."""

        def cut(frame):
            if frame is None or not isinstance(frame, (pd.DataFrame, pd.Series)) or frame.empty:
                return frame
            out = frame
            if start is not None:
                out = out[out.index >= pd.Timestamp(start)]
            if end is not None:
                out = out[out.index <= pd.Timestamp(end)]
            return out

        return MarketData(
            close=cut(self.close),
            open=cut(self.open),
            high=cut(self.high),
            low=cut(self.low),
            volume=cut(self.volume),
            traded=cut(self.traded),
            macro=cut(self.macro),
            benchmark=cut(self.benchmark),
            news=self.news,
            fundamentals=self.fundamentals,
            fundamentals_archive=self.fundamentals_archive,
            sources=self.sources,
        )

    def describe(self) -> str:
        by_source: Dict[str, int] = {}
        for src in self.sources.values():
            by_source[src] = by_source.get(src, 0) + 1
        parts = [
            f"{len(self.symbols)} symbols",
            f"{len(self.dates)} bars",
            f"{self.dates.min().date()} -> {self.dates.max().date()}" if len(self.dates) else "empty",
            "sources: " + ", ".join(f"{k}={v}" for k, v in sorted(by_source.items())),
        ]
        if not self.macro.empty:
            parts.append(f"macro: {len(self.macro.columns)} series")
        if not self.news.empty:
            parts.append(f"news: {len(self.news)} headlines")
        if not self.fundamentals.empty:
            parts.append(f"fundamentals: {len(self.fundamentals)} symbols")
        if not self.fundamentals_archive.empty:
            parts.append("fundamentals: point-in-time archive")
        return " | ".join(parts)


def load_market_data(
    cfg: Config,
    cache: Optional[Cache] = None,
    *,
    for_backtest: bool = True,
    symbols: Optional[Sequence[str]] = None,
) -> MarketData:
    """Assemble a :class:`MarketData` bundle from the configured sources.

    ``for_backtest`` controls the honesty rules: snapshot fundamentals and live
    RSS headlines are only attached when running live, because neither is
    point-in-time and both would leak the future into a historical simulation.
    """
    cache = cache or Cache(cfg.resolve(cfg.run.cache_dir), cfg.data.cache_ttl_hours)
    symbols = list(symbols if symbols is not None else cfg.universe.symbols)
    repo = PriceRepository(cfg, cache)

    log.info("fetching prices for %d symbols", len(symbols))
    frames = repo.fetch_many(symbols, cfg.data.start, cfg.data.end)

    index = pd.DatetimeIndex(sorted(set().union(*(f.index for f in frames.values()))))
    panels = {}
    for column in OHLCV_COLUMNS:
        wide = pd.DataFrame(
            {sym: f[column].reindex(index) for sym, f in frames.items()}, index=index
        )
        panels[column] = wide

    traded = panels["close"].notna()
    for column in ("open", "high", "low", "close"):
        panels[column] = panels[column].ffill(limit=_MAX_FFILL_DAYS)
    panels["volume"] = panels["volume"].fillna(0.0)

    # A symbol whose price could not be carried forward at all is unusable on that
    # day; drop days where nothing at all is valued.
    valid_rows = panels["close"].notna().any(axis=1)
    for column in OHLCV_COLUMNS:
        panels[column] = panels[column][valid_rows]
    traded = traded[valid_rows]

    benchmark = None
    bench_symbol = cfg.universe.benchmark
    if bench_symbol:
        if bench_symbol in panels["close"].columns:
            benchmark = panels["close"][bench_symbol].copy()
        else:
            bench_frame = repo.fetch(bench_symbol, cfg.data.start, cfg.data.end)
            if bench_frame is not None and not bench_frame.empty:
                benchmark = bench_frame["close"].reindex(panels["close"].index).ffill()
            else:
                log.info("benchmark %s unavailable — reports will omit it", bench_symbol)

    macro = pd.DataFrame()
    if cfg.data.use_macro:
        macro = MacroData(cfg, cache).load(start=cfg.data.start)
        if not macro.empty:
            macro = macro.reindex(panels["close"].index).ffill()

    news = pd.DataFrame()
    fundamentals = pd.DataFrame()
    fundamentals_archive = pd.DataFrame()

    if cfg.data.use_news and not for_backtest:
        news_data = NewsData(cfg, cache)
        news = news_data.tag_symbols(news_data.recent(), list(frames))
    elif cfg.data.use_news and for_backtest:
        log.info(
            "backtest mode: skipping live RSS headlines (no historical archive behind "
            "them). Supply one via NewsData.load_archive to backtest sentiment."
        )

    if cfg.data.use_fundamentals:
        if for_backtest:
            log.info(
                "backtest mode: skipping snapshot fundamentals (they are not "
                "point-in-time). Supply an archive to backtest the value/quality factors."
            )
        else:
            fundamentals = FundamentalData(cfg, cache).snapshot(list(frames))

    data = MarketData(
        close=panels["close"],
        open=panels["open"],
        high=panels["high"],
        low=panels["low"],
        volume=panels["volume"],
        traded=traded,
        macro=macro,
        benchmark=benchmark,
        news=news,
        fundamentals=fundamentals,
        fundamentals_archive=fundamentals_archive,
        sources=repo.sources,
    )
    log.info("market data ready: %s", data.describe())
    return data
