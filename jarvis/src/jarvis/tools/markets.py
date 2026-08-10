"""Finanzmärkte beobachten — Kurse, Verlauf, Watchlist, Schwellwerte.

Bewusste Grenze: dieses Modul **beobachtet und rechnet, es handelt nicht**.
Es gibt keine Order-Funktion und keine Broker-Anbindung. Zwischen "Kurse lesen"
und "Geld bewegen" liegt ein Risikosprung, den ein Sprachmodell nicht ohne
ausdrückliche, separat gebaute Absicherung überspringen sollte; wie eine solche
Erweiterung aussehen müsste, steht in der README.

Datenquelle ist die öffentliche Yahoo-Finance-Chart-Schnittstelle, mit Stooq
als Rückfallebene. Beides ohne Schlüssel, beides ohne Zusage auf Verfügbarkeit
oder Richtigkeit — für Entscheidungen mit echtem Geld gehört eine bezahlte
Quelle dahinter.
"""

from __future__ import annotations

import json
import os
import statistics
from datetime import datetime
from pathlib import Path
from typing import Any

import httpx

from .base import Tool, ToolContext, ToolError, obj, prop

YAHOO_CHART = "https://query1.finance.yahoo.com/v8/finance/chart/{symbol}"
STOOQ_QUOTE = "https://stooq.com/q/l/"
USER_AGENT = "Mozilla/5.0 (compatible; jarvis-agent/1.0)"
TIMEOUT = 15.0

VALID_RANGES = ("1d", "5d", "1mo", "3mo", "6mo", "1y", "2y", "5y", "max")
VALID_INTERVALS = ("1m", "5m", "15m", "1h", "1d", "1wk", "1mo")


def _client() -> httpx.Client:
    # Ein eigenes CA-Bundle (Firmenproxy) wird respektiert, wenn gesetzt.
    verify: Any = True
    for env in ("SSL_CERT_FILE", "REQUESTS_CA_BUNDLE"):
        bundle = os.environ.get(env)
        if bundle and Path(bundle).is_file():
            verify = bundle
            break
    return httpx.Client(
        timeout=TIMEOUT,
        headers={"User-Agent": USER_AGENT},
        follow_redirects=True,
        verify=verify,
    )


# --------------------------------------------------------------------------- #
# Kursabruf
# --------------------------------------------------------------------------- #


def _fetch_chart(client: httpx.Client, symbol: str, rng: str, interval: str) -> dict[str, Any]:
    try:
        response = client.get(
            YAHOO_CHART.format(symbol=symbol),
            params={"range": rng, "interval": interval},
        )
        response.raise_for_status()
        payload = response.json()
    except (httpx.HTTPError, json.JSONDecodeError) as exc:
        raise ToolError(
            f"Kursabruf für '{symbol}' fehlgeschlagen: {exc}. "
            "Prüfe die Internetverbindung oder das Kürzel."
        ) from exc

    chart = (payload or {}).get("chart") or {}
    if chart.get("error"):
        message = (chart["error"] or {}).get("description") or chart["error"]
        raise ToolError(f"'{symbol}' liefert einen Fehler: {message}")
    results = chart.get("result") or []
    if not results:
        raise ToolError(
            f"Für '{symbol}' gibt es keine Daten. Yahoo-Kürzel sind z.B. "
            "AAPL, SAP.DE, ^GDAXI, BTC-EUR."
        )
    return results[0]


def _quote_line(result: dict[str, Any]) -> str:
    meta = result.get("meta") or {}
    symbol = meta.get("symbol", "?")
    price = meta.get("regularMarketPrice")
    previous = meta.get("chartPreviousClose") or meta.get("previousClose")
    currency = meta.get("currency", "")

    if price is None:
        return f"  {symbol}: kein aktueller Kurs verfügbar"

    line = f"  {symbol:<12} {price:>12,.2f} {currency}"
    if previous:
        change = price - previous
        pct = change / previous * 100
        arrow = "▲" if change > 0 else ("▼" if change < 0 else "•")
        line += f"   {arrow} {change:+,.2f} ({pct:+.2f}%)"
    ts = meta.get("regularMarketTime")
    if ts:
        line += f"   Stand {datetime.fromtimestamp(ts).strftime('%d.%m. %H:%M')}"
    return line


def _stooq_fallback(client: httpx.Client, symbol: str) -> str | None:
    """Sehr einfache Rückfallebene für US-Aktien, wenn Yahoo nicht antwortet."""
    candidate = symbol.lower()
    if "." not in candidate and not candidate.startswith("^"):
        candidate = f"{candidate}.us"
    try:
        response = client.get(
            STOOQ_QUOTE, params={"s": candidate, "f": "sd2t2ohlc", "h": "", "e": "csv"}
        )
        response.raise_for_status()
        rows = response.text.strip().splitlines()
    except httpx.HTTPError:
        return None
    if len(rows) < 2:
        return None
    fields = rows[1].split(",")
    if len(fields) < 7 or fields[6] in ("N/D", ""):
        return None
    return f"  {symbol:<12} {float(fields[6]):>12,.2f}   (Quelle Stooq, {fields[1]})"


def market_quote(ctx: ToolContext, args: dict[str, Any]) -> str:
    symbols = args.get("symbols") or []
    if isinstance(symbols, str):
        symbols = [symbols]
    symbols = [str(s).strip() for s in symbols if str(s).strip()][:20]
    if not symbols:
        raise ToolError("Es wurde kein Kürzel angegeben.")

    lines = ["Aktuelle Kurse:"]
    with _client() as client:
        for symbol in symbols:
            try:
                lines.append(_quote_line(_fetch_chart(client, symbol, "5d", "1d")))
            except ToolError as exc:
                fallback = _stooq_fallback(client, symbol)
                lines.append(fallback or f"  {symbol}: {exc}")
    return "\n".join(lines)


def market_history(ctx: ToolContext, args: dict[str, Any]) -> str:
    symbol = str(args["symbol"]).strip()
    rng = str(args.get("range") or "3mo")
    interval = str(args.get("interval") or "1d")
    if rng not in VALID_RANGES:
        raise ToolError(f"range muss eines von {', '.join(VALID_RANGES)} sein.")
    if interval not in VALID_INTERVALS:
        raise ToolError(f"interval muss eines von {', '.join(VALID_INTERVALS)} sein.")

    with _client() as client:
        result = _fetch_chart(client, symbol, rng, interval)

    meta = result.get("meta") or {}
    quotes = ((result.get("indicators") or {}).get("quote") or [{}])[0]
    closes = [c for c in (quotes.get("close") or []) if c is not None]
    if len(closes) < 2:
        raise ToolError(f"Für '{symbol}' liegen im Zeitraum {rng} zu wenig Daten vor.")

    first, last = closes[0], closes[-1]
    change = last - first
    pct = change / first * 100
    currency = meta.get("currency", "")

    lines = [
        f"{meta.get('symbol', symbol)} — Zeitraum {rng}, Takt {interval}",
        f"  Start:      {first:,.2f} {currency}",
        f"  Aktuell:    {last:,.2f} {currency}",
        f"  Veränderung:{change:+,.2f} ({pct:+.2f}%)",
        f"  Hoch/Tief:  {max(closes):,.2f} / {min(closes):,.2f}",
        f"  Datenpunkte:{len(closes)}",
    ]
    if len(closes) >= 20:
        lines.append(f"  SMA20:      {statistics.fmean(closes[-20:]):,.2f}")
    if len(closes) >= 50:
        lines.append(f"  SMA50:      {statistics.fmean(closes[-50:]):,.2f}")
    if len(closes) >= 20:
        returns = [
            (closes[i] - closes[i - 1]) / closes[i - 1] for i in range(1, len(closes))
        ]
        lines.append(
            f"  Schwankung: {statistics.pstdev(returns) * 100:.2f}% je Periode"
        )
    lines.append("")
    lines.append(
        "Das sind reine Kennzahlen, keine Anlageempfehlung — Kurse der "
        "Vergangenheit sagen nichts über die Zukunft."
    )
    return "\n".join(lines)


# --------------------------------------------------------------------------- #
# Watchlist
# --------------------------------------------------------------------------- #


def _load_watchlist(ctx: ToolContext) -> list[dict[str, Any]]:
    path = ctx.config.watchlist_path
    if not path.is_file():
        seed = [dict(e) for e in ctx.config.markets.seed_watchlist]
        if seed:
            _save_watchlist(ctx, seed)
        return seed
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return []
    return data if isinstance(data, list) else []


def _save_watchlist(ctx: ToolContext, entries: list[dict[str, Any]]) -> None:
    path = ctx.config.watchlist_path
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(entries, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )


def watchlist_show(ctx: ToolContext, args: dict[str, Any]) -> str:
    entries = _load_watchlist(ctx)
    if not entries:
        return (
            "Die Watchlist ist leer. Mit watchlist_add kannst du Werte "
            "aufnehmen, optional mit Schwellwerten für Alarme."
        )

    lines = ["Watchlist:"]
    alerts: list[str] = []
    with _client() as client:
        for entry in entries:
            symbol = str(entry.get("symbol", "")).strip()
            if not symbol:
                continue
            label = str(entry.get("label") or symbol)
            try:
                result = _fetch_chart(client, symbol, "5d", "1d")
            except ToolError as exc:
                lines.append(f"  {label} ({symbol}): {exc}")
                continue
            lines.append(f"  {label}:")
            lines.append("  " + _quote_line(result))

            price = (result.get("meta") or {}).get("regularMarketPrice")
            if price is None:
                continue
            above, below = entry.get("alert_above"), entry.get("alert_below")
            if above is not None and price >= float(above):
                alerts.append(f"  {label} ({symbol}) steht bei {price:,.2f} — über {above}")
            if below is not None and price <= float(below):
                alerts.append(f"  {label} ({symbol}) steht bei {price:,.2f} — unter {below}")
            if entry.get("note"):
                lines.append(f"    Notiz: {entry['note']}")

    if alerts:
        lines += ["", "ALARME:"] + alerts
    else:
        lines += ["", "Keine Schwellwerte erreicht."]
    return "\n".join(lines)


def watchlist_add(ctx: ToolContext, args: dict[str, Any]) -> str:
    symbol = str(args["symbol"]).strip()
    if not symbol:
        raise ToolError("Es wurde kein Kürzel angegeben.")

    # Vor dem Speichern prüfen, ob es das Kürzel überhaupt gibt.
    with _client() as client:
        result = _fetch_chart(client, symbol, "5d", "1d")
    resolved = (result.get("meta") or {}).get("symbol", symbol)

    entries = _load_watchlist(ctx)
    entries = [e for e in entries if str(e.get("symbol", "")).upper() != resolved.upper()]
    entry: dict[str, Any] = {"symbol": resolved, "label": str(args.get("label") or resolved)}
    if args.get("note"):
        entry["note"] = str(args["note"])
    if args.get("alert_above") is not None:
        entry["alert_above"] = float(args["alert_above"])
    if args.get("alert_below") is not None:
        entry["alert_below"] = float(args["alert_below"])
    entries.append(entry)
    _save_watchlist(ctx, entries)

    thresholds = []
    if "alert_above" in entry:
        thresholds.append(f"Alarm über {entry['alert_above']}")
    if "alert_below" in entry:
        thresholds.append(f"Alarm unter {entry['alert_below']}")
    suffix = f" ({', '.join(thresholds)})" if thresholds else ""
    return f"'{entry['label']}' ({resolved}) in die Watchlist aufgenommen{suffix}."


def watchlist_remove(ctx: ToolContext, args: dict[str, Any]) -> str:
    symbol = str(args["symbol"]).strip().upper()
    entries = _load_watchlist(ctx)
    remaining = [e for e in entries if str(e.get("symbol", "")).upper() != symbol]
    if len(remaining) == len(entries):
        raise ToolError(f"'{symbol}' steht nicht auf der Watchlist.")
    _save_watchlist(ctx, remaining)
    return f"'{symbol}' von der Watchlist entfernt."


# --------------------------------------------------------------------------- #
# Registrierung
# --------------------------------------------------------------------------- #


def build_tools() -> list[Tool]:
    return [
        Tool(
            name="market_quote",
            description=(
                "Holt aktuelle Kurse zu einem oder mehreren Kürzeln, mit "
                "Tagesveränderung. Yahoo-Schreibweise: AAPL, SAP.DE, ^GDAXI, "
                "BTC-EUR, EURUSD=X."
            ),
            input_schema=obj(
                {
                    "symbols": {
                        "type": "array",
                        "items": {"type": "string"},
                        "description": "Liste von Kürzeln, maximal 20.",
                    }
                },
                ["symbols"],
            ),
            risk="read",
            handler=market_quote,
            summarize=lambda a: f"Kurse für {a.get('symbols')}",
        ),
        Tool(
            name="market_history",
            description=(
                "Liefert Kennzahlen über einen Zeitraum: Start- und Endkurs, "
                "Veränderung, Hoch/Tief, gleitende Durchschnitte und Schwankung."
            ),
            input_schema=obj(
                {
                    "symbol": prop("string", "Einzelnes Kürzel."),
                    "range": prop(
                        "string", "Zeitraum, Standard 3mo.", enum=list(VALID_RANGES)
                    ),
                    "interval": prop(
                        "string", "Takt der Datenpunkte, Standard 1d.",
                        enum=list(VALID_INTERVALS),
                    ),
                },
                ["symbol"],
            ),
            risk="read",
            handler=market_history,
            summarize=lambda a: f"Verlauf {a.get('symbol')} über {a.get('range', '3mo')}",
        ),
        Tool(
            name="watchlist_show",
            description=(
                "Zeigt alle beobachteten Werte mit aktuellem Kurs und prüft die "
                "hinterlegten Schwellwerte. Das Werkzeug für das Tagesbriefing."
            ),
            input_schema=obj({}),
            risk="read",
            handler=watchlist_show,
            summarize=lambda a: "Watchlist prüfen",
        ),
        Tool(
            name="watchlist_add",
            description=(
                "Nimmt einen Wert in die Watchlist auf, optional mit Schwellwerten "
                "für Alarme. Das Kürzel wird vorher gegen die Kursquelle geprüft."
            ),
            input_schema=obj(
                {
                    "symbol": prop("string", "Kürzel in Yahoo-Schreibweise."),
                    "label": prop("string", "Klartextname, z.B. 'Siemens'."),
                    "note": prop("string", "Warum du das beobachtest."),
                    "alert_above": prop("number", "Alarm, wenn der Kurs darüber steht."),
                    "alert_below": prop("number", "Alarm, wenn der Kurs darunter fällt."),
                },
                ["symbol"],
            ),
            risk="write",
            handler=watchlist_add,
            summarize=lambda a: f"{a.get('symbol')} in die Watchlist aufnehmen",
        ),
        Tool(
            name="watchlist_remove",
            description="Entfernt einen Wert aus der Watchlist.",
            input_schema=obj({"symbol": prop("string", "Kürzel.")}, ["symbol"]),
            risk="write",
            handler=watchlist_remove,
            summarize=lambda a: f"{a.get('symbol')} aus der Watchlist entfernen",
        ),
    ]
