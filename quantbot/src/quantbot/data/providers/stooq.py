"""Stooq daily bars.

A plain CSV endpoint with no key and no meaningful rate limit, covering US and
European equities, indices, FX and commodities. Stooq occasionally answers with a
JavaScript bot-check page instead of CSV; that is detected and treated as a
provider failure rather than as an empty symbol.
"""

from __future__ import annotations

import io
from typing import Optional

import pandas as pd
import requests

from ...utils.logging import get_logger
from ..base import PriceProvider, ProviderError, normalise_frame

log = get_logger(__name__)

_URL = "https://stooq.com/q/d/l/"

#: Yahoo-style index tickers translated to Stooq's naming.
_INDEX_MAP = {
    "^GSPC": "^spx",
    "^SPX": "^spx",
    "^DJI": "^dji",
    "^IXIC": "^ndq",
    "^NDX": "^ndx",
    "^RUT": "^rut",
    "^VIX": "^vix",
    "^GDAXI": "^dax",
    "^STOXX50E": "^sx5e",
    "^FTSE": "^ukx",
    "^N225": "^nkx",
}


def to_stooq_symbol(symbol: str) -> str:
    """Map a canonical ticker onto Stooq's symbol convention."""
    s = symbol.strip()
    if s.upper() in _INDEX_MAP:
        return _INDEX_MAP[s.upper()]
    if s.startswith("^"):
        return s.lower()
    if "." in s or "/" in s or "=" in s:
        # Already exchange-qualified (SAP.DE), an FX pair (EURUSD=X) or similar.
        return s.lower().replace("=x", "")
    return f"{s.lower()}.us"


class StooqProvider(PriceProvider):
    name = "stooq"
    adjusted = True  # Stooq serves split- and dividend-adjusted series for equities.

    def __init__(self, timeout: float = 20.0) -> None:
        self.timeout = timeout

    def fetch(self, symbol: str, start: str, end: Optional[str]) -> Optional[pd.DataFrame]:
        params = {"s": to_stooq_symbol(symbol), "i": "d"}
        params["d1"] = pd.Timestamp(start).strftime("%Y%m%d")
        if end:
            params["d2"] = pd.Timestamp(end).strftime("%Y%m%d")

        try:
            resp = requests.get(_URL, params=params, timeout=self.timeout)
        except requests.RequestException as exc:
            raise ProviderError(f"stooq transport error for {symbol}: {exc}") from exc
        if resp.status_code >= 400:
            raise ProviderError(f"stooq HTTP {resp.status_code} for {symbol}")

        text = resp.text
        head = text.lstrip()[:200].lower()
        if head.startswith(("<!doctype", "<html")):
            raise ProviderError("stooq served a bot-check page instead of CSV")
        if text.strip().lower().startswith("no data") or "," not in text:
            return None

        try:
            frame = pd.read_csv(io.StringIO(text))
        except Exception as exc:
            raise ProviderError(f"stooq CSV unparseable for {symbol}: {exc}") from exc
        if "Date" not in frame.columns:
            return None
        frame = frame.set_index("Date")
        return normalise_frame(frame, symbol=symbol)
