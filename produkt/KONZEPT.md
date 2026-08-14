# Produktkonzept — digitale Werkzeuge als Einmalkauf

Stand: 14.08.2026 · Status: Entwurf zur Freigabe

Dieses Dokument beschreibt, **was** gebaut wird, **für wen**, **wie es aussieht**
und **wie es verkauft wird** — bevor eine Zeile Produkt entsteht.

---

## 1. Die Format-Frage (und warum sie die Nische kippt)

Der Einwand lautete: *Wer nutzt für sowas überhaupt Excel? Ich selbst trage
alles in die Notizen-App.*

Der Einwand ist richtig, und er hat eine unbequeme Konsequenz. Der typische
Käufer eines Excel-Haushaltsbuchs ist ein sehr spezieller Menschentyp: jemand,
der Struktur mag, gern plant und am Laptop sitzt. Wer seine Ausgaben in die
Notizen tippt, wird **nie** eine Excel-Datei kaufen — egal wie gut sie ist.

Es gibt genau zwei Auswege:

**A) Format ändern** — statt Excel eine App/Web-Anwendung fürs Handy.
**B) Markt ändern** — dorthin gehen, wo Excel das native Werkzeug ist.

### Warum nicht A (App)

Eine App bricht die Grundbedingung „einmal bauen, dann Ruhe":

- Support-Fläche: Nutzer schreiben bei Bugs, Browser-Updates brechen Dinge,
  Daten gehen verloren → Rückerstattungen.
- Die Konkurrenz ist **kostenlos**: Finanzguru, YNAB, Bankingsapps mit
  Kategorisierung. Gegen kostenlos mit 10 € Einmalzahlung anzutreten, ist die
  schlechteste Position, die es gibt.
- Eine App ist ein Produkt mit Lebenszyklus, kein Asset.

→ **Verworfen.** Widerspricht dem Ziel.

### Warum B (Markt ändern)

Selbstständige, Handwerker und Kleinunternehmer **leben** in Tabellen. Für die
ist Excel kein Hindernis, sondern das erwartete Format. Sie wollen die Datei
sogar bewusst, weil sie sie an ihren Steuerberater weitergeben können.

→ **Gewählt.**

---

## 2. Marktvergleich in Zahlen

| | Privathaushalt | Selbstständige / Kleinunternehmer |
|---|---|---|
| Marktgröße | sehr groß | deutlich kleiner |
| Übliche Preise | 3–15 €, meist 5–9 € | 15–40 € |
| Konkurrenz | brutal, tausende Listings | überschaubar, oft veraltet |
| Kaufentscheidung | emotional, „sieht hübsch aus" | rational, „spart mir Ärger" |
| Excel-Akzeptanz | niedrig (Handy-Nutzer) | hoch (Arbeitswerkzeug) |
| Steuerlich absetzbar | nein | **ja** — Betriebsausgabe |
| Rückerstattungsquote | höher | niedriger |

Die entscheidende Zeile ist die vorletzte. Ein Selbstständiger, der 29 € für ein
Werkzeug ausgibt, setzt es als Betriebsausgabe ab — es fühlt sich für ihn an wie
gut 20 €, und er überlegt keine drei Tage. Eine Privatperson überlegt bei 9 €
länger als ein Unternehmer bei 29 €.

**Fazit:** Der kleinere Markt bringt mehr Geld pro Verkauf, hat weniger
Konkurrenz und weniger Ärger. Große Märkte sind nur dann besser, wenn man sie
auch erreicht — und erreichen kostet Reichweite, die wir nicht haben.

### Der ehrliche Haken an dieser Entscheidung

Mein ursprüngliches Argument war: „Geh auf einen Marktplatz, der das
Traffic-Problem löst." Etsy ist aber ein **Verbraucher**-Marktplatz. Mit einem
Produkt für Selbstständige verlieren wir einen Teil dieses Vorteils.

Es gibt dort zwar eine Business-Ecke (Rechnungsvorlagen, Businessplaner
verkaufen sich real), aber sie ist kleiner als die Planer-Ecke. Deshalb:

- Etsy-Listing trotzdem — es kostet 0,20 $, das Risiko ist null.
- Parallel Gumroad + eine simple Landingpage für Suchmaschinen-Traffic.
- Wenn sich nach 6–8 Wochen zeigt, dass Etsy nur Verbraucher liefert, kommt als
  **Produkt 3** doch ein Haushaltsplaner dazu, um diesen Traffic abzugreifen.

Das ist ein Test, keine Glaubensfrage.

---

## 3. Produkt 1 — „Stundensatz & Steuerrücklage"

**Voller Titel:** Stundensatz-Rechner & Steuerrücklagen-Planer für
Selbstständige (Excel + Google Sheets)

**Zielgruppe:** Solo-Selbstständige, Freiberufler, Handwerker, Kleinunternehmer
in Deutschland — besonders in den ersten 3 Jahren.

**Das Problem, das es löst:** Die zwei Fragen, an denen Selbstständige
reihenweise scheitern:
1. *Was muss ich pro Stunde verlangen, damit am Ende genug übrig bleibt?*
2. *Wie viel Geld muss ich für die Steuernachzahlung zurücklegen?*

Beides wird typischerweise geschätzt — und zwar zu niedrig. Der klassische
Fehler ist, mit 8 Stunden am Tag × 20 Tagen zu rechnen und dabei Urlaub,
Krankheit, Buchhaltung, Akquise und Leerlauf zu ignorieren. Wer 160 Stunden im
Monat ansetzt, aber nur 90 abrechnen kann, kalkuliert sich systematisch kaputt.

**Warum kein rechtliches Risiko:** Das Werkzeug ist ein *Planungs-* und
*Kalkulationshilfsmittel*, kein steuerlicher Aufzeichnungsnachweis. Damit
entfällt das GoBD-Problem, an dem Kassenbuch und Fahrtenbuch in Excel scheitern
(dort verlangt das Finanzamt Unveränderbarkeit, die Excel prinzipbedingt nicht
bietet). Ein Haftungsausschluss ist trotzdem Teil des Produkts.

### Aufbau der Datei — Blatt für Blatt

**Blatt 1 · Start**
Was das Werkzeug macht, in drei Sätzen. Bedienung in 3 Schritten. Farblegende
(gelbe Zellen = du trägst ein, graue Zellen = rechnet sich selbst). Hinweis:
keine Steuerberatung.

**Blatt 2 · Deine Zahlen** — das einzige Blatt mit Eingaben

- Gewünschtes Netto pro Monat
- Private Fixkosten: Miete, Krankenversicherung, Altersvorsorge, Sonstiges
- Betriebliche Fixkosten: Büro, Software, Versicherungen, Kfz, Steuerberater,
  Fortbildung, Sonstiges
- Arbeitszeit: Wochenstunden, Urlaubstage, erwartete Krankheitstage, Feiertage
- **Produktivquote** — welcher Anteil der Arbeitszeit ist wirklich abrechenbar
  (Voreinstellung 60 %, mit Erklärung, warum 100 % Unsinn ist)
- Steuerannahmen: Einkommensteuersatz, Gewerbesteuer ja/nein
- Umsatzsteuer: Regelbesteuerung oder Kleinunternehmer nach § 19 UStG
- Gewinnaufschlag / Risikopuffer in %

**Blatt 3 · Dein Stundensatz** — Ergebnis

- Verfügbare Arbeitstage → tatsächlich produktive Stunden pro Jahr
- Nötiger Jahresumsatz (netto)
- **Mindest-Stundensatz** und **empfohlener Stundensatz** (mit Puffer)
- Tagessatz, Wochensatz
- Ampelfeld: aktueller eigener Stundensatz eintragen → grün/gelb/rot
- Sensitivitätstabelle: Wie sich der Stundensatz bei 40 / 50 / 60 / 70 / 80 %
  Produktivquote verändert — der Aha-Moment des Produkts

**Blatt 4 · Steuerrücklage**

- Monatliche Einnahmen eintragen
- Automatisch: Rücklage für Einkommensteuer, Umsatzsteuer, ggf. Gewerbesteuer
- Laufender Soll-Stand des Rücklagenkontos
- Ist-Stand eintragbar → Warnung bei Unterdeckung

**Blatt 5 · Liquiditätsplan 12 Monate**

- Erwartete Einnahmen und Ausgaben je Monat
- Fortlaufender Kontostand
- Rote Markierung in Monaten mit negativem Saldo, damit Engpässe vorher sichtbar
  werden

**Blatt 6 · Auswertung**
Zwei Diagramme: Kostenstruktur und Kontostandsverlauf über 12 Monate.

**Blatt 7 · Hinweise & Haftung**
Rechtlicher Hinweis, Nutzungsbedingungen, Kontakt für Rückfragen.

### Technische Umsetzung

- Alle Formelzellen gesperrt, nur Eingabefelder beschreibbar (Blattschutz ohne
  Passwort, damit niemand ausgesperrt wird)
- Dropdown-Listen statt Freitext, wo es geht
- Bedingte Formatierung für die Ampeln
- Keine Makros — sonst blockieren Excel und Google Sheets die Datei
- Druckbereiche eingerichtet, A4 hochkant
- Zusätzlich als Google-Sheets-Version (Kopier-Link), damit es auch ohne Excel
  läuft

### Lieferumfang

1. `.xlsx`-Datei
2. Google-Sheets-Version als Kopier-Link
3. Kurzanleitung als PDF (2 Seiten)

### Preis

| | |
|---|---|
| Einführungspreis (erste ~4 Wochen) | **19 €** |
| Regulär | **29 €** |

Der Einführungspreis dient dazu, die ersten Bewertungen einzusammeln — ohne
Bewertungen verkauft sich auf Marktplätzen nichts.

---

## 4. Reihenfolge danach

**Produkt 2 — Angebots- & Nachkalkulation für Handwerker.** Material, Lohn,
Zuschläge, Deckungsbeitrag, Soll-Ist-Vergleich nach Auftragsende. Baut auf
Produkt 1 auf, gleiche Zielgruppe → Bundle-Verkauf möglich.

**Produkt 3 — Haushaltsplaner.** Nur, wenn der Etsy-Test zeigt, dass dort
Verbraucher-Traffic ankommt, den wir sonst verschenken.

Ein Bundle aus 1 + 2 für 39 € ist erfahrungsgemäß der beste Umsatzhebel, weil
ein erheblicher Teil der Käufer das größere Paket nimmt.

---

## 5. Realistische Erwartung

- Erste Verkäufe: Tage bis Wochen nach Listing
- Ein einzelnes Listing: 0–50 € pro Monat
- Tragfähig wird es ab ca. 15–25 Listings
- Laufender Aufwand danach: wenige Stunden im Monat
- Fixkosten: praktisch null (0,20 $ pro Etsy-Listing alle 4 Monate)

Das ist kein Autopilot. Es ist das Nächstbeste: einmal viel Arbeit, danach
wenig.

---

## 6. Risiken

| Risiko | Gegenmaßnahme |
|---|---|
| Etsy liefert nur Verbraucher-Traffic | Gumroad + Landingpage parallel; Produkt 3 als Auffangbecken |
| Käufer erwartet Steuerberatung | Haftungsausschluss auf Blatt 1 und 7, klare Listing-Beschreibung |
| Google Sheets rechnet anders als Excel | Beide Versionen vor Verkauf durchtesten |
| Zu wenig Sichtbarkeit | Menge: 15–25 Listings statt 3 |
| Steuerrecht ändert sich | Steuersätze als Eingabefelder, nicht fest verdrahtet |

---

## 7. Freigabe

Offen, bevor gebaut wird:

- [ ] Zielgruppe Selbstständige statt Privathaushalt — einverstanden?
- [ ] Produkt 1 wie oben beschrieben — passt der Umfang?
- [ ] Preis 19 € / 29 € — passt?
- [ ] Verkaufskanal Etsy + Gumroad — einverstanden?
