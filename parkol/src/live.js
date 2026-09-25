// Live-Belegung der Parkhäuser.
//
// Schnittstelle: Eine Quelle liefert JSON der Form
//   { status: 'ok'|'fehler', stand: ISO-Zeit, eintraege: [{ name, gesamt, frei, status, trend }] }
// Standardquelle ist die Datei live.json neben der App, die ein GitHub-Actions-Job
// aus dem Parkleitsystem der Stadt erzeugt. Weitere Quellen (z. B. eine künftige
// Open-Data-API mit CORS-Freigabe) können in LIVE_SOURCES ergänzt werden.

export const LIVE_SOURCES = [
  { name: 'ParkOL-Spiegel des Parkleitsystems', url: 'live.json' },
];

/** Bewertet eine geladene Live-Datei: state 'ok' | 'stale' | 'none'. */
export function interpretLive(data, now, maxAgeMin) {
  if (!data || data.status !== 'ok' || !Array.isArray(data.eintraege) || !data.eintraege.length || !data.stand) {
    return { state: 'none', stand: null, byName: new Map() };
  }
  const stand = new Date(data.stand);
  const ageMin = (now - stand) / 60_000;
  const byName = new Map(data.eintraege.map((e) => [e.name, e]));
  return { state: ageMin <= maxAgeMin ? 'ok' : 'stale', stand, ageMin, byName };
}

/** Lädt die erste erreichbare Quelle. Fehler werden still behandelt (→ state 'none'). */
export async function loadLive(base, maxAgeMin, fetchImpl = fetch) {
  for (const src of LIVE_SOURCES) {
    try {
      const url = new URL(src.url, base);
      url.searchParams.set('t', String(Math.floor(Date.now() / 60_000)));
      const res = await fetchImpl(url, { cache: 'no-store' });
      if (!res.ok) continue;
      const data = await res.json();
      const r = interpretLive(data, new Date(), maxAgeMin);
      if (r.state !== 'none') return { ...r, source: src.name };
    } catch {
      // nächste Quelle versuchen
    }
  }
  return { state: 'none', stand: null, byName: new Map() };
}
