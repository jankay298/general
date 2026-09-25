// Verknüpft OSM-Objekte mit den Regeln aus data/tarife.json und bewertet sie
// für einen Zeitpunkt: kostenlos / kostenpflichtig / Parkhaus / unbekannt.

import { anchor, distanceToRing, pointInRing, ringFromLatLon } from './geo.js';
import { euro, euroRange, garageCost, garageHourly, zoneCost, zoneStatus } from './pricing.js';
import { formatRelative, holidayName, HOLIDAY_LABELS } from './time.js';
import { parseConditional, parseDuration, parseOpeningHours } from './windows.js';

export const CATEGORY = { FREE: 'free', PAID: 'paid', GARAGE: 'garage', UNKNOWN: 'unknown' };

/** Unsicherheitsband an der (genäherten) Zonengrenze in Metern. */
const BOUNDARY_UNCERTAIN_M = 120;

const OSM_SOURCE = { text: 'OpenStreetMap-Mitwirkende (ODbL)', url: 'https://www.openstreetmap.org/copyright', verified: null };

/** Bereitet tarife.json für schnelle Auswertung vor. */
export function prepareRules(tarife) {
  const zones = tarife.zonen.map((z) => ({
    ...z,
    ring: z.gebiet.punkte ? ringFromLatLon(z.gebiet.punkte) : null,
    rule: {
      windows: z.zeiten.gebuehrenpflichtig,
      holidaysOff: z.zeiten.feiertage_frei !== false,
      takt_minuten: z.preis.takt_minuten,
      preis_pro_takt: z.preis.preis_pro_takt,
      tageshoechstsatz: z.preis.tageshoechstsatz ?? null,
    },
  }));
  const byOsm = new Map();
  for (const p of tarife.parkhaeuser) for (const id of p.osm_ids ?? []) byOsm.set(id, p);
  return { raw: tarife, holidays: tarife.feiertage.liste, zones, byOsm };
}

/** Zone für einen Punkt: { zone, uncertain } */
export function zoneAt(rules, point) {
  for (const z of rules.zones) {
    if (!z.ring) continue;
    const inside = pointInRing(point, z.ring);
    const d = distanceToRing(point, z.ring);
    if (inside || d <= (z.gebiet.puffer_meter ?? 0)) {
      return { zone: z, uncertain: !z.gebiet.verifiziert && d < BOUNDARY_UNCERTAIN_M };
    }
  }
  const rest = rules.zones.find((z) => z.gebiet.rest);
  const nearest = rules.zones.filter((z) => z.ring).map((z) => distanceToRing(point, z.ring));
  const near = nearest.some((d) => d < BOUNDARY_UNCERTAIN_M);
  return { zone: rest, uncertain: near && rules.zones.some((z) => z.ring && !z.gebiet.verifiziert) };
}

const baseId = (id) => String(id).split('#')[0];

const KIND_LABEL = {
  'multi-storey': 'Parkhaus',
  underground: 'Tiefgarage',
  rooftop: 'Parkdeck',
  surface: 'Parkplatz',
  street_side: 'Straßenparken',
  lane: 'Parkstreifen',
  layby: 'Parkbucht',
};

const SIDE_LABEL = { left: 'linke Straßenseite', right: 'rechte Straßenseite', both: 'beide Straßenseiten' };

/** Statische Infos, die nicht von der Uhrzeit abhängen (einmal pro Objekt). */
export function describe(feature, rules) {
  const p = feature.properties;
  const entry = rules.byOsm.get(baseId(feature.id)) ?? null;
  const point = anchor(feature.geometry);
  const isGarage = p.kind === 'garage' || (entry && entry.tarif !== undefined && !entry.zone);
  const { zone, uncertain } = entry?.zone
    ? { zone: rules.zones.find((z) => z.id === entry.zone), uncertain: !entry.verifiziert }
    : zoneAt(rules, point);
  let kindLabel = KIND_LABEL[p.parking] ?? (p.kind === 'street' ? 'Straßenparken' : 'Parkplatz');
  if (p.kind === 'garage' && !KIND_LABEL[p.parking]) kindLabel = 'Parkhaus';
  const name = entry?.name ?? p.name ?? null;
  const title = name ?? (p.kind === 'street' ? 'Straßenparken' : kindLabel);
  return {
    id: feature.id,
    kind: isGarage ? 'garage' : p.kind === 'street' ? 'street' : 'lot',
    kindLabel: p.kind === 'street' && p.side ? `${kindLabel} (${SIDE_LABEL[p.side] ?? p.side})` : kindLabel,
    title,
    point,
    entry,
    zone,
    zoneUncertain: uncertain,
    capacity: p.capacity ?? null,
    customersOnly: p.access === 'customers',
    liveName: entry?.live_name ?? null,
  };
}

/**
 * Bewertet ein Objekt zum Zeitpunkt t (Wanduhrzeit) für eine Parkdauer in Minuten.
 * Liefert alles, was Karte, Liste und Detailansicht anzeigen.
 */
export function evaluate(feature, info, rules, t, durationMin = 60) {
  const p = feature.properties;
  const notes = [];
  const sources = [];
  const unverified = [];
  const res = {
    category: CATEGORY.UNKNOWN,
    priceNow: 'Regeln unbekannt',
    priceShort: '?',
    hourly: null,
    cost: null,
    change: null,
    maxstay: null,
    notes,
    sources,
    unverified,
  };

  const holiday = holidayName(t, rules.holidays);
  if (holiday) notes.push(`${HOLIDAY_LABELS[holiday]} (Feiertag in Niedersachsen)`);
  if (info.customersOnly) notes.push('Nur für Kundinnen und Kunden');
  if (p.residents) notes.push(`Bewohnerparkzone ${p.residents === 'yes' ? '' : p.residents}`.trim() + ' – ggf. nur mit Bewohnerausweis');
  if (p.disc === 'yes') notes.push('Parkscheibe erforderlich');

  // Höchstparkdauer (OSM hat Vorrang vor der allgemeinen Zonenangabe)
  const ms = parseDuration(p.maxstay);
  const msCond = parseConditional(p.maxstayConditional);
  if (ms != null) res.maxstay = `${formatMinutes(ms)} (laut OSM)`;
  else if (msCond) res.maxstay = msCond.map((c) => `${formatMinutes(parseDuration(c.value)) ?? c.value} (${ohText(c.windows)})`).join('; ') + ' (laut OSM)';
  else if (p.maxstay) res.maxstay = `${p.maxstay} (laut OSM)`;

  if (info.kind === 'garage') return evaluateGarage(feature, info, rules, t, durationMin, res);

  const zone = info.zone;
  if (!res.maxstay && zone) {
    res.maxstay = zone.hoechstparkdauer.text;
    // nur eine tatsächlich behauptete Höchstparkdauer kann "unbestätigt" sein
    if (zone.hoechstparkdauer.minuten != null && !zone.hoechstparkdauer.verifiziert) unverified.push('Höchstparkdauer');
  }

  // Gebührenregel bestimmen
  let feeTag = p.fee;
  let windowsFromOsm = null;
  const feeCond = parseConditional(p.feeConditional);
  if (feeTag && !['yes', 'no'].includes(feeTag)) {
    // manche Objekte tragen die Zeiten direkt in fee=… (z. B. "Mo-Sa 08:00-19:00")
    const oh = parseOpeningHours(feeTag);
    if (oh) {
      windowsFromOsm = oh;
      feeTag = 'yes';
    }
  }
  if (feeCond) {
    const yes = feeCond.filter((c) => c.value === 'yes');
    if (yes.length) {
      windowsFromOsm = { windows: yes.flatMap((c) => c.windows), holidaysOff: yes.every((c) => c.holidaysOff) };
      feeTag = 'yes';
    }
  } else if (p.feeConditional) {
    notes.push(`Gebührenzeiten in OSM nicht auswertbar: „${p.feeConditional}“`);
    feeTag = feeTag === 'no' ? 'no' : null;
  }

  if (feeTag === 'no') {
    res.category = CATEGORY.FREE;
    res.priceNow = 'kostenlos';
    res.priceShort = '0 €';
    res.cost = { min: 0, max: 0 };
    sources.push({ ...OSM_SOURCE, text: 'Gebührenfreiheit laut OpenStreetMap (Community-Daten)' });
    return res;
  }

  if (feeTag === 'yes' && zone) {
    const rule = windowsFromOsm ? { ...zone.rule, windows: windowsFromOsm.windows, holidaysOff: windowsFromOsm.holidaysOff || zone.rule.holidaysOff } : zone.rule;
    const st = zoneStatus(rule, t, rules.holidays);
    res.hourly = st.hourly;
    res.zoneName = zone.name;
    const priceText = `${euro(st.hourly)}/h`;
    res.cost = { min: zoneCost(rule, t, durationMin, rules.holidays), max: zoneCost(rule, t, durationMin, rules.holidays) };
    if (st.paid) {
      res.category = CATEGORY.PAID;
      res.priceNow = `${priceText} (${zone.preis.takt_minuten}-Min.-Takt)`;
      res.priceShort = euro(st.hourly).replace(' €', '€');
      if (st.changeAt) res.change = { type: 'free_from', at: st.changeAt, text: `kostenlos ab ${formatRelative(st.changeAt, t)}` };
    } else {
      res.category = CATEGORY.FREE;
      res.priceNow = 'jetzt kostenlos';
      res.priceShort = '0 €';
      if (st.changeAt) res.change = { type: 'paid_from', at: st.changeAt, text: `kostenpflichtig ab ${formatRelative(st.changeAt, t)} (${priceText})` };
    }
    res.feeTimes = windowsFromOsm ? `${ohText(windowsFromOsm.windows)} (laut OSM)` : zone.zeiten.text;
    if (zone.preis.tageshoechstsatz != null) res.feeTimes += `, max. ${euro(zone.preis.tageshoechstsatz)} pro Tag`;
    sources.push({ ...OSM_SOURCE, text: 'Gebührenpflicht laut OpenStreetMap (Community-Daten)' });
    sources.push({ text: `Preis ${zone.name}: ${zone.preis.quelle}`, url: zone.preis.url, verified: zone.preis.verifiziert });
    if (!windowsFromOsm) sources.push({ text: `Gebührenzeiten: ${zone.zeiten.quelle}`, url: zone.zeiten.url, verified: zone.zeiten.verifiziert });
    if (!zone.preis.verifiziert) unverified.push('Preis');
    if (!windowsFromOsm && !zone.zeiten.verifiziert) unverified.push('Gebührenzeiten');
    if (info.zoneUncertain) {
      unverified.push('Zonenzuordnung');
      notes.push('Liegt nahe der Zonengrenze – Zone ist eine Näherung, bitte Automat/Beschilderung prüfen.');
    }
    return res;
  }

  // Gebühren unbekannt
  res.category = CATEGORY.UNKNOWN;
  res.priceNow = 'Regeln unbekannt';
  res.priceShort = '?';
  if (zone) {
    const hint = p.disc === 'yes'
      ? 'Parkscheibe: vermutlich kostenlos, aber zeitlich begrenzt.'
      : `Keine Gebührenangabe in OSM. Falls ein Parkscheinautomat steht, gilt ${zone.name}: ${zone.preis.text}, ${zone.zeiten.text}.`;
    notes.push(hint);
  }
  sources.push({ ...OSM_SOURCE, text: 'Objekt aus OpenStreetMap – ohne Gebührenangabe' });
  return res;
}

function evaluateGarage(feature, info, rules, t, durationMin, res) {
  const p = feature.properties;
  const entry = info.entry;
  const tarif = entry?.tarif ?? null;
  const { sources, unverified } = res;
  res.category = CATEGORY.GARAGE;
  if (tarif) {
    const hourly = garageHourly(tarif, t, rules.holidays);
    const cost = garageCost(tarif, t, durationMin, rules.holidays);
    res.cost = cost;
    res.tariffText = tarif.text;
    if (hourly) {
      const r = euroRange(hourly);
      res.priceNow = hourly.firstHour ? `${r} erste Stunde` : `${r}/h`;
      res.priceShort = euro(hourly.max).replace(' €', '€');
      res.hourly = hourly.max;
    } else if (tarif.tageshoechstsatz != null) {
      res.priceNow = `Tagesmax. ${euro(tarif.tageshoechstsatz)}`;
      res.priceShort = `max ${euro(tarif.tageshoechstsatz).replace(',00 €', '€')}`;
    } else {
      res.priceNow = 'Tarif unbekannt';
      res.priceShort = '?';
    }
    if (tarif.tageshoechstsatz != null) res.dayMax = euro(tarif.tageshoechstsatz);
    res.change = null;
    sources.push({ text: `Tarif: ${entry.quelle}`, url: entry.url, verified: entry.verifiziert });
    if (!entry.verifiziert) unverified.push('Tarif');
  } else if (p.fee === 'no') {
    res.priceNow = 'kostenlos (laut OSM)';
    res.priceShort = '0 €';
    res.cost = { min: 0, max: 0 };
    sources.push({ ...OSM_SOURCE, text: 'Gebührenfreiheit laut OpenStreetMap' });
  } else {
    res.priceNow = 'Tarif unbekannt';
    res.priceShort = '?';
    if (entry) {
      sources.push({ text: entry.quelle, url: entry.url, verified: false });
      unverified.push('Tarif');
    }
  }
  if (!res.maxstay) res.maxstay = 'keine Angabe';
  if (entry?.oeffnungszeiten) res.openingHours = entry.oeffnungszeiten;
  else if (p.openingHours) res.openingHours = `${p.openingHours} (laut OSM)`;
  sources.push({ ...OSM_SOURCE, text: 'Lage und Stellplätze: OpenStreetMap' });
  return res;
}

export function formatMinutes(min) {
  if (min == null) return null;
  if (min < 60) return `${min} Min.`;
  const h = Math.floor(min / 60);
  const m = min % 60;
  return m ? `${h} Std. ${m} Min.` : `${h} Std.`;
}

/** Zeitfenster lesbar: "Mo–Sa 08:00–19:00". */
export function ohText(windows) {
  const order = ['Mo', 'Di', 'Mi', 'Do', 'Fr', 'Sa', 'So', 'Fe'];
  return windows
    .map((w) => {
      const idx = w.tage.map((d) => order.indexOf(d)).sort((a, b) => a - b);
      let days = w.tage.join(', ');
      const contiguous = idx.every((v, i) => i === 0 || v === idx[i - 1] + 1);
      if (idx.length > 2 && contiguous) days = `${order[idx[0]]}–${order[idx.at(-1)]}`;
      return `${days} ${w.von}–${w.bis}`;
    })
    .join('; ');
}
