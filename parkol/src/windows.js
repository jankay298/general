// Zeitfenster ("Mo–Sa 08:00–19:00") auswerten und aus OSM-Syntax lesen.
//
// Ein Zeitfenster hat die Form { tage: ['Mo', …, 'Fe'], von: 'HH:MM', bis: 'HH:MM' }.
// 'bis' ist exklusiv. Fenster über Mitternacht (von > bis) sind erlaubt.

import { DAY_CODES, addMinutes, dayCodes, minutesOfDay, parseHM, startOfDay } from './time.js';

const OSM_DAYS = { Mo: 'Mo', Tu: 'Di', We: 'Mi', Th: 'Do', Fr: 'Fr', Sa: 'Sa', Su: 'So', PH: 'Fe' };
const WEEK = ['Mo', 'Tu', 'We', 'Th', 'Fr', 'Sa', 'Su'];

function compile(windows) {
  return windows.map((w) => ({ tage: new Set(w.tage), von: parseHM(w.von), bis: parseHM(w.bis) }));
}

const compiledCache = new WeakMap();
function compiled(windows) {
  if (!compiledCache.has(windows)) compiledCache.set(windows, compile(windows));
  return compiledCache.get(windows);
}

/**
 * Ist zum Zeitpunkt `t` eines der Fenster aktiv?
 * opts.holidays: Liste der Feiertagsnamen; opts.holidaysOff: an Feiertagen nie aktiv
 * (außer das Fenster nennt ausdrücklich 'Fe').
 */
export function isActive(windows, t, opts) {
  const codes = dayCodes(t, opts.holidays);
  const isHoliday = codes.includes('Fe');
  const prevCodes = dayCodes(addMinutes(startOfDay(t), -1), opts.holidays);
  const prevHoliday = prevCodes.includes('Fe');
  const min = minutesOfDay(t);
  for (const w of compiled(windows)) {
    if (w.von < w.bis) {
      if (min >= w.von && min < w.bis && matches(w, codes, isHoliday, opts)) return true;
    } else if (w.von > w.bis) {
      // über Mitternacht: Teil am Abend gehört zum aktuellen, Teil am Morgen zum Vortag
      if (min >= w.von && matches(w, codes, isHoliday, opts)) return true;
      if (min < w.bis && matches(w, prevCodes, prevHoliday, opts)) return true;
    }
  }
  return false;
}

function matches(w, codes, isHoliday, opts) {
  if (isHoliday) {
    if (w.tage.has('Fe')) return true;
    if (opts.holidaysOff) return false;
  }
  return codes.some((c) => c !== 'Fe' && w.tage.has(c));
}

/** Nächster Zeitpunkt > t, an dem sich der Zustand ändert (oder null innerhalb von `horizonDays`). */
export function nextChange(windows, t, opts, horizonDays = 9) {
  const now = isActive(windows, t, opts);
  const day0 = startOfDay(t);
  const candidates = new Set();
  for (let d = 0; d <= horizonDays; d++) {
    const base = addMinutes(day0, d * 1440);
    candidates.add(base.getTime());
    for (const w of compiled(windows)) {
      candidates.add(addMinutes(base, w.von).getTime());
      candidates.add(addMinutes(base, w.bis).getTime());
    }
  }
  const sorted = [...candidates].filter((c) => c > t.getTime()).sort((a, b) => a - b);
  for (const c of sorted) {
    const at = new Date(c);
    if (isActive(windows, at, opts) !== now) return at;
  }
  return null;
}

/**
 * Liest eine einfache OSM-opening_hours-Angabe in Zeitfenster.
 * Unterstützt: "Mo-Sa 08:00-19:00", "Mo-Fr 08:00-18:00; Sa 08:00-14:00",
 * "Mo-Su,PH 07:00-24:00", "Mo,We 09:00-12:00,14:00-18:00", "24/7", "PH off".
 * Liefert { windows, holidaysOff } oder null, wenn die Angabe nicht verstanden wird.
 */
export function parseOpeningHours(value) {
  if (value == null) return null;
  const s = String(value).trim();
  if (s === '24/7') return { windows: [{ tage: [...DAY_CODES, 'Fe'], von: '00:00', bis: '24:00' }], holidaysOff: false };
  const windows = [];
  let holidaysOff = false;
  for (const rawRule of s.split(';')) {
    const rule = rawRule.trim();
    if (!rule) continue;
    if (/^PH\s+off$/i.test(rule)) { holidaysOff = true; continue; }
    const m = /^([A-Za-z,\- ]+?)\s+([\d:,\- ]+)$/.exec(rule);
    if (!m) {
      // nur Uhrzeiten → alle Tage
      if (/^[\d:,\- ]+$/.test(rule)) {
        const times = parseTimes(rule);
        if (!times) return null;
        for (const [von, bis] of times) windows.push({ tage: [...DAY_CODES], von, bis });
        continue;
      }
      return null;
    }
    const days = parseDays(m[1]);
    const times = parseTimes(m[2]);
    if (!days || !times) return null;
    for (const [von, bis] of times) windows.push({ tage: days, von, bis });
  }
  return windows.length ? { windows, holidaysOff } : null;
}

function parseDays(str) {
  const out = new Set();
  for (const part of str.split(',').map((x) => x.trim()).filter(Boolean)) {
    const range = /^(Mo|Tu|We|Th|Fr|Sa|Su)\s*-\s*(Mo|Tu|We|Th|Fr|Sa|Su)$/.exec(part);
    if (range) {
      let i = WEEK.indexOf(range[1]);
      const end = WEEK.indexOf(range[2]);
      for (let n = 0; n < 7; n++) {
        out.add(OSM_DAYS[WEEK[i]]);
        if (i === end) break;
        i = (i + 1) % 7;
      }
    } else if (OSM_DAYS[part]) {
      out.add(OSM_DAYS[part]);
    } else {
      return null;
    }
  }
  return out.size ? [...out] : null;
}

function parseTimes(str) {
  const out = [];
  for (const part of str.split(',').map((x) => x.trim()).filter(Boolean)) {
    const m = /^(\d{1,2}:\d{2})\s*-\s*(\d{1,2}:\d{2})$/.exec(part);
    if (!m) return null;
    const norm = (x) => x.padStart(5, '0');
    try {
      parseHM(m[1]);
      parseHM(m[2]);
    } catch {
      return null;
    }
    out.push([norm(m[1]), norm(m[2])]);
  }
  return out.length ? out : null;
}

/**
 * Liest einen OSM-Conditional-Wert wie "yes @ (Mo-Sa 08:00-19:00)" oder
 * "2 hours @ (Mo-Fr 08:00-18:00)". Liefert [{ value, windows, holidaysOff }] oder null.
 */
export function parseConditional(value) {
  if (!value) return null;
  const out = [];
  const re = /([^@;]+?)\s*@\s*\(([^)]*)\)/g;
  let m;
  while ((m = re.exec(value))) {
    const oh = parseOpeningHours(m[2]);
    if (!oh) return null;
    out.push({ value: m[1].trim(), ...oh });
  }
  return out.length ? out : null;
}

/** OSM-Dauer ("90 minutes", "2 hours", "1:30") → Minuten, sonst null. */
export function parseDuration(value) {
  if (!value) return null;
  const s = String(value).trim();
  let m = /^(\d+(?:\.\d+)?)\s*(minutes?|mins?|min)$/i.exec(s);
  if (m) return Math.round(+m[1]);
  m = /^(\d+(?:\.\d+)?)\s*(hours?|h)$/i.exec(s);
  if (m) return Math.round(+m[1] * 60);
  m = /^(\d{1,2}):(\d{2})$/.exec(s);
  if (m) return +m[1] * 60 + +m[2];
  m = /^(\d+)$/.exec(s);
  if (m) return +m[1];
  return null;
}
