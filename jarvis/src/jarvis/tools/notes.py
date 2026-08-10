"""Gedächtnis-Werkzeuge — was Jarvis über dich behalten soll.

Die wichtigsten Notizen wandern beim nächsten Start automatisch in den
Systemprompt, damit Jarvis sie nicht erst suchen muss.
"""

from __future__ import annotations

from typing import Any

from .base import Tool, ToolContext, ToolError, obj, prop


def remember(ctx: ToolContext, args: dict[str, Any]) -> str:
    text = str(args["text"]).strip()
    tags = str(args.get("tags") or "").strip()
    try:
        fact = ctx.memory.remember(text, tags)
    except ValueError as exc:
        raise ToolError(str(exc)) from exc
    return f"Gemerkt als #{fact.id}: {fact.text}"


def recall(ctx: ToolContext, args: dict[str, Any]) -> str:
    query = str(args.get("query") or "")
    limit = min(int(args.get("limit") or 20), 100)
    facts = ctx.memory.recall(query, limit)
    if not facts:
        return f"Nichts gemerkt zu '{query}'." if query else "Das Gedächtnis ist leer."
    header = f"{len(facts)} Notizen" + (f" zu '{query}'" if query else "") + ":"
    return header + "\n" + "\n".join(f"  {f.render()}" for f in facts)


def forget(ctx: ToolContext, args: dict[str, Any]) -> str:
    fact_id = int(args["fact_id"])
    if ctx.memory.forget(fact_id):
        return f"Notiz #{fact_id} gelöscht."
    raise ToolError(f"Es gibt keine Notiz #{fact_id}.")


def build_tools() -> list[Tool]:
    return [
        Tool(
            name="remember",
            description=(
                "Speichert eine dauerhafte Notiz über den Nutzer, sein Projekt "
                "oder eine getroffene Absprache. Nutze das für Dinge, die auch "
                "in einer Woche noch gelten — nicht für den Inhalt des laufenden "
                "Gesprächs. Keine Passwörter, Schlüssel oder Zugangsdaten."
            ),
            input_schema=obj(
                {
                    "text": prop("string", "Ein Sachverhalt, ein Satz."),
                    "tags": prop("string", "Komma-getrennte Schlagworte, z.B. 'arbeit,steuer'."),
                },
                ["text"],
            ),
            risk="write",
            handler=remember,
            summarize=lambda a: f"Merken: {str(a.get('text', ''))[:70]}",
        ),
        Tool(
            name="recall",
            description="Durchsucht das Gedächtnis nach Stichwort oder Schlagwort.",
            input_schema=obj(
                {
                    "query": prop("string", "Suchbegriff; leer = die neuesten Notizen."),
                    "limit": prop("integer", "Anzahl Treffer (Standard 20)."),
                }
            ),
            risk="read",
            handler=recall,
            summarize=lambda a: f"Gedächtnis nach '{a.get('query', '')}' durchsuchen",
        ),
        Tool(
            name="forget",
            description="Löscht eine Notiz anhand ihrer Nummer aus recall.",
            input_schema=obj({"fact_id": prop("integer", "Nummer der Notiz.")}, ["fact_id"]),
            risk="write",
            handler=forget,
            summarize=lambda a: f"Notiz #{a.get('fact_id')} löschen",
        ),
    ]
