# Rechenmodell Angebotskalkulation

Verbindliche Referenz für die Angebotslogik. Die Excel-Vorlage
`assets/angebotskalkulator/Angebotskalkulator_Handwerk.xlsx` implementiert exakt dieses
Modell — wenn die App später dieselben Eingaben bekommt, muss sie auf denselben Cent
kommen. Die Vorlage ist damit gleichzeitig der Testfall für die Engine.

## Warum diese Reihenfolge

Ein Handwerker rechnet nicht „Preis pro Quadratmeter". Er rechnet von unten:
Zeit × Lohn + Material = Kosten, darauf Gemeinkosten, darauf Gewinn. Nur so lässt sich
hinterher sagen, *warum* ein Angebot verloren ging — zu wenig Marge oder zu viel Zeit
angesetzt. Ein reiner Pauschalpreis kann das nicht.

## Stammdaten (je Betrieb einmal)

| Größe | Symbol | Vorgabe | Bedeutung |
|---|---|---|---|
| Mittellohn je produktiver Stunde | `L` | 42,00 € | Bruttolohn + Lohnnebenkosten ÷ produktive Stunden |
| Gemeinkostenzuschlag Lohn | `gkL` | 25 % | Büro, Fahrzeuge, Werkzeug, unproduktive Zeit |
| Gemeinkostenzuschlag Material | `gkM` | 10 % | Beschaffung, Lagerung, Verschnitt |
| Wagnis & Gewinn | `wg` | 12 % | Aufschlag auf die Selbstkosten |
| Skonto | `sk` | 2 % | wird **in den Preis einkalkuliert**, siehe unten |
| Rabatt | `rb` | 0 % | Nachlass auf die Zwischensumme |
| Fahrtkosten je km | `km€` | 0,70 € | |
| Umsatzsteuer | `ust` | 19 % | 0 bei Kleinunternehmer nach § 19 UStG |

Alle Vorgabewerte sind Branchen-Richtwerte zur Orientierung, keine externe Preisliste.
Sie sind vom Betrieb durch eigene Nachkalkulationswerte zu ersetzen — insbesondere der
Mittellohn, der das Ergebnis am stärksten bewegt.

## Leistungskatalog

Je Leistung genau zwei Kennzahlen, beide je Einheit:

- `t` — Zeit je Einheit in Stunden
- `m` — Materialkosten je Einheit in Euro

Der Katalog ist der Ort, an dem Erfahrung gespeichert wird. Nach jedem Auftrag gehören
die tatsächlichen Werte zurück in den Katalog (Nachkalkulation).

## Positionsrechnung

Für jede Position mit Menge `q`:

```
Stunden       h  = q × t
Lohnkosten    KL = h × L
Materialkosten KM = q × m
Selbstkosten  SK = KL × (1 + gkL) + KM × (1 + gkM)
Einheitspreis EP = SK × (1 + wg) × (1 + sk) ÷ q
Gesamtpreis   GP = EP × q
```

**Rundung:** jeder Zwischenwert auf 2 Nachkommastellen (Stunden ebenfalls auf 2), `EP`
kaufmännisch auf 2. `GP` wird bewusst aus dem **gerundeten** `EP × q` gebildet, nicht aus
`SK × (1+wg) × (1+sk)` — sonst stimmt im gedruckten Angebot Einzelpreis × Menge nicht mit
der Zeile überein, und genau darüber stolpert jeder prüfende Kunde.

## Angebotssumme

```
Zwischensumme Positionen  = Σ GP
An- und Abfahrt           = km × km€ × (1 + sk)
Zwischensumme             = Positionen + Fahrt
Rabatt                    = − Zwischensumme × rb
Nettobetrag               = Zwischensumme + Rabatt
Umsatzsteuer              = Nettobetrag × ust
Gesamtbetrag brutto       = Nettobetrag + Umsatzsteuer
```

### Skonto gehört in den Einheitspreis

Der Skontosatz wird auf den Preis aufgeschlagen, nicht als eigene Summenzeile geführt.
Zwei Gründe:

1. Zieht der Kunde die 2 % Skonto, bleibt die kalkulierte Marge erhalten. Als separater
   Aufschlag am Ende wäre er für den Kunden sichtbar und würde wegverhandelt.
2. Das gedruckte Angebot geht auf: Positionen + Fahrt − Rabatt = Nettobetrag. Eine
   Zusatzzeile dazwischen wirft jede Kundenprüfung aus dem Tritt.

## Interne Kennzahlen (nie im Angebot an den Kunden)

```
Selbstkosten gesamt = Σ SK + km × km€      (Fahrt hier zum reinen Kostensatz, ohne Skonto)
Deckungsbeitrag     = Nettobetrag − Selbstkosten gesamt
Marge               = Deckungsbeitrag ÷ Nettobetrag
Erlös je Stunde     = Nettobetrag ÷ Σ h
```

Der Erlös je Stunde ist die Zahl, an der ein Betrieb ein Angebot schnell prüft: liegt sie
unter dem eigenen Zielwert, ist der Auftrag zu billig — unabhängig davon, wie groß die
Summe unten aussieht.

## Aufmaß-Näherung Malerarbeiten

Für den häufigsten Fall („Wohnung streichen") reicht die Wohnfläche als Eingabe:

```
Wandfläche brutto = Wohnfläche × Faktor      Faktor 2,3–2,8 bei mehreren Räumen
Wandfläche netto  = max(0; brutto − Abzug Fenster/Türen)
Deckenfläche      = Wohnfläche
Spachtelfläche    = (Wand netto + Decke) × Anteil     Anteil typ. 30 %
```

Der Faktor ist eine Faustregel für Wohnungen mit mehreren Räumen. Bei einem einzelnen
großen Raum ist er kleiner (weniger Wand je Quadratmeter Boden), bei vielen kleinen
Räumen größer. Die App sollte ihn nachfragen oder aus der Raumanzahl schätzen, statt ihn
stillschweigend zu setzen.

## Referenzfall für Tests

Eingabe: Wohnfläche 45 m², Faktor 2,5, Abzug 12 m², 4 Türen, Spachtelanteil 30 %,
Fahrstrecke 40 km, Stammdaten wie oben, Positionen M12, M01, M02, M03, M04, M05, M06,
M09, M08, M10.

| Ergebnis | Wert |
|---|---|
| Gesamtstunden | 45,17 |
| Materialkosten | 401,13 € |
| Selbstkosten gesamt | 2.840,68 € |
| Nettobetrag | 3.240,90 € |
| Umsatzsteuer 19 % | 615,77 € |
| **Gesamtbetrag brutto** | **3.856,67 €** |
| Deckungsbeitrag | 400,22 € |
| Marge | 12,4 % |
| Erlös je Stunde | 71,75 € |

Weicht die Engine hiervon ab, ist die Rundungsreihenfolge die erste Verdächtige.

## Für die Sprach-App

Das Gespräch muss genau diese Felder einsammeln — mehr braucht die Rechnung nicht:

```jsonc
{
  "kunde":   { "name": "", "strasse": "", "plz_ort": "" },
  "objekt":  "Wohnung 45 m², komplett streichen",
  "aufmass": { "wohnflaeche": 45, "faktor_wand": 2.5, "abzug": 12,
               "tueren": 4, "spachtel_anteil": 0.30, "fahrstrecke_km": 40 },
  "positionen": [ { "katalog_nr": "M05", "menge": 100.5 } ]
}
```

Die Sprachführung sollte fehlende Angaben **schätzen und die Schätzung aussprechen**
(„ich rechne mit 2,5 m² Wandfläche je m² Wohnfläche, macht 112 m² — passt das?"), statt
zu blockieren. Ein Handwerker auf der Baustelle bricht ein Gespräch ab, das ihn zehn
Pflichtfelder fragt.

Fehlt der Mittellohn, darf die App **nicht** raten: dieser eine Wert entscheidet über
Gewinn oder Verlust und muss einmalig beim Einrichten des Betriebs abgefragt werden.
