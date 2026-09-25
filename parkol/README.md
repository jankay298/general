# ParkOL – Parken in der Oldenburger Innenstadt

Kostenlose Web-App (PWA) für iPhone-Safari und Desktop. Zeigt auf einer Karte:

- wo Parkplätze, Parkstreifen und Parkhäuser sind (OpenStreetMap),
- was Parken dort **jetzt** kostet – oder zu einem gewählten Zeitpunkt („Samstag 14 Uhr“),
- wo es gerade **kostenlos** ist (z. B. Straßenparken ab 19 Uhr, sonntags),
- bei Parkhäusern die **freien Plätze** aus dem Parkleitsystem der Stadt (mit Zeitstempel).

Farben: 🟢 jetzt kostenlos · 🟡 kostenpflichtig · 🔵 Parkhaus · ⚪ grau = Regeln unbekannt.
Angaben, die nicht amtlich oder beim Betreiber belegt sind, tragen den Hinweis **„unbestätigt“**.

Keine Kosten, keine API-Keys: Leaflet + OSM-Kacheln, Nominatim-Suche, statisches Hosting auf GitHub Pages.

---

## Lokal starten

Voraussetzung: Node.js 20 oder neuer.

```bash
cd parkol
npm install
npm run dev          # Entwicklungsserver: http://localhost:5173
npm run build        # Produktions-Build nach dist/
npm run preview      # Build ansehen: http://localhost:4173
```

Tests:

```bash
npm run test:unit    # Unit-Tests (Preis-/Zeitlogik, Parser, Daten)
npx playwright install chromium   # einmalig
npm run test:e2e     # baut und startet Playwright (mobil 390×844 + Desktop)
npm test             # alles
```

Die E2E-Tests legen Screenshots unter `screenshots/` ab.

## Auf GitHub Pages veröffentlichen

1. Branch mit `parkol/` und `.github/workflows/parkol-pages.yml` in den **Standard-Branch** mergen.
2. Auf GitHub: **Settings → Pages → Build and deployment → Source: „GitHub Actions“**.
3. Der Workflow **ParkOL** läuft bei jedem Push (Tests → Build → Deployment) und **alle 15 Minuten**
   (nur Build + Live-Daten + Deployment). Manuell: Tab *Actions* → *ParkOL* → *Run workflow*.
4. Die App liegt dann unter `https://<benutzer>.github.io/<repo>/` – hier: `https://jankay298.github.io/general/`.

Auf dem iPhone: Seite in Safari öffnen → Teilen → **„Zum Home-Bildschirm“**.

Hinweise:
- Für öffentliche Repos sind Actions und Pages kostenlos. GitHub pausiert zeitgesteuerte Workflows
  nach 60 Tagen ohne Aktivität im Repo – dann im Tab *Actions* wieder aktivieren.
- Zeitgesteuerte Läufe starten bei GitHub oft einige Minuten verspätet; die App zeigt daher immer den Stand der Live-Daten.

## `data/tarife.json` pflegen

Alle Preise und Regeln stehen in `data/tarife.json`. Nach einer Änderung: `npm test`, committen, pushen – der Workflow veröffentlicht automatisch.

Aufbau:

| Bereich | Inhalt |
| --- | --- |
| `feiertage` | Liste der gebührenfreien Feiertage (Namen siehe `src/time.js`, z. B. `reformationstag`) |
| `zonen[]` | `preis` (`takt_minuten`, `preis_pro_takt`, optional `tageshoechstsatz`), `zeiten.gebuehrenpflichtig` (Zeitfenster), `hoechstparkdauer.minuten`, `gebiet.punkte` (Polygon, `[Breite, Länge]`) |
| `parkhaeuser[]` | `osm_ids` (Verknüpfung zur Karte), `live_name` (Name im Parkleitsystem), `tarif.perioden[]`, `tarif.tageshoechstsatz`, `tarif.text` |
| `live` | `max_alter_minuten`: ab wann Live-Daten als „veraltet“ gelten |

Regeln:
- **Jede Angabe braucht `quelle` (Text) und `verifiziert` (true/false).** `true` nur bei amtlicher Quelle oder Betreiberseite. Alles andere `false` → die App zeigt „unbestätigt“. Ein Unit-Test prüft, dass die Felder vorhanden sind.
- Zeitfenster: `{ "tage": ["Mo","Di","Mi","Do","Fr","Sa"], "von": "08:00", "bis": "19:00" }` – `bis` ist exklusiv (19:00 = ab 19 Uhr frei), `"Fe"` = Feiertag, `"24:00"` erlaubt, über Mitternacht (z. B. `22:00`–`06:00`) erlaubt.
- Parkhaus-Periode: `takt_minuten` + `preis_pro_takt`, optional `erste_stunde` (Preis der ersten Stunde), `preis_pro_takt_min` (dynamischer Tarif: Spanne), `max` (Obergrenze innerhalb der Periode).
- OSM-ID eines Parkhauses finden: auf [openstreetmap.org](https://www.openstreetmap.org) das Objekt anklicken, die URL enthält z. B. `way/30251488`.
- Beispiel „Parkgebühren steigen auf 3,60 €/h“: in Zone I `preis_pro_takt` auf `0.90` setzen, `text`, `quelle` und ggf. `verifiziert` anpassen.

## OSM-Daten neu laden

```bash
cd parkol
npm run fetch:osm          # schreibt data/parking.geojson
npm test                   # prüft u. a. Abdeckung Innenstadt/Stau/Hafen
```

Das Skript fragt die Overpass API einmalig ab (mit Ausweichservern) – die App selbst fragt Overpass nie.
Gebiet: `BBOX` in `scripts/fetch-osm.mjs`. Anderer Server: `OVERPASS_URL=https://… npm run fetch:osm`.
Fehlende oder falsche Parkinfos am besten direkt in OpenStreetMap verbessern (Tags `fee`, `parking:both=lane`, `parking:both:fee`, `parking:both:fee:conditional`, `maxstay`), danach neu laden.

## Live-Belegung der Parkhäuser

- **Quelle:** Parkleitsystem der Stadt Oldenburg, <https://oldenburg-service.de/pls.php> (10 Parkhäuser/-plätze, laut Stadt alle 5 Min. aktualisiert).
- **Was fehlt:** Eine offizielle Schnittstelle. Im Open-Data-Portal der Stadt (<https://opendata.oldenburg.de>, per API geprüft am 25.09.2026, 90 Datensätze) gibt es **keinen Datensatz zur Parkhausbelegung**. In der **Mobilithek** habe ich keinen Oldenburger Belegungsdatensatz gefunden; die dortige Suche ließ sich ohne Browser/Konto aber nicht abschließend prüfen. Die Parkleitsystem-Seite erlaubt **kein CORS** – der Browser darf sie nicht direkt lesen.
- **Ausblick:** Nach der EU-Verordnung 2024/490 sollen dynamische Belegungsdaten bis **1. Dezember 2026** über den nationalen Zugangspunkt (Mobilithek) bereitstehen. Danach lohnt es sich, dort erneut nach Oldenburg zu suchen.
- **Lösung:** Der GitHub-Actions-Job liest die Seite alle 15 Minuten aus (`scripts/fetch-live.mjs`) und veröffentlicht `live.json` mit der App. Die App zeigt den Stand, kennzeichnet Daten älter als 45 Minuten als „veraltet“ und zeigt sonst „keine Live-Daten“.
- **Was besser wäre:** eine offizielle Schnittstelle der Stadt (z. B. DATEX II über die Mobilithek oder JSON mit `Access-Control-Allow-Origin: *`). Dann in `src/live.js` → `LIVE_SOURCES` eintragen; Format siehe Kommentar dort.
- Lokal testen: `npm run fetch:live -- public/live.json` (Datei danach nicht committen; im Repo liegt ein Platzhalter).
- Parkhäuser, die nicht im Parkleitsystem sind (z. B. Staulinie, Rosenstraße, City Center), zeigen immer „keine Live-Daten“.

## Welche Daten fehlen oder sind unbestätigt

Stand der Recherche: 25.09.2026.

**Belegt (`verifiziert: true`):**
- Zone I 2,80 €/h und Zone II 1,60 €/h, je angefangene Viertelstunde – Stadt Oldenburg (Ratsbeschluss 11/2024, Seite „Parken“ 2026).
- Gebührenpflicht „werktags 8–19 Uhr“ – Stadt Oldenburg (als Mo–Sa ausgelegt).
- Parkhaustarife Waffenplatz, Staulinie (Contipark), Theatergarage (APCOA), Schlosshöfe, August Carrée – Betreiberseiten.
- Feiertage Niedersachsen.

**Unbestätigt (`verifiziert: false`):**
- **Grenze der Zone I** – eigenes Polygon nach der Straßenliste der Verordnung, nicht der amtliche Lageplan. Objekte nahe der Grenze werden markiert.
- **Zone III** (Weser-Ems-Halle): Preis (Stufe 2024) und Gebiet.
- **Höchstparkdauer**: keine allgemeine Regel bekannt; angezeigt wird nur, was in OSM steht.
- **Parkhäuser Heiligengeist-Höfe, Galeria, Alter Stadthafen/CinemaxX**: Tarife aus Drittquellen.
- **P+R-Parkhaus ZOB**: nur Tagesmaximum 5 € aus der Presse; Stundentarif unbekannt, geplante Erhöhung auf 8 € ungeklärt.
- **Parkhaus Rosenstraße, Parkdeck City Center** und weitere OSM-Parkhäuser: Tarif unbekannt.
- **Zuordnung Live-Einträge** „Parkhaus Bahnhof / ZOB“ und „Alter Stadthafen / Cinemaxx“ zu den OSM-Objekten.

**Grundsätzlich:** Ob an einer Straße Gebühren anfallen, kommt aus OpenStreetMap (Community-Daten). Straßen ohne Gebührenangabe in OSM erscheinen grau. Veranstaltungen, Marktage, Baustellen und Bewohnerparkregeln sind nicht vollständig abgebildet.

## Aufbau

```
parkol/
├── data/parking.geojson    OSM-Parkflächen (generiert, eingecheckt)
├── data/tarife.json        Preise & Regeln (von Hand gepflegt)
├── scripts/                fetch-osm, osm-to-geojson, fetch-live, make-icons
├── src/time.js             Ortszeit, Feiertage
├── src/windows.js          Zeitfenster, OSM-opening_hours
├── src/pricing.js          Zonen- und Parkhauspreise
├── src/rules.js            OSM-Objekt + tarife.json → Bewertung
├── src/live*.js            Live-Belegung (Parser, Client)
├── src/search.js           Nominatim-Suche
├── src/main.js, style.css  Oberfläche
├── public/                 Manifest, Service Worker, Icons, live.json-Platzhalter
└── tests/unit, tests/e2e   Vitest, Playwright
```

## Lizenzen & Quellen

- Kartendaten und Parkflächen © [OpenStreetMap-Mitwirkende](https://www.openstreetmap.org/copyright), ODbL 1.0.
- Kartenkacheln: OpenStreetMap Foundation ([Tile Usage Policy](https://operations.osmfoundation.org/policies/tiles/)); Suche: [Nominatim](https://operations.osmfoundation.org/policies/nominatim/).
- Gebühren: Parkgebührenordnung der Stadt Oldenburg (3.51) und Veröffentlichungen der Stadt; Parkhaustarife der Betreiber.
- Live-Belegung: Parkleitsystem der Stadt Oldenburg.

Bei stärkerer Nutzung sollte ein eigener Kachel-Anbieter gewählt werden (die OSM-Kachelserver sind nicht für stark frequentierte Apps gedacht).
