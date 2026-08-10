"""Werkzeug-Grundtypen und der gemeinsame Kontext."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import TYPE_CHECKING, Any, Callable

if TYPE_CHECKING:  # pragma: no cover - nur für Typprüfer
    from ..config import Config
    from ..memory import Memory


class ToolError(Exception):
    """Erwartbarer Fehler — wird als ``is_error`` an das Modell zurückgegeben.

    Das Modell soll den Text lesen und daraus lernen, statt dass der Prozess
    abstürzt. Deshalb sind die Meldungen in ganzen Sätzen formuliert.
    """


@dataclass
class ToolContext:
    """Alles, was ein Werkzeug zur Laufzeit braucht."""

    config: "Config"
    memory: "Memory"
    extras: dict[str, Any] = field(default_factory=dict)


Handler = Callable[[ToolContext, dict[str, Any]], str]
Summarizer = Callable[[dict[str, Any]], str]


@dataclass
class Tool:
    name: str
    description: str
    input_schema: dict[str, Any]
    risk: str  # read | write | external
    handler: Handler
    summarize: Summarizer | None = None

    def to_api(self) -> dict[str, Any]:
        return {
            "name": self.name,
            "description": self.description,
            "input_schema": self.input_schema,
        }

    def summary_for(self, arguments: dict[str, Any]) -> str:
        """Einzeiler für die Rückfrage und das Prüfprotokoll."""
        if self.summarize is not None:
            try:
                return self.summarize(arguments)
            except Exception:  # pragma: no cover - defensiv
                pass
        if not arguments:
            return self.name
        parts = []
        for key, value in list(arguments.items())[:4]:
            text = str(value)
            if len(text) > 60:
                text = text[:57] + "..."
            parts.append(f"{key}={text}")
        return f"{self.name}({', '.join(parts)})"


def obj(
    properties: dict[str, Any], required: list[str] | None = None
) -> dict[str, Any]:
    """Kurzform für ein JSON-Schema-Objekt."""
    return {
        "type": "object",
        "properties": properties,
        "required": required or [],
    }


def prop(type_: str, description: str, **extra: Any) -> dict[str, Any]:
    return {"type": type_, "description": description, **extra}
