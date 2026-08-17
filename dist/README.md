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
| **Parameter** | Feineinstellung als `Name=Wert;Name=Wert`, z.B. `OpeningRangeMinutes=30;AtrStopMultiple=1.5`. Leer lassen heißt: Vorgabewerte |
| **Anlageklasse** | `Equity`, `Index`, `Commodity` oder `Crypto` |

Die möglichen Namen für das Parameterfeld. Ein Name, den die gewählte Strategie nicht liest,
erscheint beim Start als Warnung im Log (`WARNUNG: Parameter '…' wird von … nicht gelesen`) —
ein Tippfehler bleibt also nicht unbemerkt, stoppt den Bot aber auch nicht:

**OpeningRangeBreakout** — handelt den Ausbruch aus der Eröffnungsspanne.

| Name | Vorgabe | Bedeutung |
|---|---|---|
| `OpeningRangeMinutes` | 30 | Länge der Eröffnungsspanne in Minuten |
| `BreakoutBufferTicks` | 0 | Aufschlag über der Spanne, bevor der Ausbruch zählt |
| `StopLossMode` | `OppositeRangeSide` | oder `Atr` |
| `AtrPeriod` | 14 | Periode der ATR |
| `AtrStopMultiple` | 1.5 | Stopabstand in ATR, nur bei `StopLossMode=Atr` |
| `TakeProfitR` | 2.0 | Ziel als Vielfaches des Risikos; 0 schaltet es ab |
| `OneTradePerDay` | true | nur der erste Ausbruch des Tages |
| `AllowLong` / `AllowShort` | true | Richtung einschränken |

**VwapReversion** — handelt die Rückkehr zum VWAP nach einer Abweichung.

| Name | Vorgabe | Bedeutung |
|---|---|---|
| `BandSigma` | 2.0 | Abweichung in Standardabweichungen, ab der eingestiegen wird |
| `MinBarsForVwap` | 12 | Bars, bevor der VWAP als belastbar gilt |
| `AtrPeriod` | 14 | Periode der ATR |
| `StopAtrMultiple` | 1.5 | Stopabstand in ATR |
| `MaxEntriesPerDay` | 2 | Einstiege je Tag |
| `ExitAtVwap` | true | Ausstieg bei Erreichen des VWAP |
| `AllowLong` / `AllowShort` | true | Richtung einschränken |

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

Geprüft auf echten Minutendaten — Krypto (BTC, ETH, SOL, 2021–2024, 2,1 Mio. Bars) und Gold
(Januar bis April 2023, 114.238 Bars mit **gemessenem** Spread). 144 Kombinationen:

| | |
|---|---|
| Von der Drawdown-Grenze abgeschaltet | **144 von 144** |
| Aktive Tage bis dahin (Median) | 20 |
| Erwartungswert je Trade (Median) | **−0.375 R** |
| Kombinationen mit positivem Erwartungswert | 16 von 144 |
| davon mit zu kleiner Stichprobe | die Mehrheit — 86 von 144 sind unter der Schwelle |

Je Symbol, Median des Erwartungswerts: BTCUSD −0.165 R, ETHUSD −0.228 R, XAUUSD −0.242 R,
SOLUSD −1.652 R. Die beste Einzelkombination (BTCUSD, +0.194 R über 91 Trades) ist die beste
aus 144 Versuchen — genau das, was auch reiner Zufall liefern würde.

Aufschlussreich war eine frühere Kostenrechnung: ohne Kosten liegt der Erwartungswert nahe null,
also bei einem Münzwurf. Es sind Spread und Kommission, die daraus einen verlässlichen Verlust
machen. Die beiden mitgelieferten Strategien haben **keinen Vorteil**, den sie bezahlen könnten.

Der Bot ist also zum Zusehen auf Demo gedacht, nicht zum Geldverdienen. Was er zuverlässig
zeigt, ist das Einhalten seiner Regeln — und dass er sich selbst abschaltet, wenn eine Strategie
nicht liefert.
