"""News and world-events ingestion.

Headlines come from RSS: market wires, central-bank press rooms, and politics
desks. Each item is timestamped, deduplicated across feeds, and tagged with any
universe symbols it mentions.

**One honest limitation, stated up front.** RSS serves a rolling window of the
last few days — there is no historical archive behind it. That makes news
sentiment a *live* signal, not a backtestable one. Pretending otherwise (scoring
today's headlines and applying them to 2018 prices) is a particularly seductive
form of look-ahead bias. So:

* live trading uses :meth:`NewsData.recent`;
* backtests use :meth:`NewsData.load_archive` and need a real timestamped archive
  (``date,headline,symbols,source`` CSV) that you supply;
* with no archive, the sentiment factor is switched off for the backtest and its
  weight is redistributed across the remaining factors, which the strategy logs.
"""

from __future__ import annotations

import datetime as dt
import hashlib
import re
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Sequence

import pandas as pd
import requests

from ..config import Config
from ..utils.cache import Cache
from ..utils.logging import get_logger

log = get_logger(__name__)

_HEADERS = {"User-Agent": "quantbot/0.1 (+https://github.com/); research use"}

#: Company names that headlines use instead of the ticker.
_DEFAULT_ALIASES: Dict[str, Sequence[str]] = {
    "AAPL": ("apple",),
    "MSFT": ("microsoft",),
    "GOOGL": ("alphabet", "google"),
    "AMZN": ("amazon",),
    "NVDA": ("nvidia",),
    "META": ("meta platforms", "facebook"),
    "TSLA": ("tesla",),
    "JPM": ("jpmorgan", "jp morgan"),
    "XOM": ("exxon", "exxonmobil"),
    "JNJ": ("johnson & johnson", "johnson and johnson"),
    "WMT": ("walmart",),
    "UNH": ("unitedhealth",),
    "V": ("visa inc",),
    "PG": ("procter & gamble",),
    "HD": ("home depot",),
    "BAC": ("bank of america",),
    "KO": ("coca-cola", "coca cola"),
    "PFE": ("pfizer",),
    "CVX": ("chevron",),
    "INTC": ("intel",),
    "AMD": ("advanced micro devices",),
    "NFLX": ("netflix",),
    "BA": ("boeing",),
    "DIS": ("walt disney", "disney"),
}


def _stable_id(title: str, published: pd.Timestamp) -> str:
    key = f"{title.strip().lower()}|{published.date()}"
    return hashlib.sha1(key.encode("utf-8")).hexdigest()[:16]


def _text(node: Optional[ET.Element]) -> str:
    if node is None:
        return ""
    return "".join(node.itertext()).strip()


def _strip_html(value: str) -> str:
    return re.sub(r"\s+", " ", re.sub(r"<[^>]+>", " ", value)).strip()


class NewsData:
    """Fetches headlines from RSS feeds and tags them with universe symbols."""

    def __init__(self, cfg: Config, cache: Cache, timeout: float = 20.0) -> None:
        self.cfg = cfg
        self.cache = cache
        self.timeout = timeout
        self.aliases: Dict[str, Sequence[str]] = dict(_DEFAULT_ALIASES)

    # ------------------------------------------------------------------ live

    def recent(self, feeds: Optional[Iterable[str]] = None, ttl_hours: float = 0.5) -> pd.DataFrame:
        """Headlines from the configured feeds.

        Columns: ``published, title, summary, source, url, id``. Sorted oldest
        first, deduplicated on (title, publication date).
        """
        feeds = list(feeds if feeds is not None else self.cfg.data.news_feeds)
        frames: List[pd.DataFrame] = []
        for url in feeds:
            frame = self.cache.frame(
                "news",
                f"feed-{url}",
                lambda u=url: self._fetch_feed(u),
                ttl_hours=ttl_hours,
                index_col=0,
                parse_dates=True,
            )
            if frame is not None and not frame.empty:
                frames.append(frame)

        if not frames:
            log.warning("no news feed returned anything — sentiment will be neutral")
            return pd.DataFrame(columns=["published", "title", "summary", "source", "url", "id"])

        news = pd.concat(frames, ignore_index=True)
        news["published"] = pd.to_datetime(news["published"], errors="coerce", utc=True)
        news = news.dropna(subset=["published"])
        news["published"] = news["published"].dt.tz_convert(None)
        news["id"] = [
            _stable_id(t, p) for t, p in zip(news["title"].astype(str), news["published"])
        ]
        news = news.drop_duplicates(subset=["id"]).sort_values("published").reset_index(drop=True)

        cutoff = pd.Timestamp.utcnow().tz_localize(None) - pd.Timedelta(
            days=self.cfg.data.news_lookback_days
        )
        return news[news["published"] >= cutoff].reset_index(drop=True)

    def _fetch_feed(self, url: str) -> Optional[pd.DataFrame]:
        try:
            resp = requests.get(url, headers=_HEADERS, timeout=self.timeout)
        except requests.RequestException as exc:
            log.info("news feed unreachable (%s): %s", url, exc)
            return None
        if resp.status_code >= 400:
            log.info("news feed HTTP %s: %s", resp.status_code, url)
            return None

        try:
            root = ET.fromstring(resp.content)
        except ET.ParseError as exc:
            log.info("news feed is not valid XML (%s): %s", url, exc)
            return None

        source = _text(root.find(".//channel/title")) or _text(root.find(".//{*}title")) or url
        rows: List[dict] = []

        # RSS 2.0
        for item in root.findall(".//item"):
            published = _parse_date(_text(item.find("pubDate")) or _text(item.find("{*}date")))
            if published is None:
                continue
            rows.append(
                {
                    "published": published,
                    "title": _strip_html(_text(item.find("title"))),
                    "summary": _strip_html(_text(item.find("description")))[:600],
                    "source": source,
                    "url": _text(item.find("link")),
                }
            )
        # Atom
        for entry in root.findall(".//{http://www.w3.org/2005/Atom}entry"):
            ns = "{http://www.w3.org/2005/Atom}"
            published = _parse_date(
                _text(entry.find(f"{ns}published")) or _text(entry.find(f"{ns}updated"))
            )
            if published is None:
                continue
            link = entry.find(f"{ns}link")
            rows.append(
                {
                    "published": published,
                    "title": _strip_html(_text(entry.find(f"{ns}title"))),
                    "summary": _strip_html(_text(entry.find(f"{ns}summary")))[:600],
                    "source": source,
                    "url": link.get("href", "") if link is not None else "",
                }
            )

        rows = [r for r in rows if r["title"]]
        if not rows:
            return None
        log.debug("fetched %d headlines from %s", len(rows), source)
        return pd.DataFrame(rows)

    # --------------------------------------------------------------- archive

    def load_archive(self, path: str | Path) -> pd.DataFrame:
        """Load a historical news archive for backtesting.

        Expected columns: ``date`` (or ``published``), ``title`` (or ``headline``),
        optionally ``symbols`` (comma separated), ``summary``, ``source``.
        """
        p = Path(path)
        if not p.is_file():
            raise FileNotFoundError(f"news archive not found: {p}")
        raw = pd.read_csv(p)
        cols = {str(c).strip().lower(): c for c in raw.columns}

        date_col = cols.get("published") or cols.get("date") or cols.get("timestamp")
        title_col = cols.get("title") or cols.get("headline")
        if date_col is None or title_col is None:
            raise ValueError(
                f"{p} needs a date column (date/published/timestamp) and a title column "
                f"(title/headline); found {list(raw.columns)}"
            )

        out = pd.DataFrame(
            {
                "published": pd.to_datetime(raw[date_col], errors="coerce"),
                "title": raw[title_col].astype(str).map(_strip_html),
                "summary": raw[cols["summary"]].astype(str) if "summary" in cols else "",
                "source": raw[cols["source"]].astype(str) if "source" in cols else "archive",
            }
        ).dropna(subset=["published"])

        if "symbols" in cols:
            out["symbols"] = raw[cols["symbols"]].fillna("").astype(str)
        out["id"] = [_stable_id(t, p_) for t, p_ in zip(out["title"], out["published"])]
        out = out.drop_duplicates(subset=["id"]).sort_values("published").reset_index(drop=True)
        log.info("loaded %d archived headlines from %s", len(out), p.name)
        return out

    # ---------------------------------------------------------------- tagging

    def tag_symbols(self, news: pd.DataFrame, symbols: Sequence[str]) -> pd.DataFrame:
        """Add a ``symbols`` column listing which universe tickers each item mentions.

        Ticker matching is word-boundary and case-sensitive for the symbol itself
        (so "V" does not match every sentence containing a capital V mid-word),
        plus case-insensitive matching on known company aliases.
        """
        if news.empty:
            return news.assign(symbols="")
        if "symbols" in news.columns and news["symbols"].astype(str).str.len().gt(0).any():
            return news  # the archive already carries its own tagging

        haystacks = (news["title"].fillna("") + " " + news.get("summary", "").fillna("")).tolist()
        patterns = {}
        for sym in symbols:
            alts = [re.escape(sym)]
            patterns[sym] = (
                re.compile(rf"(?<![A-Za-z0-9.]){'|'.join(alts)}(?![A-Za-z0-9])"),
                [re.compile(rf"\b{re.escape(a)}\b", re.IGNORECASE) for a in self.aliases.get(sym, ())],
            )

        tagged: List[str] = []
        for text in haystacks:
            lower = text.lower()
            hits = [
                sym
                for sym, (ticker_re, alias_res) in patterns.items()
                if ticker_re.search(text) or any(a.search(lower) for a in alias_res)
            ]
            tagged.append(",".join(hits))
        return news.assign(symbols=tagged)


def _parse_date(value: str) -> Optional[pd.Timestamp]:
    """Parse the several date formats RSS feeds use in practice."""
    if not value:
        return None
    try:
        from email.utils import parsedate_to_datetime

        parsed = parsedate_to_datetime(value)
        if parsed.tzinfo is None:
            parsed = parsed.replace(tzinfo=dt.timezone.utc)
        return pd.Timestamp(parsed)
    except (TypeError, ValueError, IndexError):
        pass
    try:
        ts = pd.Timestamp(value)
        return ts.tz_localize("UTC") if ts.tzinfo is None else ts
    except (ValueError, TypeError):
        return None
