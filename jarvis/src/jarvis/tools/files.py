"""Dateien und Ordner — lesen, analysieren, aufräumen.

Alle Pfade werden gegen ``files.roots`` geprüft. Das Modell kann keinen Pfad
außerhalb dieser Wurzeln anfassen, auch nicht über ``..`` oder Symlinks:
:func:`_safe_path` löst erst vollständig auf und vergleicht danach.
"""

from __future__ import annotations

import hashlib
import shutil
from collections import defaultdict
from datetime import datetime
from pathlib import Path
from typing import Any

from .base import Tool, ToolContext, ToolError, obj, prop

# Endung -> Zielordner beim Aufräumen.
CATEGORIES: dict[str, tuple[str, ...]] = {
    "Bilder": (".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".svg", ".bmp", ".tiff"),
    "Dokumente": (".pdf", ".doc", ".docx", ".odt", ".rtf", ".txt", ".md", ".epub"),
    "Tabellen": (".xls", ".xlsx", ".ods", ".csv", ".tsv"),
    "Praesentationen": (".ppt", ".pptx", ".odp", ".key"),
    "Archive": (".zip", ".tar", ".gz", ".bz2", ".xz", ".7z", ".rar"),
    "Audio": (".mp3", ".wav", ".flac", ".m4a", ".ogg", ".aac"),
    "Video": (".mp4", ".mov", ".avi", ".mkv", ".webm", ".m4v"),
    "Code": (".py", ".js", ".ts", ".tsx", ".java", ".c", ".cpp", ".h", ".rs", ".go",
             ".rb", ".php", ".sh", ".sql", ".json", ".yaml", ".yml", ".toml", ".html", ".css"),
    "Installer": (".dmg", ".pkg", ".exe", ".msi", ".deb", ".rpm", ".appimage"),
}

_EXT_TO_CATEGORY: dict[str, str] = {
    ext: name for name, exts in CATEGORIES.items() for ext in exts
}
OTHER = "Sonstiges"

MAX_READ_CHARS = 40_000


# --------------------------------------------------------------------------- #
# Pfadsicherheit
# --------------------------------------------------------------------------- #


def _roots(ctx: ToolContext) -> list[Path]:
    roots = [r for r in ctx.config.files.roots]
    if not roots:
        raise ToolError(
            "Es ist kein Arbeitsverzeichnis freigegeben. Trage die erlaubten "
            "Ordner unter [files] roots in jarvis.toml ein."
        )
    return roots


def _safe_path(ctx: ToolContext, raw: str, *, must_exist: bool = True) -> Path:
    if not raw or not str(raw).strip():
        raise ToolError("Es wurde kein Pfad angegeben.")
    candidate = Path(str(raw)).expanduser()
    try:
        resolved = candidate.resolve()
    except OSError as exc:  # pragma: no cover - sehr selten
        raise ToolError(f"Pfad '{raw}' lässt sich nicht auflösen: {exc}") from exc

    for root in _roots(ctx):
        try:
            root_resolved = root.resolve()
        except OSError:
            continue
        if resolved == root_resolved or resolved.is_relative_to(root_resolved):
            if must_exist and not resolved.exists():
                raise ToolError(f"'{resolved}' existiert nicht.")
            return resolved

    allowed = ", ".join(str(r) for r in _roots(ctx))
    raise ToolError(
        f"'{resolved}' liegt außerhalb der freigegebenen Ordner. "
        f"Erlaubt sind: {allowed}"
    )


def _human_size(num: float) -> str:
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if abs(num) < 1024.0:
            return f"{num:3.1f} {unit}"
        num /= 1024.0
    return f"{num:.1f} PB"


def _category_for(path: Path) -> str:
    return _EXT_TO_CATEGORY.get(path.suffix.lower(), OTHER)


def _hash_file(path: Path, chunk: int = 1 << 20) -> str | None:
    """SHA-256 in Blöcken — ein Videoschnipsel soll nicht den Speicher sprengen."""
    digest = hashlib.sha256()
    try:
        with path.open("rb") as handle:
            while block := handle.read(chunk):
                digest.update(block)
    except OSError:
        return None
    return digest.hexdigest()


def _unique_target(target: Path) -> Path:
    """Nie überschreiben — bei Namenskollision ``name (2).ext`` erzeugen."""
    if not target.exists():
        return target
    stem, suffix, parent = target.stem, target.suffix, target.parent
    for counter in range(2, 1000):
        candidate = parent / f"{stem} ({counter}){suffix}"
        if not candidate.exists():
            return candidate
    raise ToolError(f"Zu viele Namenskollisionen für '{target.name}'.")


# --------------------------------------------------------------------------- #
# Handler
# --------------------------------------------------------------------------- #


def list_directory(ctx: ToolContext, args: dict[str, Any]) -> str:
    directory = _safe_path(ctx, args["path"])
    if not directory.is_dir():
        raise ToolError(f"'{directory}' ist kein Ordner.")
    pattern = str(args.get("pattern") or "*")
    limit = min(int(args.get("limit") or 100), 500)

    entries = sorted(
        directory.glob(pattern),
        key=lambda p: (p.is_file(), p.name.lower()),
    )
    if not entries:
        return f"'{directory}' enthält nichts, das auf '{pattern}' passt."

    lines = [f"Inhalt von {directory} (Muster '{pattern}'):"]
    for entry in entries[:limit]:
        try:
            stat = entry.stat()
        except OSError:
            continue
        changed = datetime.fromtimestamp(stat.st_mtime).strftime("%Y-%m-%d %H:%M")
        if entry.is_dir():
            lines.append(f"  [Ordner] {entry.name}/  (geändert {changed})")
        else:
            lines.append(
                f"  {entry.name}  ({_human_size(stat.st_size)}, geändert {changed})"
            )
    if len(entries) > limit:
        lines.append(f"  ... und {len(entries) - limit} weitere Einträge.")
    return "\n".join(lines)


def read_text_file(ctx: ToolContext, args: dict[str, Any]) -> str:
    path = _safe_path(ctx, args["path"])
    if not path.is_file():
        raise ToolError(f"'{path}' ist keine Datei.")
    max_chars = min(int(args.get("max_chars") or 8000), MAX_READ_CHARS)
    try:
        text = path.read_text(encoding="utf-8", errors="replace")
    except OSError as exc:
        raise ToolError(f"'{path}' ist nicht lesbar: {exc}") from exc
    if len(text) > max_chars:
        return (
            f"{path} (gekürzt auf {max_chars} von {len(text)} Zeichen):\n\n"
            + text[:max_chars]
        )
    return f"{path}:\n\n{text}"


def directory_report(ctx: ToolContext, args: dict[str, Any]) -> str:
    directory = _safe_path(ctx, args["path"])
    if not directory.is_dir():
        raise ToolError(f"'{directory}' ist kein Ordner.")
    recursive = bool(args.get("recursive", False))
    files = [p for p in (directory.rglob("*") if recursive else directory.iterdir()) if p.is_file()]
    if not files:
        return f"'{directory}' enthält keine Dateien."

    by_category: dict[str, list[Path]] = defaultdict(list)
    total = 0
    for f in files:
        try:
            total += f.stat().st_size
        except OSError:
            continue
        by_category[_category_for(f)].append(f)

    lines = [
        f"Bericht für {directory}{' (rekursiv)' if recursive else ''}:",
        f"  {len(files)} Dateien, zusammen {_human_size(total)}",
        "",
        "Nach Kategorie:",
    ]
    for name, group in sorted(by_category.items(), key=lambda kv: -len(kv[1])):
        size = sum(p.stat().st_size for p in group if p.exists())
        lines.append(f"  {name}: {len(group)} Dateien ({_human_size(size)})")

    biggest = sorted(files, key=lambda p: p.stat().st_size if p.exists() else 0, reverse=True)[:5]
    lines += ["", "Größte Dateien:"]
    lines += [f"  {p.name} ({_human_size(p.stat().st_size)})" for p in biggest if p.exists()]

    oldest = sorted(files, key=lambda p: p.stat().st_mtime if p.exists() else 0)[:5]
    lines += ["", "Am längsten unangetastet:"]
    for p in oldest:
        if not p.exists():
            continue
        age = datetime.fromtimestamp(p.stat().st_mtime).strftime("%Y-%m-%d")
        lines.append(f"  {p.name} (zuletzt geändert {age})")
    return "\n".join(lines)


def organize_directory(ctx: ToolContext, args: dict[str, Any]) -> str:
    directory = _safe_path(ctx, args["path"])
    if not directory.is_dir():
        raise ToolError(f"'{directory}' ist kein Ordner.")
    mode = str(args.get("mode") or "type")
    if mode not in ("type", "date"):
        raise ToolError("mode muss 'type' oder 'date' sein.")
    dry_run = bool(args.get("dry_run", True))

    # Nur die lose herumliegenden Dateien direkt im Ordner, keine Unterordner
    # und keine versteckten Dateien.
    plan: list[tuple[Path, Path]] = []
    for entry in sorted(directory.iterdir()):
        if not entry.is_file() or entry.name.startswith("."):
            continue
        if mode == "type":
            folder = _category_for(entry)
        else:
            folder = datetime.fromtimestamp(entry.stat().st_mtime).strftime("%Y-%m")
        plan.append((entry, directory / folder / entry.name))

    if not plan:
        return f"In '{directory}' gibt es nichts einzusortieren."

    if dry_run:
        lines = [
            f"Vorschau ({len(plan)} Dateien würden verschoben, nichts wurde geändert):"
        ]
        grouped: dict[str, int] = defaultdict(int)
        for _, target in plan:
            grouped[target.parent.name] += 1
        for folder, count in sorted(grouped.items()):
            lines.append(f"  -> {folder}/: {count} Dateien")
        lines.append("")
        lines.append("Beispiele:")
        for source, target in plan[:8]:
            lines.append(f"  {source.name}  ->  {target.parent.name}/")
        lines.append("")
        lines.append("Zum Ausführen dasselbe Werkzeug mit dry_run=false aufrufen.")
        return "\n".join(lines)

    moved, failed = 0, []
    for source, target in plan:
        try:
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.move(str(source), str(_unique_target(target)))
            moved += 1
        except OSError as exc:
            failed.append(f"{source.name}: {exc}")

    result = f"{moved} von {len(plan)} Dateien in '{directory}' einsortiert."
    if failed:
        result += "\nNicht verschoben:\n" + "\n".join(f"  {f}" for f in failed[:10])
    return result


def move_file(ctx: ToolContext, args: dict[str, Any]) -> str:
    source = _safe_path(ctx, args["source"])
    target = _safe_path(ctx, args["destination"], must_exist=False)
    if target.is_dir():
        target = target / source.name
    target = _unique_target(target)
    try:
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.move(str(source), str(target))
    except OSError as exc:
        raise ToolError(f"Verschieben fehlgeschlagen: {exc}") from exc
    return f"'{source}' verschoben nach '{target}'."


def find_duplicates(ctx: ToolContext, args: dict[str, Any]) -> str:
    directory = _safe_path(ctx, args["path"])
    if not directory.is_dir():
        raise ToolError(f"'{directory}' ist kein Ordner.")
    files = [p for p in directory.rglob("*") if p.is_file()]

    # Erst nach Größe gruppieren — nur bei gleicher Größe lohnt das Hashen.
    by_size: dict[int, list[Path]] = defaultdict(list)
    for f in files:
        try:
            by_size[f.stat().st_size].append(f)
        except OSError:
            continue

    duplicates: dict[str, list[Path]] = defaultdict(list)
    for size, group in by_size.items():
        if len(group) < 2 or size == 0:
            continue
        for f in group:
            digest = _hash_file(f)
            if digest is None:
                continue
            duplicates[digest].append(f)

    real = {d: g for d, g in duplicates.items() if len(g) > 1}
    if not real:
        return f"Keine inhaltsgleichen Dateien in '{directory}' gefunden."

    wasted = sum(
        (len(g) - 1) * g[0].stat().st_size for g in real.values() if g[0].exists()
    )
    lines = [
        f"{len(real)} Gruppen inhaltsgleicher Dateien in '{directory}' "
        f"(unnötig belegt: {_human_size(wasted)}):"
    ]
    for group in list(real.values())[:20]:
        lines.append(f"  {_human_size(group[0].stat().st_size)} je Datei:")
        lines += [f"    {p}" for p in group]
    lines.append("")
    lines.append("Gelöscht wird nichts automatisch — sag mir, welche Kopie weg soll.")
    return "\n".join(lines)


# --------------------------------------------------------------------------- #
# Registrierung
# --------------------------------------------------------------------------- #


def build_tools() -> list[Tool]:
    path_prop = prop("string", "Absoluter Pfad innerhalb der freigegebenen Ordner.")
    return [
        Tool(
            name="list_directory",
            description=(
                "Listet den Inhalt eines Ordners mit Größe und Änderungsdatum. "
                "Nutze das, bevor du irgendetwas verschiebst, damit du weißt, "
                "womit du es zu tun hast."
            ),
            input_schema=obj(
                {
                    "path": path_prop,
                    "pattern": prop("string", "Glob-Muster, z.B. '*.pdf'. Standard '*'."),
                    "limit": prop("integer", "Maximale Zahl an Einträgen (Standard 100)."),
                },
                ["path"],
            ),
            risk="read",
            handler=list_directory,
            summarize=lambda a: f"Ordner {a.get('path')} auflisten",
        ),
        Tool(
            name="read_text_file",
            description=(
                "Liest eine Textdatei (txt, md, csv, Quellcode, Konfiguration). "
                "Für Binärformate wie PDF oder DOCX ungeeignet."
            ),
            input_schema=obj(
                {
                    "path": path_prop,
                    "max_chars": prop("integer", "Maximale Zeichenzahl (Standard 8000)."),
                },
                ["path"],
            ),
            risk="read",
            handler=read_text_file,
            summarize=lambda a: f"Datei {a.get('path')} lesen",
        ),
        Tool(
            name="directory_report",
            description=(
                "Fasst einen Ordner zusammen: Anzahl und Größe nach Kategorie, "
                "größte Dateien, am längsten unangetastete Dateien. Der richtige "
                "erste Schritt für 'räum mal auf'."
            ),
            input_schema=obj(
                {
                    "path": path_prop,
                    "recursive": prop("boolean", "Unterordner einbeziehen (Standard false)."),
                },
                ["path"],
            ),
            risk="read",
            handler=directory_report,
            summarize=lambda a: f"Bericht über {a.get('path')}",
        ),
        Tool(
            name="organize_directory",
            description=(
                "Sortiert lose Dateien eines Ordners in Unterordner ein — nach "
                "Dateityp (Bilder, Dokumente, ...) oder nach Monat. "
                "Mit dry_run=true (Standard) wird nur der Plan gezeigt und nichts "
                "verändert; zeige dem Nutzer immer erst diesen Plan. Bestehende "
                "Dateien werden nie überschrieben."
            ),
            input_schema=obj(
                {
                    "path": path_prop,
                    "mode": prop(
                        "string",
                        "'type' sortiert nach Dateityp, 'date' nach Änderungsmonat.",
                        enum=["type", "date"],
                    ),
                    "dry_run": prop(
                        "boolean",
                        "true = nur Vorschau (Standard), false = wirklich verschieben.",
                    ),
                },
                ["path"],
            ),
            risk="write",
            handler=organize_directory,
            summarize=lambda a: (
                f"{a.get('path')} nach {a.get('mode', 'type')} sortieren"
                + ("" if a.get("dry_run", True) is False else " (nur Vorschau)")
            ),
        ),
        Tool(
            name="move_file",
            description=(
                "Verschiebt oder benennt eine einzelne Datei um. Ziel darf ein "
                "Ordner oder ein vollständiger Dateipfad sein. Überschreibt nie."
            ),
            input_schema=obj(
                {
                    "source": prop("string", "Zu verschiebende Datei."),
                    "destination": prop("string", "Zielordner oder Zieldateipfad."),
                },
                ["source", "destination"],
            ),
            risk="write",
            handler=move_file,
            summarize=lambda a: f"{a.get('source')} -> {a.get('destination')}",
        ),
        Tool(
            name="find_duplicates",
            description=(
                "Findet inhaltsgleiche Dateien (Vergleich per SHA-256) und zeigt, "
                "wie viel Platz die Kopien belegen. Löscht selbst nichts."
            ),
            input_schema=obj({"path": path_prop}, ["path"]),
            risk="read",
            handler=find_duplicates,
            summarize=lambda a: f"Duplikate in {a.get('path')} suchen",
        ),
    ]
