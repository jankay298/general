"""Yahoo Finance.

Uses ``yfinance`` when it is installed and works, otherwise talks to the public
chart endpoint directly. Yahoo rate-limits aggressively and blocks some hosting
ranges outright (HTTP 429) — both paths therefore fail softly so the repository
moves on to the next provider instead of aborting the run.
"""

from __future__ import annotations

import datetime as dt
from typing import Optional

import pandas as pd
import requests

from ...utils.logging import get_logger
from ..base import PriceProvider, ProviderError, normalise_frame

log = get_logger(__name__)

_CHART_URL = "https://query1.finance.yahoo.com/v8/finance/chart/{symbol}"
_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 "
        "(KHTML, like Gecko) Chrome/124.0 Safari/537.36"
    ),
    "Accept": "application/json",
}


class YahooProvider(PriceProvider):
    name = "yahoo"
    adjusted = True

    def __init__(self, timeout: float = 20.0, use_yfinance: bool = True) -> None:
        self.timeout = timeout
        self.use_yfinance = use_yfinance
        self._yf = None
        self._yf_broken = False
        if use_yfinance:
            try:
                import yfinance  # noqa: F401

                self._yf = yfinance
            except ImportError:
                log.debug("yfinance not installed; using the raw chart endpoint")

    def fetch(self, symbol: str, start: str, end: Optional[str]) -> Optional[pd.DataFrame]:
        if self._yf is not None and not self._yf_broken:
            frame = self._fetch_yfinance(symbol, start, end)
            if frame is not None:
                return frame
        return self._fetch_chart_api(symbol, start, end)

    # ------------------------------------------------------------------ paths

    def _fetch_yfinance(self, symbol: str, start: str, end: Optional[str]) -> Optional[pd.DataFrame]:
        try:
            raw = self._yf.download(
                symbol,
                start=start,
                end=end,
                interval="1d",
                auto_adjust=True,
                progress=False,
                threads=False,
            )
        except Exception as exc:
            log.debug("yfinance failed for %s (%s); falling back to chart API", symbol, exc)
            return None
        if raw is None or raw.empty:
            # One empty frame is a missing symbol; a run of them means we are blocked.
            return None
        return normalise_frame(raw, symbol=symbol)

    def _fetch_chart_api(self, symbol: str, start: str, end: Optional[str]) -> Optional[pd.DataFrame]:
        p1 = int(pd.Timestamp(start).timestamp())
        p2 = int(pd.Timestamp(end).timestamp()) if end else int(dt.datetime.now().timestamp())
        params = {
            "period1": p1,
            "period2": p2,
            "interval": "1d",
            "events": "div,split",
            "includeAdjustedClose": "true",
        }
        try:
            resp = requests.get(
                _CHART_URL.format(symbol=symbol),
                params=params,
                headers=_HEADERS,
                timeout=self.timeout,
            )
        except requests.RequestException as exc:
            raise ProviderError(f"yahoo transport error for {symbol}: {exc}") from exc

        if resp.status_code == 404:
            return None
        if resp.status_code == 429:
            raise ProviderError("yahoo rate limited this host (HTTP 429)")
        if resp.status_code >= 400:
            raise ProviderError(f"yahoo HTTP {resp.status_code} for {symbol}")

        try:
            payload = resp.json()["chart"]
        except (ValueError, KeyError) as exc:
            raise ProviderError(f"yahoo sent an unparseable body for {symbol}: {exc}") from exc

        if payload.get("error"):
            log.debug("yahoo error for %s: %s", symbol, payload["error"])
            return None
        results = payload.get("result") or []
        if not results:
            return None

        result = results[0]
        timestamps = result.get("timestamp") or []
        quote = (result.get("indicators", {}).get("quote") or [{}])[0]
        if not timestamps or not quote:
            return None

        frame = pd.DataFrame(
            {
                "open": quote.get("open"),
                "high": quote.get("high"),
                "low": quote.get("low"),
                "close": quote.get("close"),
                "volume": quote.get("volume"),
            },
            index=pd.to_datetime(timestamps, unit="s", utc=True),
        )
        adj = result.get("indicators", {}).get("adjclose")
        if adj and adj[0].get("adjclose"):
            frame["adj_close"] = adj[0]["adjclose"]
        return normalise_frame(frame, symbol=symbol)
