// Adresssuche über Nominatim (OpenStreetMap).
// Nutzungsrichtlinie: https://operations.osmfoundation.org/policies/nominatim/
// – höchstens 1 Anfrage pro Sekunde, keine Autovervollständigung (Suche nur auf
//   ausdrückliches Absenden), Ergebnisse werden zwischengespeichert,
//   Quellenangabe wird angezeigt. Der Browser sendet den Referer der App mit.

const ENDPOINT = 'https://nominatim.openstreetmap.org/search';
// Stadtgebiet Oldenburg (links, oben, rechts, unten)
const VIEWBOX = '8.12,53.21,8.33,53.08';
const MIN_INTERVAL_MS = 1100;

let last = 0;
const cache = new Map();

export async function searchPlaces(query, fetchImpl = fetch) {
  const q = query.trim();
  if (cache.has(q)) return cache.get(q);
  const wait = last + MIN_INTERVAL_MS - Date.now();
  if (wait > 0) await new Promise((r) => setTimeout(r, wait));
  last = Date.now();
  const url = new URL(ENDPOINT);
  url.search = new URLSearchParams({
    q: /oldenburg/i.test(q) ? q : `${q}, Oldenburg`,
    format: 'jsonv2',
    limit: '5',
    viewbox: VIEWBOX,
    bounded: '1',
    countrycodes: 'de',
    'accept-language': 'de',
  });
  let res;
  try {
    res = await fetchImpl(url, { headers: { Accept: 'application/json' } });
  } catch {
    throw new Error('Suche nicht erreichbar (offline?).');
  }
  if (!res.ok) throw new Error(`Suche fehlgeschlagen (HTTP ${res.status}).`);
  const data = await res.json();
  const results = data.map((r) => {
    const parts = String(r.display_name).split(',').map((s) => s.trim());
    return { name: r.name || parts[0], detail: parts.slice(1, 3).join(', '), point: [+r.lon, +r.lat] };
  });
  cache.set(q, results);
  return results;
}
