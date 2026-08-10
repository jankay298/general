# quantbot

Ein systematischer Trading-Bot: er bewertet Märkte charttechnisch, fundamental,
makroökonomisch und über Nachrichten, backtestet die Strategie ehrlich und kann
sie automatisiert handeln.

```bash
pip install -e .
quantbot init                    # Konfigurationsdatei anlegen
quantbot backtest -c quantbot.yml
quantbot signals -c quantbot.yml # Zielportfolio für heute
quantbot trade -c quantbot.yml   # Trockenlauf, es wird nichts gesendet
```

Ohne Netzwerk und ohne API-Key funktioniert alles: der `synthetic`-Provider
erzeugt einen deterministischen Markt mit Marktfaktor, Volatilitätsregimen und
fetten Rändern. Das ist kein Spielzeug — es ist die Voraussetzung dafür, dass die
Testsuite überhaupt aussagekräftig ist.

---

## Was der Bot anschaut

| Ebene | Quelle | Signale |
| --- | --- | --- |
| **Charttechnik** | Kursdaten | Trend (Distanz zur 200-Tage-Linie, MA-Cross, Donchian-Ausbruch, ADX-gewichtet), Momentum (12-1, risikoadjustiert), kurzfristige Gegenbewegung, Volatilität, ATR |
| **Unternehmensdaten** | Alpha Vantage `OVERVIEW`, yfinance | Value (Gewinn-, Buch-, Umsatz-, EBITDA-Rendite), Qualität (ROE, Margen, Verschuldung), Wachstum |
| **Makroökonomie** | FRED (ohne API-Key) | Zinsstruktur, VIX, High-Yield-Spreads, Dollar-Index, Sahm-Regel-Lücke, Breakeven-Inflation, Öl |
| **Nachrichten & Weltpolitik** | RSS (MarketWatch, EZB, Fed, Politico) | Sentiment pro Titel, zeitlich gewichtet; separater marktweiter Stimmungsindex |

Daraus entsteht **ein** Score pro Wertpapier pro Tag. Die Gewichtung der
Faktoren hängt vom Makro-Regime ab.

---

## Die Strategie

Bewusst konventionell — Cross-Sectional-Faktor-Ranking mit Makro-Overlay und
Volatilitätssteuerung. Genau diese Bauart hat jahrzehntelange Out-of-Sample-Evidenz.
Originalität in einer Handelsstrategie ist meistens nur Überanpassung, die noch
niemand bemerkt hat.

1. **Jedes Wertpapier auf jedem Faktor bewerten.** Standardisiert **innerhalb
   jedes Tages** über das Universum. Die Frage lautet immer „günstig im Vergleich
   zu allem anderen, das ich heute kaufen könnte", nie „günstig im Vergleich zu
   2013".
2. **Faktoren regimeabhängig mischen.** Ein Stress-Score aus Finanzmarktdaten
   entscheidet, ob das Buch auf Trend oder auf Qualität setzt:

   | Regime | Trend | Momentum | Value | Qualität | Wachstum | Sentiment | Reversal | Bruttoexposure |
   | --- | --- | --- | --- | --- | --- | --- | --- | --- |
   | risk_on | 0.35 | 0.18 | 0.12 | 0.08 | 0.12 | 0.10 | 0.05 | 100% |
   | neutral | 0.25 | 0.15 | 0.18 | 0.18 | 0.07 | 0.07 | 0.10 | 70% |
   | risk_off | 0.12 | 0.05 | 0.10 | 0.35 | 0.03 | 0.10 | 0.25 | 35% |

   Faktoren ohne Daten fallen weg, die restlichen Gewichte werden renormiert. Ein
   ausgefallener Feed verschiebt also die Betonung, statt das Buch stillschweigend
   zu verkleinern.
3. **Handelbarkeit filtern.** Genug Historie, kürzlich tatsächlich gehandelt,
   ausreichend liquide.
4. **Ranken und auswählen** — oberes Segment long, optional unteres short.
5. **Größe nach Konviktion / Volatilität**, dann auf Zielvolatilität skalieren,
   dann harte Limits.

### Warum die Details so sind

**Regime-Erkennung prognostiziert nichts.** Sie misst, wie angespannt die
Finanzierungsbedingungen *gerade* sind, und passt das Risikobudget an. Die
Z-Scores sind rollierend und kausal — gegen die volle Historie zu standardisieren
würde 2020 nach 2016 hineinlecken.

**Regime sind klebrig.** Zwei Bremsen: eine Hysterese-Bande (Verlassen erfordert
mehr als Betreten) und eine Mindest-Verweildauer von 21 Bars. Ohne die zweite
erzeugt ein Score, der monatelang um eine Schwelle pendelt, alle zwei Wochen ein
neues „Regime" — jedes davon skaliert das gesamte Buch neu. Finanzierungs-
bedingungen ändern sich nicht so oft; das Label hat Rauschen gemessen.

**Positionsauswahl mit Hysterese.** Ein Titel, der von Rang 12 auf Rang 13
abrutscht, wird nicht verkauft. Sonst wird er verkauft und sofort durch den
Titel ersetzt, der von 13 auf 12 gestiegen ist — zwei Round Trips an Kosten, um
keine geänderte Meinung auszudrücken. In der Messung senkt das den Turnover von
1394% auf 1069% p.a. und hebt die Sharpe Ratio gleichzeitig.

**Inverse-Volatilitäts-Gewichtung.** Gleiche Dollar-Gewichte sind keine gleichen
Risikogewichte. 5% in einem Biotech mit 60% Vol tragen ein Vielfaches zum
Portfoliorisiko bei wie 5% in einem Versorger.

**Volatilitätssteuerung.** Realisierte Vol ist stark autokorreliert, deshalb ist
die Skalierung auf ein Vol-Ziel eine der wenigen Anpassungen, die risikoadjustierte
Renditen zuverlässig verbessert. Die Kovarianzmatrix wird geschrumpft, weil eine
Stichprobenkovarianz über 60 Tage und 30 Titel überwiegend Rauschen ist.

**Drawdown-Kill-Switch.** Die eine Risikokontrolle, die jedes Signal überstimmt.
Ein Modell, das auf eine Art falsch liegt, für die es nie trainiert wurde,
produziert selbstbewusste, konsistente und vollständig falsche Positionen — keine
Positionsgrößensteuerung hilft dagegen. Anhalten und für eine feste Abkühlphase
beiseitestehen schon.

---

## Ehrlichkeit im Backtest

Die meisten Backtests sterben an denselben vier Stellen. Was dieser Bot dagegen tut:

**Signale am Schluss von Tag *t*, Ausführung zur Eröffnung von *t+1*.** Diese
einzelne Regel ist der Unterschied zwischen einem Backtest und einer Fantasie.
Zur selben Schlusskurs auszuführen, aus dem das Signal stammt, ist der häufigste
Fehler in Backtesting-Code, er ist im Ergebnis unsichtbar, und er lässt eine
zufällige Strategie hervorragend aussehen. `execution_lag_bars: 0` wird von der
Konfigurationsprüfung als Look-Ahead-Bias zurückgewiesen.

**Kosten auf jede Ausführung.** Kommission, Spread und Market Impact nach dem
Wurzelgesetz `coeff * sqrt(Teilnahmequote)`. Impact zu ignorieren ist genau das,
was einen Backtest skalierbar aussehen lässt, obwohl er es nicht ist.

**Orders werden am Volumen gedeckelt.** Eine Simulation, die 40% des Tagesvolumens
zur Eröffnung kauft, hat einen Preis simuliert, den es nicht gegeben hätte.

**Momentaufnahmen bleiben draußen.** Alpha Vantage und yfinance liefern
*aktuelle* Fundamentaldaten. Die auf Kurse von 2017 anzuwenden verrät der
Strategie von 2017, welche Unternehmen sich gut entwickeln würden. Der Backtest
lädt sie deshalb gar nicht erst — er braucht ein Point-in-Time-Archiv. Dasselbe
gilt für RSS-Schlagzeilen: dahinter steht kein Archiv, also ist Sentiment ein
Live-Signal, kein backtestbares. Der Bot sagt das beim Start laut.

**Walk-Forward statt einem Durchlauf.** Ein einzelner Backtest über die gesamte
Historie sagt aus, wie es gelaufen wäre, *wenn man 2015 gewusst hätte, welche
Parameter zu verwenden sind*. Wusste man nicht. `quantbot walkforward` passt auf
dem an, was bekannt war, handelt den nächsten Abschnitt blind, rollt weiter — und
berichtet nur die blinden Abschnitte. Die Differenz zum In-Sample-Ergebnis ist die
nützlichste Zahl im ganzen Paket: sie ist die Größe der Lüge, die ein naiver
Backtest erzählt hätte.

**Deflated Sharpe Ratio.** Testet man genug Varianten, sieht eine rein zufällig
hervorragend aus. Diese Kennzahl diskontiert die Schlagzeilenzahl um die Anzahl
der Versuche.

**Baselines laufen mit.** Jeder Backtest vergleicht gegen Gleichgewichtung und
gegen einfaches Trendfolgen — durch dieselbe Engine, dieselben Kosten, dieselbe
Ausführungsverzögerung. Schlägt die Multi-Faktor-Variante das einfache
Trendfolgen nicht, sind die zusätzlichen Faktoren Dekoration und gehören entfernt.

---

## Live-Handel

```bash
quantbot trade -c quantbot.yml                    # Trockenlauf (Standard)
quantbot trade -c quantbot.yml --live-mode --yes-really-trade
quantbot account -c quantbot.yml                  # Kontostand
```

Ein Durchlauf: Daten aktualisieren, Zielbuch von der Strategie holen, **tatsächliches
Buch vom Broker lesen**, Differenz bilden, Preflight-Checks, dann erst Orders.

Die Positionen vom Broker statt aus lokalem State zu lesen bedeutet, dass sich der
Bot von einer Teilausführung, einem manuellen Trade oder einem Absturz mitten im
gestrigen Rebalancing erholt — er sieht schlicht das wahre Buch und handelt von
dort aus auf das Ziel zu. Ein Bot, der seinem eigenen Gedächtnis vertraut,
verdoppelt irgendwann eine Position und merkt es nicht.

**Drei Sicherheitseigenschaften:**

- **Trockenlauf ist der Standard.** Echte Orders brauchen `mode: live` in der
  Konfiguration **und** `--yes-really-trade` auf der Kommandozeile. Keines allein
  genügt.
- **Preflight-Checks blockieren.** Veraltete Daten, gesperrtes Konto, überdimensionierte
  Order, ausgelöster Drawdown-Guard — der Lauf stoppt. Es gibt keinen
  „warnen und weitermachen"-Pfad, denn der ganze Sinn sind die Momente, in denen
  niemand zuschaut.
- **Equity-Historie überlebt Neustarts.** Der Drawdown-Guard muss von dem Verlust
  wissen, der vor drei Wochen begann.

Broker: `paper` (lokal, persistent, keine Keys) und `alpaca` (REST, standardmäßig
Paper-Endpoint). Zugangsdaten kommen aus Umgebungsvariablen, nie aus der
Konfigurationsdatei — so kann eine Konfiguration ins Repository, ohne Keys zu leaken.

---

## Datenquellen und was ohne Keys funktioniert

| Quelle | Key nötig | Bemerkung |
| --- | --- | --- |
| FRED (Makro) | nein | CSV-Endpunkt, eine Serie pro Abruf. Mehrere Serien auf einmal liefern ein ZIP. |
| RSS-Nachrichten | nein | Rollierendes Fenster von einigen Tagen, kein Archiv |
| Stooq | nein | Zeigt gelegentlich eine JS-Bot-Prüfung statt CSV |
| Yahoo / yfinance | nein | Ratenbegrenzt; blockiert manche Hosting-Bereiche komplett (HTTP 429) |
| Alpha Vantage | ja (kostenlos) | 5 Aufrufe/Minute; der angepasste Endpunkt ist inzwischen kostenpflichtig |
| Alpaca | ja | Nur für den Handel, nicht für Recherche |

Preis-Provider werden als Kette konfiguriert und der Reihe nach probiert. Ein
Transportfehler deaktiviert den Provider für den Lauf (das Problem liegt beim
Provider, nicht am Symbol); ein leeres Ergebnis überspringt nur das Symbol.
Alles wird mit TTL auf Platte gecacht, damit ein zweiter Backtest nicht das
Tageskontingent verbrennt.

**Ein Fallstrick, der abgesichert ist.** Der `synthetic`-Provider antwortet für
*jedes* Tickersymbol. Ein Lauf, dessen echte Provider alle blockiert sind, fällt
also auf ihn zurück und produziert einen völlig normal aussehenden Backtest über
einen erfundenen Markt. Deshalb wird das laut protokolliert und im HTML-Report
als rotes Banner ausgewiesen — und die Herkunft wird neben den Kursdaten
mitgecacht, damit auch der zweite Lauf sie noch kennt. Wer das hart haben will,
entfernt `synthetic` aus `data.price_providers`; dann sind fehlende Daten ein
Fehler statt einer Erfindung.

**Nicht abgedeckte Verzerrung: Survivorship Bias.** Ein heute
zusammengestelltes Universum enthält per Konstruktion die Überlebenden. Für
belastbare Ergebnisse braucht es historische Indexzusammensetzungen; die liefert
keine kostenlose Quelle. Der Bot kann das nicht für Sie lösen — er kann es nur
benennen.

---

## Projektstruktur

```
src/quantbot/
├── config.py            Typisierte Konfiguration, validiert sich selbst
├── portfolio.py         Doppelte Buchführung, geteilt von Backtest und Live
├── cli.py
├── data/                Preise, Fundamentaldaten, Makro, News
│   ├── base.py          Provider-Vertrag, Normalisierung der Rohframes
│   ├── providers/       yahoo, stooq, alphavantage, csv, synthetic
│   ├── macro.py         FRED inkl. Veröffentlichungsverzögerung
│   ├── news.py          RSS-Aggregation, Deduplizierung, Symbol-Tagging
│   └── repository.py    Panels, Kalender-Ausrichtung, Handels-Maske
├── features/            Indikatoren, Cross-Section, Regime, Sentiment
├── strategy/            composite + Baselines
├── risk/                Positionsgrößen, Limits, Kill-Switch, Trailing Stops
├── backtest/            Engine, Kosten, Kennzahlen, Walk-Forward, Report
└── execution/           Broker-Interface, Paper, Alpaca, Live-Runner
```

Die Schichten hängen nur nach unten voneinander ab.

---

## Tests

```bash
pytest
```

92 Tests, ohne Netzwerk, ohne Keys. Die wichtigsten sind in
`tests/test_no_lookahead.py`: sie berechnen ein Signal, zerstören anschließend
jeden Datenpunkt nach einem Stichtag, rechnen neu und prüfen, dass sich davor
nichts bewegt hat. Look-Ahead-Bias kündigt sich nicht an — er produziert einen
Backtest, der bloß *sehr gut* statt offensichtlich kaputt aussieht, und übersteht
Code-Reviews, weil die schuldige Zeile harmlos aussieht.

---

## Grenzen — bitte lesen

Ein Backtest ist eine Untergrenze dafür, wie falsch man liegen kann, keine
Prognose.

- **Survivorship Bias** ist nicht abgedeckt (siehe oben).
- **Sentiment und Fundamentaldaten sind im Backtest abgeschaltet**, solange kein
  Point-in-Time-Archiv vorliegt. Die Backtest-Zahlen stammen also aus den
  Kursfaktoren plus Makro-Overlay — weniger, als der Live-Bot verwendet.
- **Das Marktmodell reagiert nicht auf Sie.** Impact ist modelliert, aber
  Rückkopplung nicht.
- **Regime ohne Präzedenzfall in der Stichprobe** sind genau die, in denen die
  Strategie versagt.
- **Kein Steuermodell**, kein Wechselkursrisiko bei gemischten Währungen, keine
  Behandlung von Kapitalmaßnahmen jenseits angepasster Kurse.
- Das ist Software, keine Anlageberatung. Beginnen Sie im Papierhandel, handeln
  Sie die Walk-Forward-Zahlen und nicht die In-Sample-Zahlen, und riskieren Sie
  nur Geld, dessen Verlust Sie verkraften.

---

## Konfiguration

Alles wird von einer YAML-Datei gesteuert — dieselbe Datei für Backtest und
Live-Betrieb. Das ist Absicht: ein Backtest, dessen Parameter von denen des
Live-Loops abweichen können, ist schlimmer als gar kein Backtest.

Die wichtigsten Stellschrauben:

```yaml
strategy:
  long_only: true
  max_positions: 12
  long_quantile: 0.30
  selection_buffer: 0.5        # Hysterese; 0 schaltet sie ab
  no_trade_band_relative: 0.25 # Turnover-Bremse

risk:
  target_vol: 0.12             # annualisierte Zielvolatilität
  max_weight: 0.15
  max_sector_weight: 0.35
  drawdown_killswitch: 0.25
  atr_stop_mult: 4.0

backtest:
  rebalance: W-WED             # BME (monatlich) halbiert den Turnover
  execution_lag_bars: 1        # 0 wird als Look-Ahead zurückgewiesen
  costs:
    commission_bps: 1.0
    spread_bps: 2.0
    impact_coeff_bps: 10.0
```

`quantbot init` schreibt eine kommentierte Startdatei.
