# Angebotskalkulator — Handwerk & Logistik

Excel-Vorlage, die aus Aufmaß und Leistungen ein druckfertiges Angebot rechnet.
Verkaufsfertiges digitales Asset (Einmalkauf) und zugleich die Referenz-Implementierung
der Angebotslogik für die Sprach-App.

**Datei:** `Angebotskalkulator_Handwerk.xlsx` · 5 Blätter · 371 Formeln · keine Makros

## Die fünf Blätter

| Blatt | Zweck |
|---|---|
| **Anleitung** | Farblegende und Reihenfolge der Bearbeitung |
| **Stammdaten** | Firmendaten und die zehn Kalkulationsgrößen des Betriebs |
| **Leistungskatalog** | 24 Leistungen mit Zeit- und Materialansatz je Einheit, erweiterbar |
| **Kalkulation** | Aufmaß-Helfer, 20 Positionszeilen, Summen, interne Kennzahlen |
| **Angebot** | Druckfertig auf A4, zieht sich alles automatisch |

Farbcode: blaue Schrift = Eingabe, schwarz = Formel, grün = Verweis auf ein anderes
Blatt, gelbe Füllung = Schlüsselannahme, die vor dem ersten Angebot zu prüfen ist.

## Was die Vorlage kann

- **Aufmaß aus einer Zahl.** Wohnfläche eintragen, daraus werden Wand-, Decken- und
  Spachtelfläche geschätzt — die Faktoren sind sichtbar und änderbar, nichts passiert
  im Verborgenen.
- **Kalkulation von unten.** Zeit × Mittellohn + Material, darauf Gemeinkosten, darauf
  Wagnis & Gewinn. Kein Pauschalpreis je Quadratmeter.
- **Skonto im Preis.** 2 % sind einkalkuliert; zieht der Kunde sie, bleibt die Marge.
  Das gedruckte Angebot geht trotzdem auf: Positionen + Fahrt − Rabatt = Netto.
- **Interne Kennzahlen.** Deckungsbeitrag, Marge und Erlös je Arbeitsstunde — die
  Zahlen, an denen sich ein Angebot vor dem Rausschicken prüfen lässt. Sie stehen nur
  in der Kalkulation, nie im Angebot an den Kunden.
- **Zwei Branchen.** Maler, Trockenbau, Boden, Elektro und Logistik (Umzug, Transport,
  Einlagerung) im selben Katalog.

Vorbelegt ist der Fall „Wohnung 45 m² komplett streichen" inklusive Vorarbeiten und
vier Türen: **3.240,90 € netto / 3.856,67 € brutto** bei 45,2 Stunden und 12,4 % Marge.
Wohnfläche überschreiben genügt — alles rechnet sich neu.

## Vor dem ersten Angebot

1. **Mittellohn** in `Stammdaten!B14` auf den eigenen Betrieb setzen. Bruttolohn plus
   Lohnnebenkosten geteilt durch die *produktiven* Stunden — nicht der Stundensatz aus
   der Preisliste. Dieser eine Wert bewegt das Ergebnis am stärksten.
2. Gemeinkostenzuschläge und Wagnis & Gewinn prüfen.
3. Firmendaten eintragen; sie erscheinen im Angebotskopf.

Die mitgelieferten Zeit- und Materialansätze sind Branchen-Richtwerte zur Orientierung,
keine externe Preisliste und keine verbindliche Kalkulation. Nach jedem Auftrag gehören
die tatsächlichen Werte zurück in den Katalog — dann wird die Vorlage mit jedem Projekt
genauer.

## Neu erzeugen

```bash
python3 tools/build_angebotskalkulator.py [zielpfad.xlsx]
```

Die Datei ist vollständig aus dem Skript generiert; Änderungen gehören dorthin, nicht in
die .xlsx. Nach jeder Änderung neu durchrechnen lassen und auf null Formelfehler prüfen:

```bash
python3 <xlsx-skill>/scripts/recalc.py assets/angebotskalkulator/Angebotskalkulator_Handwerk.xlsx 180
```

Die Formelkette und der Referenzfall stehen in [`docs/rechenmodell.md`](../../docs/rechenmodell.md).
