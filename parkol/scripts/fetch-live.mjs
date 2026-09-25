#!/usr/bin/env node
// Holt die aktuelle Parkhausbelegung vom Parkleitsystem der Stadt Oldenburg
// und schreibt sie als JSON-Datei (Standard: dist/live.json).
//
// Die Quelle (HTML ohne CORS-Freigabe) kann der Browser nicht direkt lesen,
// deshalb läuft dieses Skript im GitHub-Actions-Job vor jedem Deployment.
//
// Aufruf: node scripts/fetch-live.mjs [ziel.json]
// Schlägt der Abruf fehl, wird eine Datei mit status "fehler" geschrieben –
// die App zeigt dann "keine Live-Daten".

import { writeFile, mkdir } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { parsePls } from '../src/live-parse.js';

const SOURCE = 'https://oldenburg-service.de/pls.php';
const out = resolve(process.argv[2] ?? 'dist/live.json');

let result;
try {
  const res = await fetch(SOURCE, {
    headers: { 'User-Agent': 'ParkOL/1.0 (+https://github.com/jankay298/general)' },
    signal: AbortSignal.timeout(30_000),
  });
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  const parsed = parsePls(await res.text());
  if (!parsed.eintraege.length) throw new Error('Keine Einträge gefunden – Seitenstruktur geändert?');
  result = { status: 'ok', quelle: SOURCE, abgerufen: new Date().toISOString(), ...parsed };
} catch (err) {
  console.error(`Live-Daten nicht verfügbar: ${err.message}`);
  result = { status: 'fehler', quelle: SOURCE, abgerufen: new Date().toISOString(), fehler: err.message, stand: null, eintraege: [] };
}

await mkdir(dirname(out), { recursive: true });
await writeFile(out, JSON.stringify(result, null, 1));
console.log(`${out}: ${result.status}, ${result.eintraege.length} Einträge, Stand ${result.stand}`);
