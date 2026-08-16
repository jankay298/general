# cTrader Daytrading-Framework — Architektur

Ein Framework für viele austauschbare Daytrading-Strategien, nicht ein Bot mit einer Strategie.
Der Weg einer Strategie ist immer derselbe: **Backtest → Demo → (viel später) Live**.

## Projektstruktur

```
/src
  /Strategies      netstandard2.0  Strategie-Logik. KEINE cAlgo-Abhängigkeit.
  /Execution       netstandard2.0  Risiko, Positionsgröße, Session- und Tagesregeln.
  /Evaluation      netstandard2.0  Erwartungsprofil und laufende Selbstkontrolle.
  /Data            net8.0          Datenquellen, Qualitätsprüfung, Parquet-Ablage.
  /Backtester      net8.0          Matrix, Walk-Forward, Kostenmodell, Berichte, CLI.
  /CBot            net6.0          cTrader-Adapter für den Handel.
  /CBotExport      net6.0          cTrader-Adapter für den Datenexport.
/tests             net8.0          313 Tests über alle Schichten.
/config                            Symbol- und Risikokonfiguration.
/data                              Marktdaten (nicht im Repo).
/results                           Ergebnisse (nicht im Repo).
```

Die Zielframeworks sind kein Zufall: Alles, was der cBot laden muss, ist `netstandard2.0` und
läuft damit sowohl in cTrader (.NET 6 / .NET Framework 4) als auch im Backtester auf .NET 8.
Was der cBot nie sieht — Datenpipeline und Backtester — darf net8.0 nutzen.

## Die zentrale Regel

Strategien kennen **keine** Order-Ausführung, keinen Broker, keinen Kontostand und keine
Positionsgröße. Sie bekommen je abgeschlossener Bar einen `MarketSnapshot` und geben ein
`Signal` zurück — oder `null`.

Der Grund ist nicht Ästhetik, sondern Testbarkeit und Geschwindigkeit: Der eingebaute
cTrader-Backtester rechnet immer nur ein Symbol und eine Strategie pro Durchlauf. Eine Matrix
aus vielen Strategien × vielen Assets × vielen Zeiträumen braucht einen eigenen Backtester —
und der kann nur existieren, wenn die Logik nichts von cAlgo weiß.

Zweite Konsequenz derselben Regel: Die harten Daytrading- und Risikoregeln liegen in der
Ausführungsschicht. Eine Strategie kann sie nicht umgehen, weil sie sie gar nicht sieht.

## Datentypen (`Daytrading.Strategies.Model`)

| Typ | Zweck | Erzwungene Invariante |
|---|---|---|
| `Candle` | abgeschlossene Kursbar | Zeitstempel ist UTC; OHLC ist konsistent |
| `BarSeries` / `IBarSeries` | Kurshistorie | Strategien sehen nur die lesende Sicht; Append erzwingt chronologische, duplikatfreie Bars |
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
Der Test `Strategy_sees_history_only_up_to_and_including_the_current_bar` prüft das je Bar.

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

`StrategyParameters` parst immer mit `InvariantCulture`, wirft bei unlesbaren Werten (statt still
auf den Default zurückzufallen), protokolliert jeden verwendeten Wert einschließlich der Defaults
und meldet übergebene, aber nie gelesene Schlüssel. `Fingerprint` identifiziert eine
Parameterkombination eindeutig.

`StrategyCatalog` ist das gemeinsame Verzeichnis von Backtester und cBot: Beide erzeugen unter
demselben Namen dieselbe Strategie. Zwei getrennte Verzeichnisse wären der sicherste Weg, im
Livebetrieb etwas anderes laufen zu lassen als im Test.

### Beispielstrategien

**`OpeningRangeBreakout`** bildet in den ersten *n* Minuten der Session eine Spanne und
signalisiert den ersten Bar-Schluss darüber oder darunter. Stop wahlweise an der Gegenseite der
Range oder als ATR-Vielfaches.

**`VwapReversion`** setzt auf die Rückkehr zum sessionverankerten VWAP, wenn der Kurs mehrere
Standardabweichungen abweicht — also auf das Scheitern von Ausbrüchen. Bewusst gegensätzlich zum
ORB: Welche von beiden auf einem Symbol funktioniert, entscheidet der Backtest. Sie nutzt zudem
den aktiven Ausstieg über `Signal.CloseAll` und meldet, wenn die Daten kein Volumen enthalten,
statt still einen ungewichteten Durchschnitt als VWAP auszugeben.

## Risikoregeln (festgelegt)

Gilt je Handelskonto, also je Strategie-Instanz. Werte in `config/risk.defaults.json`.

| Grenze | Wert | Bedeutung |
|---|---|---|
| Risiko pro Trade | max. 1 % | Positionsgröße = Risikobetrag ÷ Stopabstand. Nie eine feste Lotgröße. |
| Gleichzeitig offenes Risiko | max. 4 % | Summe des Risikos aller offenen Positionen, gemessen bis zum jeweiligen Stop. |
| Tagesverlust | max. 4 % | **Realisiert**, gemessen ab der Kontostand-Basis bei Tagesbeginn. |
| Tages-Drawdown | max. 4 % | **Unrealisiert**, gemessen vom höchsten Equity-Stand des laufenden Tages. |
| Gesamt-Drawdown | max. 10 % | Vom historischen Equity-Hoch. Danach Strategie stoppen und Alarm loggen. |
| Gleichzeitige Positionen | max. 5 | Nur Sicherheitsnetz. Die bindende Grenze ist das offene Risiko. |
| Trades pro Tag | 6 | |
| Flat vor Sessionende | 15 Min | Zwangsschließung, kein Overnight, kein Wochenendhalten. |

Tagesverlust und Tages-Drawdown sind zwei verschiedene Dinge, auch wenn beide bei 4 % liegen:
Der eine misst, was heute schon verloren **ist**, der andere, wie weit die Equity vom Tageshoch
zurückgefallen ist — ein Tag mit +3 % und anschließendem Rückgang auf −1 % reißt die
Drawdown-Grenze, obwohl der realisierte Verlust erst bei 1 % liegt.

### Mehrere Positionen gleichzeitig — die bindende Regel

```
(Equity-Hoch des Tages − aktuelle Equity) + Summe des offenen Risikos ≤ 4 %
```

**Wenn alle offenen Stops gleichzeitig auslösen, ist die Tagesgrenze immer noch eingehalten.**
Die Anzahl der Positionen ergibt sich daraus von selbst:

| Situation | Erlaubtes zusätzliches Risiko | Also maximal |
|---|---|---|
| Tagesbeginn, nichts verloren, nichts offen | 4 % | 4 Positionen zu je 1 % |
| 2 Positionen offen (je 1 %) | 2 % | 2 weitere |
| 2 % Rückgang vom Tageshoch | 2 % | 2 Positionen zu je 1 % |
| 4 % erreicht | 0 % | kein neuer Trade heute |

Das offene Risiko wird vom **aktuellen** Preis bis zum Stop gemessen, nicht vom Einstieg: Der
Weg zwischen Einstieg und aktuellem Preis steckt bereits im Tages-Drawdown und würde sonst
doppelt zählen.

Zwei bekannte Lücken, bewusst offen und im Report auszuweisen:

- **Korrelation.** Gold und Silber gleichzeitig sind rechnerisch zwei Positionen zu je 1 %,
  faktisch aber weitgehend eine Wette zu 2 %.
- **Gaps und Slippage.** Die 4 % sind der geplante, nicht der maximal mögliche Tagesverlust.

## Ausführungsschicht (`Daytrading.Execution`)

Reine **Entscheidungsschicht**: Sie öffnet und schließt nichts selbst, sondern liefert
Anweisungen. Der Backtester führt sie gegen sein Kostenmodell aus, der cBot gegen cTrader.
Beide treffen damit garantiert dieselben Entscheidungen.

| Baustein | Aufgabe |
|---|---|
| `RiskLimits` | die Grenzen, mit Widerspruchsprüfung beim Start |
| `AccountState` | Kontostand, Equity, Tageshoch, Allzeithoch, Trade-Zähler |
| `PositionSizer` | die einzige Stelle, an der eine Positionsgröße entsteht |
| `RiskGate` | nimmt ein Signal an oder lehnt es mit typisiertem Grund ab |
| `SessionGuard` | flat vor Sessionende, Overnight-Sperre, maximale Haltedauer |
| `TradingSessionCalendar` | Handelszeiten je Zeitzone samt Börsenfeiertagen |
| `ExecutionEngine` | verbindet alles zu einer Liste von `ExecutionInstruction` |
| `TradeRecord` | ein Trade-Log-Format für Backtest, Demo und Live |

Ablauf je Bar: Handelstag fortschreiben → Ausstiege aus den Tagesregeln → Ausstiegswunsch der
Strategie → erst danach ein neuer Einstieg durch das `RiskGate`.

Positionsgröße: `Größe = Risikobetrag / (Stopabstand × Punktwert je Einheit)`, immer **abgerundet**
auf die Schrittweite. Passt nicht einmal die Mindestgröße ins Budget, wird der Trade abgelehnt
statt verkleinert.

**Nicht konfigurierbar** sind Pflicht-Stop-Loss, kein Halten über Nacht und kein Nachkaufen in
Verlustpositionen. Das sind Regeln, keine Einstellungen; ein Schalter dafür wäre genau die Lücke,
die diese Schicht schließen soll.

## Datenpipeline (`Daytrading.Data`)

`IDataProvider` mit drei Implementierungen:

| Quelle | Abdeckung | Hinweis |
|---|---|---|
| `BinancePublicDataProvider` | Krypto, 1m, vollständige Historie | kostenlos, Monats- und Tagesarchive, lokaler Cache |
| `CsvFileDataProvider` | alles, was der Export-cBot schreibt | **Referenzquelle** mit der Preisstellung des eigenen Brokers |
| `OandaDataProvider` | Metalle, Rohstoffe, Indizes | misst den historischen Spread aus Bid- und Ask-Kerzen; braucht einen Token |

Der Weg: laden → normalisieren → prüfen → als Parquet ablegen → Manifest und Qualitätsreport
schreiben. Grundsätze:

- **Lücken werden gemeldet, nie interpoliert.** Erfundene Bars erzeugen Trades, die es nie gab.
- Duplikate mit identischen Werten sind harmlos; Duplikate mit **unterschiedlichen** Kursen
  deuten auf vermischte Quellen und disqualifizieren das Symbol.
- Maßstab für Vollständigkeit ist der **Sessionkalender**, nicht der Kalender: Wochenenden und
  Feiertage sind keine Lücken. US-Börsenfeiertage werden aus Regeln berechnet, inklusive
  Karfreitag und der Verschiebung von Wochenendfeiertagen.
- Gespeichert wird die feinste Auflösung der Quelle, partitioniert nach Symbol, Timeframe und
  Jahr. Gröberes entsteht beim Laden durch Aggregation — der umgekehrte Weg existiert nicht.
- Symbole unterhalb der Mindestqualität fliegen aus dem Backtest und werden im Report benannt.

## Backtester (`Daytrading.Backtester`)

Ausführungsregeln, bewusst so und nicht anders:

- **Einstiege frühestens auf der Open der Folgebar.** Auf dem Close der Signalbar auszuführen
  wäre Look-ahead.
- **Zeitgesteuerte Ausstiege auf dem Close der laufenden Bar.** Sie hängen nicht vom Kurs ab,
  verschaffen also keinen Informationsvorteil — und aufgeschoben würden sie „kein Overnight"
  brechen.
- **Stop und Ziel in derselben Bar: der Stop gilt als zuerst erreicht.** Der Verlauf innerhalb
  der Bar ist unbekannt; die pessimistische Annahme ist die einzige, die nicht schönrechnet.
- **Kurslücken über den Stop hinweg werden zum Eröffnungskurs gefüllt.** Ein Stop ist keine
  Garantie.
- **Swap wird ignoriert** — ohne Overnight-Positionen fällt keiner an.

Kostenmodell: Kursdaten gelten als Mittelkurs, gekauft wird zum Brief und verkauft zum Geld
(ein Hin und Zurück kostet einen vollen Spread), in den Randzeiten der Session mit dem
konfigurierten Faktor multipliziert. Slippage wirkt immer gegen die Position, bei Stops stärker,
bei Limits gar nicht.

Kennzahlen je Kombination: Nettoergebnis, Max Drawdown in Betrag und Prozent, Profitfaktor,
Sharpe, Sortino, Trefferquote, Erwartungswert je Trade und in R, Anzahl Trades, durchschnittliche
Haltedauer, längste Verlustserie, Ergebnis je Wochentag und Stunde — und die Zahl der
**Kalenderwochen bis zur Mindeststichprobe**, damit unrealistisch langsame Kombinationen auffallen.

Die Walk-Forward-Analyse wählt Parameter auf einem Trainingsfenster und misst sie im folgenden,
unangetasteten Fenster. Das Erwartungsprofil entsteht ausschließlich aus diesen
Out-of-Sample-Fenstern. Die Auswahl bevorzugt nicht den höchsten Gewinn, sondern den
Erwartungswert bei ausreichender Stichprobe — der beste Lauf aus vier Trades ist keiner.

Die Regime-Auswertung ordnet jedem Handelstag Volatilität und Trendrichtung zu (Terzile über den
Zeitraum) und wertet die Trades danach aus. Das ist eine Auswertung im Nachhinein und fließt in
keine Handelsentscheidung ein.

## Bewertungsschicht (`Daytrading.Evaluation`)

Das `ExpectationProfile` aus der Walk-Forward-Analyse ist der Maßstab für den Demo- und
Livebetrieb — in **R-Vielfachen** statt in Kontowährung, damit es nach Ein- oder Auszahlungen
vergleichbar bleibt. Es wird beim Deployment eingefroren.

`StrategyHealthMonitor` hält den Betrieb dagegen und kennt vier Zustände:

| Zustand | Bedeutung | Risikofaktor |
|---|---|---|
| Healthy | im erwarteten Bereich | 1.0 |
| Watch | auffällig, aber innerhalb der Streuung | 1.0 |
| Degraded | messbar außerhalb der Erwartung | 0.5 |
| Suspended | kein neuer Trade; offene Positionen laufen zu Ende | 0 |

Auslöser: Drawdown über dem historischen Maximum, Verlustserie länger als je beobachtet,
rollierender Erwartungswert unter dem unteren Konfidenzband, stark abweichende Handelsfrequenz.
Ein abweichender Spread wird als **Ausführungsproblem** gemeldet und führt zu keiner
Zustandsänderung — diese Unterscheidung entscheidet darüber, ob man den Broker wechselt oder die
Strategie abschaltet.

Vor jeder Verschlechterung steht eine **Mindeststichprobe**. Nach fünf schlechten Trades wird
nichts abgeschaltet. Aus `Suspended` führt nur eine bestandene Neuvalidierung nach Abkühlphase
zurück — und dann nach `Watch`, nicht direkt nach `Healthy`. Jede Zustandsänderung wird mit
Zeitstempel, Auslöser und Datengrundlage protokolliert.

Der Monitor ist **regelbasiert und nicht selbstoptimierend**: Er ändert keine Parameter, sondern
senkt das Risiko, stoppt neue Trades oder meldet ein Ausführungsproblem.

## cBot (`Daytrading.CBot`, `Daytrading.CBot.Export`)

Zwei getrennte Assemblies, weil cTrader je Assembly genau einen Algo-Typ erlaubt.

`DaytradingBot` übersetzt Bars in einen `MarketSnapshot`, fragt die gewählte Strategie, lässt die
Ausführungsschicht entscheiden und führt deren Anweisungen aus. `OnBar` ruft cTrader auf, wenn
eine neue Bar beginnt — die vorherige also gerade geschlossen hat. Entschieden wird auf dieser
geschlossenen Bar, ausgeführt sofort: genau die Open der Folgebar, mit der auch der Backtester
rechnet.

Weil `ExecuteMarketOrder` nur Pips kennt, das Framework aber in Preisen rechnet, wird ohne Stop
eröffnet und der Stop unmittelbar danach als absoluter Preis gesetzt. Schlägt das fehl, wird die
Position sofort geschlossen — eine Position ohne Stop widerspricht der Grundregel.

`BarExportBot` schreibt die Bars des Charts als CSV, mit Broker, Konto und Exportzeitpunkt in der
Kopfzeile. Ohne diese Herkunft wäre die Datei als Referenz wertlos.

## Tests

```bash
dotnet test
```

313 Tests: Datentyp-Invarianten und Look-ahead-Freiheit, Verhalten und Determinismus beider
Strategien, Positionsgröße und Risikobudget, Sessionregeln, Datenqualität und Aggregation,
Ausführungsregeln des Backtests, Kennzahlen, Walk-Forward und die Zustandsübergänge der
Bewertungsschicht.

## Plattformen

Alles außer den cBots ist reines .NET ohne native Abhängigkeiten und läuft auf Windows, macOS
(Intel wie Apple Silicon) und Linux; die CI baut und testet auf allen vieren. Einrichtung:
[`setup.md`](setup.md). Betrieb und der Weg von Backtest zu Demo: [`betrieb.md`](betrieb.md).

## Noch offen

- Korrelationsgruppen für das gemeinsame Risiko verwandter Symbole
- Kostenpflichtige Aktienquellen (Pi Trading, FirstRate) — erst nach bewusster Entscheidung
- Automatischer monatlicher Walk-Forward-Lauf, der die Ergebnismatrix fortschreibt
