"""Alpha Vantage daily bars.

Needs a free API key in the environment (``ALPHAVANTAGE_API_KEY`` by default);
``demo`` works for a couple of showcase symbols. The free tier is metered per
minute *and* per day, so requests are throttled here and every response is cached
upstream by the repository.

The adjusted endpoint moved behind the paid plan, so we ask for it, notice the
premium message, and fall back to the unadjusted series with ``adjusted = False``
set on the instance — the repository turns that into a visible warning.
"""

from __future__ import annotations

import io
import os
import threading
import time
from typing import Optional

import pandas as pd
import requests

from ...utils.logging import get_logger
from ..base import PriceProvider, ProviderError, normalise_frame

log = get_logger(__name__)

_URL = "https://www.alphavantage.co/query"


class AlphaVantageProvider(PriceProvider):
    name = "alphavantage"
    rate_limit_per_minute = 5

    def __init__(
        self,
        key_env: str = "ALPHAVANTAGE_API_KEY",
        timeout: float = 30.0,
        requests_per_minute: int = 5,
    ) -> None:
        self.api_key = os.environ.get(key_env, "").strip()
        self.timeout = timeout
        self.min_interval = 60.0 / max(1, requests_per_minute)
        self.adjusted = True  # downgraded to False the first time we hit the paywall
        self._last_call = 0.0
        self._lock = threading.Lock()
        self._premium_blocked = False
        self._exhausted = False  # daily quota gone; stop trying for this process

    def available(self) -> bool:
        return bool(self.api_key) and not self._exhausted

    # ------------------------------------------------------------- throttling

    def _throttle(self) -> None:
        with self._lock:
            wait = self.min_interval - (time.monotonic() - self._last_call)
            if wait > 0:
                log.debug("alphavantage: sleeping %.1fs to respect the rate limit", wait)
                time.sleep(wait)
            self._last_call = time.monotonic()

    # ----------------------------------------------------------------- fetch

    def fetch(self, symbol: str, start: str, end: Optional[str]) -> Optional[pd.DataFrame]:
        if not self.api_key:
            raise ProviderError("alphavantage: no API key configured")
        if self._exhausted:
            raise ProviderError("alphavantage: daily quota exhausted earlier in this run")

        functions = [] if self._premium_blocked else ["TIME_SERIES_DAILY_ADJUSTED"]
        functions.append("TIME_SERIES_DAILY")

        for function in functions:
            frame = self._request(function, symbol)
            if frame is None:
                continue
            frame = normalise_frame(frame, symbol=symbol)
            if frame is None:
                continue
            frame = frame.loc[frame.index >= pd.Timestamp(start)]
            if end:
                frame = frame.loc[frame.index <= pd.Timestamp(end)]
            return frame if not frame.empty else None
        return None

    def _request(self, function: str, symbol: str) -> Optional[pd.DataFrame]:
        self._throttle()
        params = {
            "function": function,
            "symbol": symbol,
            "outputsize": "full",
            "datatype": "csv",
            "apikey": self.api_key,
        }
        try:
            resp = requests.get(_URL, params=params, timeout=self.timeout)
        except requests.RequestException as exc:
            raise ProviderError(f"alphavantage transport error for {symbol}: {exc}") from exc
        if resp.status_code >= 400:
            raise ProviderError(f"alphavantage HTTP {resp.status_code} for {symbol}")

        text = resp.text.strip()
        lowered = text[:400].lower()

        # Errors come back as JSON even when datatype=csv was requested.
        if lowered.startswith("{"):
            if "premium" in lowered:
                log.info(
                    "alphavantage: %s needs a paid plan — using unadjusted daily prices",
                    function,
                )
                self._premium_blocked = True
                self.adjusted = False
                return None
            if "rate limit" in lowered or "higher api call" in lowered:
                self._exhausted = True
                raise ProviderError("alphavantage: API call frequency / daily limit reached")
            if "error message" in lowered or "invalid api call" in lowered:
                return None  # unknown symbol
            log.debug("alphavantage unexpected JSON for %s: %s", symbol, text[:200])
            return None

        if "timestamp" not in lowered.split("\n")[0]:
            return None
        try:
            frame = pd.read_csv(io.StringIO(text))
        except Exception as exc:
            raise ProviderError(f"alphavantage CSV unparseable for {symbol}: {exc}") from exc
        if "timestamp" not in frame.columns:
            return None
        return frame.set_index("timestamp")

    # ------------------------------------------------------------ fundamentals

    def overview(self, symbol: str) -> Optional[dict]:
        """Company fundamentals (the ``OVERVIEW`` endpoint), or None."""
        if not self.api_key or self._exhausted:
            return None
        self._throttle()
        try:
            resp = requests.get(
                _URL,
                params={"function": "OVERVIEW", "symbol": symbol, "apikey": self.api_key},
                timeout=self.timeout,
            )
            payload = resp.json()
        except (requests.RequestException, ValueError) as exc:
            log.debug("alphavantage OVERVIEW failed for %s: %s", symbol, exc)
            return None
        if not isinstance(payload, dict) or "Symbol" not in payload:
            note = str(payload)[:160] if payload else ""
            if "rate limit" in note.lower():
                self._exhausted = True
            return None
        return payload
