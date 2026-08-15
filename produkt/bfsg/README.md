# BFSG-Prüfwerkzeug

Automatisierte Vorprüfung von Websites nach **WCAG 2.1 Stufe AA** — die technische
Grundlage für den bezahlten Prüfbericht.

## Einrichten

```bash
cd produkt/bfsg
npm install
npx playwright install chromium   # nur beim ersten Mal
```

## Benutzen

```bash
node scan.mjs https://kundenseite.de              # Startseite + bis zu 5 Unterseiten
node scan.mjs https://kundenseite.de --seiten 12  # mehr Unterseiten
node scan.mjs https://kundenseite.de --nur-start  # nur die Startseite
```

Ergebnis landet in `bericht/`:

| Datei | Zweck |
|---|---|
| `<domain>-<datum>.json` | Rohdaten, für Nachprüfungen und Vergleiche |
| `<domain>-<datum>.html` | Der Bericht für den Kunden |

Den HTML-Bericht im Browser öffnen und über **Drucken → Als PDF sichern** ausliefern.

## Was das Werkzeug prüft

Es trennt zwei Dinge sauber, weil sie rechtlich unterschiedlich wiegen:

- **Verstöße** — Regeln, die einem WCAG-Erfolgskriterium zugeordnet sind. Nur diese sind
  im BFSG-Kontext rechtlich relevant.
- **Empfehlungen** — Regeln ohne WCAG-Bezug (Überschriftenstruktur, Landmarken). Nicht
  zwingend, aber deutlich spürbar für die Bedienbarkeit.

Schweregrade: kritisch, schwer, mittel, gering. Befunde werden über alle geprüften Seiten
zusammengefasst, sodass ein Fehler im Seitenkopf einmal auftaucht und nicht dreißigmal.

## Was es NICHT prüft — bitte ernst nehmen

Automatische Prüfung erfasst nur einen Teil der tatsächlichen Barrieren, typischerweise
etwa ein Drittel. **Nicht** abgedeckt und nur manuell beurteilbar:

- Ob die Seite vollständig **mit der Tastatur** bedienbar ist
- Ob die **Fokusreihenfolge** sinnvoll ist und der Fokus sichtbar bleibt
- Ob **Alternativtexte inhaltlich sinnvoll** sind (`alt="Bild"` besteht die Maschinenprüfung)
- Ob **Fehlermeldungen in Formularen** verständlich und zugänglich sind
- Ob **Sprache und Inhalte** verständlich sind
- Verhalten mit echten **Screenreadern**

Deshalb ist der Ablauf beim bezahlten Auftrag: erst der automatische Lauf, dann eine
manuelle Durchsicht, dann der Bericht.

## Rechtliche Grenze

Das Werkzeug erzeugt eine **technische Befundaufnahme**, kein Testat und keine
Rechtsberatung. Formulierungen wie „rechtssicher", „BFSG-konform" oder „abmahnsicher"
gehören nicht in ein Angebot — wer das zusagt, haftet dafür. Zulässig und ausreichend ist:
*„Technische Prüfung nach WCAG 2.1 AA mit dokumentiertem Befund und Handlungsempfehlungen."*

## Ablauf eines Auftrags

1. `node scan.mjs <url> --seiten 10`
2. Manuelle Durchsicht: Tastaturbedienung, Fokus, Alternativtexte, Formularfehler
3. Manuelle Befunde im Bericht ergänzen
4. Als PDF ausliefern
5. Optional: Umsetzung anbieten, danach Nachprüfung mit demselben Werkzeug — die
   JSON-Datei des ersten Laufs belegt den Fortschritt

## Testen

`test/fixture.html` enthält absichtlich eingebaute Fehler (fehlendes `lang`, Bild ohne
`alt`, zu schwacher Kontrast, `<iframe>` ohne `title`, fehlender `<title>`, Sprung von
`h2` auf `h4`, fehlende `h1`). So lässt sich nach Änderungen prüfen, ob die Engine noch
findet, was sie finden soll:

```bash
cd test && python3 -m http.server 8099 &
node ../scan.mjs http://127.0.0.1:8099/fixture.html --nur-start
```

Erwartet werden 5 WCAG-Verstöße (darunter `image-alt` als kritisch) und 4 Empfehlungen
(darunter `heading-order` und `page-has-heading-one`).
