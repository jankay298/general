# Entscheidungen

## Projekt & Technik
- **Ordner `parkol/` im bestehenden Repo.** Das Repository enthält bereits ein anderes Projekt (grafify). ParkOL liegt daher in einem eigenen Unterordner; der GitHub-Pages-Workflow liegt unter `.github/workflows/parkol-pages.yml` im Repo-Wurzelverzeichnis.
- **Vite + Vanilla JS + Leaflet.** Einzige Laufzeitabhängigkeit ist Leaflet. Vitest und Playwright nur für Tests.
- **Playwright auf 1.56.1 gepinnt**, weil die Entwicklungsumgebung genau dazu passende Browser vorinstalliert hat; auf GitHub Actions lädt `npx playwright install` dieselbe Version.
- **Relativer `base: './'`**, damit der Build unter `https://<user>.github.io/<repo>/` ohne Anpassung läuft.
- **Canvas-Renderer** (`preferCanvas`, `tolerance: 10`) für ~500 Objekte: schneller auf dem iPhone und größere Tipp-Toleranz für dünne Linien. Parkhäuser sind DOM-Marker mit Beschriftung (Preis bzw. freie Plätze).

## Zeit & Preise
- **Alle Regeln in Oldenburger Ortszeit.** Intern werden „Wanduhrzeiten“ verwendet (UTC-Felder tragen die Berliner Uhrzeit). Dadurch rechnet die App auch auf Geräten in anderen Zeitzonen richtig. Sommer-/Winterzeitwechsel innerhalb einer Parkdauer werden ignoriert (max. 1 h Abweichung, zweimal im Jahr nachts).
- **„werktags 8–19 Uhr“ = Mo–Sa.** Die Stadt schreibt „werktags“; im Straßenverkehrsrecht gehört der Samstag dazu. Einzelne OSM-Einträge bestätigen „Mo-Sa 08:00-19:00“. Beschilderung bleibt maßgeblich (steht in der App).
- **Feiertage:** gesetzliche Feiertage in Niedersachsen (inkl. Reformationstag) gelten als gebührenfrei, weil sie keine Werktage sind. Fronleichnam/Heilige Drei Könige sind in Niedersachsen keine Feiertage.
- **Abrechnung „je angefangene Viertelstunde“ nur innerhalb der Gebührenzeit.** Wer um 18:30 für 2 Stunden parkt, zahlt 2 Viertelstunden (1,40 €). Über Nacht werden die Abschnitte getrennt berechnet.
- **Aktueller Tarif = Stufe 2024.** Die Verordnung sieht höhere Stufen ab 2025/2026 vor, der Rat hat sie ausgesetzt; die Stadt nennt 2026 weiterhin 2,80 €/h bzw. 1,60 €/h. Für Zone III ist die Aussetzung nicht ausdrücklich belegt → `verifiziert: false`.
- **Parkhaustarife vereinfacht modelliert:** Preis je Takt nach der Periode bei Taktbeginn, Tageshöchstsatz je 24 h ab Einfahrt, dynamische Tarife (Schlosshöfe) als Spanne. App-/Kartenrabatte (Pcard, APCOA FLOW) nur als Text.

## Zonen
- **Zone I ist ein selbst gezeichnetes Polygon** entlang der in der Verordnung genannten Randstraßen (mit 25 m Puffer, weil die Randstraßen dazugehören). Der amtliche Lageplan liegt nicht maschinenlesbar vor. Deshalb: `gebiet.verifiziert: false`, und Objekte **innerhalb von 120 m zur Grenze** bekommen den Hinweis „Zonenzuordnung unbestätigt“. Die Grenze ist als gestrichelte Linie auf der Karte sichtbar.
- **Zone III** als grobes Rechteck um die Weser-Ems-Halle.
- **Pferdemarkt** ist per Verordnung ausdrücklich Zone I; der Parkplatz Theaterwall wird Zone I zugeordnet (unbestätigt).

## Farben / Kategorien
- **Gelb/Grün nur, wenn OSM eine Gebührenangabe hat** (`fee`, `parking:*:fee`, `fee:conditional`). Die Zone liefert Preis und Zeiten, OSM sagt, *ob* dort Gebühren anfallen. Ohne Angabe → **grau** mit dem Hinweis, welcher Zonentarif gälte, falls ein Automat steht. So wird nichts erfunden – dafür ist ein Teil der Straßen grau.
- **`fee:conditional` aus OSM** hat Vorrang vor den Zonenzeiten (z. B. Parkplätze mit 7–24 Uhr).
- **Parkhäuser sind immer blau** (wie gefordert), Beschriftung: freie Plätze (live), sonst Preis, sonst „?“. Unbestätigte Tarife haben einen gestrichelten gelben Rand.
- **Kundenparkplätze** (`access=customers`) sind standardmäßig ausgeblendet (Schalter „Kundenparkplätze zeigen“), private Flächen werden gar nicht geladen.
- **„unbestätigt“-Kennzeichnung** erscheint, wenn eine *konkret angezeigte* Angabe aus `tarife.json` `verifiziert: false` ist (Preis, Gebührenzeiten, Tarif, Zonenzuordnung). Fehlende Angaben („keine Höchstparkdauer bekannt“) sind keine Behauptung und lösen den Hinweis nicht aus. OSM-Angaben werden in den Quellen ausdrücklich als „Community-Daten“ bezeichnet.

## Live-Belegung
- **Quelle:** Parkleitsystem der Stadt, `https://oldenburg-service.de/pls.php` (HTML-Tabelle, laut Stadt alle 5 Minuten aktualisiert). Im städtischen Open-Data-Portal gibt es keinen Belegungsdatensatz, in der Mobilithek wurde keiner gefunden (nicht abschließend prüfbar); die Seite sendet **keinen CORS-Header**, der Browser darf sie also nicht direkt lesen.
- **Lösung ohne eigenen Server:** Ein GitHub-Actions-Job ruft die Seite alle 15 Minuten ab (`scripts/fetch-live.mjs`), legt `live.json` in den Build und veröffentlicht neu. Die App zeigt den **Stand der Daten** an, markiert Daten älter als 45 Minuten als **„veraltet“** und zeigt ohne Daten **„keine Live-Daten“**. Es wird nie ein Wert ohne Zeitstempel angezeigt.
- **Kein direkter Abrufversuch im Browser**, weil CORS-Fehler in der Konsole landen würden, ohne je Daten zu liefern. Die Schnittstelle (`LIVE_SOURCES` in `src/live.js`) ist für eine künftige CORS-fähige Quelle vorbereitet.
- **Zuordnung Live-Eintrag ↔ OSM-Objekt** steht in `tarife.json` (`live_name`). Unsichere Zuordnungen (ZOB, Stadthafen) sind vermerkt.

## Suche
- **Nominatim** nur auf ausdrückliches Absenden (keine Autovervollständigung), max. 1 Anfrage/s, Ergebnisse gecacht, auf das Stadtgebiet begrenzt (`viewbox`, `bounded=1`), Quellenangabe in der Ergebnisliste – gemäß Nutzungsrichtlinie.

## Iterationen nach Screenshots
1. Mobil war die Seite breiter als 390 px (Grid-Spalte wuchs mit dem Inhalt) → `grid-template-columns: minmax(0, 1fr)`; Filter als 2×2-Raster.
2. Detailansicht lag unter den Filtern und war erst nach Scrollen sichtbar → an den Anfang des Panels verschoben, Panel scrollt beim Antippen nach oben.
3. „unbestätigt: Höchstparkdauer“ stand an fast jedem Objekt, obwohl gar keine Höchstparkdauer behauptet wird → nur noch bei konkreten Angaben.
4. Parkhäuser ohne Tarif zeigten „P P“ → „P ?“; mit Live-Daten „P 231 frei“.
5. Startausschnitt auf Innenstadt inkl. Stau/Alter Stadthafen (`fitBounds`).
