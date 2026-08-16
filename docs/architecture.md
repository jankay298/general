# cTrader Daytrading-Framework — Architektur

Ein Framework für viele austauschbare Daytrading-Strategien, nicht ein Bot mit einer Strategie.
Der Weg einer Strategie ist immer derselbe: **Backtest → Demo → (viel später) Live**.

## Projektstruktur

```
/src
  /Strategies      Class Library (netstandard2.0) — Strategie-Logik, KEINE cAlgo-Abhängigkeit
  /Backtester      Console App (net8.0)           — noch nicht gebaut
  /CBot            cTrader cBot                   — noch nicht gebaut
/tests
  /Daytrading.Strategies.Tests                    — xUnit (net8.0)
/data              Marktdaten (nicht im Repo)
/results           Backtest-Ergebnisse (nicht im Repo)
```

`netstandard2.0` für die Strategie-Bibliothek ist Absicht: dieselbe Assembly muss vom Backtester
auf .NET 8 und vom cBot innerhalb von cTrader Automate geladen werden können.

## Die zentrale Regel

Strategien kennen **keine** Order-Ausführung, keinen Broker, keinen Kontostand und keine
Positionsgröße. Sie bekommen je abgeschlossener Bar einen `MarketSnapshot` und geben ein
`Signal` zurück — oder `null`.

Der Grund ist nicht Ästhetik, sondern Testbarkeit und Geschwindigkeit: Der eingebaute
cTrader-Backtester rechnet immer nur ein Symbol und eine Strategie pro Durchlauf. Eine Matrix
aus vielen Strategien × vielen Assets × vielen Zeiträumen braucht einen eigenen Backtester —
und der kann nur existieren, wenn die Logik nichts von cAlgo weiß.

Zweite Konsequenz derselben Regel: Die harten Daytrading- und Risikoregeln (Pflicht-Stop,
Positionsgröße aus Risiko in %, Tagesverlustgrenze, Drawdown-Grenze, Zwangsschließung vor
Sessionende, maximale Anzahl Positionen und Trades) liegen in der Ausführungsschicht.
Eine Strategie kann sie nicht umgehen, weil sie sie gar nicht sieht.

## Datentypen (`Daytrading.Strategies.Model`)

| Typ | Zweck | Erzwungene Invariante |
|---|---|---|
| `Candle` | abgeschlossene Kursbar | Zeitstempel ist UTC; OHLC ist konsistent |
| `BarSeries` / `IBarSeries` | Kurshistorie | Strategien sehen nur `IBarSeries` (nur lesend); Append erzwingt chronologische, duplikatfreie Bars |
| `MarketSnapshot` | was die Strategie je Bar sieht | mindestens eine Bar; kein Kontostand, keine Kosten, keine Zukunft |
| `Signal` | Handelsabsicht | Einstieg **nur** mit Stop-Loss auf der richtigen Seite und Abstand > 0 |
| `Position` | offene Position | positive Größe, UTC-Einstiegszeit |
| `TradingSession` | Handelstag in UTC | Ende nach Beginn; Handelstag ist reines Datum |
| `SymbolInfo` | Marktstruktur | positive Tick-Größe; **keine** Kontodaten |
| `Timeframe` | Bargröße | positive Dauer |

Preise und Geldbeträge sind `decimal`. Exakte Tick-Rundung und exakte P&L-Summen sind hier mehr
wert als die Rechengeschwindigkeit von `double`.

### Schutz gegen Look-ahead-Bias

Strukturell statt disziplinarisch: Der Host hängt eine Bar erst an die `BarSeries` an, wenn sie
abgeschlossen ist, und ruft danach `OnBar`. `IBarSeries` hat keine API, um in die Zukunft zu
sehen. `MarketSnapshot.BarCloseTimeUtc` ist der Entscheidungszeitpunkt — nicht der Bar-Beginn.
Der Test `MarketSnapshotTests.Strategy_sees_history_only_up_to_and_including_the_current_bar`
prüft das je Bar.

## Strategien

```csharp
public interface IStrategy
{
    StrategyDescriptor Descriptor { get; }   // Name + Version, landet in Matrix und Trade-Log
    int WarmupBars { get; }                  // nach Initialize gültig, hängt von den Parametern ab
    void Initialize(StrategyContext context);
    Signal? OnBar(MarketSnapshot snapshot);
}
```

`StrategyContext` liefert Symbol, Timeframe, Parameter, Log und einen festen Zufallsseed —
und bewusst nichts weiter.

`StrategyParameters` parst immer mit `InvariantCulture`, wirft bei unlesbaren Werten (statt still
auf den Default zurückzufallen), protokolliert jeden verwendeten Wert einschließlich der Defaults
und meldet übergebene, aber nie gelesene Schlüssel — der übliche Tippfehler, der sonst eine ganze
Matrix mit anderen Parametern rechnet als gedacht. `Fingerprint` identifiziert eine
Parameterkombination eindeutig und ist die Grundlage dafür, später die Anzahl der getesteten
Kombinationen belegen zu können.

Die `Version` im `StrategyDescriptor` ist wichtiger, als sie aussieht: Ein eingefrorenes
Erwartungsprofil der Bewertungsschicht gilt für genau eine Version. Ändert sich die Logik, muss
die Version steigen — sonst wird Live-Verhalten gegen ein Profil gemessen, das zu anderem Code gehört.

## Beispielstrategie: Opening-Range-Breakout

`OpeningRangeBreakoutStrategy` bildet in den ersten *n* Minuten der Session eine Preisspanne und
signalisiert den ersten Bar-Schluss darüber oder darunter.

| Parameter | Default | Bedeutung |
|---|---|---|
| `OpeningRangeMinutes` | 30 | Länge des Eröffnungsfensters ab Sessionbeginn |
| `BreakoutBufferTicks` | 0 | Mindestabstand über/unter der Range in Ticks |
| `StopLossMode` | `OppositeRangeSide` | oder `AtrMultiple` |
| `AtrPeriod` / `AtrStopMultiple` | 14 / 1.5 | nur für `AtrMultiple` |
| `TakeProfitR` | 2.0 | Ziel als Vielfaches des Stopabstands, 0 = kein Ziel |
| `OneTradePerDay` | true | nur der erste Ausbruch je Handelstag |
| `AllowLong` / `AllowShort` | true / true | Richtungsfilter |
| `RiskPercent` | 0 | Risikowunsch je Trade, 0 = Default der Ausführungsschicht |

Was die Strategie **nicht** tut und auch nicht tun kann: Positionsgröße berechnen, vor
Sessionende schließen, Tagesverluste begrenzen, Haltedauer beenden. Das ist Sache der
Ausführungsschicht.

Verworfen wird ein Ausbruch, wenn keine Bar vollständig im Eröffnungsfenster liegt (etwa bei
verkürzten Handelstagen oder zu grober Bargröße) oder wenn der Stopabstand kleiner als ein Tick
wäre. Beides wird protokolliert statt stillschweigend zu einem Default-Stop zu führen.

## Indikatoren

Selbst implementiert und inkrementell (`RollingWindow<T>`, `AverageTrueRange` nach Wilder), damit
die Bibliothek ohne Fremdabhängigkeit und ohne cAlgo auskommt und der Aufwand je Bar konstant bleibt.

## Tests

```bash
dotnet test
```

92 Tests: Datentyp-Invarianten, Look-ahead-Freiheit, Parameter-Parsing, Indikatoren und das
Verhalten der Beispielstrategie einschließlich Determinismus (gleicher Input → gleiche Signale).
Der `StrategyHarness` im Testprojekt ist ein minimaler Host und der Beleg, dass Strategien
vollständig ohne cTrader testbar sind.

## Risikoregeln (festgelegt)

Gilt je Handelskonto, also je Strategie-Instanz. Werte in `config/risk.defaults.json`.

| Grenze | Wert | Bedeutung |
|---|---|---|
| Risiko pro Trade | max. 1 % | Positionsgröße = Risikobetrag ÷ Stopabstand. Nie eine feste Lotgröße. |
| Tagesverlust | max. 4 % | **Realisiert**, gemessen ab der Kontostand-Basis bei Tagesbeginn. |
| Tages-Drawdown | max. 4 % | **Unrealisiert**, gemessen vom höchsten Equity-Stand des laufenden Tages. |
| Gesamt-Drawdown | max. 10 % | Vom historischen Equity-Hoch. Danach Strategie stoppen und Alarm loggen. |
| Gleichzeitige Positionen | 1 | *Annahme, bitte bestätigen.* |
| Trades pro Tag | 6 | *Annahme, bitte bestätigen.* |
| Flat vor Sessionende | 15 Min | Zwangsschließung, kein Overnight, kein Wochenendhalten. |

Tagesverlust und Tages-Drawdown sind zwei verschiedene Dinge, auch wenn beide bei 4 % liegen:
Der eine misst, was heute schon verloren **ist**, der andere, wie weit die Equity vom Tageshoch
zurückgefallen ist — ein Tag mit +3 % und anschließendem Rückgang auf −1 % reißt die
Drawdown-Grenze, obwohl der realisierte Verlust erst bei 1 % liegt. Beide werden getrennt geprüft
und getrennt protokolliert.

Zwei Konsequenzen aus den Zahlen:

- Vier ausgestoppte Trades zu vollem Risiko beenden den Handelstag. Das Limit von 6 Trades lässt
  also Raum für Teilverluste und Nullnummern, nicht für sechs volle Stops.
- Das Risiko des nächsten Trades wird auf das **verbleibende Tagesbudget** gedeckelt
  (`capRiskToRemainingDailyBudget`). Andernfalls würde der letzte Trade des Tages die
  Tagesgrenze planmäßig überschreiten statt sie einzuhalten.

Diese Regeln liegen vollständig in der Ausführungsschicht. Eine Strategie kann sie weder lesen
noch umgehen — sie kennt weder Kontostand noch Equity.

## Plattformen

Alles außer dem cBot ist reines .NET ohne native Abhängigkeiten und läuft auf Windows, macOS
(Intel wie Apple Silicon) und Linux; die CI baut und testet auf allen vieren. Einrichtung,
Details zu cTrader auf dem Mac und zum Dauerbetrieb: [`setup.md`](setup.md).

## Noch nicht gebaut

Datenpipeline (Binance, OANDA, Datenqualitätsreport, Parquet-Cache), Backtester mit Kostenmodell
und Walk-Forward, cBot-Adapter, Bewertungsschicht mit Erwartungsprofil und den Zuständen
Healthy / Watch / Degraded / Suspended.
