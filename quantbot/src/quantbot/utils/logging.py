"""Logging setup.

Two sinks: a readable console line for the human, and an optional JSON-lines file
so a live run leaves an auditable trail of every decision it made.
"""

from __future__ import annotations

import json
import logging
import sys
from pathlib import Path
from typing import Optional

_CONFIGURED = False


class _JsonLines(logging.Formatter):
    def format(self, record: logging.LogRecord) -> str:
        payload = {
            "ts": self.formatTime(record, "%Y-%m-%dT%H:%M:%S%z"),
            "level": record.levelname,
            "logger": record.name,
            "msg": record.getMessage(),
        }
        if record.exc_info:
            payload["exc"] = self.formatException(record.exc_info)
        # Anything attached via logger.info(..., extra={"trade": {...}}) rides along.
        for key, value in getattr(record, "__dict__", {}).items():
            if key.startswith("qb_"):
                payload[key[3:]] = value
        return json.dumps(payload, default=str)


def setup_logging(level: str = "INFO", logfile: Optional[str | Path] = None) -> None:
    """Configure root logging once; repeat calls only adjust the level."""
    global _CONFIGURED
    root = logging.getLogger()
    root.setLevel(getattr(logging, str(level).upper(), logging.INFO))
    if _CONFIGURED:
        return

    console = logging.StreamHandler(sys.stderr)
    console.setFormatter(logging.Formatter("%(asctime)s %(levelname)-7s %(name)-28s %(message)s",
                                           datefmt="%H:%M:%S"))
    root.addHandler(console)

    if logfile is not None:
        path = Path(logfile)
        path.parent.mkdir(parents=True, exist_ok=True)
        fh = logging.FileHandler(path, encoding="utf-8")
        fh.setFormatter(_JsonLines())
        root.addHandler(fh)

    # These are chatty and never say anything we need.
    for noisy in ("urllib3", "requests", "matplotlib", "peewee", "yfinance"):
        logging.getLogger(noisy).setLevel(logging.WARNING)

    _CONFIGURED = True


def get_logger(name: str) -> logging.Logger:
    return logging.getLogger(name)
