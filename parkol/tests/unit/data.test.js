import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { anchor } from '../../src/geo.js';

const geo = JSON.parse(readFileSync(new URL('../../data/parking.geojson', import.meta.url), 'utf8'));
const inBox = (f, [s, w, n, e]) => {
  const [lon, lat] = anchor(f.geometry);
  return lat >= s && lat <= n && lon >= w && lon <= e;
};

describe('OSM-Datensatz data/parking.geojson', () => {
  it('hat Metadaten mit Quelle und Lizenz', () => {
    expect(geo.metadata.lizenz).toContain('ODbL');
    expect(geo.metadata.osm_stand).toBeTruthy();
  });
  it('enthält Parkflächen in der Innenstadt (Schlossplatz bis Pferdemarkt)', () => {
    const n = geo.features.filter((f) => inBox(f, [53.136, 8.205, 53.148, 8.22])).length;
    expect(n).toBeGreaterThan(50);
  });
  it('enthält Parkflächen im Bereich Stau / Alter Stadthafen', () => {
    const n = geo.features.filter((f) => inBox(f, [53.139, 8.218, 53.1435, 8.235])).length;
    expect(n).toBeGreaterThan(10);
  });
  it('enthält Parkhäuser, Parkplätze und Straßenparken', () => {
    const kinds = new Set(geo.features.map((f) => f.properties.kind));
    expect([...kinds].sort()).toEqual(['garage', 'lot', 'street']);
  });
  it('alle Features haben gültige Geometrie und eindeutige IDs', () => {
    const ids = new Set();
    for (const f of geo.features) {
      expect(anchor(f.geometry)).not.toBeNull();
      expect(ids.has(f.id)).toBe(false);
      ids.add(f.id);
    }
  });
});
