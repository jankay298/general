#!/usr/bin/env node
// Lädt Parkflächen der Oldenburger Innenstadt einmalig aus OpenStreetMap
// (Overpass API) und speichert sie als GeoJSON in data/parking.geojson.
//
// Aufruf: npm run fetch:osm
// Optional: OVERPASS_URL=https://… npm run fetch:osm
//
// Daten © OpenStreetMap-Mitwirkende, ODbL 1.0.

import { writeFile, mkdir } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { osmToGeoJSON } from './osm-to-geojson.mjs';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const OUT = resolve(ROOT, 'data/parking.geojson');

// Innenstadt Oldenburg inkl. Bahnhof, Stau, Alter Stadthafen und Pferdemarkt.
// Reihenfolge: Süd, West, Nord, Ost
export const BBOX = [53.128, 8.19, 53.155, 8.245];

const ENDPOINTS = [
  process.env.OVERPASS_URL,
  'https://overpass-api.de/api/interpreter',
  'https://overpass.private.coffee/api/interpreter',
  'https://maps.mail.ru/osm/tools/overpass/api/interpreter',
].filter(Boolean);

const bbox = BBOX.join(',');
const QUERY = `
[out:json][timeout:120];
(
  nwr["amenity"="parking"](${bbox});
  way["highway"]["parking:both"](${bbox});
  way["highway"]["parking:left"](${bbox});
  way["highway"]["parking:right"](${bbox});
  way["highway"]["parking:lane:both"](${bbox});
  way["highway"]["parking:lane:left"](${bbox});
  way["highway"]["parking:lane:right"](${bbox});
);
out tags geom;
`;

async function query(url) {
  const res = await fetch(url, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/x-www-form-urlencoded',
      'User-Agent': 'ParkOL-datenabruf/1.0 (https://github.com/jankay298/general)',
    },
    body: new URLSearchParams({ data: QUERY }),
    signal: AbortSignal.timeout(180_000),
  });
  const text = await res.text();
  if (!res.ok || !text.trimStart().startsWith('{')) {
    throw new Error(`HTTP ${res.status}: ${text.slice(0, 200).replace(/\s+/g, ' ')}`);
  }
  return JSON.parse(text);
}

async function fetchWithRetry() {
  let lastErr;
  for (let round = 0; round < 3; round++) {
    for (const url of ENDPOINTS) {
      try {
        console.log(`Overpass: ${url} (Versuch ${round + 1})`);
        return await query(url);
      } catch (err) {
        lastErr = err;
        console.warn(`  fehlgeschlagen: ${err.message}`);
        await new Promise((r) => setTimeout(r, 5000 * (round + 1)));
      }
    }
  }
  throw lastErr;
}

const osm = await fetchWithRetry();
const geojson = osmToGeoJSON(osm);
geojson.metadata = {
  quelle: 'OpenStreetMap über Overpass API',
  lizenz: 'ODbL 1.0 – © OpenStreetMap-Mitwirkende',
  abgerufen: new Date().toISOString(),
  osm_stand: osm.osm3s?.timestamp_osm_base ?? null,
  bbox: BBOX,
};
await mkdir(dirname(OUT), { recursive: true });
await writeFile(OUT, JSON.stringify(geojson));
const counts = {};
for (const f of geojson.features) counts[f.properties.kind] = (counts[f.properties.kind] ?? 0) + 1;
console.log(`Gespeichert: ${OUT} – ${geojson.features.length} Objekte`, counts);
