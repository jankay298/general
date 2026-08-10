# Jarvis

Ein persönlicher KI-Assistent, der auf deinem Rechner läuft und echte Werkzeuge
bedient: dein Dateisystem, dein Postfach, Kursdaten und die Websuche. Kein
Chatfenster, das nur redet — er tut Dinge, und du siehst jede einzelne davon.

```
du > Schau in mein Postfach: was ist heute liegen geblieben?

Jarvis:
  ⚙ Postfach 'INBOX' auflisten
  ↳ 47 Nachrichten in 'INBOX' (ungelesen), die 12 neuesten:
  ⚙ Nachricht uid=8821 lesen
  ↳ Von: buchhaltung@lieferant.de

Drei Sachen brauchen heute eine Antwort:

1. Lieferant fragt nach der Freigabe für Rechnung 2026-114 (seit Dienstag offen)
2. Terminvorschlag von Frau Berger für Donnerstag 14 Uhr — sie wartet auf Zusage
3. Deine Bank meldet ein abgelaufenes TAN-Verfahren, Frist ist der 20.

Der Rest sind Newsletter. Soll ich Antwortentwürfe für 1 und 2 schreiben?
```

---

## Was hier echt ist und was nicht

Die KI-Agenten, die auf Instagram alles gleichzeitig können, bestehen technisch
immer aus denselben vier Teilen: einem Sprachmodell, **Werkzeugen**, einem
**Gedächtnis** und einem **Zeitplaner**. Das ist baubar, und es steht in diesem
Verzeichnis. Was in den Videos meist weggeschnitten wird:

**Kein Agent ist zuverlässiger als das, was er anfassen darf.** Jarvis kann
Ordner sortieren, weil er auf Dateien zugreift. Er kann dein Postfach
zusammenfassen, weil er IMAP spricht. Für jede neue Fähigkeit muss jemand eine
Schnittstelle anbinden — es gibt keinen Schalter, der "alles" freischaltet.

**Er kostet Geld pro Aufgabe.** Jede Anfrage geht an die Anthropic-API und wird
nach Tokens abgerechnet. Ein Tagesbriefing mit Postfach und Recherche liegt
grob bei ein paar Cent; eine lange Recherchesitzung kann in den Bereich eines
Euro gehen. `--usage` zeigt dir den Verbrauch.

**Er macht Fehler.** Deshalb ist hier alles, was etwas verändert oder nach
außen wirkt, hinter einer Freigabe. Deshalb ist die Vorschau beim Aufräumen der
Standard und das Verschieben die Ausnahme. Und deshalb schreibt er E-Mails als
Entwurf, den du gegenliest, statt sie zu verschicken.

**Traden kann er nicht.** Er beobachtet Kurse, rechnet Kennzahlen und schlägt
Alarm bei Schwellwerten. Zwischen "Kurse lesen" und "Geld bewegen" liegt ein
Risikosprung, den man nicht nebenbei einbaut — siehe [Was bewusst
fehlt](#was-bewusst-fehlt).

---

## Einrichtung

Voraussetzung: Python 3.11 oder neuer und ein API-Schlüssel von
[console.anthropic.com](https://console.anthropic.com/settings/keys).

```bash
cd jarvis
pip install -e .

jarvis init          # legt ~/.config/jarvis/jarvis.toml und .env an
```

Dann die beiden Dateien ausfüllen:

**`~/.config/jarvis/.env`** — hier stehen die Geheimnisse, sonst nirgends:

```
ANTHROPIC_API_KEY=sk-ant-...
JARVIS_MAIL_PASSWORD=dein-app-passwort
```

**`~/.config/jarvis/jarvis.toml`** — mindestens diese beiden Abschnitte:

```toml
[files]
roots = ["~/Downloads", "~/Dokumente"]   # nur hier darf Jarvis arbeiten

[mail]
enabled = true
imap_host = "imap.gmx.net"
smtp_host = "mail.gmx.net"
user = "dein.name@gmx.net"
drafts = "Entwürfe"
```

Bei GMX muss der IMAP-Zugriff einmal in den Einstellungen freigeschaltet
werden (*Einstellungen → POP3/IMAP-Abruf*). Nutze ein separates App-Passwort,
nicht dein Hauptpasswort. Für Web.de, Gmail und Outlook stehen die Hostnamen
als Kommentar in der Konfigurationsdatei.

Prüfen, ob alles sitzt:

```bash
jarvis doctor
```

`doctor` verbindet sich testweise mit dem Postfach und der Kursquelle und sagt
dir konkret, was noch fehlt.

---

## Benutzung

```bash
jarvis chat                             # Gespräch, merkt sich den Verlauf
jarvis ask "Räum meinen Downloads-Ordner auf"
jarvis briefing                         # Kurse + Postfach + Nachrichtenlage
jarvis journal                          # was hat er zuletzt getan?
jarvis memory list                      # was hat er sich gemerkt?
```

Im Chat gibt es ein paar Befehle: `/tools` zeigt alle Werkzeuge mit ihrer
Freigaberegel, `/memory` das Gedächtnis, `/journal` die letzten Aktionen,
`/clear` verwirft den Verlauf, `/exit` beendet.

### Was er kann

| Bereich | Werkzeuge |
| --- | --- |
| **Dateien** | Ordner auflisten und auswerten, Textdateien lesen, nach Typ oder Monat einsortieren, Duplikate finden, einzelne Dateien verschieben |
| **Postfach** | Nachrichten auflisten, lesen, durchsuchen, verschieben, markieren, Antwortentwürfe ablegen, (nach Freigabe) versenden |
| **Märkte** | Kurse abrufen, Verlauf mit gleitenden Durchschnitten und Schwankung, Watchlist mit Schwellwert-Alarmen |
| **Recherche** | Websuche und Seitenabruf — läuft auf Anthropics Servern, nichts einzurichten |
| **Gedächtnis** | dauerhafte Notizen, die beim nächsten Start automatisch wieder da sind |

---

## Die Freigabeliste

Jedes Werkzeug ist einer von drei Risikoklassen zugeordnet:

| Klasse | Bedeutung | Standard |
| --- | --- | --- |
| `read` | liest nur — Ordner ansehen, Mails lesen, Kurse abrufen | ohne Rückfrage |
| `write` | verändert lokal etwas — Datei verschieben, Entwurf ablegen | fragt nach |
| `external` | wirkt nach außen — E-Mail versenden | fragt nach |

Kommt eine Rückfrage, siehst du genau, was passieren soll:

```
  Freigabe nötig — move_file (verändernd)
  /home/jan/Downloads/rechnung.pdf -> /home/jan/Dokumente/Buchhaltung
  [j] einmal   [n] nein   [i] immer   [x] nie mehr
```

`i` und `x` merkt sich Jarvis dauerhaft — in `permissions.local.json` im
Datenverzeichnis, damit deine handgeschriebene `jarvis.toml` mit ihren
Kommentaren unangetastet bleibt. Feineinstellung geht dort direkt:

```toml
[permissions.tools]
mail_draft = "allow"    # Entwürfe schreiben ist harmlos
mail_send  = "ask"      # senden nicht
move_file  = "deny"     # gar nicht erst anbieten
```

Zwei Dinge, die unabhängig von der Freigabeliste gelten:

**Dateizugriff ist auf `files.roots` begrenzt.** Nicht per Konvention, sondern
weil jeder Pfad erst vollständig aufgelöst und dann gegen die Wurzeln geprüft
wird — `..` und Symlinks führen nicht hinaus. Dafür gibt es eigene Tests.

**Alles wird protokolliert.** Jeder Werkzeugaufruf landet mit Zeitstempel,
Zusammenfassung und Entscheidung in der Datenbank. `jarvis journal` zeigt ihn.

---

## Routinen

Aufgaben, die von selbst laufen sollen, stehen in der Konfiguration:

```toml
[[routines]]
name = "briefing"
schedule = "weekdays 07:30"
prompt = """
Prüfe die Watchlist, fasse die ungelesenen Mails der letzten 24 Stunden
zusammen und nenne die Nachrichtenlage zu meinen Werten. Kurz halten.
"""
email_to = "dein.name@gmx.net"    # optional
```

Zeitpläne: `daily 07:30`, `weekdays 07:30`, `weekly Mon 18:00`, `every 30m`,
`every 4h`.

Ausgeführt wird nichts von allein — `jarvis routines run` prüft, was fällig
ist, und arbeitet es ab. Für echten Dauerbetrieb übernimmt das dein
Betriebssystem; einen eigenen Hintergrunddienst gibt es bewusst nicht, weil
cron und systemd das besser können und einen Neustart überleben.

**Linux (crontab -e):**

```cron
*/15 * * * * /usr/local/bin/jarvis routines run >> ~/.local/share/jarvis/cron.log 2>&1
```

**macOS (launchd)** oder **Windows (Aufgabenplanung)**: denselben Befehl alle
15 Minuten aufrufen.

Ergebnisse landen als Markdown-Datei in `~/.local/share/jarvis/logs/`.

> Routinen laufen unbeaufsichtigt. Werkzeuge, die auf `ask` stehen, werden
> dabei **abgelehnt** — es ist niemand da, der bestätigen könnte. Was eine
> Routine tun soll, muss also auf `allow` stehen. Das ist Absicht: eine
> Rückfrage, die niemand sieht, ist keine Sicherheit.

---

## Was bewusst fehlt

**Automatisches Traden.** Das Marktmodul liest und rechnet, es handelt nicht.
Wer das erweitern will, braucht drei Dinge, die alle nicht optional sind: eine
Broker-Anbindung mit eigenem Schlüssel, einen Papierhandels-Modus, in dem das
System Monate läuft, bevor echtes Geld fließt, und harte Grenzen außerhalb des
Modells (maximale Ordergröße, Tagesverlustgrenze, Notausschalter). Der
natürliche Ort dafür wäre ein `tools/broker.py` mit Risikoklasse `external`
und `deny` als Standard. Ein Sprachmodell, das ohne diese Schicht Aufträge
erteilen darf, ist kein Assistent, sondern ein Risiko.

**Eine Shell.** Ein Werkzeug, das beliebige Befehle ausführt, macht jede
andere Absicherung hier bedeutungslos. Wenn du es brauchst, bau es bewusst:
mit Kommando-Positivliste, ohne Shell-Operatoren, in einem Container.

**Sprachein- und -ausgabe.** Wäre nett, ändert aber nichts an dem, was der
Agent kann. `jarvis ask` lässt sich problemlos hinter ein
Spracherkennungs-Tool hängen.

---

## Aufbau

```
src/jarvis/
  agent.py        die Schleife: Modell → Werkzeuge → Freigabe → Ergebnis
  config.py       jarvis.toml einlesen, .env laden
  permissions.py  Freigabeliste und Rückfragen
  memory.py       SQLite: Notizen, Verlauf, Prüfprotokoll
  routines.py     Zeitpläne verstehen und Fälligkeit bestimmen
  console.py      Terminalausgabe
  cli.py          Befehle
  tools/
    files.py      Dateien und Ordner
    mail.py       IMAP/SMTP
    markets.py    Kurse und Watchlist
    notes.py      Gedächtnis
```

Die interessanteste Datei ist `agent.py`. Die Schleife ist absichtlich von Hand
geschrieben statt mit dem `tool_runner` des SDK: jeder Werkzeugaufruf muss
*vor* der Ausführung durch die Freigabeliste und ins Protokoll, und
`pause_turn` (die serverseitige Websuche kann pausieren) wird ausdrücklich
behandelt. Wer die Schleife lesen kann, kann Jarvis erweitern.

### Ein Werkzeug hinzufügen

```python
# src/jarvis/tools/kalender.py
from .base import Tool, ToolContext, ToolError, obj, prop

def naechste_termine(ctx: ToolContext, args: dict) -> str:
    tage = int(args.get("tage") or 7)
    ...
    return "Mo 14:00 Zahnarzt\nDi 09:30 Team-Runde"

def build_tools() -> list[Tool]:
    return [Tool(
        name="naechste_termine",
        description="Zeigt die Termine der nächsten Tage.",
        input_schema=obj({"tage": prop("integer", "Zeitraum in Tagen.")}),
        risk="read",
        handler=naechste_termine,
    )]
```

Dann in `tools/__init__.py` in `build_registry()` eintragen. Mehr ist es nicht
— Schema, Freigabe, Protokollierung und Fehlerbehandlung kommen aus dem
Rahmen. Wirf in einem Werkzeug `ToolError` mit einem ganzen Satz: das Modell
liest ihn und versucht einen anderen Weg.

---

## Modell und Kosten

Standard ist `claude-opus-5`. Der Regler dafür ist `model.effort`:

| effort | wofür |
| --- | --- |
| `low` | kurze, klar umrissene Aufgaben, schnellste Antwort |
| `medium` | Alltag — guter Kompromiss |
| `high` | Standard, wenn Genauigkeit zählt |
| `xhigh` | lange Werkzeugketten, schwierige Recherche |
| `max` | nur, wenn Richtigkeit wichtiger ist als Kosten |

Für ein Postfach-Triage oder eine Watchlist-Prüfung reicht `medium` meist aus
und ist spürbar günstiger. Der Systemprompt und die Werkzeugdefinitionen
werden zwischengespeichert (Prompt-Caching), deshalb ist die zweite Frage in
einer Sitzung deutlich billiger als die erste.

`jarvis chat --usage` blendet nach jeder Antwort den Tokenverbrauch ein.

---

## Tests

```bash
pip install -e ".[dev]"
pytest
```

66 Tests, keiner davon braucht Netz oder einen API-Schlüssel — der Agent wird
gegen einen nachgebauten Client getestet. Der Schwerpunkt liegt dort, wo
Fehler wehtun: Pfadsicherheit (kann ein Werkzeug aus den freigegebenen Ordnern
ausbrechen?), Freigabelogik (wird eine abgelehnte Aktion wirklich nicht
ausgeführt?), Zeitpläne (wird ein verpasster Termin nachgeholt, aber nur
einmal?) und die Form der API-Anfrage (kennt das SDK jeden Parameter, den wir
schicken?).
