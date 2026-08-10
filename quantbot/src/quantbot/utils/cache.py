"""A small TTL cache on disk.

Free market-data APIs are rate limited hard — Alpha Vantage's free tier allows a
few calls a minute. Re-running a backtest must not burn that budget, so every
network fetch goes through here. DataFrames are stored as CSV (portable, no
pyarrow dependency), everything else as JSON.
"""

from __future__ import annotations

import hashlib
import json
import time
from pathlib import Path
from typing import Any, Callable, Optional, TypeVar

import pandas as pd

from .logging import get_logger

log = get_logger(__name__)

T = TypeVar("T")


class Cache:
    """Namespaced file cache. Keys are hashed, so any string is a legal key."""

    def __init__(self, root: str | Path, ttl_hours: float = 12.0, enabled: bool = True) -> None:
        self.root = Path(root)
        self.ttl_seconds = float(ttl_hours) * 3600.0
        self.enabled = enabled
        if self.enabled:
            self.root.mkdir(parents=True, exist_ok=True)

    # ------------------------------------------------------------------ paths

    def _path(self, namespace: str, key: str, suffix: str) -> Path:
        digest = hashlib.sha256(key.encode("utf-8")).hexdigest()[:24]
        # Keep a readable prefix so the cache dir can be eyeballed and pruned.
        safe = "".join(c if c.isalnum() or c in "-_." else "_" for c in key)[:48]
        return self.root / namespace / f"{safe}-{digest}{suffix}"

    def _fresh(self, path: Path, ttl_seconds: Optional[float]) -> bool:
        if not path.is_file():
            return False
        ttl = self.ttl_seconds if ttl_seconds is None else ttl_seconds
        if ttl <= 0:
            return False
        age = time.time() - path.stat().st_mtime
        return age < ttl

    # ----------------------------------------------------------- frame access

    def frame(
        self,
        namespace: str,
        key: str,
        loader: Callable[[], Optional[pd.DataFrame]],
        ttl_hours: Optional[float] = None,
        index_col: int | str | None = 0,
        parse_dates: bool = True,
    ) -> Optional[pd.DataFrame]:
        """Return a cached DataFrame or call *loader* and cache what it returns.

        A loader returning ``None`` is cached as a *negative* result for a tenth of
        the normal TTL, so a delisted or misspelled symbol does not re-hammer the
        API on every single run.
        """
        ttl = self.ttl_seconds if ttl_hours is None else ttl_hours * 3600.0
        path = self._path(namespace, key, ".csv")
        miss = self._path(namespace, key, ".miss")

        if self.enabled and self._fresh(path, ttl):
            try:
                df = pd.read_csv(path, index_col=index_col)
                if parse_dates and len(df.index):
                    df.index = pd.to_datetime(df.index, errors="coerce", utc=False)
                    df = df[df.index.notna()]
                log.debug("cache hit %s/%s (%d rows)", namespace, key, len(df))
                return df
            except Exception as exc:  # a corrupt cache file must never be fatal
                log.warning("discarding unreadable cache file %s: %s", path, exc)
                path.unlink(missing_ok=True)

        if self.enabled and self._fresh(miss, ttl / 10.0):
            log.debug("cache negative-hit %s/%s", namespace, key)
            return None

        result = loader()

        if self.enabled:
            path.parent.mkdir(parents=True, exist_ok=True)
            if result is None or result.empty:
                miss.write_text(str(time.time()), encoding="utf-8")
                path.unlink(missing_ok=True)
            else:
                tmp = path.with_suffix(".csv.tmp")
                result.to_csv(tmp)
                tmp.replace(path)  # atomic, so a killed process can't leave half a file
                miss.unlink(missing_ok=True)
        return result

    # ------------------------------------------------------------ json access

    def json(
        self,
        namespace: str,
        key: str,
        loader: Callable[[], Optional[Any]],
        ttl_hours: Optional[float] = None,
    ) -> Optional[Any]:
        ttl = self.ttl_seconds if ttl_hours is None else ttl_hours * 3600.0
        path = self._path(namespace, key, ".json")

        if self.enabled and self._fresh(path, ttl):
            try:
                return json.loads(path.read_text(encoding="utf-8"))
            except Exception as exc:
                log.warning("discarding unreadable cache file %s: %s", path, exc)
                path.unlink(missing_ok=True)

        result = loader()
        if self.enabled and result is not None:
            path.parent.mkdir(parents=True, exist_ok=True)
            tmp = path.with_suffix(".json.tmp")
            tmp.write_text(json.dumps(result, default=str), encoding="utf-8")
            tmp.replace(path)
        return result

    # -------------------------------------------------------------- housekeeping

    def clear(self, namespace: Optional[str] = None) -> int:
        """Delete cached files; returns how many were removed."""
        target = self.root / namespace if namespace else self.root
        if not target.exists():
            return 0
        removed = 0
        for p in target.rglob("*"):
            if p.is_file():
                p.unlink()
                removed += 1
        return removed
