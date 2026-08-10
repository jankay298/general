"""Local CSV files — the escape hatch for paid or proprietary data.

Drop ``<SYMBOL>.csv`` into the configured directory with a date column and OHLCV
columns in any capitalisation, and the rest of the bot treats it exactly like a
live feed. This is also how you make a backtest reproducible: pin the inputs.
"""

from __future__ import annotations

from pathlib import Path
from typing import Optional

import pandas as pd

from ...utils.logging import get_logger
from ..base import PriceProvider, normalise_frame

log = get_logger(__name__)

_DATE_COLUMNS = ("date", "timestamp", "time", "datetime", "dt")


class CsvProvider(PriceProvider):
    name = "csv"
    adjusted = True  # we have to trust whoever produced the file

    def __init__(self, directory: str | Path) -> None:
        self.directory = Path(directory)

    def available(self) -> bool:
        return self.directory.is_dir()

    def fetch(self, symbol: str, start: str, end: Optional[str]) -> Optional[pd.DataFrame]:
        path = self._locate(symbol)
        if path is None:
            return None
        try:
            raw = pd.read_csv(path)
        except Exception as exc:
            log.warning("could not read %s: %s", path, exc)
            return None

        date_col = next(
            (c for c in raw.columns if str(c).strip().lower() in _DATE_COLUMNS), None
        )
        if date_col is None:
            log.warning("%s has no recognisable date column (%s)", path, list(raw.columns))
            return None
        raw = raw.set_index(date_col)

        frame = normalise_frame(raw, symbol=symbol)
        if frame is None:
            return None
        frame = frame.loc[frame.index >= pd.Timestamp(start)]
        if end:
            frame = frame.loc[frame.index <= pd.Timestamp(end)]
        return frame if not frame.empty else None

    def _locate(self, symbol: str) -> Optional[Path]:
        if not self.directory.is_dir():
            return None
        for candidate in (symbol, symbol.upper(), symbol.lower(), symbol.replace("^", "")):
            path = self.directory / f"{candidate}.csv"
            if path.is_file():
                return path
        return None
