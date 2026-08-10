"""Werkzeugregister.

Welche Werkzeuge Jarvis bekommt, hängt an der Konfiguration: ein
abgeschaltetes Postfach erzeugt gar keine Mail-Werkzeuge, statt sie
anzubieten und dann zu scheitern.

Die Reihenfolge ist stabil (alphabetisch). Das ist kein Schönheitsdetail:
Werkzeugdefinitionen stehen ganz vorne im Prompt, und jede Umsortierung
würde den Prompt-Cache bei jedem Aufruf entwerten.
"""

from __future__ import annotations

from typing import TYPE_CHECKING, Any

from . import files, mail, markets, notes
from .base import Tool, ToolContext, ToolError

if TYPE_CHECKING:  # pragma: no cover
    from ..config import Config

__all__ = ["Tool", "ToolContext", "ToolError", "ToolRegistry", "build_registry"]


class ToolRegistry:
    def __init__(self, tools: list[Tool]) -> None:
        self._tools = {t.name: t for t in sorted(tools, key=lambda t: t.name)}

    def __contains__(self, name: object) -> bool:
        return name in self._tools

    def __len__(self) -> int:
        return len(self._tools)

    def get(self, name: str) -> Tool | None:
        return self._tools.get(name)

    @property
    def tools(self) -> list[Tool]:
        return list(self._tools.values())

    def api_definitions(self) -> list[dict[str, Any]]:
        return [t.to_api() for t in self._tools.values()]

    def by_risk(self) -> dict[str, list[str]]:
        grouped: dict[str, list[str]] = {"read": [], "write": [], "external": []}
        for tool in self._tools.values():
            grouped.setdefault(tool.risk, []).append(tool.name)
        return grouped


def build_registry(config: "Config") -> ToolRegistry:
    tools: list[Tool] = list(notes.build_tools())
    if config.files.enabled and config.files.roots:
        tools += files.build_tools()
    if config.mail.enabled:
        tools += mail.build_tools()
    if config.markets.enabled:
        tools += markets.build_tools()
    return ToolRegistry(tools)


def server_tools(config: "Config") -> list[dict[str, Any]]:
    """Von Anthropik ausgeführte Werkzeuge — hier gibt es nichts zu implementieren.

    Web-Suche und Web-Abruf laufen auf Anthropics Servern. Wir deklarieren sie
    nur; Ergebnisse kommen direkt als Inhaltsblöcke in derselben Antwort zurück.
    Deshalb gibt es dafür auch keine Risikoklasse und keine Freigabe: es
    passiert nichts auf deinem Rechner.
    """
    if not config.research.enabled:
        return []
    search: dict[str, Any] = {
        "type": "web_search_20260209",
        "name": "web_search",
        "max_uses": config.research.max_searches,
    }
    fetch: dict[str, Any] = {
        "type": "web_fetch_20260209",
        "name": "web_fetch",
        "max_uses": config.research.max_searches,
        "citations": {"enabled": True},
    }
    if config.research.blocked_domains:
        search["blocked_domains"] = config.research.blocked_domains
        fetch["blocked_domains"] = config.research.blocked_domains
    return [search, fetch]
