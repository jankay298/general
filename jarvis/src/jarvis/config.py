"""Konfiguration laden.

Reihenfolge der Suche nach ``jarvis.toml``:

1. ``$JARVIS_CONFIG``               (expliziter Pfad)
2. ``./jarvis.toml``                (Projektverzeichnis)
3. ``~/.config/jarvis/jarvis.toml`` (Standard)

Geheimnisse (API-Key, Mail-Passwort) stehen **nie** in der TOML-Datei, sondern
in Umgebungsvariablen oder einer ``.env`` neben der Konfiguration.
"""

from __future__ import annotations

import os
import tomllib
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

DEFAULT_MODEL = "claude-opus-5"

# Beta-Flag für serverseitige Fallbacks. Opus 5 kann eine Anfrage ablehnen
# (stop_reason "refusal"); mit "default" beantwortet Anthropic sie automatisch
# mit einem Ersatzmodell, statt uns eine leere Antwort zu geben.
FALLBACK_BETA = "server-side-fallback-2026-07-01"


class ConfigError(RuntimeError):
    """Konfiguration fehlt oder ist unbrauchbar."""


# --------------------------------------------------------------------------- #
# Abschnitte
# --------------------------------------------------------------------------- #


@dataclass
class ModelConfig:
    name: str = DEFAULT_MODEL
    effort: str = "high"  # low | medium | high | xhigh | max
    max_tokens: int = 32000
    show_thinking: bool = False
    fallbacks: bool = True
    max_tool_rounds: int = 25

    @classmethod
    def from_dict(cls, d: dict[str, Any]) -> "ModelConfig":
        cfg = cls(
            name=str(d.get("name", DEFAULT_MODEL)),
            effort=str(d.get("effort", "high")),
            max_tokens=int(d.get("max_tokens", 32000)),
            show_thinking=bool(d.get("show_thinking", False)),
            fallbacks=bool(d.get("fallbacks", True)),
            max_tool_rounds=int(d.get("max_tool_rounds", 25)),
        )
        if cfg.effort not in {"low", "medium", "high", "xhigh", "max"}:
            raise ConfigError(
                f"model.effort='{cfg.effort}' ist ungültig "
                "(erlaubt: low, medium, high, xhigh, max)"
            )
        if cfg.max_tokens < 1024:
            raise ConfigError("model.max_tokens muss mindestens 1024 sein")
        return cfg


@dataclass
class AgentConfig:
    name: str = "Jarvis"
    language: str = "Deutsch"
    user_name: str = ""
    persona: str = ""

    @classmethod
    def from_dict(cls, d: dict[str, Any]) -> "AgentConfig":
        return cls(
            name=str(d.get("name", "Jarvis")),
            language=str(d.get("language", "Deutsch")),
            user_name=str(d.get("user_name", "")),
            persona=str(d.get("persona", "")),
        )


@dataclass
class PermissionConfig:
    """Freigabeliste: pro Risikoklasse ein Standard, pro Werkzeug ein Override."""

    default_read: str = "allow"
    default_write: str = "ask"
    default_external: str = "ask"
    tools: dict[str, str] = field(default_factory=dict)

    _VALID = {"allow", "ask", "deny"}

    @classmethod
    def from_dict(cls, d: dict[str, Any]) -> "PermissionConfig":
        cfg = cls(
            default_read=str(d.get("default_read", "allow")),
            default_write=str(d.get("default_write", "ask")),
            default_external=str(d.get("default_external", "ask")),
            tools={str(k): str(v) for k, v in (d.get("tools") or {}).items()},
        )
        for label, value in [
            ("default_read", cfg.default_read),
            ("default_write", cfg.default_write),
            ("default_external", cfg.default_external),
            *[(f"tools.{k}", v) for k, v in cfg.tools.items()],
        ]:
            if value not in cls._VALID:
                raise ConfigError(
                    f"permissions.{label}='{value}' ist ungültig "
                    "(erlaubt: allow, ask, deny)"
                )
        return cfg


@dataclass
class FilesConfig:
    enabled: bool = True
    roots: list[Path] = field(default_factory=list)

    @classmethod
    def from_dict(cls, d: dict[str, Any]) -> "FilesConfig":
        roots = [Path(str(p)).expanduser() for p in (d.get("roots") or [])]
        return cls(enabled=bool(d.get("enabled", True)), roots=roots)


@dataclass
class MailConfig:
    enabled: bool = False
    imap_host: str = ""
    imap_port: int = 993
    smtp_host: str = ""
    smtp_port: int = 587
    user: str = ""
    from_address: str = ""
    inbox: str = "INBOX"
    drafts: str = "Drafts"
    signature: str = ""
    password_env: str = "JARVIS_MAIL_PASSWORD"

    @classmethod
    def from_dict(cls, d: dict[str, Any]) -> "MailConfig":
        cfg = cls(
            enabled=bool(d.get("enabled", False)),
            imap_host=str(d.get("imap_host", "")),
            imap_port=int(d.get("imap_port", 993)),
            smtp_host=str(d.get("smtp_host", "")),
            smtp_port=int(d.get("smtp_port", 587)),
            user=str(d.get("user", "")),
            from_address=str(d.get("from_address", "") or d.get("user", "")),
            inbox=str(d.get("inbox", "INBOX")),
            drafts=str(d.get("drafts", "Drafts")),
            signature=str(d.get("signature", "")),
            password_env=str(d.get("password_env", "JARVIS_MAIL_PASSWORD")),
        )
        if cfg.enabled and not (cfg.imap_host and cfg.user):
            raise ConfigError("mail.enabled=true braucht mail.imap_host und mail.user")
        return cfg

    @property
    def password(self) -> str:
        return os.environ.get(self.password_env, "")


@dataclass
class MarketsConfig:
    enabled: bool = True
    currency_hint: str = "EUR"
    seed_watchlist: list[dict[str, Any]] = field(default_factory=list)

    @classmethod
    def from_dict(cls, d: dict[str, Any]) -> "MarketsConfig":
        return cls(
            enabled=bool(d.get("enabled", True)),
            currency_hint=str(d.get("currency_hint", "EUR")),
            seed_watchlist=list(d.get("watchlist") or []),
        )


@dataclass
class ResearchConfig:
    """Web-Recherche läuft als serverseitiges Werkzeug bei Anthropic."""

    enabled: bool = True
    max_searches: int = 8
    blocked_domains: list[str] = field(default_factory=list)

    @classmethod
    def from_dict(cls, d: dict[str, Any]) -> "ResearchConfig":
        return cls(
            enabled=bool(d.get("enabled", True)),
            max_searches=int(d.get("max_searches", 8)),
            blocked_domains=[str(x) for x in (d.get("blocked_domains") or [])],
        )


@dataclass
class Routine:
    name: str
    prompt: str
    schedule: str
    enabled: bool = True
    email_to: str = ""

    @classmethod
    def from_dict(cls, d: dict[str, Any]) -> "Routine":
        missing = [k for k in ("name", "prompt", "schedule") if not d.get(k)]
        if missing:
            raise ConfigError(f"[[routines]] fehlt: {', '.join(missing)}")
        return cls(
            name=str(d["name"]),
            prompt=str(d["prompt"]),
            schedule=str(d["schedule"]),
            enabled=bool(d.get("enabled", True)),
            email_to=str(d.get("email_to", "")),
        )


# --------------------------------------------------------------------------- #
# Gesamtkonfiguration
# --------------------------------------------------------------------------- #


@dataclass
class Config:
    path: Path | None
    data_dir: Path
    model: ModelConfig
    agent: AgentConfig
    permissions: PermissionConfig
    files: FilesConfig
    mail: MailConfig
    markets: MarketsConfig
    research: ResearchConfig
    routines: list[Routine]

    # -- Ableitungen ------------------------------------------------------- #

    @property
    def db_path(self) -> Path:
        return self.data_dir / "jarvis.db"

    @property
    def watchlist_path(self) -> Path:
        return self.data_dir / "watchlist.json"

    @property
    def routine_state_path(self) -> Path:
        return self.data_dir / "routine-state.json"

    @property
    def permission_overrides_path(self) -> Path:
        """Von "immer erlauben"/"nie erlauben" geschriebene Overrides.

        Bewusst eine eigene Datei: so bleibt die handgepflegte ``jarvis.toml``
        mit ihren Kommentaren unangetastet.
        """
        return self.data_dir / "permissions.local.json"

    @property
    def log_dir(self) -> Path:
        return self.data_dir / "logs"

    # -- Laden ------------------------------------------------------------- #

    @classmethod
    def load(cls, explicit: str | Path | None = None) -> "Config":
        path = _find_config(explicit)
        raw: dict[str, Any] = {}
        if path is not None:
            with path.open("rb") as fh:
                raw = tomllib.load(fh)
            load_dotenv(path.parent / ".env")
        load_dotenv(Path.cwd() / ".env")

        data_dir = Path(
            os.environ.get("JARVIS_DATA_DIR")
            or raw.get("data_dir")
            or (Path.home() / ".local" / "share" / "jarvis")
        ).expanduser()

        return cls(
            path=path,
            data_dir=data_dir,
            model=ModelConfig.from_dict(raw.get("model") or {}),
            agent=AgentConfig.from_dict(raw.get("agent") or {}),
            permissions=PermissionConfig.from_dict(raw.get("permissions") or {}),
            files=FilesConfig.from_dict(raw.get("files") or {}),
            mail=MailConfig.from_dict(raw.get("mail") or {}),
            markets=MarketsConfig.from_dict(raw.get("markets") or {}),
            research=ResearchConfig.from_dict(raw.get("research") or {}),
            routines=[Routine.from_dict(r) for r in (raw.get("routines") or [])],
        )

    def ensure_dirs(self) -> None:
        self.data_dir.mkdir(parents=True, exist_ok=True)
        self.log_dir.mkdir(parents=True, exist_ok=True)


def _find_config(explicit: str | Path | None) -> Path | None:
    candidates: list[Path] = []
    if explicit:
        candidates.append(Path(explicit).expanduser())
    elif env := os.environ.get("JARVIS_CONFIG"):
        candidates.append(Path(env).expanduser())
    else:
        candidates.append(Path.cwd() / "jarvis.toml")
        candidates.append(Path.home() / ".config" / "jarvis" / "jarvis.toml")

    for candidate in candidates:
        if candidate.is_file():
            return candidate
    if explicit:
        raise ConfigError(f"Konfigurationsdatei nicht gefunden: {candidates[0]}")
    return None


def load_dotenv(path: Path) -> None:
    """Minimaler ``.env``-Leser — bestehende Variablen werden nie überschrieben."""
    if not path.is_file():
        return
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, value = line.partition("=")
        key = key.strip()
        value = value.strip().strip('"').strip("'")
        if key and key not in os.environ:
            os.environ[key] = value


def default_config_path() -> Path:
    return Path.home() / ".config" / "jarvis" / "jarvis.toml"
