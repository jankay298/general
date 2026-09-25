// Preisberechnung für städtische Zonen und Parkhäuser.

import { addMinutes, dateKey, dayCodes, minutesOfDay, parseHM } from './time.js';
import { isActive, nextChange } from './windows.js';

const round2 = (x) => Math.round(x * 100) / 100;

/**
 * Status einer Gebührenregel zum Zeitpunkt t.
 * rule: { windows, holidaysOff, takt_minuten, preis_pro_takt }
 * → { paid, hourly, changeAt }  (changeAt = nächster Wechsel kostenlos↔kostenpflichtig)
 */
export function zoneStatus(rule, t, holidays) {
  const opts = { holidays, holidaysOff: rule.holidaysOff };
  const paid = isActive(rule.windows, t, opts);
  return {
    paid,
    hourly: round2((rule.preis_pro_takt * 60) / rule.takt_minuten),
    changeAt: nextChange(rule.windows, t, opts),
  };
}

/**
 * Kosten für `minutes` Minuten Parken ab `start` nach einer Zonenregel.
 * Abgerechnet wird je angefangenem Takt innerhalb jedes zusammenhängenden
 * gebührenpflichtigen Abschnitts (Parkschein gilt nur in der Gebührenzeit).
 * Ein optionaler Tageshöchstsatz wird je Kalendertag angewendet.
 */
export function zoneCost(rule, start, minutes, holidays) {
  if (minutes <= 0) return 0;
  const opts = { holidays, holidaysOff: rule.holidaysOff };
  const end = addMinutes(start, minutes);
  const perDay = new Map();
  let t = start;
  while (t < end) {
    const active = isActive(rule.windows, t, opts);
    const change = nextChange(rule.windows, t, opts) ?? end;
    const segEnd = change < end ? change : end;
    if (active) {
      const segMin = (segEnd - t) / 60_000;
      const cost = Math.ceil(segMin / rule.takt_minuten - 1e-9) * rule.preis_pro_takt;
      const k = dateKey(t);
      perDay.set(k, (perDay.get(k) ?? 0) + cost);
    }
    t = segEnd;
  }
  let total = 0;
  for (const c of perDay.values()) total += rule.tageshoechstsatz != null ? Math.min(c, rule.tageshoechstsatz) : c;
  return round2(total);
}

/** Die Tarifperiode eines Parkhauses, die zum Zeitpunkt t gilt (oder null). */
export function garagePeriod(tarif, t, holidays) {
  const codes = dayCodes(t, holidays);
  const isHoliday = codes.includes('Fe');
  const min = minutesOfDay(t);
  const hasHolidayPeriods = tarif.perioden.some((p) => p.tage.includes('Fe'));
  for (const p of tarif.perioden) {
    const von = parseHM(p.von);
    const bis = parseHM(p.bis);
    if (min < von || min >= bis) continue;
    const dayOk = isHoliday && hasHolidayPeriods ? p.tage.includes('Fe') : codes.some((c) => c !== 'Fe' && p.tage.includes(c));
    if (dayOk) return p;
  }
  return null;
}

/**
 * Parkhauskosten für `minutes` Minuten ab `start`.
 * Liefert { min, max } (unterschiedlich bei dynamischen Tarifen) oder null, wenn
 * der Tarif nicht bekannt ist. Vereinfachungen: Der Preis eines Taktes richtet
 * sich nach der Periode bei Taktbeginn; der Tageshöchstsatz gilt je 24 h ab Einfahrt;
 * eine Periodenobergrenze ('max') gilt je zusammenhängendem Periodenabschnitt.
 */
export function garageCost(tarif, start, minutes, holidays) {
  if (!tarif?.perioden?.length) return null;
  const calc = (useMin) => {
    const windowSums = new Map();
    const periodSums = new Map();
    let t = start;
    let first = true;
    const end = addMinutes(start, minutes);
    let guard = 0;
    while (t < end && guard++ < 5000) {
      const p = garagePeriod(tarif, t, holidays);
      if (!p) return null; // Lücke im Tarif → unbekannt
      let block;
      let price;
      if (first && p.erste_stunde != null) {
        block = 60;
        price = p.erste_stunde;
      } else {
        block = p.takt_minuten;
        price = useMin && p.preis_pro_takt_min != null ? p.preis_pro_takt_min : p.preis_pro_takt;
      }
      first = false;
      if (p.max != null) {
        const key = `${tarif.perioden.indexOf(p)}|${dateKey(t)}`;
        const used = periodSums.get(key) ?? 0;
        price = Math.max(0, Math.min(price, p.max - used));
        periodSums.set(key, used + price);
      }
      const win = Math.floor((t - start) / 86_400_000);
      windowSums.set(win, (windowSums.get(win) ?? 0) + price);
      t = addMinutes(t, block);
    }
    let total = 0;
    for (const s of windowSums.values()) total += tarif.tageshoechstsatz != null ? Math.min(s, tarif.tageshoechstsatz) : s;
    return round2(total);
  };
  const max = calc(false);
  const min = calc(true);
  if (max == null || min == null) return null;
  return { min, max };
}

/** Stundenpreis eines Parkhauses zum Zeitpunkt t: { min, max, firstHour } oder null. */
export function garageHourly(tarif, t, holidays) {
  if (!tarif?.perioden?.length) return null;
  const p = garagePeriod(tarif, t, holidays);
  if (!p) return null;
  const perHour = (x) => round2((x * 60) / p.takt_minuten);
  return {
    max: p.erste_stunde ?? perHour(p.preis_pro_takt),
    min: p.erste_stunde ?? perHour(p.preis_pro_takt_min ?? p.preis_pro_takt),
    firstHour: p.erste_stunde != null,
  };
}

/** Euro-Betrag deutsch formatiert. */
export function euro(x) {
  return `${x.toFixed(2).replace('.', ',')} €`;
}

export function euroRange(r) {
  if (!r) return null;
  return r.min === r.max ? euro(r.max) : `${euro(r.min).replace(' €', '')}–${euro(r.max)}`;
}
