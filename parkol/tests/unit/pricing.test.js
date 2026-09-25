import { describe, expect, it } from 'vitest';
import tarife from '../../data/tarife.json';
import { garageCost, garageHourly, zoneCost, zoneStatus, euro } from '../../src/pricing.js';
import { prepareRules } from '../../src/rules.js';
import { easterSunday, formatHM, holidayName, nowInOldenburg, wall } from '../../src/time.js';

const rules = prepareRules(tarife);
const H = rules.holidays;
const zone = (id) => rules.zones.find((z) => z.id === id);
const garage = (id) => tarife.parkhaeuser.find((p) => p.id === id).tarif;

// Referenzwoche: Mo 21.09.2026 … So 27.09.2026
const MO = 21;

describe('Zone I – Gebührenzeiten', () => {
  const r = zone('I').rule;

  it('Mittwoch 18:59 ist kostenpflichtig, 19:00 kostenlos', () => {
    expect(zoneStatus(r, wall(2026, 9, MO + 2, 18, 59), H).paid).toBe(true);
    expect(zoneStatus(r, wall(2026, 9, MO + 2, 19, 0), H).paid).toBe(false);
  });

  it('07:59 kostenlos, 08:00 kostenpflichtig', () => {
    expect(zoneStatus(r, wall(2026, 9, MO, 7, 59), H).paid).toBe(false);
    expect(zoneStatus(r, wall(2026, 9, MO, 8, 0), H).paid).toBe(true);
  });

  it('Samstag 14:00 kostenpflichtig (werktags = Mo–Sa)', () => {
    const st = zoneStatus(r, wall(2026, 9, MO + 5, 14, 0), H);
    expect(st.paid).toBe(true);
    expect(st.hourly).toBe(2.8);
    expect(formatHM(st.changeAt)).toBe('19:00');
  });

  it('Sonntag ganztägig kostenlos, nächste Gebührenpflicht Montag 08:00', () => {
    const st = zoneStatus(r, wall(2026, 9, 27, 12, 0), H);
    expect(st.paid).toBe(false);
    expect(st.changeAt).toEqual(wall(2026, 9, 28, 8, 0));
  });

  it('Mitternacht: 23:59 und 00:00 kostenlos, Wechsel um 08:00', () => {
    expect(zoneStatus(r, wall(2026, 9, MO, 23, 59), H).paid).toBe(false);
    const st = zoneStatus(r, wall(2026, 9, MO + 1, 0, 0), H);
    expect(st.paid).toBe(false);
    expect(st.changeAt).toEqual(wall(2026, 9, MO + 1, 8, 0));
  });

  it('Feiertag (Tag der Deutschen Einheit, Samstag 03.10.2026) ist kostenlos', () => {
    expect(holidayName(wall(2026, 10, 3, 12), H)).toBe('tag_der_deutschen_einheit');
    expect(zoneStatus(r, wall(2026, 10, 3, 12, 0), H).paid).toBe(false);
  });

  it('Reformationstag (31.10.) ist in Niedersachsen Feiertag', () => {
    expect(zoneStatus(r, wall(2025, 10, 31, 10, 0), H).paid).toBe(false);
  });

  it('Freitag 19:00 → nächste Gebührenpflicht Samstag 08:00', () => {
    const st = zoneStatus(r, wall(2026, 9, MO + 4, 19, 0), H);
    expect(st.changeAt).toEqual(wall(2026, 9, MO + 5, 8, 0));
  });

  it('Samstag 19:00 → nächste Gebührenpflicht Montag 08:00 (Sonntag frei)', () => {
    const st = zoneStatus(r, wall(2026, 9, MO + 5, 19, 0), H);
    expect(st.changeAt).toEqual(wall(2026, 9, 28, 8, 0));
  });

  it('Gründonnerstag 19:00 → Karfreitag frei → Samstag 08:00', () => {
    // Ostern 2026: 5. April → Karfreitag 3. April
    const st = zoneStatus(r, wall(2026, 4, 2, 19, 0), H);
    expect(st.changeAt).toEqual(wall(2026, 4, 4, 8, 0));
  });
});

describe('Zone I – Kosten', () => {
  const r = zone('I').rule;
  it('1 Stunde am Dienstag 10:00 kostet 2,80 €', () => {
    expect(zoneCost(r, wall(2026, 9, MO + 1, 10, 0), 60, H)).toBe(2.8);
  });
  it('je angefangene Viertelstunde: 16 Minuten kosten 1,40 €', () => {
    expect(zoneCost(r, wall(2026, 9, MO + 1, 10, 0), 16, H)).toBe(1.4);
  });
  it('18:30 für 2 Stunden: nur 30 Minuten gebührenpflichtig → 1,40 €', () => {
    expect(zoneCost(r, wall(2026, 9, MO + 1, 18, 30), 120, H)).toBe(1.4);
  });
  it('18:59 für 1 Stunde: eine angefangene Viertelstunde → 0,70 €', () => {
    expect(zoneCost(r, wall(2026, 9, MO + 1, 18, 59), 60, H)).toBe(0.7);
  });
  it('19:00 für 3 Stunden: kostenlos', () => {
    expect(zoneCost(r, wall(2026, 9, MO + 1, 19, 0), 180, H)).toBe(0);
  });
  it('07:30 für 1 Stunde: nur 08:00–08:30 kostet → 1,40 €', () => {
    expect(zoneCost(r, wall(2026, 9, MO + 1, 7, 30), 60, H)).toBe(1.4);
  });
  it('über Mitternacht: Mo 18:00 bis Di 09:00 → 2 × 2,80 €', () => {
    expect(zoneCost(r, wall(2026, 9, MO, 18, 0), 15 * 60, H)).toBe(5.6);
  });
  it('Sonntag: 5 Stunden kostenlos', () => {
    expect(zoneCost(r, wall(2026, 9, 27, 10, 0), 300, H)).toBe(0);
  });
});

describe('Zone II und III', () => {
  it('Zone II: 1,60 €/h', () => {
    expect(zoneStatus(zone('II').rule, wall(2026, 9, MO, 12, 0), H).hourly).toBe(1.6);
    expect(zoneCost(zone('II').rule, wall(2026, 9, MO, 12, 0), 60, H)).toBe(1.6);
  });
  it('Zone III: Tageshöchstsatz 4,00 € greift', () => {
    expect(zoneCost(zone('III').rule, wall(2026, 9, MO, 8, 0), 11 * 60, H)).toBe(4);
  });
});

describe('Feiertage', () => {
  it('Ostersonntag korrekt berechnet', () => {
    expect(easterSunday(2025)).toEqual(wall(2025, 4, 20));
    expect(easterSunday(2026)).toEqual(wall(2026, 4, 5));
    expect(easterSunday(2027)).toEqual(wall(2027, 3, 28));
  });
  it('bewegliche Feiertage 2026', () => {
    expect(holidayName(wall(2026, 4, 3), H)).toBe('karfreitag');
    expect(holidayName(wall(2026, 4, 6), H)).toBe('ostermontag');
    expect(holidayName(wall(2026, 5, 14), H)).toBe('christi_himmelfahrt');
    expect(holidayName(wall(2026, 5, 25), H)).toBe('pfingstmontag');
  });
  it('Fronleichnam und Heilige Drei Könige sind in Niedersachsen keine Feiertage', () => {
    expect(holidayName(wall(2026, 6, 4), H)).toBeNull();
    expect(holidayName(wall(2026, 1, 6), H)).toBeNull();
  });
});

describe('Parkhäuser', () => {
  it('Waffenplatz: 2,20 € je angefangene Stunde, max. 15 €', () => {
    const t = garage('waffenplatz');
    expect(garageCost(t, wall(2026, 9, MO, 10, 0), 60, H)).toEqual({ min: 2.2, max: 2.2 });
    expect(garageCost(t, wall(2026, 9, MO, 10, 0), 61, H)).toEqual({ min: 4.4, max: 4.4 });
    expect(garageCost(t, wall(2026, 9, MO, 10, 0), 12 * 60, H)).toEqual({ min: 15, max: 15 });
  });
  it('Schlosshöfe: dynamischer Tarif als Spanne', () => {
    const t = garage('schlosshoefe');
    expect(garageCost(t, wall(2026, 9, MO, 10, 0), 120, H)).toEqual({ min: 3, max: 5 });
    expect(garageHourly(t, wall(2026, 9, MO, 10, 0), H)).toEqual({ min: 1.5, max: 2.5, firstHour: false });
  });
  it('August Carrée: erste Stunde 3 €, dann 2 € (Mo–Sa tagsüber)', () => {
    const t = garage('august-carree');
    expect(garageCost(t, wall(2026, 9, MO, 10, 0), 150, H)).toEqual({ min: 7, max: 7 });
  });
  it('August Carrée: Sonntag 1,50 €/h, max. 7 € tagsüber', () => {
    const t = garage('august-carree');
    expect(garageCost(t, wall(2026, 9, 27, 10, 0), 120, H).max).toBe(3);
    expect(garageCost(t, wall(2026, 9, 27, 8, 0), 10 * 60, H).max).toBe(7);
  });
  it('August Carrée: Feiertag nutzt Sonntagstarif', () => {
    const t = garage('august-carree');
    expect(garageHourly(t, wall(2026, 10, 3, 10, 0), H).max).toBe(1.5);
  });
  it('Heiligengeist-Höfe: erste Stunde 2 €, dann 1 € je 30 Min.', () => {
    const t = garage('heiligengeist-hoefe');
    expect(garageCost(t, wall(2026, 9, MO, 10, 0), 120, H).max).toBe(4);
  });
  it('ZOB ohne Stundentarif → Kosten unbekannt', () => {
    expect(garageCost(garage('zob'), wall(2026, 9, MO, 10, 0), 60, H)).toBeNull();
  });
});

describe('Hilfsfunktionen', () => {
  it('euro()', () => {
    expect(euro(2.8)).toBe('2,80 €');
  });
  it('nowInOldenburg rechnet in Europe/Berlin (Sommerzeit)', () => {
    // 16:59 UTC = 18:59 MESZ
    const w = nowInOldenburg(new Date('2026-09-23T16:59:00Z'));
    expect(formatHM(w)).toBe('18:59');
  });
  it('nowInOldenburg rechnet in Europe/Berlin (Winterzeit, Mitternacht)', () => {
    const w = nowInOldenburg(new Date('2026-12-24T23:00:00Z'));
    expect(w).toEqual(wall(2026, 12, 25, 0, 0));
  });
});
