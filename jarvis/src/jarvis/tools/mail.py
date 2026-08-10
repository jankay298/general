"""E-Mail über IMAP und SMTP — provider-unabhängig, mit GMX als Standard.

Bewusste Trennung der Risikoklassen:

* Lesen, suchen, zusammenfassen          -> ``read``
* Verschieben, markieren, Entwurf ablegen -> ``write``
* **Versenden**                          -> ``external``

Der Entwurf ist der wichtigste Weg: Jarvis formuliert, legt in "Entwürfe" ab,
du liest gegen und schickst selbst los. Versenden geht nur mit ausdrücklicher
Freigabe.
"""

from __future__ import annotations

import email
import imaplib
import re
import smtplib
import ssl
from contextlib import contextmanager
from datetime import datetime, timedelta
from email.header import decode_header, make_header
from email.message import EmailMessage
from email.utils import parseaddr
from typing import Any, Iterator

from .base import Tool, ToolContext, ToolError, obj, prop

MAX_BODY_CHARS = 12_000
_TAG_RE = re.compile(r"<[^>]+>")


# --------------------------------------------------------------------------- #
# Verbindung
# --------------------------------------------------------------------------- #


def _require_mail(ctx: ToolContext):
    cfg = ctx.config.mail
    if not cfg.enabled:
        raise ToolError(
            "Das Postfach ist nicht eingerichtet. Setze [mail] enabled = true "
            "in jarvis.toml und hinterlege das Passwort in der Umgebungsvariable "
            f"{cfg.password_env}."
        )
    if not cfg.password:
        raise ToolError(
            f"Die Umgebungsvariable {cfg.password_env} ist leer. Lege dort das "
            "Mail-Passwort (bei GMX: das App-Passwort für IMAP/SMTP) ab."
        )
    return cfg


@contextmanager
def _imap(ctx: ToolContext) -> Iterator[imaplib.IMAP4_SSL]:
    cfg = _require_mail(ctx)
    try:
        conn = imaplib.IMAP4_SSL(cfg.imap_host, cfg.imap_port)
        conn.login(cfg.user, cfg.password)
    except (imaplib.IMAP4.error, OSError) as exc:
        raise ToolError(
            f"IMAP-Verbindung zu {cfg.imap_host} fehlgeschlagen: {exc}. "
            "Prüfe Host, Benutzername und ob IMAP im Postfach freigeschaltet ist."
        ) from exc
    try:
        yield conn
    finally:
        try:
            conn.logout()
        except Exception:  # pragma: no cover - Abbau darf nie die Aktion kippen
            pass


def _select(conn: imaplib.IMAP4_SSL, folder: str, readonly: bool = True) -> None:
    status, _ = conn.select(f'"{folder}"', readonly=readonly)
    if status != "OK":
        raise ToolError(
            f"Ordner '{folder}' konnte nicht geöffnet werden. "
            "Mit mail_folders siehst du die tatsächlichen Ordnernamen."
        )


def _decode(value: str | None) -> str:
    if not value:
        return ""
    try:
        return str(make_header(decode_header(value)))
    except Exception:
        return value


def _body_text(msg: email.message.Message) -> str:
    """Bevorzugt text/plain; fällt sonst auf entschärftes HTML zurück."""
    plain: list[str] = []
    html: list[str] = []
    for part in msg.walk():
        if part.get_content_maintype() == "multipart":
            continue
        disposition = str(part.get("Content-Disposition") or "")
        if "attachment" in disposition.lower():
            continue
        try:
            payload = part.get_payload(decode=True)
        except Exception:
            continue
        if payload is None:
            continue
        charset = part.get_content_charset() or "utf-8"
        try:
            text = payload.decode(charset, errors="replace")
        except LookupError:
            text = payload.decode("utf-8", errors="replace")
        if part.get_content_type() == "text/plain":
            plain.append(text)
        elif part.get_content_type() == "text/html":
            html.append(text)

    if plain:
        return "\n".join(plain).strip()
    if html:
        stripped = _TAG_RE.sub(" ", "\n".join(html))
        return re.sub(r"[ \t]{2,}", " ", stripped).strip()
    return ""


def _attachments(msg: email.message.Message) -> list[str]:
    names = []
    for part in msg.walk():
        disposition = str(part.get("Content-Disposition") or "")
        if "attachment" in disposition.lower():
            names.append(_decode(part.get_filename()) or "(ohne Namen)")
    return names


def _escape_imap(value: str) -> bytes:
    """Suchbegriff als UTF-8 mit maskierten Sonderzeichen für ein IMAP-Literal."""
    return (
        value.replace("\\", "\\\\").replace('"', '\\"').encode("utf-8")
    )


def _fetch_headers(conn: imaplib.IMAP4_SSL, uid: bytes) -> dict[str, str]:
    status, data = conn.uid("FETCH", uid, "(BODY.PEEK[HEADER])")
    if status != "OK" or not data or not isinstance(data[0], tuple):
        return {}
    msg = email.message_from_bytes(data[0][1])
    return {
        "from": _decode(msg.get("From")),
        "to": _decode(msg.get("To")),
        "subject": _decode(msg.get("Subject")) or "(kein Betreff)",
        "date": _decode(msg.get("Date")),
    }


# --------------------------------------------------------------------------- #
# Handler
# --------------------------------------------------------------------------- #


def mail_folders(ctx: ToolContext, args: dict[str, Any]) -> str:
    with _imap(ctx) as conn:
        status, data = conn.list()
        if status != "OK":
            raise ToolError("Ordnerliste konnte nicht gelesen werden.")
        names = []
        for raw in data or []:
            if not isinstance(raw, bytes):
                continue
            decoded = raw.decode("utf-8", errors="replace")
            match = re.search(r'"([^"]*)"\s*$', decoded) or re.search(r"(\S+)\s*$", decoded)
            if match:
                names.append(match.group(1))
    return "Ordner im Postfach:\n" + "\n".join(f"  {n}" for n in names)


def mail_list(ctx: ToolContext, args: dict[str, Any]) -> str:
    cfg = _require_mail(ctx)
    folder = str(args.get("folder") or cfg.inbox)
    limit = min(int(args.get("limit") or 15), 60)
    unseen_only = bool(args.get("unseen_only", False))
    since_days = args.get("since_days")

    criteria: list[str] = []
    if unseen_only:
        criteria.append("UNSEEN")
    if since_days:
        since = (datetime.now() - timedelta(days=int(since_days))).strftime("%d-%b-%Y")
        criteria += ["SINCE", since]
    if not criteria:
        criteria = ["ALL"]

    with _imap(ctx) as conn:
        _select(conn, folder)
        status, data = conn.uid("SEARCH", None, *criteria)
        if status != "OK":
            raise ToolError(f"Suche in '{folder}' fehlgeschlagen.")
        uids = (data[0] or b"").split()
        if not uids:
            return f"In '{folder}' passt nichts auf diese Kriterien."
        selected = uids[-limit:][::-1]
        rows = []
        for uid in selected:
            headers = _fetch_headers(conn, uid)
            if not headers:
                continue
            sender = parseaddr(headers.get("from", ""))[1] or headers.get("from", "")
            rows.append(
                f"  uid={uid.decode()}  {headers.get('date', '')[:31]:<31} "
                f"{sender[:34]:<34} {headers.get('subject', '')}"
            )

    header = f"{len(uids)} Nachrichten in '{folder}'"
    if unseen_only:
        header += " (ungelesen)"
    header += f", die {len(rows)} neuesten:"
    return header + "\n" + "\n".join(rows)


def mail_read(ctx: ToolContext, args: dict[str, Any]) -> str:
    cfg = _require_mail(ctx)
    folder = str(args.get("folder") or cfg.inbox)
    uid = str(args["uid"]).strip()
    with _imap(ctx) as conn:
        _select(conn, folder)
        status, data = conn.uid("FETCH", uid, "(BODY.PEEK[])")
        if status != "OK" or not data or not isinstance(data[0], tuple):
            raise ToolError(f"Nachricht uid={uid} in '{folder}' nicht gefunden.")
        msg = email.message_from_bytes(data[0][1])

    body = _body_text(msg)
    if len(body) > MAX_BODY_CHARS:
        body = body[:MAX_BODY_CHARS] + "\n[... gekürzt ...]"
    files = _attachments(msg)
    lines = [
        f"Von:     {_decode(msg.get('From'))}",
        f"An:      {_decode(msg.get('To'))}",
        f"Datum:   {_decode(msg.get('Date'))}",
        f"Betreff: {_decode(msg.get('Subject'))}",
    ]
    if files:
        lines.append(f"Anhänge: {', '.join(files)}")
    lines += ["", body or "(kein Textinhalt)"]
    return "\n".join(lines)


def mail_search(ctx: ToolContext, args: dict[str, Any]) -> str:
    cfg = _require_mail(ctx)
    folder = str(args.get("folder") or cfg.inbox)
    limit = min(int(args.get("limit") or 15), 60)
    field = str(args.get("field") or "TEXT").upper()
    if field not in ("TEXT", "FROM", "SUBJECT", "TO", "BODY"):
        raise ToolError("field muss TEXT, BODY, FROM, TO oder SUBJECT sein.")
    query = str(args["query"])

    # imaplib kodiert str-Argumente als ASCII — ein "Grüße" im Suchbegriff
    # würde also schon im Client scheitern. Deshalb den Begriff selbst als
    # UTF-8-Bytes übergeben und dem Server per CHARSET sagen, wie er sie liest.
    needle = b'"' + _escape_imap(query) + b'"'

    with _imap(ctx) as conn:
        _select(conn, folder)
        try:
            status, data = conn.uid("SEARCH", "CHARSET", "UTF-8", field, needle)
        except imaplib.IMAP4.error:
            status, data = "NO", [b""]
        if status != "OK":
            # Nicht jeder Server versteht CHARSET; dann ohne, was für reine
            # ASCII-Begriffe genügt.
            try:
                status, data = conn.uid("SEARCH", None, field, needle)
            except imaplib.IMAP4.error as exc:
                raise ToolError(f"Suche nach '{query}' fehlgeschlagen: {exc}") from exc
        if status != "OK":
            raise ToolError(f"Suche nach '{query}' fehlgeschlagen.")
        uids = (data[0] or b"").split()
        if not uids:
            return f"Keine Treffer für '{query}' in '{folder}' ({field})."
        rows = []
        for uid in uids[-limit:][::-1]:
            headers = _fetch_headers(conn, uid)
            if not headers:
                continue
            sender = parseaddr(headers.get("from", ""))[1] or headers.get("from", "")
            rows.append(
                f"  uid={uid.decode()}  {headers.get('date', '')[:31]:<31} "
                f"{sender[:34]:<34} {headers.get('subject', '')}"
            )
    return f"{len(uids)} Treffer für '{query}' in '{folder}':\n" + "\n".join(rows)


def mail_move(ctx: ToolContext, args: dict[str, Any]) -> str:
    cfg = _require_mail(ctx)
    folder = str(args.get("folder") or cfg.inbox)
    target = str(args["target_folder"])
    uid = str(args["uid"]).strip()
    with _imap(ctx) as conn:
        _select(conn, folder, readonly=False)
        status, _ = conn.uid("COPY", uid, f'"{target}"')
        if status != "OK":
            raise ToolError(
                f"Kopieren nach '{target}' fehlgeschlagen — gibt es den Ordner? "
                "mail_folders zeigt die vorhandenen Namen."
            )
        conn.uid("STORE", uid, "+FLAGS", "(\\Deleted)")
        conn.expunge()
    return f"Nachricht uid={uid} von '{folder}' nach '{target}' verschoben."


_FLAGS = {
    "gelesen": ("+FLAGS", "\\Seen"),
    "ungelesen": ("-FLAGS", "\\Seen"),
    "markiert": ("+FLAGS", "\\Flagged"),
    "unmarkiert": ("-FLAGS", "\\Flagged"),
}


def mail_flag(ctx: ToolContext, args: dict[str, Any]) -> str:
    cfg = _require_mail(ctx)
    folder = str(args.get("folder") or cfg.inbox)
    uid = str(args["uid"]).strip()
    flag = str(args["flag"])
    if flag not in _FLAGS:
        raise ToolError(f"flag muss eines von {', '.join(_FLAGS)} sein.")
    op, value = _FLAGS[flag]
    with _imap(ctx) as conn:
        _select(conn, folder, readonly=False)
        status, _ = conn.uid("STORE", uid, op, f"({value})")
        if status != "OK":
            raise ToolError(f"Markierung für uid={uid} fehlgeschlagen.")
    return f"Nachricht uid={uid} als '{flag}' markiert."


def _compose(cfg, args: dict[str, Any]) -> EmailMessage:
    msg = EmailMessage()
    msg["From"] = cfg.from_address or cfg.user
    msg["To"] = str(args["to"])
    if args.get("cc"):
        msg["Cc"] = str(args["cc"])
    msg["Subject"] = str(args["subject"])
    body = str(args["body"])
    if cfg.signature:
        body = body.rstrip() + "\n\n" + cfg.signature
    msg.set_content(body)
    return msg


def mail_draft(ctx: ToolContext, args: dict[str, Any]) -> str:
    cfg = _require_mail(ctx)
    msg = _compose(cfg, args)
    folder = str(args.get("folder") or cfg.drafts)
    with _imap(ctx) as conn:
        status, _ = conn.append(
            f'"{folder}"', "\\Draft", imaplib.Time2Internaldate(datetime.now()), msg.as_bytes()
        )
        if status != "OK":
            raise ToolError(
                f"Entwurf konnte nicht in '{folder}' abgelegt werden. "
                "Prüfe mit mail_folders den richtigen Ordnernamen."
            )
    return (
        f"Entwurf an {msg['To']} mit Betreff '{msg['Subject']}' liegt in "
        f"'{folder}'. Er wurde NICHT versendet — lies ihn gegen und schick ihn "
        "aus deinem Mailprogramm ab."
    )


def mail_send(ctx: ToolContext, args: dict[str, Any]) -> str:
    cfg = _require_mail(ctx)
    if not cfg.smtp_host:
        raise ToolError("Für den Versand fehlt [mail] smtp_host in jarvis.toml.")
    msg = _compose(cfg, args)
    recipients = [a for a in [msg["To"], msg.get("Cc")] if a]
    try:
        context = ssl.create_default_context()
        if cfg.smtp_port == 465:
            with smtplib.SMTP_SSL(cfg.smtp_host, cfg.smtp_port, context=context) as smtp:
                smtp.login(cfg.user, cfg.password)
                smtp.send_message(msg)
        else:
            with smtplib.SMTP(cfg.smtp_host, cfg.smtp_port) as smtp:
                smtp.starttls(context=context)
                smtp.login(cfg.user, cfg.password)
                smtp.send_message(msg)
    except (smtplib.SMTPException, OSError) as exc:
        raise ToolError(f"Versand über {cfg.smtp_host} fehlgeschlagen: {exc}") from exc
    return f"E-Mail an {', '.join(recipients)} wurde versendet."


# --------------------------------------------------------------------------- #
# Registrierung
# --------------------------------------------------------------------------- #


def build_tools() -> list[Tool]:
    folder_prop = prop("string", "Ordnername, Standard ist der Posteingang.")
    uid_prop = prop("string", "UID aus mail_list oder mail_search.")
    return [
        Tool(
            name="mail_folders",
            description="Zeigt die Ordnernamen des Postfachs, so wie der Server sie führt.",
            input_schema=obj({}),
            risk="read",
            handler=mail_folders,
            summarize=lambda a: "Ordnerliste abrufen",
        ),
        Tool(
            name="mail_list",
            description=(
                "Listet die neuesten Nachrichten eines Ordners mit UID, Datum, "
                "Absender und Betreff. Der Einstieg für jede Postfach-Frage."
            ),
            input_schema=obj(
                {
                    "folder": folder_prop,
                    "limit": prop("integer", "Anzahl Nachrichten (Standard 15, max 60)."),
                    "unseen_only": prop("boolean", "Nur ungelesene Nachrichten."),
                    "since_days": prop("integer", "Nur Nachrichten der letzten N Tage."),
                }
            ),
            risk="read",
            handler=mail_list,
            summarize=lambda a: f"Postfach '{a.get('folder', 'INBOX')}' auflisten",
        ),
        Tool(
            name="mail_read",
            description=(
                "Liest eine einzelne Nachricht vollständig, inklusive Textinhalt "
                "und Anhangsnamen. Ändert den Gelesen-Status nicht."
            ),
            input_schema=obj({"uid": uid_prop, "folder": folder_prop}, ["uid"]),
            risk="read",
            handler=mail_read,
            summarize=lambda a: f"Nachricht uid={a.get('uid')} lesen",
        ),
        Tool(
            name="mail_search",
            description=(
                "Durchsucht einen Ordner serverseitig nach Absender, Betreff oder "
                "Volltext."
            ),
            input_schema=obj(
                {
                    "query": prop("string", "Suchbegriff."),
                    "field": prop(
                        "string",
                        "Wo gesucht wird. Standard TEXT (alles).",
                        enum=["TEXT", "BODY", "FROM", "TO", "SUBJECT"],
                    ),
                    "folder": folder_prop,
                    "limit": prop("integer", "Anzahl Treffer (Standard 15)."),
                },
                ["query"],
            ),
            risk="read",
            handler=mail_search,
            summarize=lambda a: f"Postfach nach '{a.get('query')}' durchsuchen",
        ),
        Tool(
            name="mail_move",
            description=(
                "Verschiebt eine Nachricht in einen anderen Ordner — das Werkzeug "
                "zum Sortieren und Archivieren."
            ),
            input_schema=obj(
                {
                    "uid": uid_prop,
                    "target_folder": prop("string", "Zielordner."),
                    "folder": folder_prop,
                },
                ["uid", "target_folder"],
            ),
            risk="write",
            handler=mail_move,
            summarize=lambda a: f"uid={a.get('uid')} nach '{a.get('target_folder')}'",
        ),
        Tool(
            name="mail_flag",
            description="Markiert eine Nachricht als gelesen, ungelesen oder wichtig.",
            input_schema=obj(
                {
                    "uid": uid_prop,
                    "flag": prop(
                        "string",
                        "Zu setzende Markierung.",
                        enum=list(_FLAGS),
                    ),
                    "folder": folder_prop,
                },
                ["uid", "flag"],
            ),
            risk="write",
            handler=mail_flag,
            summarize=lambda a: f"uid={a.get('uid')} als '{a.get('flag')}' markieren",
        ),
        Tool(
            name="mail_draft",
            description=(
                "Legt eine fertig formulierte Antwort als Entwurf im Postfach ab, "
                "ohne sie zu senden. Das ist der Standardweg für Antworten: der "
                "Nutzer liest gegen und schickt selbst ab."
            ),
            input_schema=obj(
                {
                    "to": prop("string", "Empfängeradresse."),
                    "subject": prop("string", "Betreff."),
                    "body": prop("string", "Nachrichtentext ohne Signatur."),
                    "cc": prop("string", "Optionale Kopie-Adresse."),
                    "folder": prop("string", "Entwurfsordner, Standard aus der Konfiguration."),
                },
                ["to", "subject", "body"],
            ),
            risk="write",
            handler=mail_draft,
            summarize=lambda a: f"Entwurf an {a.get('to')}: {a.get('subject')}",
        ),
        Tool(
            name="mail_send",
            description=(
                "Versendet eine E-Mail sofort. Nicht rückgängig zu machen. "
                "Nutze im Zweifel mail_draft; mail_send nur, wenn der Nutzer den "
                "Versand ausdrücklich verlangt hat."
            ),
            input_schema=obj(
                {
                    "to": prop("string", "Empfängeradresse."),
                    "subject": prop("string", "Betreff."),
                    "body": prop("string", "Nachrichtentext ohne Signatur."),
                    "cc": prop("string", "Optionale Kopie-Adresse."),
                },
                ["to", "subject", "body"],
            ),
            risk="external",
            handler=mail_send,
            summarize=lambda a: f"E-MAIL SENDEN an {a.get('to')}: {a.get('subject')}",
        ),
    ]
