# Die fertigen cTrader-Pakete

Zwei Dateien, beide direkt in cTrader importierbar. .NET wird dafür **nicht** gebraucht —
das ist nur zum Backtesten nötig.

| Datei | Was sie tut |
|---|---|
| `DaytradingBot.algo` | handelt eine Strategie auf einem Konto |
| `DaytradingBarExport.algo` | schreibt Kursdaten als CSV, damit der Backtest mit den Daten des eigenen Brokers rechnet |

Die Dateien werden beim Bauen von `src/CBot` und `src/CBotExport` erzeugt und liegen hier in
der jeweils zuletzt gebauten Fassung. Wer selbst baut:

```bash
dotnet build src/CBot -c Release
dotnet build src/CBotExport -c Release
```

## Einbauen

1. cTrader öffnen → **Automate**.
2. Links oben auf **+** → **Import cBot** (in älteren Fassungen: das Zahnrad → *Import*).
3. `DaytradingBot.algo` auswählen. Der Bot erscheint danach als **DaytradingBot** in der Liste.
4. Chart des gewünschten Symbols öffnen, im Chart auf **cBots** → **DaytradingBot** → **Add**.

Der Zeitrahmen des Charts ist der Takt des Bots. Er handelt auf abgeschlossenen Bars, ein
5-Minuten-Chart bedeutet also eine Entscheidung alle fünf Minuten.

## Vor dem ersten Start: bitte auf ein Demokonto

Oben rechts in cTrader muss **Demo** stehen. Der Bot ist noch nie mit echtem Geld gelaufen und
es gibt für keine Strategie ein Ergebnis, das ihn dort rechtfertigen würde — siehe unten.

## Die Einstellungen

Die Parameter sind in Gruppen sortiert. Vorbelegt ist alles so, dass der Bot ohne eine einzige
Änderung startet; sinnvoll ist das aber nur für einen ersten Blick.

### Strategie

| Parameter | Bedeutung |
|---|---|
| **Strategie** | `OpeningRangeBreakout` oder `VwapReversion` |
| **Parameter** | Feineinstellung als `Name=Wert;Name=Wert`, z.B. `OpeningRangeMinutes=30;StopAtrFactor=1.5`. Leer lassen heißt: Vorgabewerte |
| **Anlageklasse** | `Equity`, `Index`, `Commodity` oder `Crypto` |

### Session

Hier steht, wann gehandelt werden darf. Die Vorgabe ist der US-Aktienhandel.

| Symbol | Zeitzone | Beginn | Ende | Feiertage |
|---|---|---|---|---|
| US500, NAS100, Aktien | `America/New_York` | `09:30` | `16:00` | `us-equity` |
| XAUUSD, XAGUSD | `UTC` | `07:00` | `21:00` | `none` |
| Krypto | `UTC` | `00:00` | `24:00` | `none` (Handelstage: alle sieben) |

### Risiko

Das sind die vereinbarten Werte. Sie gelten im Bot und im Backtest gleichermaßen.

| Parameter | Vorgabe |
|---|---|
| Risiko je Trade | 1 % |
| Max. offenes Risiko über alle Positionen | 4 % |
| Max. Tagesverlust | 4 % |
| Max. Tages-Drawdown | 4 % |
| Max. Gesamt-Drawdown | 10 % |
| Max. gleichzeitige Positionen | 5 |
| Max. Trades pro Tag | 6 |
| Flat vor Sessionende | 15 Minuten |

Mehrere Positionen gleichzeitig sind erlaubt, aber das **offene Risiko** wird laufend
zusammengezählt: Bei 1 % je Trade sind höchstens vier gleichzeitig offen, danach lehnt der Bot
neue Einstiege ab. Läuft eine Position in den Gewinn und wird der Stop nachgezogen, sinkt ihr
offenes Risiko — dann ist wieder Platz. Sieben Positionen zu je 1 % kann es nicht geben.

### Erwartungsprofil

Diese Felder schalten die laufende Selbstkontrolle ein: Der Bot vergleicht, was er tatsächlich
erreicht, mit dem, was im Walk-Forward herauskam, und schaltet bei Abweichung das Risiko herunter
oder sich selbst ab.

Die Werte stehen in `results/profiles/<strategie>_<symbol>.json`, sobald ein Walk-Forward
gelaufen ist. **Solange sie 0 sind, ist die Selbstkontrolle aus** — der Bot handelt trotzdem und
schreibt das beim Start ins Log.

### Protokoll

`Trade-Log-Verzeichnis` auf einen beschreibbaren Ordner setzen, z.B.
`C:\ctrader-logs` oder `/Users/<name>/ctrader-logs`. Der Bot schreibt dort jeden Trade im selben
Format wie der Backtester — das ist die Grundlage für den späteren Vergleich Demo gegen Backtest.
Bleibt das Feld leer, gibt es kein Trade-Log.

## Was der Bot von sich aus tut

- **Kein Einstieg ohne Stop-Loss.** Ein Signal ohne Stop wird abgelehnt, nicht mit einem
  Vorgabewert aufgefüllt. Lässt sich der Stop nach dem Einstieg nicht setzen, wird die Position
  sofort geschlossen.
- **Positionsgröße aus Risiko und Stopabstand**, nie eine feste Lotgröße.
- **Flat vor Sessionende**, kein Halten über Nacht, keins über das Wochenende.
- **Kein Nachkaufen in den Verlust, kein Martingal, kein Grid.**
- Bei Erreichen der Tages- oder Gesamtgrenze: keine neuen Einstiege, Meldung im Log.

Diese Regeln stecken in der Ausführungsschicht, nicht in der Strategie. Eine neue Strategie kann
sie nicht umgehen.

## Was jetzt zu erwarten ist

Der Bot läuft und hält sich an seine Regeln — das lässt sich auf dem Demokonto sofort ansehen.
Etwas anderes ist die Frage, ob er **Geld verdient**, und dazu ist der Stand ehrlich gesagt
dieser:

Auf 2,1 Millionen echten Minutenbars in Krypto (BTC, ETH, SOL, 2021–2024) hat **keine** der 108
geprüften Kombinationen bestanden. Alle liefen in die Drawdown-Grenze, im Mittel nach 16 Tagen.
Out-of-Sample waren 9 von 126 Fenstern profitabel. Der Erwartungswert lag zwischen −0.33 und
−0.71 R je Trade.

Aufschlussreich war die Kostenrechnung: ohne Kosten liegt der Erwartungswert bei −0.006 R, also
praktisch bei einem Münzwurf. Es sind Spread und Kommission, die daraus einen verlässlichen
Verlust machen. Die beiden mitgelieferten Strategien haben **keinen Vorteil**, den sie bezahlen
könnten.

Für Gold sind die Daten inzwischen da (gemessener Spread statt geschätztem), die Auswertung
steht noch aus. Bis dahin gilt: Der Bot ist zum Zusehen auf Demo gedacht, nicht zum Geldverdienen.
