# /results

Backtest-Ergebnisse und Trade-Logs. **Inhalt wird nicht eingecheckt** (siehe `.gitignore`) —
Ergebnisse entstehen reproduzierbar aus Daten, Code und Konfiguration.

Geplanter Inhalt, sobald der Backtester steht:

- `matrix.csv` — eine Zeile je Strategie × Symbol × Zeitraum × Parameterkombination mit
  Nettoergebnis, Max Drawdown (Betrag und %), Profitfaktor, Sharpe, Sortino, Trefferquote,
  Erwartungswert pro Trade, Anzahl Trades, durchschnittlicher Haltedauer, längster Verlustserie
  sowie Ergebnis je Wochentag und Tageszeit
- `trades/<lauf>.csv` — jeder einzelne Trade mit Zeitstempel, Symbol, Richtung, Einstieg,
  Ausstieg, Grund des Ausstiegs und Ergebnis; identisches Format im Demo- und Livebetrieb,
  damit sich Erwartung und Realität direkt vergleichen lassen
- `data-quality/<symbol>.md` — Lücken, Duplikate, Ausreißer, Feiertage, Qualitätsstatus
- `summary.md` — lesbare Zusammenfassung inklusive Anzahl der getesteten Kombinationen
  (damit einschätzbar bleibt, wie viel Zufall im besten Ergebnis steckt)
