"""Terminalausgabe — Farben, Streaming, Rückfragen.

Kein externes Paket. Farben werden abgeschaltet, wenn die Ausgabe in eine
Datei oder Pipe geht, oder wenn NO_COLOR gesetzt ist.
"""

from __future__ import annotations

import os
import sys
from typing import TextIO

RESET = "\033[0m"
BOLD = "\033[1m"
DIM = "\033[2m"
CYAN = "\033[36m"
GREEN = "\033[32m"
YELLOW = "\033[33m"
RED = "\033[31m"
BLUE = "\033[34m"


class Console:
    def __init__(self, stream: TextIO | None = None, color: bool | None = None) -> None:
        self.stream = stream or sys.stdout
        if color is None:
            color = (
                self.stream.isatty()
                and not os.environ.get("NO_COLOR")
                and os.environ.get("TERM") != "dumb"
            )
        self.color = color
        self._at_line_start = True

    # -- Grundlagen -------------------------------------------------------- #

    def _paint(self, text: str, *codes: str) -> str:
        if not self.color or not codes:
            return text
        return "".join(codes) + text + RESET

    def write(self, text: str) -> None:
        if not text:
            return
        self.stream.write(text)
        self.stream.flush()
        self._at_line_start = text.endswith("\n")

    def line(self, text: str = "", *codes: str) -> None:
        self.newline_if_needed()
        self.write(self._paint(text, *codes) + "\n")

    def newline_if_needed(self) -> None:
        if not self._at_line_start:
            self.write("\n")

    # -- Semantische Ausgaben ---------------------------------------------- #

    def heading(self, text: str) -> None:
        self.line(text, BOLD)

    def info(self, text: str) -> None:
        self.line(text, DIM)

    def notice(self, text: str) -> None:
        self.line(f"! {text}", YELLOW)

    def success(self, text: str) -> None:
        self.line(f"✓ {text}", GREEN)

    def error(self, text: str) -> None:
        self.line(f"✗ {text}", RED)

    def agent_prefix(self, name: str) -> None:
        self.newline_if_needed()
        self.write(self._paint(f"{name} ", BOLD, CYAN))

    def tool_start(self, summary: str) -> None:
        self.newline_if_needed()
        self.line(f"  ⚙ {summary}", DIM, BLUE)

    def tool_end(self, name: str, output: str, is_error: bool) -> None:
        first = (output.splitlines() or [""])[0]
        if len(first) > 100:
            first = first[:97] + "..."
        if is_error:
            self.line(f"  ↳ {first}", RED)
        else:
            self.line(f"  ↳ {first}", DIM)

    def thinking(self, text: str) -> None:
        self.write(self._paint(text, DIM))

    # -- Eingabe ------------------------------------------------------------ #

    def ask_permission(self, tool_name: str, risk: str, summary: str) -> str:
        """Rückfrage vor einer Aktion. Gibt yes/no/always/never zurück."""
        from .permissions import RISK_LABELS

        self.newline_if_needed()
        label = RISK_LABELS.get(risk, risk)
        self.line("")
        self.line(f"  Freigabe nötig — {tool_name} ({label})", BOLD, YELLOW)
        self.line(f"  {summary}")
        self.line("  [j] einmal   [n] nein   [i] immer   [x] nie mehr", DIM)
        while True:
            try:
                answer = input("  > ").strip().lower()
            except (EOFError, KeyboardInterrupt):
                self.line("")
                return "no"
            if answer in ("j", "ja", "y", "yes", ""):
                return "yes"
            if answer in ("n", "nein", "no"):
                return "no"
            if answer in ("i", "immer", "a", "always"):
                return "always"
            if answer in ("x", "nie", "never"):
                return "never"
            self.line("  Bitte j, n, i oder x.", DIM)

    def prompt(self, label: str) -> str:
        self.newline_if_needed()
        return input(self._paint(label, BOLD, GREEN))
