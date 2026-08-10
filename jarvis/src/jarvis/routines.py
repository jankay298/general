"""Routinen — was Jarvis von selbst tut.

Ein Zeitplan ist absichtlich in Klartext geschrieben, nicht als Cron-Ausdruck:

    schedule = "daily 07:30"        jeden Tag um 07:30
    schedule = "weekdays 07:30"     Montag bis Freitag um 07:30
    schedule = "weekly Mon 18:00"   jeden Montag um 18:00
    schedule = "every 30m"          alle 30 Minuten
    schedule = "every 4h"           alle 4 Stunden

Ausführung: ``jarvis routines run`` prüft, was fällig ist. Für echten
Dauerbetrieb ruft man das aus cron / systemd / launchd auf — Beispiele stehen
in der README. Es gibt bewusst keinen eigenen Hintergrunddienst: das
Betriebssystem kann das besser und überlebt einen Neustart.
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from datetime import datetime, timedelta
from pathlib import Path

from .config import Routine

_WEEKDAYS = {
    "mon": 0, "mo": 0, "montag": 0,
    "tue": 1, "di": 1, "dienstag": 1,
    "wed": 2, "mi": 2, "mittwoch": 2,
    "thu": 3, "do": 3, "donnerstag": 3,
    "fri": 4, "fr": 4, "freitag": 4,
    "sat": 5, "sa": 5, "samstag": 5,
    "sun": 6, "so": 6, "sonntag": 6,
}

_TIME_RE = re.compile(r"^(\d{1,2}):(\d{2})$")
_EVERY_RE = re.compile(r"^every\s+(\d+)\s*([mh])$", re.IGNORECASE)


class ScheduleError(ValueError):
    """Der Zeitplan ist nicht lesbar."""


@dataclass
class Schedule:
    kind: str  # "interval" | "time"
    interval: timedelta | None = None
    at: tuple[int, int] | None = None
    weekdays: set[int] | None = None  # None = jeden Tag

    def describe(self) -> str:
        if self.kind == "interval" and self.interval:
            minutes = int(self.interval.total_seconds() // 60)
            if minutes % 60 == 0:
                return f"alle {minutes // 60} Stunden"
            return f"alle {minutes} Minuten"
        assert self.at is not None
        clock = f"{self.at[0]:02d}:{self.at[1]:02d}"
        if self.weekdays is None:
            return f"täglich um {clock}"
        if self.weekdays == {0, 1, 2, 3, 4}:
            return f"werktags um {clock}"
        names = ["Mo", "Di", "Mi", "Do", "Fr", "Sa", "So"]
        chosen = ", ".join(names[d] for d in sorted(self.weekdays))
        return f"{chosen} um {clock}"


def parse_schedule(text: str) -> Schedule:
    raw = text.strip()
    if match := _EVERY_RE.match(raw):
        amount, unit = int(match.group(1)), match.group(2).lower()
        if amount <= 0:
            raise ScheduleError(f"'{text}': das Intervall muss größer als 0 sein.")
        delta = timedelta(minutes=amount) if unit == "m" else timedelta(hours=amount)
        return Schedule(kind="interval", interval=delta)

    parts = raw.split()
    if not parts:
        raise ScheduleError("Leerer Zeitplan.")

    head = parts[0].lower()
    if head == "daily" and len(parts) == 2:
        return Schedule(kind="time", at=_parse_time(parts[1], text))
    if head in ("weekdays", "werktags") and len(parts) == 2:
        return Schedule(kind="time", at=_parse_time(parts[1], text), weekdays={0, 1, 2, 3, 4})
    if head == "weekly" and len(parts) == 3:
        day = _WEEKDAYS.get(parts[1].lower())
        if day is None:
            raise ScheduleError(f"'{parts[1]}' ist kein Wochentag.")
        return Schedule(kind="time", at=_parse_time(parts[2], text), weekdays={day})

    raise ScheduleError(
        f"Zeitplan '{text}' nicht verstanden. Erlaubt: 'daily HH:MM', "
        "'weekdays HH:MM', 'weekly Mon HH:MM', 'every 30m', 'every 4h'."
    )


def _parse_time(value: str, original: str) -> tuple[int, int]:
    match = _TIME_RE.match(value)
    if not match:
        raise ScheduleError(f"'{original}': '{value}' ist keine Uhrzeit im Format HH:MM.")
    hour, minute = int(match.group(1)), int(match.group(2))
    if not (0 <= hour < 24 and 0 <= minute < 60):
        raise ScheduleError(f"'{original}': {value} liegt außerhalb eines Tages.")
    return hour, minute


def is_due(schedule: Schedule, now: datetime, last_run: datetime | None) -> bool:
    if schedule.kind == "interval":
        assert schedule.interval is not None
        return last_run is None or (now - last_run) >= schedule.interval

    assert schedule.at is not None
    if schedule.weekdays is not None and now.weekday() not in schedule.weekdays:
        return False
    target = now.replace(
        hour=schedule.at[0], minute=schedule.at[1], second=0, microsecond=0
    )
    if now < target:
        return False
    # Fällig, solange der heutige Termin noch nicht gelaufen ist. Ein
    # verpasster Termin (Rechner war aus) wird beim nächsten Lauf nachgeholt,
    # aber höchstens einmal pro Tag.
    return last_run is None or last_run < target


class RoutineState:
    """Merkt sich, wann welche Routine zuletzt lief."""

    def __init__(self, path: Path) -> None:
        self._path = path
        self._data: dict[str, str] = {}
        if path.is_file():
            try:
                raw = json.loads(path.read_text(encoding="utf-8"))
                if isinstance(raw, dict):
                    self._data = {str(k): str(v) for k, v in raw.items()}
            except (OSError, json.JSONDecodeError):
                self._data = {}

    def last_run(self, name: str) -> datetime | None:
        value = self._data.get(name)
        if not value:
            return None
        try:
            return datetime.fromisoformat(value)
        except ValueError:
            return None

    def mark_run(self, name: str, when: datetime | None = None) -> None:
        self._data[name] = (when or datetime.now()).isoformat(timespec="seconds")
        self._path.parent.mkdir(parents=True, exist_ok=True)
        self._path.write_text(
            json.dumps(self._data, indent=2, ensure_ascii=False) + "\n",
            encoding="utf-8",
        )


@dataclass
class DueRoutine:
    routine: Routine
    schedule: Schedule
    last_run: datetime | None


def due_routines(
    routines: list[Routine], state: RoutineState, now: datetime | None = None
) -> list[DueRoutine]:
    now = now or datetime.now()
    out: list[DueRoutine] = []
    for routine in routines:
        if not routine.enabled:
            continue
        schedule = parse_schedule(routine.schedule)
        last = state.last_run(routine.name)
        if is_due(schedule, now, last):
            out.append(DueRoutine(routine, schedule, last))
    return out
