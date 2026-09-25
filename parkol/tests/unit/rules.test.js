import { describe, expect, it } from 'vitest';
import tarife from '../../data/tarife.json';
import { CATEGORY, describe as describeFeature, evaluate, prepareRules, zoneAt } from '../../src/rules.js';
import { wall } from '../../src/time.js';
import { parseConditional, parseDuration, parseOpeningHours, isActive } from '../../src/windows.js';

const rules = prepareRules(tarife);

const feature = (props, coords = [8.2140, 53.1400], type = 'Point', id = 'node/1') => ({
  type: 'Feature',
  id,
  geometry: { type, coordinates: coords },
  properties: { id, kind: 'lot', parking: 'surface', ...props },
});

const run = (f, t, d = 60) => evaluate(f, describeFeature(f, rules), rules, t, d);

describe('Zonenzuordnung', () => {
  it('Schlossplatz liegt in Zone I', () => {
    expect(zoneAt(rules, [8.2155, 53.1385]).zone.id).toBe('I');
  });
  it('Lappan/Heiligengeiststraße liegt in Zone I', () => {
    expect(zoneAt(rules, [8.2125, 53.1428]).zone.id).toBe('I');
  });
  it('Ofener Straße (westlich) liegt in Zone II', () => {
    expect(zoneAt(rules, [8.195, 53.141]).zone.id).toBe('II');
  });
  it('Weser-Ems-Halle liegt in Zone III', () => {
    expect(zoneAt(rules, [8.230, 53.149]).zone.id).toBe('III');
  });
});

describe('Bewertung von OSM-Objekten', () => {
  const tue = (h, m = 0) => wall(2026, 9, 22, h, m);

  it('fee=yes in Zone I: 18:59 gelb, 19:00 grün', () => {
    const f = feature({ fee: 'yes' });
    const a = run(f, tue(18, 59));
    expect(a.category).toBe(CATEGORY.PAID);
    expect(a.priceNow).toContain('2,80 €/h');
    expect(a.change.text).toBe('kostenlos ab 19:00');
    const b = run(f, tue(19, 0));
    expect(b.category).toBe(CATEGORY.FREE);
    expect(b.change.text).toContain('kostenpflichtig ab morgen 08:00');
  });

  it('fee=no → kostenlos', () => {
    expect(run(feature({ fee: 'no' }), tue(12)).category).toBe(CATEGORY.FREE);
  });

  it('ohne fee-Tag → unbekannt (grau) mit Hinweis auf Zone', () => {
    const r = run(feature({ fee: null }), tue(12));
    expect(r.category).toBe(CATEGORY.UNKNOWN);
    expect(r.notes.join(' ')).toContain('Zone I');
  });

  it('fee:conditional aus OSM hat Vorrang vor Zonenzeiten', () => {
    const f = feature({ fee: 'yes', feeConditional: 'yes @ (Mo-Su,PH 07:00-24:00)' });
    expect(run(f, wall(2026, 9, 27, 21, 0)).category).toBe(CATEGORY.PAID); // Sonntagabend
  });

  it('fee=Mo-Sa 08:00-19:00 wird als Zeitfenster gelesen', () => {
    const f = feature({ fee: 'Mo-Sa 08:00-19:00' }, [8.195, 53.141]);
    const r = run(f, tue(10));
    expect(r.category).toBe(CATEGORY.PAID);
    expect(r.priceNow).toContain('1,60 €/h');
  });

  it('Parkhaus mit belegtem Tarif → blau, Preis aus tarife.json', () => {
    const f = feature({ kind: 'garage', parking: 'multi-storey', fee: 'yes', name: 'Parkhaus am Waffenplatz' }, [[[8.2114, 53.1414], [8.2115, 53.1413], [8.2116, 53.1414], [8.2114, 53.1414]]], 'Polygon', 'way/30251488');
    const r = run(f, tue(10));
    expect(r.category).toBe(CATEGORY.GARAGE);
    expect(r.priceNow).toBe('2,20 €/h');
    expect(r.unverified).toEqual([]);
  });

  it('Parkhaus mit unbestätigtem Tarif → als unbestätigt markiert', () => {
    const f = feature({ kind: 'garage', parking: 'underground', fee: 'yes' }, [8.2122, 53.1451], 'Point', 'node/2777023962');
    const r = run(f, tue(10));
    expect(r.unverified).toContain('Tarif');
  });

  it('Objekt nahe der genäherten Zonengrenze → Zonenzuordnung unbestätigt', () => {
    const f = feature({ fee: 'yes' }, [8.2066, 53.1400]); // Herbartstraße
    expect(run(f, tue(10)).unverified).toContain('Zonenzuordnung');
  });

  it('Pferdemarkt ist Zone I laut Verordnung', () => {
    const f = feature({ fee: 'yes', name: 'Pferdemarkt' }, [8.214, 53.1465], 'Point', 'way/279951615');
    const r = run(f, tue(10));
    expect(r.zoneName).toContain('Zone I');
    expect(r.unverified).not.toContain('Zonenzuordnung');
  });

  it('Kosten für geplante Dauer werden berechnet', () => {
    const r = run(feature({ fee: 'yes' }), tue(18, 30), 120);
    expect(r.cost).toEqual({ min: 1.4, max: 1.4 });
  });

  it('maxstay aus OSM wird angezeigt', () => {
    const r = run(feature({ fee: 'yes', maxstay: '2 hours' }), tue(10));
    expect(r.maxstay).toBe('2 Std. (laut OSM)');
  });
});

describe('OSM-Zeitangaben', () => {
  it('parseOpeningHours versteht Tagesbereiche, Listen und PH', () => {
    expect(parseOpeningHours('Mo-Sa 08:00-19:00').windows[0].tage).toEqual(['Mo', 'Di', 'Mi', 'Do', 'Fr', 'Sa']);
    const oh = parseOpeningHours('Mo-Su,PH 07:00-24:00');
    expect(oh.windows[0].tage).toContain('Fe');
    expect(parseOpeningHours('Mo,We 9:00-12:00,14:00-18:00').windows).toHaveLength(2);
    expect(parseOpeningHours('Mo-Fr 08:00-18:00; PH off').holidaysOff).toBe(true);
    expect(parseOpeningHours('sunrise-sunset')).toBeNull();
  });
  it('Fenster über Mitternacht', () => {
    const w = [{ tage: ['Fr'], von: '22:00', bis: '06:00' }];
    const o = { holidays: rules.holidays, holidaysOff: false };
    expect(isActive(w, wall(2026, 9, 25, 23, 0), o)).toBe(true);
    expect(isActive(w, wall(2026, 9, 26, 5, 59), o)).toBe(true); // Sa früh gehört zu Fr
    expect(isActive(w, wall(2026, 9, 26, 6, 0), o)).toBe(false);
    expect(isActive(w, wall(2026, 9, 26, 23, 0), o)).toBe(false);
  });
  it('parseConditional und parseDuration', () => {
    expect(parseConditional('2 hours @ (Mo-Fr 08:00-18:00)')[0].value).toBe('2 hours');
    expect(parseDuration('90 minutes')).toBe(90);
    expect(parseDuration('2 hours')).toBe(120);
    expect(parseDuration('1:30')).toBe(90);
    expect(parseDuration('load-unload')).toBeNull();
  });
});

describe('tarife.json ist vollständig gekennzeichnet', () => {
  it('jeder Eintrag hat quelle und verifiziert', () => {
    const check = (o, path) => {
      expect(typeof o.quelle, `${path}.quelle`).toBe('string');
      expect(typeof o.verifiziert, `${path}.verifiziert`).toBe('boolean');
    };
    check(tarife.feiertage, 'feiertage');
    for (const z of tarife.zonen) for (const k of ['preis', 'zeiten', 'hoechstparkdauer', 'gebiet']) check(z[k], `zone ${z.id}.${k}`);
    for (const p of tarife.parkhaeuser) check(p, `parkhaus ${p.id}`);
  });
});
