"""Der Agent: Modell + Werkzeuge + Freigaben in einer Schleife.

Warum eine handgeschriebene Schleife und nicht der ``tool_runner`` des SDK?
Drei Gründe, die alle mit Kontrolle zu tun haben:

1. Jeder Werkzeugaufruf muss durch die Freigabeliste und ins Prüfprotokoll,
   bevor er ausgeführt wird — nicht danach.
2. ``pause_turn`` (die Web-Suche läuft serverseitig und kann pausieren) wird
   hier ausdrücklich behandelt; der Python-Runner bricht dort still ab.
3. Die Schleife ist die interessanteste Stelle des Programms. Wer sie lesen
   kann, kann Jarvis erweitern.

Modellseitig gilt für Claude Opus 5:

* Denken ist standardmäßig an; ``max_tokens`` deckt Denken **und** Antwort ab.
* ``temperature``/``top_p`` gibt es nicht mehr — Steuerung läuft über den
  Prompt und ``effort``.
* Sicherheitsklassifikatoren können ablehnen (``stop_reason == "refusal"``);
  mit ``fallbacks="default"`` beantwortet ein Ersatzmodell die Anfrage.
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from datetime import datetime
from typing import Any, Callable

import anthropic

from .config import FALLBACK_BETA, Config
from .memory import Memory, render_facts
from .permissions import Permissions
from .tools import ToolContext, ToolError, ToolRegistry, server_tools

# Modelle, die ein ``{"role": "system"}`` mitten im Gespräch akzeptieren.
# Dort landet der Tageskontext, ohne den zwischengespeicherten Systemprompt
# zu entwerten. Andere Modelle bekommen ihn in die Nutzernachricht gehängt.
MID_CONVERSATION_SYSTEM_MODELS = {
    "claude-opus-5",
    "claude-opus-4-8",
    "claude-fable-5",
    "claude-mythos-5",
}

MAX_TOOL_RESULT_CHARS = 30_000


def _noop(*_: Any, **__: Any) -> None:
    return None


@dataclass
class AgentEvents:
    """Ausgabekanäle — die Oberfläche entscheidet, was sie damit macht."""

    on_text: Callable[[str], None] = _noop
    on_thinking: Callable[[str], None] = _noop
    on_tool_start: Callable[[str, dict[str, Any], str], None] = _noop
    on_tool_end: Callable[[str, str, bool], None] = _noop
    on_notice: Callable[[str], None] = _noop


@dataclass
class Usage:
    input_tokens: int = 0
    output_tokens: int = 0
    cache_read_tokens: int = 0
    cache_write_tokens: int = 0

    def add(self, usage: Any) -> None:
        self.input_tokens += getattr(usage, "input_tokens", 0) or 0
        self.output_tokens += getattr(usage, "output_tokens", 0) or 0
        self.cache_read_tokens += getattr(usage, "cache_read_input_tokens", 0) or 0
        self.cache_write_tokens += getattr(usage, "cache_creation_input_tokens", 0) or 0

    def render(self) -> str:
        return (
            f"{self.input_tokens} rein / {self.output_tokens} raus, "
            f"{self.cache_read_tokens} aus dem Cache gelesen, "
            f"{self.cache_write_tokens} geschrieben"
        )


@dataclass
class Turn:
    """Ergebnis eines vollständigen Nutzer-Anliegens."""

    text: str
    tool_calls: int = 0
    usage: Usage = field(default_factory=Usage)
    stopped_because: str = "end_turn"


class Agent:
    def __init__(
        self,
        config: Config,
        memory: Memory,
        permissions: Permissions,
        registry: ToolRegistry,
        *,
        events: AgentEvents | None = None,
        session: str = "default",
        client: Any | None = None,
    ) -> None:
        self.config = config
        self.memory = memory
        self.permissions = permissions
        self.registry = registry
        self.events = events or AgentEvents()
        self.session = session
        self.client = client or anthropic.Anthropic()
        self.ctx = ToolContext(config=config, memory=memory)

        self._messages: list[dict[str, Any]] = []
        self._context_injected = False
        self._stable_system = self._build_stable_system()

    # ------------------------------------------------------------------ #
    # Systemprompt
    # ------------------------------------------------------------------ #

    def _build_stable_system(self) -> str:
        cfg = self.config.agent
        grouped = self.registry.by_risk()
        lines = [
            f"Du bist {cfg.name}, der persönliche Assistent"
            + (f" von {cfg.user_name}" if cfg.user_name else "")
            + ". Du läufst als Programm auf dem Rechner deines Nutzers und hast "
            "Zugriff auf echte Werkzeuge: sein Dateisystem, sein Postfach, "
            "Kursdaten und die Websuche.",
            "",
            f"Antworte auf {cfg.language}, es sei denn, der Nutzer schreibt in "
            "einer anderen Sprache.",
            "",
            "## Wie du arbeitest",
            "",
            "Handle, sobald du genug weißt. Wenn eine Aufgabe mehrere Schritte "
            "hat, führe sie zu Ende, statt nach jedem Schritt zu fragen. "
            "Frage nur nach, wenn zwei sinnvolle Lesarten der Aufgabe zu "
            "deutlich unterschiedlicher Arbeit führen würden.",
            "",
            "Berichte, was tatsächlich passiert ist. Wenn ein Werkzeug einen "
            "Fehler liefert, sag das mit der Fehlermeldung — erfinde kein "
            "Ergebnis und behaupte nichts als erledigt, was du nicht geprüft "
            "hast. Ist ein Teil einer Aufgabe blockiert, erledige den Rest und "
            "sag klar, was offen blieb und warum.",
            "",
            "Fasse dich kurz. Der Nutzer liest deine Antwort im Terminal: das "
            "Ergebnis zuerst, Begründung danach, keine Wiederholung dessen, was "
            "er gerade selbst geschrieben hat. Keine Aufzählung, wo zwei Sätze "
            "reichen.",
            "",
            "## Vorsicht an den richtigen Stellen",
            "",
            "Manche Werkzeuge verändern etwas oder wirken nach außen. Der Nutzer "
            "muss sie unter Umständen erst freigeben; wird eine Aktion "
            "abgelehnt, akzeptiere das, erkläre nichts lang und schlage einen "
            "anderen Weg vor.",
            "",
            "Beim Aufräumen von Ordnern zeigst du erst den Plan (dry_run) und "
            "führst erst nach Zustimmung aus. Bei E-Mails ist der Entwurf der "
            "Normalfall: du formulierst, der Nutzer schickt ab. Versende nur, "
            "wenn er ausdrücklich darum bittet.",
            "",
            "Bei Geld gibst du Zahlen, Kennzahlen und Einordnung — keine "
            "Kauf- oder Verkaufsempfehlung. Du kannst weder handeln noch "
            "Aufträge erteilen; sag das, wenn es verlangt wird.",
            "",
            "## Recherche",
            "",
            "Wenn die Antwort von aktuellen Informationen abhängt — Nachrichten, "
            "Kurse, Preise, Versionsstände, alles Zeitkritische — suche im Netz, "
            "statt aus dem Gedächtnis zu antworten. Nenne die Quelle.",
            "",
            "## Gedächtnis",
            "",
            "Merke dir mit `remember`, was auch in einer Woche noch gilt: "
            "Vorlieben, Projekte, Absprachen, Ordnerstrukturen. Nicht den "
            "Verlauf des laufenden Gesprächs und niemals Zugangsdaten.",
            "",
            "## Deine Werkzeuge",
            "",
        ]
        for risk, label in (
            ("read", "lesend (laufen ohne Rückfrage)"),
            ("write", "verändernd (können eine Freigabe brauchen)"),
            ("external", "nach außen wirkend (brauchen in der Regel eine Freigabe)"),
        ):
            names = grouped.get(risk) or []
            if names:
                lines.append(f"* {label}: {', '.join(names)}")
        if self.config.research.enabled:
            lines.append("* serverseitig: web_search, web_fetch")

        if cfg.persona:
            lines += ["", "## Zusätzliche Anweisungen des Nutzers", "", cfg.persona]
        return "\n".join(lines)

    def _system_blocks(self) -> list[dict[str, Any]]:
        """Zwei Blöcke: der stabile wird zwischengespeichert, der zweite nicht.

        Werkzeuge und der stabile Text stehen vor dem Cache-Punkt und werden
        damit gemeinsam gecacht. Die gemerkten Notizen ändern sich, sobald
        Jarvis etwas Neues lernt — sie stehen deshalb bewusst dahinter.
        """
        blocks: list[dict[str, Any]] = [
            {
                "type": "text",
                "text": self._stable_system,
                "cache_control": {"type": "ephemeral"},
            }
        ]
        facts = self.memory.recall(limit=40)
        if facts:
            blocks.append(
                {
                    "type": "text",
                    "text": "## Was du über den Nutzer weißt\n\n" + render_facts(facts),
                }
            )
        return blocks

    def _context_line(self) -> str:
        now = datetime.now()
        parts = [
            f"Heute ist {now.strftime('%A, %d.%m.%Y')}, Ortszeit {now.strftime('%H:%M')}."
        ]
        if self.config.files.roots:
            roots = ", ".join(str(r) for r in self.config.files.roots)
            parts.append(f"Freigegebene Ordner: {roots}.")
        if self.config.mail.enabled:
            parts.append(f"Postfach: {self.config.mail.user}.")
        return " ".join(parts)

    # ------------------------------------------------------------------ #
    # Verlauf
    # ------------------------------------------------------------------ #

    def load_history(self) -> int:
        """Lädt den Textverlauf einer früheren Sitzung.

        Bewusst nur Text: Werkzeugblöcke aus einem alten Prozess wieder
        einzuspielen ist fehleranfällig, und für den Gesprächsfaden reicht,
        was gesagt wurde.
        """
        turns = self.memory.load_turns(self.session)
        self._messages = [t for t in turns if t.get("content")]
        if self._messages:
            self._context_injected = True
        return len(self._messages)

    def reset(self) -> None:
        """Verwirft den Verlauf im Speicher (die Datenbank bleibt unberührt)."""
        self._messages.clear()
        self._context_injected = False

    @property
    def messages(self) -> list[dict[str, Any]]:
        return list(self._messages)

    def _remember_text_turn(self, role: str, text: str) -> None:
        if text.strip():
            self.memory.append_turn(self.session, role, text.strip())

    # ------------------------------------------------------------------ #
    # Anfrage bauen
    # ------------------------------------------------------------------ #

    def _request_kwargs(self) -> dict[str, Any]:
        model = self.config.model
        tools = self.registry.api_definitions() + server_tools(self.config)
        kwargs: dict[str, Any] = {
            "model": model.name,
            "max_tokens": model.max_tokens,
            "system": self._system_blocks(),
            "messages": self._messages,
            "output_config": {"effort": model.effort},
        }
        if tools:
            kwargs["tools"] = tools
        if model.show_thinking:
            # Standard ist "omitted": die Denkblöcke kommen leer zurück.
            kwargs["thinking"] = {"type": "adaptive", "display": "summarized"}
        if model.fallbacks:
            kwargs["fallbacks"] = "default"
            kwargs["betas"] = [FALLBACK_BETA]
        return kwargs

    # ------------------------------------------------------------------ #
    # Hauptschleife
    # ------------------------------------------------------------------ #

    def run(self, user_input: str) -> Turn:
        text = user_input.strip()
        if not text:
            return Turn(text="")

        self._append_user_message(text)
        self._remember_text_turn("user", text)

        usage = Usage()
        tool_calls = 0
        final_text: list[str] = []
        stopped = "end_turn"

        for _round in range(self.config.model.max_tool_rounds):
            response = self._call_model()
            usage.add(response.usage)
            stopped = response.stop_reason or "end_turn"

            if stopped == "refusal":
                detail = getattr(response, "stop_details", None)
                category = getattr(detail, "category", None) if detail else None
                note = (
                    "Diese Anfrage wurde von den Sicherheitsfiltern abgelehnt"
                    + (f" (Kategorie: {category})" if category else "")
                    + "."
                )
                self.events.on_notice(note)
                final_text.append(note)
                break

            self._messages.append({"role": "assistant", "content": response.content})
            final_text.append(_text_of(response.content))

            if stopped == "pause_turn":
                # Ein serverseitiges Werkzeug hat sein Rundenlimit erreicht.
                # Die Antwort steht bereits im Verlauf; erneut senden setzt fort.
                continue

            if stopped == "max_tokens":
                self.events.on_notice(
                    "Antwort abgeschnitten (max_tokens erreicht). Erhöhe "
                    "model.max_tokens oder senke model.effort."
                )
                break

            if stopped != "tool_use":
                break

            results = self._run_tools(response.content)
            tool_calls += len(results)
            if not results:
                break
            self._messages.append({"role": "user", "content": results})
        else:
            self.events.on_notice(
                f"Abbruch nach {self.config.model.max_tool_rounds} Werkzeugrunden."
            )
            stopped = "max_rounds"

        answer = "\n\n".join(t for t in final_text if t.strip()).strip()
        self._remember_text_turn("assistant", answer)
        return Turn(text=answer, tool_calls=tool_calls, usage=usage, stopped_because=stopped)

    def _append_user_message(self, text: str) -> None:
        context = self._context_line()
        if self._context_injected:
            self._messages.append({"role": "user", "content": text})
            return

        self._context_injected = True
        if self.config.model.name in MID_CONVERSATION_SYSTEM_MODELS:
            # Als Systemnachricht *hinter* der Nutzernachricht: der
            # zwischengespeicherte Systemprompt bleibt Byte für Byte gleich.
            self._messages.append({"role": "user", "content": text})
            self._messages.append({"role": "system", "content": context})
        else:
            self._messages.append({"role": "user", "content": f"{context}\n\n{text}"})

    def _call_model(self) -> Any:
        kwargs = self._request_kwargs()
        try:
            with self.client.beta.messages.stream(**kwargs) as stream:
                for event in stream:
                    if event.type != "content_block_delta":
                        continue
                    delta = event.delta
                    if delta.type == "text_delta":
                        self.events.on_text(delta.text)
                    elif delta.type == "thinking_delta":
                        self.events.on_thinking(delta.thinking)
                return stream.get_final_message()
        except anthropic.APIStatusError as exc:
            raise AgentError(_explain_api_error(exc)) from exc
        except anthropic.APIConnectionError as exc:
            raise AgentError(
                f"Keine Verbindung zur Anthropic-API: {exc}. Netzwerk prüfen."
            ) from exc

    # ------------------------------------------------------------------ #
    # Werkzeuge ausführen
    # ------------------------------------------------------------------ #

    def _run_tools(self, content: Any) -> list[dict[str, Any]]:
        results: list[dict[str, Any]] = []
        for block in content:
            if getattr(block, "type", None) != "tool_use":
                continue
            results.append(self._run_one_tool(block))
        return results

    def _run_one_tool(self, block: Any) -> dict[str, Any]:
        name = block.name
        arguments = dict(block.input or {})
        tool = self.registry.get(name)

        if tool is None:
            message = f"Das Werkzeug '{name}' gibt es nicht."
            self.memory.log_action(name, "unbekannt", "fehler", message)
            return _tool_result(block.id, message, is_error=True)

        summary = tool.summary_for(arguments)
        self.events.on_tool_start(name, arguments, summary)

        verdict = self.permissions.check(name, tool.risk, summary)
        if not verdict.allowed:
            message = f"Aktion nicht ausgeführt: {verdict.reason}"
            self.memory.log_action(name, tool.risk, "abgelehnt", summary, verdict.reason)
            self.events.on_tool_end(name, message, True)
            return _tool_result(block.id, message, is_error=True)

        try:
            output = tool.handler(self.ctx, arguments)
            is_error = False
        except ToolError as exc:
            output, is_error = str(exc), True
        except Exception as exc:  # pragma: no cover - Netz der letzten Instanz
            output = f"Unerwarteter Fehler in '{name}': {exc.__class__.__name__}: {exc}"
            is_error = True

        if len(output) > MAX_TOOL_RESULT_CHARS:
            output = output[:MAX_TOOL_RESULT_CHARS] + "\n[... Ausgabe gekürzt ...]"

        self.memory.log_action(
            name,
            tool.risk,
            "fehler" if is_error else "ausgeführt",
            summary,
            output[:500],
        )
        self.events.on_tool_end(name, output, is_error)
        return _tool_result(block.id, output, is_error=is_error)


class AgentError(RuntimeError):
    """Fehler, der dem Nutzer als ganzer Satz gezeigt werden kann."""


# --------------------------------------------------------------------------- #
# Hilfsfunktionen
# --------------------------------------------------------------------------- #


def _tool_result(tool_use_id: str, content: str, *, is_error: bool) -> dict[str, Any]:
    block: dict[str, Any] = {
        "type": "tool_result",
        "tool_use_id": tool_use_id,
        "content": content or "(keine Ausgabe)",
    }
    if is_error:
        block["is_error"] = True
    return block


def _text_of(content: Any) -> str:
    parts = [
        block.text
        for block in content
        if getattr(block, "type", None) == "text" and getattr(block, "text", "")
    ]
    return "\n".join(parts).strip()


def _explain_api_error(exc: anthropic.APIStatusError) -> str:
    status = exc.status_code
    base = f"Die Anthropic-API antwortete mit {status}: {exc.message}"
    hints = {
        401: "Der API-Schlüssel fehlt oder ist ungültig — prüfe ANTHROPIC_API_KEY.",
        403: "Der Schlüssel hat keinen Zugriff auf dieses Modell.",
        404: "Das Modell gibt es nicht — prüfe model.name in jarvis.toml.",
        429: "Ratenlimit erreicht. Kurz warten und erneut versuchen.",
        529: "Die API ist gerade überlastet. Kurz warten und erneut versuchen.",
    }
    if status in hints:
        return f"{base}\n{hints[status]}"
    if status >= 500:
        return f"{base}\nServerseitiges Problem — später erneut versuchen."
    return base


def dump_messages(messages: list[dict[str, Any]]) -> str:
    """Für die Fehlersuche: den aktuellen Verlauf lesbar machen."""
    return json.dumps(messages, indent=2, ensure_ascii=False, default=str)
