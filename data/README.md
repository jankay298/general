# /data

Historische Kursdaten. **Inhalt wird nicht eingecheckt** (siehe `.gitignore`) — zu groß, und
teilweise lizenzgebunden.

Geplante Ablage, sobald die Datenpipeline steht:

```
/data
  /raw/<quelle>/<symbol>/...        unveränderte Downloads
  /normalized/<symbol>/<jahr>/...   Parquet, UTC, dedupliziert
  /manifests/<symbol>.json          Quelle, Zeitraum, Auflösung, Download-Datum, Qualitätsstatus
```

Grundsätze:

- Alles intern auf **UTC** normalisiert, Zeitzone der Quelle im Manifest dokumentiert.
- Immer in der **feinsten verfügbaren Auflösung** laden und im Code nach oben aggregieren.
  Der umgekehrte Weg ist nicht möglich.
- Lücken werden **gemeldet, nicht interpoliert**. Symbole unterhalb einer Mindestqualität
  fliegen aus dem Backtest und werden im Report benannt.
- Aktien: **splitbereinigt**, und das sichtbar dokumentiert.
- Referenz für Kostenannahmen sind die Daten des eigenen cTrader-Brokers; externe Quellen liefern
  die historische Tiefe und werden gegen die Brokerdaten plausibilisiert.
