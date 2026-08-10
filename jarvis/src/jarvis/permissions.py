"""Freigabeliste — entscheidet, was Jarvis ohne Rückfrage tun darf.

Jedes Werkzeug trägt eine Risikoklasse:

* ``read``     — liest nur (Postfach lesen, Ordner auflisten, Kurse abrufen)
* ``write``    — verändert lokal etwas (Datei verschieben, Notiz speichern)
* ``external`` — wirkt nach außen und ist schwer rückgängig zu machen
                 (E-Mail versenden)

Die Konfiguration setzt pro Klasse einen Standard und kann ihn pro Werkzeug
überschreiben. Antwortet man im Chat mit "immer"/"nie", landet das als
Override in ``permissions.local.json`` — die handgepflegte ``jarvis.toml``
bleibt unberührt.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Literal

Decision = Literal["allow", "ask", "deny"]
RiskLevel = Literal["read", "write", "external"]

RISK_LABELS: dict[str, str] = {
    "read": "lesend",
    "write": "verändernd",
    "external": "nach außen wirkend",
}


@dataclass
class Verdict:
    allowed: bool
    reason: str


# Rückfrage-Callback: (tool_name, risk, summary) -> "yes" | "no" | "always" | "never"
AskFn = Callable[[str, str, str], str]


class Permissions:
    def __init__(
        self,
        *,
        default_read: Decision = "allow",
        default_write: Decision = "ask",
        default_external: Decision = "ask",
        tools: dict[str, str] | None = None,
        overrides_path: Path | None = None,
        ask_fn: AskFn | None = None,
    ) -> None:
        self._defaults: dict[str, str] = {
            "read": default_read,
            "write": default_write,
            "external": default_external,
        }
        self._tools = dict(tools or {})
        self._overrides_path = overrides_path
        self._overrides = _read_overrides(overrides_path)
        self._ask_fn = ask_fn

    # -- Abfrage ----------------------------------------------------------- #

    def policy_for(self, tool_name: str, risk: str) -> Decision:
        """Effektive Regel: Override > Konfiguration > Risiko-Standard."""
        for source in (self._overrides, self._tools):
            if tool_name in source:
                return source[tool_name]  # type: ignore[return-value]
        return self._defaults.get(risk, "ask")  # type: ignore[return-value]

    def check(self, tool_name: str, risk: str, summary: str) -> Verdict:
        policy = self.policy_for(tool_name, risk)

        if policy == "allow":
            return Verdict(True, "durch Freigabeliste erlaubt")
        if policy == "deny":
            return Verdict(
                False,
                f"'{tool_name}' ist in der Freigabeliste gesperrt. "
                "Ändere permissions in jarvis.toml, wenn das gewollt ist.",
            )

        # policy == "ask"
        if self._ask_fn is None:
            return Verdict(
                False,
                f"'{tool_name}' braucht eine Bestätigung, aber Jarvis läuft "
                "gerade unbeaufsichtigt (Routine/Skript). Aktion nicht "
                "ausgeführt — bitte im interaktiven Chat wiederholen oder das "
                "Werkzeug in der Freigabeliste auf 'allow' setzen.",
            )

        answer = self._ask_fn(tool_name, risk, summary)
        if answer == "always":
            self._persist(tool_name, "allow")
            return Verdict(True, "vom Nutzer dauerhaft erlaubt")
        if answer == "never":
            self._persist(tool_name, "deny")
            return Verdict(False, "vom Nutzer dauerhaft gesperrt")
        if answer == "yes":
            return Verdict(True, "vom Nutzer einmalig bestätigt")
        return Verdict(False, "vom Nutzer abgelehnt")

    # -- Overrides --------------------------------------------------------- #

    def _persist(self, tool_name: str, decision: Decision) -> None:
        self._overrides[tool_name] = decision
        if self._overrides_path is None:
            return
        self._overrides_path.parent.mkdir(parents=True, exist_ok=True)
        self._overrides_path.write_text(
            json.dumps(self._overrides, indent=2, ensure_ascii=False) + "\n",
            encoding="utf-8",
        )

    @property
    def overrides(self) -> dict[str, str]:
        return dict(self._overrides)


def _read_overrides(path: Path | None) -> dict[str, str]:
    if path is None or not path.is_file():
        return {}
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}
    if not isinstance(raw, dict):
        return {}
    return {
        str(k): str(v) for k, v in raw.items() if v in ("allow", "ask", "deny")
    }
