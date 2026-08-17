# Betrieb: vom Backtest bis zum Demokonto

Die Reihenfolge ist nicht verhandelbar: **Backtest → Walk-Forward → Demo über Monate → erst dann
Live.** Jede Abkürzung ersetzt Messung durch Hoffnung.

## 1. Ohne alles ausprobieren

```bash
dotnet run --project src/Backtester -- demo
```

Rechnet die gesamte Kette auf synthetischen Daten durch — ohne Netz, ohne Broker, ohne
Marktdaten. Danach liegen in `/results` die Matrix, die Trade-Logs, die Regime-Tabelle, die
Walk-Forward-Fenster und eine lesbare Zusammenfassung.

Wichtig: Die Ergebnisse sagen **nichts** über Strategien aus. Synthetische Kurse sind ein
Zufallspfad; nach Kosten verliert dort jede Strategie. Der Lauf beweist nur, dass Pipeline,
Risikoschicht, Kostenmodell und Auswertung zusammenspielen. Genau deshalb ist ein negatives
Ergebnis hier das erwartete.

## 2. Echte Daten holen

```bash
# Krypto, kostenlos und ohne Zugangsdaten
dotnet run --project src/Backtester -- ingest --symbol BTCUSD --from 2022-01-01 --to 2025-01-01

# Metalle, Indizes, Währungen — ebenfalls ohne Anmeldung, mit gemessenem Spread
dotnet run --project src/Backtester -- ingest --source dukascopy --symbol XAUUSD --from 2023-01-01 --to 2025-01-01

# Dieselben Instrumente über OANDA (braucht einen Token, siehe unten)
export OANDA_API_TOKEN=...
dotnet run --project src/Backtester -- ingest --source oanda --symbol XAUUSD --from 2022-01-01 --to 2025-01-01

# Brokerdaten aus dem Export-cBot
dotnet run --project src/Backtester -- ingest --source csv --csv data/raw/csv --symbol US500
```

**Dukascopy ist die Quelle der ersten Wahl für alles außer Krypto und Aktien.** Sie braucht
weder Konto noch Token und liefert Geld- und Briefkurs getrennt — der Spread wird damit
*gemessen* statt geschätzt. Bei Haltedauern von Minuten ist das die Größe, an der das Ergebnis
hängt. Zwei Beobachtungen aus der Praxis:

- Der Abruf läuft bewusst langsam (`--throttle`, Vorgabe 250 ms). Ein Jahr eines Instruments
  sind rund 500 Dateien. Wird zu schnell geladen, antwortet der Server mit HTTP 429.
- Einzelne Tage scheitern an Zeitüberschreitungen. Der Lauf bricht deswegen nicht ab, sondern
  meldet sie am Ende und lädt beim nächsten Aufruf nur die Lücken nach — alles Geladene liegt
  unter `data/raw/dukascopy/`. **Denselben Befehl einfach noch einmal ausführen**, bis keine
  Fehltage mehr gemeldet werden.

Der gemessene Spread wird nach dem Abruf ausgegeben und mit dem Wert in `config/symbols.json`
verglichen. Weicht er ab, gilt der gemessene: Für XAUUSD standen dort 0.30, gemessen wurden
im Mittel 0.396 — ein Drittel mehr Kosten je Trade, als der Backtest angenommen hätte.

Nach jedem Lauf steht in `results/data-quality/<symbol>.md`, was mit den Daten nicht stimmt.
Symbole mit Status `Rejected` fliegen aus dem Backtest — das ist gewollt und wird benannt, statt
sie stillschweigend zu übergehen.

**Die Referenz ist immer der eigene Broker.** Externe Quellen liefern die historische Tiefe;
die Kostenannahmen für den Livebetrieb kommen aus dem Export des eigenen cTrader-Kontos.

## 3. Matrix und Walk-Forward rechnen

```bash
dotnet run --project src/Backtester -- backtest    --from 2022-01-01 --to 2025-01-01
dotnet run --project src/Backtester -- walkforward --from 2022-01-01 --to 2025-01-01 --train 180 --test 60
```

Was in `results/summary.md` zuerst zu lesen ist:

- **Anzahl der gerechneten Kombinationen.** Je mehr, desto wahrscheinlicher ist die beste ein
  Zufallstreffer.
- **Die Spalte „Belastbar"**. Ein schönes Ergebnis aus zwölf Trades ist eine Anekdote.
- **Wochen bis zur Mindeststichprobe.** Eine Kombination, die dafür zwei Jahre braucht, ist im
  Demobetrieb nicht überprüfbar — unabhängig davon, wie gut sie aussieht.
- **Out-of-Sample statt Training.** Nur die Walk-Forward-Zahlen zählen.

## 4. Die cBots in cTrader einrichten

Die fertigen Pakete liegen unter `dist/` und werden in cTrader importiert; wer lieber selbst
baut, öffnet die Projekte in cTrader. Die ausführliche Klickanleitung steht in
[`dist/README.md`](../dist/README.md).

| Projekt | Paket | Zweck |
|---|---|---|
| `src/CBotExport` | `dist/DaytradingBarExport.algo` | schreibt Bars als CSV — einmal je Symbol und Zeitrahmen |
| `src/CBot` | `dist/DaytradingBot.algo` | handelt eine Strategie auf einem Konto |

**Ein Konto, eine Strategie, ein Symbol.** Nur so bleiben die Ergebnisse getrennt vergleichbar,
und nur so gelten die Kontogrenzen für genau das, was sie begrenzen sollen.

Die Parameter des Handels-cBots spiegeln die Konfiguration des Backtests: Strategie und
Parameterzeile, Session und Zeitzone, die Risikogrenzen, das eingefrorene Erwartungsprofil und
ein Verzeichnis für das Trade-Log.

Die Werte des Erwartungsprofils stehen in `results/profiles/<strategie>_<symbol>.json`:
Erwartungswert in R samt Streuung, schlechtester Drawdown in R, längste Verlustserie, Trades pro
Woche und der angenommene Spread. Ohne diese Werte läuft der Bot, aber die laufende
Selbstkontrolle ist aus — er sagt das beim Start.

Vor dem ersten Livebetrieb einmal prüfen: Der Punktwert wird aus `PipValue / PipSize` abgeleitet.
Eine falsche Umrechnung verschiebt jede Positionsgröße. Auf dem Demokonto mit einer kleinen
Position gegenrechnen.

## 5. Demo laufen lassen und gegenhalten

Der Bot schreibt dasselbe CSV-Format wie der Backtester. Damit lässt sich die Erwartung direkt
gegen die Realität halten:

| Frage | Woran man es sieht |
|---|---|
| Handelt er überhaupt wie erwartet? | Trades pro Woche gegen das Profil |
| Stimmt die Größenordnung der Ergebnisse? | Erwartungswert in R gegen das Profil |
| Sind die Kosten wie angenommen? | realisierter Spread gegen `AssumedSpread` |
| Läuft etwas grundsätzlich schief? | Zustandsmeldungen des Monitors im cTrader-Log |

Der Monitor meldet Zustandswechsel mit Zeitstempel, Auslöser und Datengrundlage. Bei `Degraded`
halbiert sich das Risiko je Trade automatisch, bei `Suspended` eröffnet der Bot nichts Neues
mehr; offene Positionen laufen regulär zu Ende.

Ein abweichender Spread ist ein **Ausführungsproblem**, kein Strategieproblem. Die Strategie
abzuschalten, weil der Broker teurer ist als angenommen, wäre die falsche Reaktion.

## 6. Reaktivierung und Neuvalidierung

Eine abgeschaltete Kombination kommt nicht von selbst zurück. Nach der Abkühlphase (Default
30 Tage) braucht es einen **frischen Out-of-Sample-Test**; besteht er, läuft die Kombination
unter Beobachtung wieder an — nicht sofort mit vollem Vertrauen.

Monatlich lohnt ein neuer Walk-Forward-Lauf mit den neuesten Daten, angehängt an die
Ergebnismatrix. So wird sichtbar, ob eine Kombination über die Zeit hält oder langsam wegdriftet.

## 7. Was gegen Live spricht, bis das Gegenteil gemessen ist

- Demo über **mehrere Monate**, nicht Wochen.
- Genug Trades, um die Mindeststichprobe des Profils zu erreichen — sonst ist der Vergleich
  nicht belastbar.
- Realisierte Kosten in der Größenordnung der Annahme.
- Kein offener Befund im Monitor.

Der Backtest sagt nicht voraus, was passieren wird. Er sagt, dass eine Regel in der
Vergangenheit nicht falsch war. Der Demobetrieb prüft, ob sie auch unter realen Bedingungen und
mit realen Kosten trägt.
