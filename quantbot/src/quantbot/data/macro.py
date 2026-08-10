"""Macroeconomic series from FRED.

The ``fredgraph.csv`` endpoint needs no API key, which keeps the bot runnable out
of the box. It only serves plain CSV for a *single* series — asking for several
at once returns a ZIP — so series are fetched and cached one by one.

**Publication lag is applied on purpose.** FRED indexes an observation by the
period it describes, not by the day it was published: the July unemployment rate
carries an index date in July but only exists in the world in early August. A
backtest that reads it on the observation date is trading on information nobody
had, and it will look brilliant. Every series therefore gets shifted forward by a
realistic release lag before anything downstream sees it.
"""

from __future__ import annotations

import io
from typing import Dict, Optional

import pandas as pd
import requests

from ..config import Config
from ..utils.cache import Cache
from ..utils.logging import get_logger

log = get_logger(__name__)

_URL = "https://fred.stlouisfed.org/graph/fredgraph.csv"

#: Calendar days between the period an observation describes and its publication.
#: Daily market rates are effectively same-day; surveys and national accounts are not.
_RELEASE_LAG_DAYS: Dict[str, int] = {
    # Daily market data — published the same evening.
    "DGS10": 1, "DGS2": 1, "DGS3MO": 1, "DGS30": 1, "T10Y2Y": 1, "T10Y3M": 1,
    "VIXCLS": 1, "BAMLH0A0HYM2": 1, "BAMLC0A0CM": 1, "DTWEXBGS": 2,
    "DCOILWTICO": 4, "DEXUSEU": 1, "T10YIE": 1, "T5YIFR": 1, "SOFR": 1,
    "NFCI": 7, "STLFSI4": 7,
    # Weekly.
    "ICSA": 5, "WALCL": 4,
    # Monthly surveys and prices.
    "UNRATE": 21, "PAYEMS": 21, "CPIAUCSL": 30, "CPILFESL": 30, "PCEPILFE": 32,
    "INDPRO": 32, "RSAFS": 32, "HOUST": 30, "UMCSENT": 14, "PERMIT": 30,
    "M2SL": 45, "TOTALSA": 20,
    # Quarterly national accounts.
    "GDPC1": 30, "GDP": 30, "CP": 90,
}
_DEFAULT_LAG_DAYS = 30


def release_lag_days(series_id: str) -> int:
    """Publication lag for a FRED series, in calendar days."""
    return _RELEASE_LAG_DAYS.get(series_id.upper(), _DEFAULT_LAG_DAYS)


class MacroData:
    """Fetches, lags and aligns FRED series onto a trading calendar."""

    def __init__(self, cfg: Config, cache: Cache, timeout: float = 30.0) -> None:
        self.cfg = cfg
        self.cache = cache
        self.timeout = timeout

    # ------------------------------------------------------------------ fetch

    def _fetch_series(self, series_id: str, start: str) -> Optional[pd.DataFrame]:
        def loader() -> Optional[pd.DataFrame]:
            params = {"id": series_id, "cosd": pd.Timestamp(start).strftime("%Y-%m-%d")}
            try:
                resp = requests.get(_URL, params=params, timeout=self.timeout)
            except requests.RequestException as exc:
                log.warning("FRED transport error for %s: %s", series_id, exc)
                return None
            if resp.status_code >= 400:
                log.warning("FRED HTTP %s for %s", resp.status_code, series_id)
                return None
            text = resp.text
            if text[:2] == "PK":  # a ZIP: we asked for something the CSV path can't serve
                log.warning("FRED returned an archive for %s — skipping", series_id)
                return None
            try:
                frame = pd.read_csv(io.StringIO(text))
            except Exception as exc:
                log.warning("FRED CSV unparseable for %s: %s", series_id, exc)
                return None
            date_col = frame.columns[0]
            frame[date_col] = pd.to_datetime(frame[date_col], errors="coerce")
            frame = frame.dropna(subset=[date_col]).set_index(date_col)
            # FRED writes missing observations as "."; to_numeric turns those into NaN.
            value_col = frame.columns[0]
            frame[value_col] = pd.to_numeric(frame[value_col], errors="coerce")
            frame = frame.dropna()
            return frame if not frame.empty else None

        return self.cache.frame("macro", f"fred-{series_id}-{start}", loader)

    # ------------------------------------------------------------------ public

    def load(
        self,
        series: Optional[Dict[str, str]] = None,
        start: Optional[str] = None,
        apply_release_lag: bool = True,
    ) -> pd.DataFrame:
        """Return a daily DataFrame of macro series, forward-filled and lagged.

        Columns are the friendly names from ``data.macro_series``. The result is
        safe to join onto a price panel by date: on any given day it only contains
        values that had actually been published by then.
        """
        series = series or self.cfg.data.macro_series
        start = start or self.cfg.data.start
        if not series:
            return pd.DataFrame()

        columns: Dict[str, pd.Series] = {}
        for series_id, friendly in series.items():
            frame = self._fetch_series(series_id, start)
            if frame is None or frame.empty:
                log.info("macro series %s unavailable — continuing without it", series_id)
                continue
            values = frame.iloc[:, 0]
            values.index = pd.to_datetime(values.index).normalize()
            if apply_release_lag:
                values.index = values.index + pd.Timedelta(days=release_lag_days(series_id))
            columns[friendly] = values[~values.index.duplicated(keep="last")].sort_index()

        if not columns:
            log.warning("no macro series could be loaded — the regime overlay will stay neutral")
            return pd.DataFrame()

        panel = pd.DataFrame(columns)
        # Daily grid, then forward fill: on a day with no fresh print, the latest
        # published value is exactly what a trader would have been looking at.
        grid = pd.date_range(panel.index.min(), panel.index.max(), freq="D")
        panel = panel.reindex(grid).ffill()
        panel.index.name = "date"

        derived = self._derive(panel)
        return pd.concat([panel, derived], axis=1) if not derived.empty else panel

    @staticmethod
    def _derive(panel: pd.DataFrame) -> pd.DataFrame:
        """Cheap combinations that are more informative than their parts."""
        out: Dict[str, pd.Series] = {}
        if {"yield_10y", "yield_2y"} <= set(panel.columns) and "curve_10y2y" not in panel:
            out["curve_10y2y"] = panel["yield_10y"] - panel["yield_2y"]
        if "vix" in panel:
            # Whether fear is rising matters more than its absolute level.
            out["vix_chg_21d"] = panel["vix"].diff(21)
        if "hy_oas" in panel:
            out["hy_oas_chg_21d"] = panel["hy_oas"].diff(21)
        if "unemployment" in panel:
            # The Sahm-rule shape: unemployment rising off its own 12m low.
            out["unemp_vs_min_12m"] = panel["unemployment"] - panel["unemployment"].rolling(
                365, min_periods=60
            ).min()
        return pd.DataFrame(out, index=panel.index) if out else pd.DataFrame()
