"""Kommandozeile — der Weg, wie man Jarvis benutzt."""

from __future__ import annotations

import argparse
import os
import sys
from datetime import datetime
from pathlib import Path
from typing import Any

from . import __version__
from .agent import Agent, AgentError, AgentEvents
from .config import Config, ConfigError, default_config_path
from .console import BOLD, DIM, Console
from .memory import Memory
from .permissions import Permissions
from .routines import DueRoutine, RoutineState, ScheduleError, due_routines, parse_schedule
from .tools import build_registry

EXAMPLE_CONFIG = Path(__file__).resolve().parent / "data" / "jarvis.example.toml"

BRIEFING_PROMPT = (
    "Erstelle mein Tagesbriefing. Gehe dabei so vor: "
    "1) Prüfe die Watchlist und nenne, was sich seit gestern bewegt hat und "
    "welche Schwellwerte erreicht wurden. "
    "2) Sieh in den Posteingang und fasse die ungelesenen Nachrichten der "
    "letzten 24 Stunden zusammen — sortiert nach dem, was heute eine Antwort "
    "braucht. "
    "3) Suche nach Nachrichten, die für die Werte auf meiner Watchlist "
    "relevant sind, und nenne die Quelle. "
    "Halte das Ganze kurz genug, um es im Stehen zu lesen."
)


# --------------------------------------------------------------------------- #
# Aufbau
# --------------------------------------------------------------------------- #


def _setup(args: argparse.Namespace, *, interactive: bool) -> tuple[Config, Memory, Agent, Console]:
    console = Console()
    config = Config.load(getattr(args, "config", None))
    config.ensure_dirs()

    if not os.environ.get("ANTHROPIC_API_KEY"):
        raise ConfigError(
            "ANTHROPIC_API_KEY ist nicht gesetzt. Lege den Schlüssel in der "
            "Umgebung oder in einer .env-Datei neben jarvis.toml ab. "
            "Schlüssel gibt es unter https://console.anthropic.com/settings/keys"
        )

    memory = Memory(config.db_path)
    registry = build_registry(config)
    permissions = Permissions(
        default_read=config.permissions.default_read,  # type: ignore[arg-type]
        default_write=config.permissions.default_write,  # type: ignore[arg-type]
        default_external=config.permissions.default_external,  # type: ignore[arg-type]
        tools=config.permissions.tools,
        overrides_path=config.permission_overrides_path,
        ask_fn=console.ask_permission if interactive else None,
    )

    events = AgentEvents(
        on_text=console.write,
        on_thinking=console.thinking if config.model.show_thinking else (lambda _t: None),
        on_tool_start=lambda _name, _args, summary: console.tool_start(summary),
        on_tool_end=console.tool_end,
        on_notice=console.notice,
    )
    agent = Agent(
        config,
        memory,
        permissions,
        registry,
        events=events,
        session=getattr(args, "session", None) or "default",
    )
    return config, memory, agent, console


# --------------------------------------------------------------------------- #
# Befehle
# --------------------------------------------------------------------------- #


def cmd_init(args: argparse.Namespace) -> int:
    console = Console()
    target = Path(args.path).expanduser() if args.path else default_config_path()
    if target.exists() and not args.force:
        console.error(f"{target} existiert bereits. Mit --force überschreiben.")
        return 1
    if not EXAMPLE_CONFIG.is_file():
        console.error(f"Vorlage nicht gefunden: {EXAMPLE_CONFIG}")
        return 1

    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(EXAMPLE_CONFIG.read_text(encoding="utf-8"), encoding="utf-8")

    env_file = target.parent / ".env"
    if not env_file.exists():
        env_file.write_text(
            "# Geheimnisse gehören hierher, nicht in jarvis.toml\n"
            "ANTHROPIC_API_KEY=\n"
            "JARVIS_MAIL_PASSWORD=\n",
            encoding="utf-8",
        )
        try:
            env_file.chmod(0o600)
        except OSError:
            pass

    console.success(f"Konfiguration angelegt: {target}")
    console.info(f"Geheimnisse eintragen in: {env_file}")
    console.line("")
    console.line("Nächste Schritte:", BOLD)
    console.line(f"  1. {env_file} ausfüllen (mindestens ANTHROPIC_API_KEY)")
    console.line(f"  2. {target} anpassen — vor allem [files] roots und [mail]")
    console.line("  3. jarvis doctor")
    console.line("  4. jarvis chat")
    return 0


def cmd_ask(args: argparse.Namespace) -> int:
    config, memory, agent, console = _setup(args, interactive=sys.stdin.isatty())
    try:
        question = " ".join(args.text).strip()
        if not question:
            console.error("Es wurde keine Frage übergeben.")
            return 1
        if args.continue_session:
            agent.load_history()
        console.agent_prefix(f"{config.agent.name}:")
        turn = agent.run(question)
        console.newline_if_needed()
        if args.usage:
            console.info(f"[{turn.usage.render()}, {turn.tool_calls} Werkzeugaufrufe]")
        return 0
    except AgentError as exc:
        console.newline_if_needed()
        console.error(str(exc))
        return 1
    finally:
        memory.close()


def cmd_chat(args: argparse.Namespace) -> int:
    config, memory, agent, console = _setup(args, interactive=True)
    try:
        if args.new:
            removed = memory.clear_session(agent.session)
            if removed:
                console.info(f"{removed} alte Nachrichten der Sitzung verworfen.")
        else:
            loaded = agent.load_history()
            if loaded:
                console.info(f"Sitzung '{agent.session}' fortgesetzt ({loaded} Nachrichten).")

        console.heading(f"{config.agent.name} — {config.model.name}, effort={config.model.effort}")
        console.info(
            f"{len(agent.registry)} lokale Werkzeuge"
            + (", Websuche aktiv" if config.research.enabled else "")
            + ".  /help für Befehle, /exit zum Beenden."
        )

        while True:
            try:
                user_input = console.prompt("\ndu > ").strip()
            except (EOFError, KeyboardInterrupt):
                console.line("")
                break
            if not user_input:
                continue
            if user_input.startswith("/"):
                if _handle_slash(user_input, agent, memory, console, config):
                    break
                continue

            try:
                console.agent_prefix(f"\n{config.agent.name}:")
                turn = agent.run(user_input)
                console.newline_if_needed()
                if args.usage:
                    console.info(f"[{turn.usage.render()}, {turn.tool_calls} Werkzeugaufrufe]")
            except AgentError as exc:
                console.newline_if_needed()
                console.error(str(exc))
            except KeyboardInterrupt:
                console.newline_if_needed()
                console.notice("Abgebrochen.")
        return 0
    finally:
        memory.close()


def _handle_slash(
    command: str, agent: Agent, memory: Memory, console: Console, config: Config
) -> bool:
    """Gibt True zurück, wenn der Chat beendet werden soll."""
    parts = command.split()
    name = parts[0].lower()

    if name in ("/exit", "/quit", "/q"):
        return True
    if name == "/help":
        console.line("")
        console.line("Befehle:", BOLD)
        console.line("  /tools      zeigt alle Werkzeuge und ihre Freigaberegel")
        console.line("  /memory     zeigt die gemerkten Notizen")
        console.line("  /journal    zeigt die letzten Werkzeugaufrufe")
        console.line("  /clear      verwirft den Gesprächsverlauf dieser Sitzung")
        console.line("  /exit       beenden")
        return False
    if name == "/tools":
        console.line("")
        for tool in agent.registry.tools:
            policy = agent.permissions.policy_for(tool.name, tool.risk)
            console.line(f"  {tool.name:<22} {tool.risk:<9} {policy}")
        if config.research.enabled:
            console.line(f"  {'web_search':<22} {'server':<9} immer")
            console.line(f"  {'web_fetch':<22} {'server':<9} immer")
        return False
    if name == "/memory":
        facts = memory.recall(limit=50)
        console.line("")
        if not facts:
            console.info("  Noch nichts gemerkt.")
        for fact in facts:
            console.line(f"  {fact.render()}")
        return False
    if name == "/journal":
        console.line("")
        for entry in memory.journal(limit=25):
            console.line(
                f"  {entry.created_at}  {entry.decision:<10} {entry.tool:<20} {entry.summary}"
            )
        return False
    if name == "/clear":
        memory.clear_session(agent.session)
        agent.reset()
        console.success("Verlauf dieser Sitzung gelöscht.")
        return False

    console.notice(f"Unbekannter Befehl {name}. /help zeigt die Liste.")
    return False


def cmd_briefing(args: argparse.Namespace) -> int:
    args.text = [BRIEFING_PROMPT]
    args.continue_session = False
    return cmd_ask(args)


def cmd_routines(args: argparse.Namespace) -> int:
    console = Console()
    config = Config.load(getattr(args, "config", None))
    config.ensure_dirs()
    state = RoutineState(config.routine_state_path)

    if args.routines_command == "list":
        if not config.routines:
            console.info("Keine Routinen konfiguriert. [[routines]] in jarvis.toml anlegen.")
            return 0
        console.heading("Routinen")
        for routine in config.routines:
            try:
                schedule = parse_schedule(routine.schedule)
                plan = schedule.describe()
            except ScheduleError as exc:
                console.error(f"{routine.name}: {exc}")
                continue
            last = state.last_run(routine.name)
            last_text = last.strftime("%d.%m. %H:%M") if last else "noch nie"
            status = "" if routine.enabled else "  [deaktiviert]"
            console.line(f"  {routine.name:<22} {plan:<26} zuletzt: {last_text}{status}")
        return 0

    # run
    due = []
    if args.name:
        matches = [r for r in config.routines if r.name == args.name]
        if not matches:
            console.error(f"Routine '{args.name}' gibt es nicht.")
            return 1
        due = [
            DueRoutine(
                matches[0], parse_schedule(matches[0].schedule), state.last_run(args.name)
            )
        ]
    else:
        try:
            due = due_routines(config.routines, state)
        except ScheduleError as exc:
            console.error(str(exc))
            return 1

    if not due:
        console.info("Gerade ist nichts fällig.")
        return 0

    exit_code = 0
    for item in due:
        exit_code |= _run_routine(item.routine, config, state, console, args)
    return exit_code


def _run_routine(routine, config: Config, state: RoutineState, console: Console, args) -> int:
    console.heading(f"Routine '{routine.name}'")
    namespace = argparse.Namespace(
        config=getattr(args, "config", None), session=f"routine:{routine.name}"
    )
    try:
        _cfg, memory, agent, _console = _setup(namespace, interactive=False)
    except ConfigError as exc:
        console.error(str(exc))
        return 1

    try:
        turn = agent.run(routine.prompt)
    except AgentError as exc:
        console.error(str(exc))
        memory.close()
        return 1

    console.newline_if_needed()
    state.mark_run(routine.name)

    stamp = datetime.now().strftime("%Y-%m-%d_%H%M")
    log_file = config.log_dir / f"{routine.name}_{stamp}.md"
    log_file.write_text(
        f"# {routine.name} — {datetime.now():%d.%m.%Y %H:%M}\n\n{turn.text}\n",
        encoding="utf-8",
    )
    console.info(f"Protokoll: {log_file}")

    if routine.email_to and config.mail.enabled:
        _mail_routine_result(routine, turn.text, config, console)

    memory.close()
    return 0


def _mail_routine_result(routine, text: str, config: Config, console: Console) -> None:
    """Ergebnis per Mail zustellen — vom Nutzer konfiguriert, nicht vom Modell."""
    from .tools.base import ToolContext
    from .tools.mail import mail_send

    try:
        ctx = ToolContext(config=config, memory=None)  # type: ignore[arg-type]
        mail_send(
            ctx,
            {
                "to": routine.email_to,
                "subject": f"[{config.agent.name}] {routine.name} — {datetime.now():%d.%m.%Y}",
                "body": text,
            },
        )
        console.success(f"Ergebnis an {routine.email_to} gesendet.")
    except Exception as exc:
        console.error(f"Versand an {routine.email_to} fehlgeschlagen: {exc}")


def cmd_journal(args: argparse.Namespace) -> int:
    console = Console()
    config = Config.load(getattr(args, "config", None))
    config.ensure_dirs()
    with Memory(config.db_path) as memory:
        entries = memory.journal(limit=args.limit)
        if not entries:
            console.info("Noch keine Werkzeugaufrufe protokolliert.")
            return 0
        console.heading(f"Die letzten {len(entries)} Werkzeugaufrufe")
        for entry in entries:
            console.line(
                f"  {entry.created_at}  {entry.decision:<10} {entry.tool:<20} {entry.summary}"
            )
    return 0


def cmd_memory(args: argparse.Namespace) -> int:
    console = Console()
    config = Config.load(getattr(args, "config", None))
    config.ensure_dirs()
    with Memory(config.db_path) as memory:
        if args.memory_command == "add":
            fact = memory.remember(" ".join(args.text), args.tags or "")
            console.success(f"Gemerkt als #{fact.id}.")
        elif args.memory_command == "forget":
            if memory.forget(args.fact_id):
                console.success(f"Notiz #{args.fact_id} gelöscht.")
            else:
                console.error(f"Es gibt keine Notiz #{args.fact_id}.")
                return 1
        else:
            facts = memory.recall(args.query or "", limit=args.limit)
            if not facts:
                console.info("Nichts gemerkt.")
                return 0
            for fact in facts:
                console.line(f"  {fact.render()}")
    return 0


def cmd_doctor(args: argparse.Namespace) -> int:
    console = Console()
    problems = 0

    console.heading("Konfiguration")
    try:
        config = Config.load(getattr(args, "config", None))
    except ConfigError as exc:
        console.error(str(exc))
        return 1
    if config.path:
        console.success(f"gelesen: {config.path}")
    else:
        console.notice("Keine jarvis.toml gefunden — es gelten die Standardwerte.")
        console.info("  'jarvis init' legt eine an.")
    config.ensure_dirs()
    console.info(f"  Daten: {config.data_dir}")
    console.info(f"  Modell: {config.model.name}, effort={config.model.effort}")

    console.line("")
    console.heading("Zugangsdaten")
    if os.environ.get("ANTHROPIC_API_KEY"):
        console.success("ANTHROPIC_API_KEY ist gesetzt.")
    else:
        console.error("ANTHROPIC_API_KEY fehlt.")
        problems += 1

    console.line("")
    console.heading("Dateizugriff")
    if not config.files.enabled:
        console.info("abgeschaltet.")
    elif not config.files.roots:
        console.notice("Kein Ordner freigegeben — die Datei-Werkzeuge fehlen dadurch.")
    else:
        for root in config.files.roots:
            if root.is_dir():
                console.success(f"{root}")
            else:
                console.error(f"{root} existiert nicht.")
                problems += 1

    console.line("")
    console.heading("Postfach")
    if not config.mail.enabled:
        console.info("abgeschaltet.")
    elif not config.mail.password:
        console.error(f"{config.mail.password_env} ist leer.")
        problems += 1
    else:
        problems += _check_mail(config, console)

    console.line("")
    console.heading("Kursdaten")
    if not config.markets.enabled:
        console.info("abgeschaltet.")
    else:
        problems += _check_markets(config, console)

    console.line("")
    console.heading("Routinen")
    if not config.routines:
        console.info("keine konfiguriert.")
    for routine in config.routines:
        try:
            console.success(f"{routine.name}: {parse_schedule(routine.schedule).describe()}")
        except ScheduleError as exc:
            console.error(f"{routine.name}: {exc}")
            problems += 1

    console.line("")
    if problems:
        console.error(f"{problems} Punkt(e) müssen noch geklärt werden.")
        return 1
    console.success("Alles bereit.")
    return 0


def _check_mail(config: Config, console: Console) -> int:
    import imaplib

    try:
        conn = imaplib.IMAP4_SSL(config.mail.imap_host, config.mail.imap_port)
        conn.login(config.mail.user, config.mail.password)
        status, _ = conn.select(f'"{config.mail.inbox}"', readonly=True)
        conn.logout()
    except Exception as exc:
        console.error(f"IMAP {config.mail.imap_host}: {exc}")
        return 1
    if status != "OK":
        console.error(f"Ordner '{config.mail.inbox}' nicht lesbar.")
        return 1
    console.success(f"IMAP {config.mail.imap_host} — Posteingang lesbar.")
    return 0


def _check_markets(config: Config, console: Console) -> int:
    from .tools.base import ToolContext
    from .tools.markets import market_quote

    try:
        ctx = ToolContext(config=config, memory=None)  # type: ignore[arg-type]
        result = market_quote(ctx, {"symbols": ["AAPL"]})
    except Exception as exc:
        console.error(f"Kursabruf fehlgeschlagen: {exc}")
        return 1
    console.success("Kursquelle erreichbar.")
    console.info("  " + result.splitlines()[-1].strip())
    return 0


# --------------------------------------------------------------------------- #
# Argumente
# --------------------------------------------------------------------------- #


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="jarvis",
        description="Persönlicher KI-Assistent mit Zugriff auf Dateien, "
        "Postfach, Kursdaten und Websuche.",
    )
    parser.add_argument("--version", action="version", version=f"jarvis {__version__}")
    parser.add_argument("--config", help="Pfad zu einer jarvis.toml.")
    sub = parser.add_subparsers(dest="command", required=True)

    p_init = sub.add_parser("init", help="Konfigurationsdatei anlegen.")
    p_init.add_argument("path", nargs="?", help="Zielpfad (Standard ~/.config/jarvis/jarvis.toml).")
    p_init.add_argument("--force", action="store_true", help="Vorhandene Datei überschreiben.")
    p_init.set_defaults(func=cmd_init)

    p_chat = sub.add_parser("chat", help="Interaktives Gespräch.")
    p_chat.add_argument("--session", default="default", help="Name der Sitzung.")
    p_chat.add_argument("--new", action="store_true", help="Verlauf der Sitzung verwerfen.")
    p_chat.add_argument("--usage", action="store_true", help="Tokenverbrauch anzeigen.")
    p_chat.set_defaults(func=cmd_chat)

    p_ask = sub.add_parser("ask", help="Eine einzelne Frage stellen.")
    p_ask.add_argument("text", nargs="+", help="Die Frage oder der Auftrag.")
    p_ask.add_argument("--session", default="oneshot", help="Name der Sitzung.")
    p_ask.add_argument(
        "--continue", dest="continue_session", action="store_true",
        help="Den Verlauf der Sitzung mitschicken.",
    )
    p_ask.add_argument("--usage", action="store_true", help="Tokenverbrauch anzeigen.")
    p_ask.set_defaults(func=cmd_ask)

    p_brief = sub.add_parser("briefing", help="Tagesbriefing erstellen.")
    p_brief.add_argument("--session", default="briefing")
    p_brief.add_argument("--usage", action="store_true")
    p_brief.set_defaults(func=cmd_briefing)

    p_rout = sub.add_parser("routines", help="Geplante Aufgaben.")
    rsub = p_rout.add_subparsers(dest="routines_command", required=True)
    r_list = rsub.add_parser("list", help="Alle Routinen und ihren Zeitplan zeigen.")
    r_list.set_defaults(func=cmd_routines)
    r_run = rsub.add_parser("run", help="Fällige Routinen ausführen.")
    r_run.add_argument("--name", help="Nur diese Routine, unabhängig vom Zeitplan.")
    r_run.set_defaults(func=cmd_routines)

    p_journal = sub.add_parser("journal", help="Protokoll der Werkzeugaufrufe.")
    p_journal.add_argument("--limit", type=int, default=30)
    p_journal.set_defaults(func=cmd_journal)

    p_mem = sub.add_parser("memory", help="Gedächtnis ansehen und pflegen.")
    msub = p_mem.add_subparsers(dest="memory_command", required=True)
    m_list = msub.add_parser("list", help="Notizen anzeigen.")
    m_list.add_argument("query", nargs="?", help="Suchbegriff.")
    m_list.add_argument("--limit", type=int, default=50)
    m_list.set_defaults(func=cmd_memory)
    m_add = msub.add_parser("add", help="Notiz hinzufügen.")
    m_add.add_argument("text", nargs="+")
    m_add.add_argument("--tags", help="Komma-getrennte Schlagworte.")
    m_add.set_defaults(func=cmd_memory)
    m_forget = msub.add_parser("forget", help="Notiz löschen.")
    m_forget.add_argument("fact_id", type=int)
    m_forget.set_defaults(func=cmd_memory)

    p_doctor = sub.add_parser("doctor", help="Einrichtung prüfen.")
    p_doctor.set_defaults(func=cmd_doctor)

    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        result: Any = args.func(args)
        return int(result or 0)
    except ConfigError as exc:
        Console().error(str(exc))
        return 1
    except KeyboardInterrupt:
        Console().line("")
        return 130


if __name__ == "__main__":  # pragma: no cover
    sys.exit(main())
