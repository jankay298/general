import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { berlinToIso, parsePls } from '../../src/live-parse.js';
import { interpretLive } from '../../src/live.js';

const html = readFileSync(new URL('./fixtures/pls.html', import.meta.url), 'utf8');

describe('Parkleitsystem-Parser', () => {
  it('liest alle Einträge und den Stand', () => {
    const r = parsePls(html);
    expect(r.eintraege.length).toBe(10);
    expect(r.eintraege[0]).toMatchObject({ name: 'Parkhaus Am Waffenplatz', gesamt: 568, frei: 231, status: 'Offen' });
    expect(r.eintraege.find((e) => e.name === 'Parkhaus Theatergarage').status).toBe('Störung');
    expect(r.stand).toBe('2026-09-25T08:48:00.000Z');
  });
  it('rechnet Berliner Zeit korrekt in UTC um (Sommer/Winter)', () => {
    expect(berlinToIso('01.07.2026 12:00:00')).toBe('2026-07-01T10:00:00.000Z');
    expect(berlinToIso('01.12.2026 12:00:00')).toBe('2026-12-01T11:00:00.000Z');
  });
});

describe('Live-Daten im Client', () => {
  const data = { status: 'ok', stand: '2026-09-25T08:48:00.000Z', eintraege: [{ name: 'A', gesamt: 10, frei: 3, status: 'Offen' }] };
  it('frische Daten → ok', () => {
    const r = interpretLive(data, new Date('2026-09-25T09:00:00Z'), 45);
    expect(r.state).toBe('ok');
    expect(r.byName.get('A').frei).toBe(3);
  });
  it('alte Daten → stale', () => {
    expect(interpretLive(data, new Date('2026-09-25T10:00:00Z'), 45).state).toBe('stale');
  });
  it('Fehler oder fehlende Datei → none', () => {
    expect(interpretLive(null, new Date(), 45).state).toBe('none');
    expect(interpretLive({ status: 'fehler', eintraege: [] }, new Date(), 45).state).toBe('none');
  });
});
