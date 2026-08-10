from datetime import datetime, timedelta

import pytest

from jarvis.config import Routine
from jarvis.routines import (
    RoutineState,
    ScheduleError,
    due_routines,
    is_due,
    parse_schedule,
)


def test_parse_daily():
    schedule = parse_schedule("daily 07:30")
    assert schedule.kind == "time"
    assert schedule.at == (7, 30)
    assert schedule.weekdays is None
    assert schedule.describe() == "täglich um 07:30"


def test_parse_weekdays():
    schedule = parse_schedule("weekdays 09:00")
    assert schedule.weekdays == {0, 1, 2, 3, 4}
    assert "werktags" in schedule.describe()


def test_parse_weekly_accepts_german_and_english_days():
    assert parse_schedule("weekly Mon 18:00").weekdays == {0}
    assert parse_schedule("weekly Freitag 18:00").weekdays == {4}


def test_parse_interval():
    assert parse_schedule("every 30m").interval == timedelta(minutes=30)
    assert parse_schedule("every 4h").interval == timedelta(hours=4)
    assert parse_schedule("every 4h").describe() == "alle 4 Stunden"


@pytest.mark.parametrize(
    "text", ["", "irgendwann", "daily 25:00", "daily 7:5", "every 0m", "weekly Xyz 10:00"]
)
def test_bad_schedules_are_rejected_with_a_message(text):
    with pytest.raises(ScheduleError):
        parse_schedule(text)


# --------------------------------------------------------------------------- #
# Fälligkeit
# --------------------------------------------------------------------------- #


def test_daily_is_due_after_its_time_and_only_once():
    schedule = parse_schedule("daily 07:30")
    monday_0800 = datetime(2026, 8, 10, 8, 0)

    assert is_due(schedule, monday_0800, None) is True
    assert is_due(schedule, monday_0800, datetime(2026, 8, 10, 7, 35)) is False
    # Gestern gelaufen -> heute wieder fällig.
    assert is_due(schedule, monday_0800, datetime(2026, 8, 9, 7, 35)) is True


def test_daily_is_not_due_before_its_time():
    schedule = parse_schedule("daily 07:30")
    assert is_due(schedule, datetime(2026, 8, 10, 6, 0), None) is False


def test_missed_run_is_caught_up_the_same_day():
    """Rechner war um 07:30 aus — um 11:00 soll die Routine trotzdem laufen."""
    schedule = parse_schedule("daily 07:30")
    assert is_due(schedule, datetime(2026, 8, 10, 11, 0), datetime(2026, 8, 9, 7, 31)) is True


def test_weekdays_skips_the_weekend():
    schedule = parse_schedule("weekdays 07:30")
    saturday = datetime(2026, 8, 15, 9, 0)
    monday = datetime(2026, 8, 10, 9, 0)
    assert saturday.weekday() == 5
    assert is_due(schedule, saturday, None) is False
    assert is_due(schedule, monday, None) is True


def test_interval_respects_the_gap():
    schedule = parse_schedule("every 2h")
    now = datetime(2026, 8, 10, 12, 0)
    assert is_due(schedule, now, None) is True
    assert is_due(schedule, now, now - timedelta(hours=1)) is False
    assert is_due(schedule, now, now - timedelta(hours=3)) is True


# --------------------------------------------------------------------------- #
# Zustand
# --------------------------------------------------------------------------- #


def test_state_round_trips(tmp_path):
    path = tmp_path / "state.json"
    state = RoutineState(path)
    assert state.last_run("briefing") is None

    moment = datetime(2026, 8, 10, 7, 31)
    state.mark_run("briefing", moment)

    assert RoutineState(path).last_run("briefing") == moment


def test_disabled_routines_are_never_due(tmp_path):
    state = RoutineState(tmp_path / "state.json")
    routines = [
        Routine(name="an", prompt="p", schedule="every 1m", enabled=True),
        Routine(name="aus", prompt="p", schedule="every 1m", enabled=False),
    ]
    names = [d.routine.name for d in due_routines(routines, state)]
    assert names == ["an"]
