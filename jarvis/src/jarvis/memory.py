"""Gedächtnis und Protokoll — eine SQLite-Datei, kein Server.

Drei Tabellen:

* ``facts``   — dauerhafte Notizen, die Jarvis über dich und deine Arbeit lernt
* ``turns``   — Gesprächsverlauf pro Sitzung (damit ``jarvis chat`` fortsetzbar ist)
* ``journal`` — Prüfprotokoll: welches Werkzeug wurde wann mit welcher
                Entscheidung aufgerufen. Das ist die Grundlage dafür, einem
                Agenten überhaupt vertrauen zu können.
"""

from __future__ import annotations

import json
import sqlite3
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable

SCHEMA = """
CREATE TABLE IF NOT EXISTS facts (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    text       TEXT NOT NULL,
    tags       TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS turns (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    session    TEXT NOT NULL,
    role       TEXT NOT NULL,
    content    TEXT NOT NULL,
    created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS turns_session_idx ON turns(session, id);
CREATE TABLE IF NOT EXISTS journal (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    tool       TEXT NOT NULL,
    risk       TEXT NOT NULL,
    decision   TEXT NOT NULL,
    summary    TEXT NOT NULL,
    detail     TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL
);
"""


def _now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


@dataclass
class Fact:
    id: int
    text: str
    tags: str
    updated_at: str

    def render(self) -> str:
        suffix = f"  [{self.tags}]" if self.tags else ""
        return f"#{self.id} {self.text}{suffix}"


@dataclass
class JournalEntry:
    id: int
    tool: str
    risk: str
    decision: str
    summary: str
    created_at: str


class Memory:
    def __init__(self, path: Path) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        self._conn = sqlite3.connect(path)
        self._conn.row_factory = sqlite3.Row
        self._conn.executescript(SCHEMA)
        self._conn.commit()

    def close(self) -> None:
        self._conn.close()

    def __enter__(self) -> "Memory":
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()

    # -- Fakten ------------------------------------------------------------ #

    def remember(self, text: str, tags: str = "") -> Fact:
        text = text.strip()
        if not text:
            raise ValueError("Leere Notiz kann nicht gespeichert werden.")
        now = _now()
        existing = self._conn.execute(
            "SELECT id FROM facts WHERE lower(text) = lower(?)", (text,)
        ).fetchone()
        if existing:
            self._conn.execute(
                "UPDATE facts SET tags = ?, updated_at = ? WHERE id = ?",
                (tags, now, existing["id"]),
            )
            self._conn.commit()
            return Fact(existing["id"], text, tags, now)
        cur = self._conn.execute(
            "INSERT INTO facts (text, tags, created_at, updated_at) VALUES (?,?,?,?)",
            (text, tags, now, now),
        )
        self._conn.commit()
        return Fact(int(cur.lastrowid or 0), text, tags, now)

    def recall(self, query: str = "", limit: int = 20) -> list[Fact]:
        if query.strip():
            pattern = f"%{query.strip()}%"
            rows = self._conn.execute(
                "SELECT id, text, tags, updated_at FROM facts "
                "WHERE text LIKE ? OR tags LIKE ? "
                "ORDER BY updated_at DESC LIMIT ?",
                (pattern, pattern, limit),
            ).fetchall()
        else:
            rows = self._conn.execute(
                "SELECT id, text, tags, updated_at FROM facts "
                "ORDER BY updated_at DESC LIMIT ?",
                (limit,),
            ).fetchall()
        return [Fact(r["id"], r["text"], r["tags"], r["updated_at"]) for r in rows]

    def forget(self, fact_id: int) -> bool:
        cur = self._conn.execute("DELETE FROM facts WHERE id = ?", (fact_id,))
        self._conn.commit()
        return cur.rowcount > 0

    # -- Gesprächsverlauf --------------------------------------------------- #

    def append_turn(self, session: str, role: str, content: Any) -> None:
        self._conn.execute(
            "INSERT INTO turns (session, role, content, created_at) VALUES (?,?,?,?)",
            (session, role, json.dumps(content, ensure_ascii=False, default=str), _now()),
        )
        self._conn.commit()

    def load_turns(self, session: str, limit: int = 200) -> list[dict[str, Any]]:
        rows = self._conn.execute(
            "SELECT role, content FROM turns WHERE session = ? "
            "ORDER BY id DESC LIMIT ?",
            (session, limit),
        ).fetchall()
        out: list[dict[str, Any]] = []
        for row in reversed(rows):
            try:
                out.append({"role": row["role"], "content": json.loads(row["content"])})
            except json.JSONDecodeError:
                continue
        return out

    def clear_session(self, session: str) -> int:
        cur = self._conn.execute("DELETE FROM turns WHERE session = ?", (session,))
        self._conn.commit()
        return cur.rowcount

    def sessions(self) -> list[tuple[str, int, str]]:
        rows = self._conn.execute(
            "SELECT session, COUNT(*) AS n, MAX(created_at) AS last "
            "FROM turns GROUP BY session ORDER BY last DESC"
        ).fetchall()
        return [(r["session"], r["n"], r["last"]) for r in rows]

    # -- Prüfprotokoll ------------------------------------------------------ #

    def log_action(
        self,
        tool: str,
        risk: str,
        decision: str,
        summary: str,
        detail: str = "",
    ) -> None:
        self._conn.execute(
            "INSERT INTO journal (tool, risk, decision, summary, detail, created_at) "
            "VALUES (?,?,?,?,?,?)",
            (tool, risk, decision, summary, detail[:4000], _now()),
        )
        self._conn.commit()

    def journal(self, limit: int = 50) -> list[JournalEntry]:
        rows = self._conn.execute(
            "SELECT id, tool, risk, decision, summary, created_at FROM journal "
            "ORDER BY id DESC LIMIT ?",
            (limit,),
        ).fetchall()
        return [
            JournalEntry(
                r["id"], r["tool"], r["risk"], r["decision"], r["summary"], r["created_at"]
            )
            for r in rows
        ]


def render_facts(facts: Iterable[Fact]) -> str:
    lines = [f.render() for f in facts]
    return "\n".join(lines)
