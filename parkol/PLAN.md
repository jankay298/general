# ParkOL – Plan

Status: ✅ erledigt · ⚠️ erledigt mit Einschränkung (siehe Anmerkung)

## 1. Recherche
- ✅ Oldenburger Parkgebührenordnung (3.51, Stand 1.9.2023) gelesen: Zonen I/II/III, Stufenplan.
- ✅ Ratsbeschluss 25.11.2024: Erhöhung ab 2025 ausgesetzt → Zone I 2,80 €/h, Zone II 1,60 €/h (Stadtseite „Parken“ bestätigt das 2026 weiterhin).
- ✅ Gebührenzeiten: Stadtseite „werktags 8–19 Uhr“.
- ✅ Parkhaustarife bei Betreibern: Contipark (Waffenplatz, Staulinie), APCOA (Theatergarage), Schlosshöfe, August Carrée.
- ⚠️ Heiligengeist-Höfe, Galeria, ZOB, Stadthafen/CinemaxX: nur Drittquellen → `verifiziert: false`.
- ✅ Live-Belegung: Parkleitsystem der Stadt (`oldenburg-service.de/pls.php`), HTML, keine CORS-Freigabe, keine offizielle API gefunden.

## 2. Daten
- ✅ `scripts/fetch-osm.mjs`: Overpass-Abfrage (amenity=parking, parking:left/right/both, altes parking:lane-Schema), Umwandlung in schlankes GeoJSON (`scripts/osm-to-geojson.mjs`), Ausfall-Server.
- ✅ `data/parking.geojson` eingecheckt (Innenstadt inkl. Bahnhof, Stau, Alter Stadthafen, Pferdemarkt, Weser-Ems-Halle).
- ✅ `data/tarife.json` mit `quelle`/`verifiziert` je Angabe.

## 3. Logik (reine Funktionen, getestet)
- ✅ Wanduhrzeit Europe/Berlin (`src/time.js`), Feiertage Niedersachsen inkl. Ostern-basierter Feiertage.
- ✅ Zeitfenster + einfacher OSM-opening_hours-Parser (`src/windows.js`).
- ✅ Zonenpreise je angefangenem Takt, nur innerhalb der Gebührenzeit, Tageshöchstsatz (`src/pricing.js`).
- ✅ Parkhaustarife: Perioden, erste Stunde, dynamische Spanne, Tages-/Periodenmaximum.
- ✅ Bewertung je OSM-Objekt → grün/gelb/blau/grau, „kostenlos ab“, Höchstparkdauer, Quellen, „unbestätigt“ (`src/rules.js`).

## 4. Oberfläche
- ✅ Leaflet + OSM-Kacheln, Quellenangabe.
- ✅ Filter: Alle / Jetzt kostenlos / Parkhäuser / Straße/Parkplatz; Kundenparkplätze optional.
- ✅ Zeitauswahl Jetzt/Planen (Tag + Uhrzeit), Parkdauer für Kostenschätzung.
- ✅ Detailansicht mit Quellen und Kennzeichnung „unbestätigt“.
- ✅ Mein Standort, Adresssuche (Nominatim), Liste nach Entfernung.
- ✅ Mobile first (390×844 ohne horizontales Scrollen), Desktop mit Seitenleiste.
- ✅ PWA: Manifest, Icons, Service Worker.

## 5. Live-Daten
- ✅ `scripts/fetch-live.mjs` + `src/live-parse.js`: HTML → `live.json`.
- ✅ GitHub-Actions-Job alle 15 Min.: bauen, Live-Daten abrufen, auf Pages veröffentlichen.
- ✅ Client: frisch / veraltet / „keine Live-Daten“; Live nur im Modus „Jetzt“.

## 6. Tests & Selbstkontrolle
- ✅ Unit-Tests (Vitest): Randfälle 18:59/19:00, 07:59/08:00, Samstag, Sonntag, Feiertage, Mitternacht, Takt, Tageshöchstsatz, Parkhäuser, Parser, Datenabdeckung.
- ✅ E2E (Playwright, mobil 390×844 + Desktop): Karte, Marker, Filter, Zeitauswahl, Details, Live-Zustände, Suche, Standort, PWA, keine Konsolenfehler, kein horizontales Scrollen.
- ✅ Screenshots geprüft und Layout verbessert (siehe DECISIONS.md, „Iterationen“).

## 7. Dokumentation & Deployment
- ✅ README.md, DECISIONS.md, Workflow `.github/workflows/parkol-pages.yml`.
