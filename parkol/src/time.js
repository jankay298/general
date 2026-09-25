// Zeitrechnung in Oldenburger Ortszeit ("Wanduhrzeit").
//
// Alle Regeln sind in lokaler Zeit (Europe/Berlin) formuliert. Intern wird
// eine Wanduhrzeit als Date-Objekt dargestellt, dessen UTC-Felder die
// Berliner Uhrzeit tragen. So bleibt die Rechnung unabhängig von der
// Zeitzone des Geräts. Sommer-/Winterzeitwechsel werden bei Dauern ignoriert.

export const DAY_CODES = ['So', 'Mo', 'Di', 'Mi', 'Do', 'Fr', 'Sa'];
export const DAY_NAMES = ['Sonntag', 'Montag', 'Dienstag', 'Mittwoch', 'Donnerstag', 'Freitag', 'Samstag'];

/** Erzeugt eine Wanduhrzeit (Monat 1–12). */
export function wall(y, m, d, hh = 0, mm = 0) {
  return new Date(Date.UTC(y, m - 1, d, hh, mm));
}

/** Aktuelle Wanduhrzeit in Oldenburg. */
export function nowInOldenburg(now = new Date()) {
  const parts = new Intl.DateTimeFormat('en-GB', {
    timeZone: 'Europe/Berlin',
    year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
  }).formatToParts(now);
  const p = Object.fromEntries(parts.map((x) => [x.type, x.value]));
  return wall(+p.year, +p.month, +p.day, +p.hour, +p.minute);
}

/** Wandelt eine echte Zeit (z. B. ISO-String) in Berliner Wanduhrzeit. */
export function toWall(dateLike) {
  return nowInOldenburg(new Date(dateLike));
}

export const addMinutes = (w, min) => new Date(w.getTime() + min * 60_000);
export const minutesOfDay = (w) => w.getUTCHours() * 60 + w.getUTCMinutes();
export const dateKey = (w) => w.toISOString().slice(0, 10);
export const startOfDay = (w) => wall(w.getUTCFullYear(), w.getUTCMonth() + 1, w.getUTCDate());

/** "HH:MM" → Minuten seit Mitternacht ("24:00" → 1440). */
export function parseHM(s) {
  const m = /^(\d{1,2}):(\d{2})$/.exec(String(s).trim());
  if (!m) throw new Error(`Ungültige Uhrzeit: ${s}`);
  const v = +m[1] * 60 + +m[2];
  if (v > 1440 || +m[2] > 59) throw new Error(`Ungültige Uhrzeit: ${s}`);
  return v;
}

export const pad2 = (n) => String(n).padStart(2, '0');
export const formatHM = (w) => `${pad2(w.getUTCHours())}:${pad2(w.getUTCMinutes())}`;

/** Ostersonntag (gregorianisch, Meeus/Jones/Butcher). */
export function easterSunday(year) {
  const a = year % 19;
  const b = Math.floor(year / 100);
  const c = year % 100;
  const d = Math.floor(b / 4);
  const e = b % 4;
  const f = Math.floor((b + 8) / 25);
  const g = Math.floor((b - f + 1) / 3);
  const h = (19 * a + b - d - g + 15) % 30;
  const i = Math.floor(c / 4);
  const k = c % 4;
  const l = (32 + 2 * e + 2 * i - h - k) % 7;
  const m = Math.floor((a + 11 * h + 22 * l) / 451);
  const month = Math.floor((h + l - 7 * m + 114) / 31);
  const day = ((h + l - 7 * m + 114) % 31) + 1;
  return wall(year, month, day);
}

const HOLIDAY_RULES = {
  neujahr: (y) => wall(y, 1, 1),
  heilige_drei_koenige: (y) => wall(y, 1, 6),
  karfreitag: (y) => addMinutes(easterSunday(y), -2 * 1440),
  ostermontag: (y) => addMinutes(easterSunday(y), 1 * 1440),
  tag_der_arbeit: (y) => wall(y, 5, 1),
  christi_himmelfahrt: (y) => addMinutes(easterSunday(y), 39 * 1440),
  pfingstmontag: (y) => addMinutes(easterSunday(y), 50 * 1440),
  fronleichnam: (y) => addMinutes(easterSunday(y), 60 * 1440),
  tag_der_deutschen_einheit: (y) => wall(y, 10, 3),
  reformationstag: (y) => wall(y, 10, 31),
  allerheiligen: (y) => wall(y, 11, 1),
  erster_weihnachtstag: (y) => wall(y, 12, 25),
  zweiter_weihnachtstag: (y) => wall(y, 12, 26),
};

const holidayCache = new Map();

/** Liefert eine Map "YYYY-MM-DD" → Feiertagsname für das Jahr. */
export function holidays(year, names) {
  const key = `${year}|${names.join(',')}`;
  if (!holidayCache.has(key)) {
    const map = new Map();
    for (const n of names) {
      const rule = HOLIDAY_RULES[n];
      if (!rule) throw new Error(`Unbekannter Feiertag in tarife.json: ${n}`);
      map.set(dateKey(rule(year)), n);
    }
    holidayCache.set(key, map);
  }
  return holidayCache.get(key);
}

export const HOLIDAY_LABELS = {
  neujahr: 'Neujahr', heilige_drei_koenige: 'Heilige Drei Könige', karfreitag: 'Karfreitag',
  ostermontag: 'Ostermontag', tag_der_arbeit: 'Tag der Arbeit', christi_himmelfahrt: 'Christi Himmelfahrt',
  pfingstmontag: 'Pfingstmontag', fronleichnam: 'Fronleichnam', tag_der_deutschen_einheit: 'Tag der Deutschen Einheit',
  reformationstag: 'Reformationstag', allerheiligen: 'Allerheiligen', erster_weihnachtstag: '1. Weihnachtstag',
  zweiter_weihnachtstag: '2. Weihnachtstag',
};

export function holidayName(w, names) {
  return holidays(w.getUTCFullYear(), names).get(dateKey(w)) ?? null;
}

export const dayCode = (w) => DAY_CODES[w.getUTCDay()];

/** Menge der Tagescodes, die für ein Datum gelten ("Mo" … "So", zusätzlich "Fe" an Feiertagen). */
export function dayCodes(w, holidayNames) {
  const codes = [dayCode(w)];
  if (holidayName(w, holidayNames)) codes.push('Fe');
  return codes;
}

/** Formatiert eine Wanduhrzeit relativ zu einer Referenz: "19:00", "morgen 08:00", "Mo 08:00". */
export function formatRelative(w, ref) {
  const days = Math.round((startOfDay(w) - startOfDay(ref)) / 86_400_000);
  if (days === 0) return formatHM(w);
  if (days === 1) return `morgen ${formatHM(w)}`;
  if (days > 1 && days < 7) return `${dayCode(w)} ${formatHM(w)}`;
  return `${pad2(w.getUTCDate())}.${pad2(w.getUTCMonth() + 1)}. ${formatHM(w)}`;
}
