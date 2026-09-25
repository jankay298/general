import L from 'leaflet';
import 'leaflet/dist/leaflet.css';
import './style.css';

import geojsonUrl from '../data/parking.geojson?url';
import tarifeUrl from '../data/tarife.json?url';
import { distance, formatDistance } from './geo.js';
import { loadLive } from './live.js';
import { euroRange } from './pricing.js';
import { CATEGORY, describe, evaluate, formatMinutes, prepareRules } from './rules.js';
import { searchPlaces } from './search.js';
import { DAY_NAMES, addMinutes, dayCode, formatHM, nowInOldenburg, pad2, startOfDay } from './time.js';

const COLORS = {
  free: '#1a9850',
  paid: '#e0a100',
  garage: '#1f5fbf',
  unknown: '#8a8f98',
};
const CENTER = [53.1405, 8.2146];
const INNENSTADT = [[53.1365, 8.2075], [53.1465, 8.2300]];
const LIST_LIMIT = 40;

const $ = (sel) => document.querySelector(sel);
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

const state = {
  filter: 'all',
  mode: 'now',
  planDay: 0,
  planTime: '14:00',
  duration: 60,
  showCustomers: false,
  target: null, // { name, point:[lon,lat] }
  me: null, // [lon,lat]
  selected: null,
  live: { state: 'none', byName: new Map() },
  items: [],
  rules: null,
  meta: null,
};

// Testschnittstelle (nur lesend genutzt von den E2E-Tests)
window.__parkol = { ready: false, state };

// ---------------------------------------------------------------- Karte
const map = L.map('map', {
  center: CENTER,
  zoom: 15,
  minZoom: 12,
  maxZoom: 19,
  zoomControl: true,
  preferCanvas: true,
  renderer: L.canvas({ tolerance: 10 }),
});
map.attributionControl.setPrefix('<a href="https://leafletjs.com" target="_blank" rel="noopener">Leaflet</a>');
L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
  maxZoom: 19,
  attribution: '&copy; <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener">OpenStreetMap</a>-Mitwirkende',
}).addTo(map);
// Startausschnitt: Innenstadt von Pferdemarkt bis Stau/Alter Stadthafen
map.fitBounds(INNENSTADT, { animate: false });
// bei kleinem Maßstab kompaktere Parkhaus-Beschriftungen (weniger Überlappung)
const updateZoomClass = () => map.getContainer().classList.toggle('z-low', map.getZoom() <= 15);
map.on('zoomend', updateZoomClass);
updateZoomClass();

const layers = {
  shapes: L.layerGroup().addTo(map),
  garages: L.layerGroup().addTo(map),
  zone: L.layerGroup().addTo(map),
  pins: L.layerGroup().addTo(map),
};

// ---------------------------------------------------------------- Zeit
function currentTime() {
  if (state.mode === 'now') return nowInOldenburg();
  const today = startOfDay(nowInOldenburg());
  const [h, m] = state.planTime.split(':').map(Number);
  return addMinutes(addMinutes(today, state.planDay * 1440), h * 60 + m);
}

function fillDaySelect() {
  const sel = $('#plan-day');
  const today = startOfDay(nowInOldenburg());
  sel.innerHTML = '';
  for (let i = 0; i < 8; i++) {
    const d = addMinutes(today, i * 1440);
    const label = i === 0 ? 'Heute' : i === 1 ? 'Morgen' : DAY_NAMES[d.getUTCDay()];
    const opt = new Option(`${label}, ${pad2(d.getUTCDate())}.${pad2(d.getUTCMonth() + 1)}.`, String(i));
    opt.dataset.day = dayCode(d);
    sel.append(opt);
  }
  sel.value = String(state.planDay);
}

// ---------------------------------------------------------------- Daten laden
async function loadJSON(url) {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`${url}: HTTP ${res.status}`);
  return res.json();
}

async function init() {
  fillDaySelect();
  bindUI();
  const [geo, tarife] = await Promise.all([loadJSON(geojsonUrl), loadJSON(tarifeUrl)]);
  state.rules = prepareRules(tarife);
  state.meta = geo.metadata;
  drawZones();
  state.items = geo.features.map((f) => ({ feature: f, info: describe(f, state.rules), layer: null, marker: null, ev: null }));
  buildLayers();
  refresh();
  window.__parkol.ready = true;
  document.body.dataset.ready = 'true';
  $('#data-stand').textContent = `Parkflächen: OpenStreetMap, Stand ${formatDate(geo.metadata?.osm_stand)}. Tarife: Stand ${tarife.stand}.`;
  refreshLive();
  setInterval(() => {
    if (state.mode === 'now') refresh();
  }, 60_000);
  setInterval(refreshLive, 5 * 60_000);
}

function formatDate(iso) {
  if (!iso) return 'unbekannt';
  const d = new Date(iso);
  return d.toLocaleDateString('de-DE', { day: '2-digit', month: '2-digit', year: 'numeric', timeZone: 'Europe/Berlin' });
}

async function refreshLive() {
  state.live = await loadLive(document.baseURI, state.rules.raw.live.max_alter_minuten);
  window.__parkol.live = state.live.state;
  refresh();
}

function drawZones() {
  for (const z of state.rules.zones) {
    if (!z.ring || z.id !== 'I') continue;
    L.polygon(z.ring.map(([lon, lat]) => [lat, lon]), {
      color: '#16325c',
      weight: 2,
      dashArray: '6 6',
      fill: false,
      interactive: false,
    })
      .bindTooltip(`${z.name} (Grenze genähert)`, { sticky: true })
      .addTo(layers.zone);
  }
}

// ---------------------------------------------------------------- Layer
function buildLayers() {
  for (const item of state.items) {
    const { feature, info } = item;
    const onClick = (e) => {
      L.DomEvent.stopPropagation(e);
      select(item, false);
    };
    if (info.kind === 'garage') {
      item.marker = L.marker([info.point[1], info.point[0]], { icon: garageIcon(item), keyboard: true, title: info.title, riseOnHover: true });
      item.marker.on('click', onClick);
      continue;
    }
    const g = feature.geometry;
    if (g.type === 'Point') {
      item.layer = L.circleMarker([g.coordinates[1], g.coordinates[0]], { radius: 8, weight: 2 });
    } else if (g.type === 'LineString') {
      item.layer = L.polyline(g.coordinates.map(([x, y]) => [y, x]), { weight: 6, lineCap: 'round' });
    } else {
      const rings = g.type === 'Polygon' ? [g.coordinates] : g.coordinates;
      item.layer = L.polygon(rings.map((poly) => poly[0].map(([x, y]) => [y, x])), { weight: 2, fillOpacity: 0.45 });
    }
    item.layer.on('click', onClick);
  }
}

function garageIcon(item) {
  const ev = item.ev;
  const live = liveFor(item);
  let label = ev?.priceShort ?? 'P';
  let cls = 'garage-pin';
  if (live && state.mode === 'now') {
    label = live.status === 'Offen' ? `${live.frei} frei` : live.status;
    cls += live.frei === 0 || live.status !== 'Offen' ? ' full' : '';
    cls += state.live.state === 'stale' ? ' stale' : '';
  }
  if (ev?.unverified?.length) cls += ' unverified';
  if (label === '?') cls += ' nolabel';
  return L.divIcon({
    className: 'garage-icon',
    html: `<div class="${cls}" data-testid="garage-marker"><b>P</b><span>${esc(label)}</span></div>`,
    iconSize: [54, 34],
    iconAnchor: [27, 17],
  });
}

function liveFor(item) {
  if (!item.info.liveName || state.live.state === 'none') return null;
  return state.live.byName.get(item.info.liveName) ?? null;
}

function passesFilter(item) {
  if (!state.showCustomers && item.info.customersOnly) return false;
  const cat = item.ev.category;
  switch (state.filter) {
    case 'free':
      return cat === CATEGORY.FREE || (cat === CATEGORY.GARAGE && item.ev.cost?.max === 0);
    case 'garage':
      return item.info.kind === 'garage';
    case 'street':
      return item.info.kind !== 'garage';
    default:
      return true;
  }
}

// ---------------------------------------------------------------- Aktualisieren
function refresh() {
  if (!state.rules) return;
  const t = currentTime();
  const counts = { free: 0, paid: 0, garage: 0, unknown: 0, visible: 0 };
  for (const item of state.items) {
    item.ev = evaluate(item.feature, item.info, state.rules, t, state.duration);
    item.visible = passesFilter(item);
    const color = COLORS[item.ev.category];
    if (item.layer) {
      item.layer.setStyle({ color: darker(item.ev.category), fillColor: color, opacity: 1, fillOpacity: item.feature.geometry.type === 'Point' ? 0.9 : 0.45 });
      if (item.feature.geometry.type === 'LineString') item.layer.setStyle({ color });
      toggle(layers.shapes, item.layer, item.visible);
    }
    if (item.marker) {
      item.marker.setIcon(garageIcon(item));
      toggle(layers.garages, item.marker, item.visible);
    }
    if (item.visible) {
      counts.visible++;
      counts[item.ev.category]++;
    }
  }
  window.__parkol.counts = counts;
  window.__parkol.time = t.toISOString();
  renderSummary(t, counts);
  renderLiveStatus();
  renderList();
  if (state.selected) renderDetail(state.selected);
}

function toggle(group, layer, on) {
  if (on && !group.hasLayer(layer)) group.addLayer(layer);
  if (!on && group.hasLayer(layer)) group.removeLayer(layer);
}

function darker(cat) {
  return { free: '#0f6b36', paid: '#8a6200', garage: '#123d80', unknown: '#555a62' }[cat];
}

function timeLabel(t) {
  const prefix = state.mode === 'now' ? 'Jetzt' : 'Geplant';
  return `${prefix}: ${DAY_NAMES[t.getUTCDay()]}, ${formatHM(t)} Uhr`;
}

function renderSummary(t, c) {
  $('#summary').innerHTML = `<b>${esc(timeLabel(t))}</b> · ${c.visible} Orte · <span class="c-free">${c.free} kostenlos</span>`;
}

function renderLiveStatus() {
  const el = $('#live-status');
  const live = state.live;
  if (state.mode !== 'now') {
    el.className = 'live-status muted';
    el.textContent = 'Live-Belegung wird nur für „Jetzt“ angezeigt.';
    return;
  }
  if (live.state === 'ok') {
    el.className = 'live-status ok';
    el.textContent = `Live-Belegung Parkhäuser: Stand ${formatHM(nowInOldenburg(live.stand))} Uhr (Parkleitsystem Stadt Oldenburg)`;
  } else if (live.state === 'stale') {
    el.className = 'live-status warn';
    el.textContent = `Live-Daten veraltet (Stand ${formatHM(nowInOldenburg(live.stand))} Uhr) – freie Plätze können abweichen.`;
  } else {
    el.className = 'live-status muted';
    el.textContent = 'Keine Live-Daten zur Parkhausbelegung verfügbar.';
  }
}

// ---------------------------------------------------------------- Liste
function referencePoint() {
  if (state.target) return { point: state.target.point, label: `Nähe ${state.target.name}` };
  if (state.me) return { point: state.me, label: 'In der Nähe (Standort)' };
  const c = map.getCenter();
  return { point: [c.lng, c.lat], label: 'Kartenmitte' };
}

function renderList() {
  const ref = referencePoint();
  $('#list-title').textContent = `Sortiert nach Entfernung – ${ref.label}`;
  const visible = state.items.filter((i) => i.visible);
  for (const i of visible) i.dist = distance(ref.point, i.info.point);
  visible.sort((a, b) => a.dist - b.dist);
  const ol = $('#list');
  ol.innerHTML = visible
    .slice(0, LIST_LIMIT)
    .map((i) => {
      const live = liveFor(i);
      const liveTxt = live && state.mode === 'now' ? `<span class="live ${state.live.state}">${live.status === 'Offen' ? `${live.frei} frei` : esc(live.status)}</span>` : '';
      const cost = i.ev.cost ? `${state.duration} Min.: ${esc(euroRange(i.ev.cost))}` : '';
      return `<li><button type="button" class="item" data-id="${esc(i.feature.id)}">
        <i class="dot ${i.ev.category}"></i>
        <span class="item-main"><span class="item-title">${esc(i.info.title)}</span>
        <span class="item-sub">${esc(i.info.kindLabel)} · ${esc(i.ev.priceNow)}${i.ev.change ? ` · ${esc(i.ev.change.text)}` : ''}</span>
        <span class="item-sub">${cost ? `${cost}` : ''}${i.ev.unverified.length ? ' <span class="badge warn">unbestätigt</span>' : ''}</span></span>
        <span class="item-side">${liveTxt}<span class="dist">${formatDistance(i.dist)}</span></span>
      </button></li>`;
    })
    .join('');
  if (!visible.length) ol.innerHTML = '<li class="empty">Keine passenden Orte für diesen Filter.</li>';
}

// ---------------------------------------------------------------- Detail
function select(item, fly = true) {
  state.selected = item;
  renderDetail(item);
  const panel = $('#panel');
  panel.classList.remove('collapsed');
  $('#panel-toggle').setAttribute('aria-expanded', 'true');
  $('#panel-body').scrollTop = 0;
  if (fly) map.flyTo([item.info.point[1], item.info.point[0]], Math.max(map.getZoom(), 17), { duration: 0.5 });
  highlight(item);
}

let highlightLayer = null;
function highlight(item) {
  if (highlightLayer) layers.pins.removeLayer(highlightLayer);
  highlightLayer = L.circleMarker([item.info.point[1], item.info.point[0]], { radius: 18, color: '#16325c', weight: 3, fill: false, interactive: false });
  layers.pins.addLayer(highlightLayer);
}

function renderDetail(item) {
  const el = $('#detail');
  const { info, ev } = item;
  const live = liveFor(item);
  const t = currentTime();
  const rows = [];
  const row = (k, v) => v && rows.push(`<dt>${esc(k)}</dt><dd>${v}</dd>`);
  row('Art', esc(info.kindLabel));
  row('Stellplätze', info.capacity ? `${info.capacity} (laut OSM)` : null);
  if (info.kind === 'garage' || live) {
    if (state.mode !== 'now') row('Freie Plätze', 'Live-Daten nur für „Jetzt“');
    else if (live) row('Freie Plätze', `<b>${live.status === 'Offen' ? `${live.frei} von ${live.gesamt}` : esc(live.status)}</b> ${state.live.state === 'stale' ? '<span class="badge warn">veraltet</span>' : ''}<br><small>Parkleitsystem, Stand ${formatHM(nowInOldenburg(state.live.stand))} Uhr</small>`);
    else if (info.kind === 'garage') row('Freie Plätze', 'keine Live-Daten');
  }
  row(`Preis (${DAY_NAMES[t.getUTCDay()]} ${formatHM(t)})`, `<b>${esc(ev.priceNow)}</b>`);
  if (ev.change) row(ev.change.type === 'free_from' ? 'Kostenlos ab' : 'Kostenpflichtig ab', esc(ev.change.text.replace(/^kosten(los|pflichtig) ab /, '')));
  if (ev.cost) row(`Kosten für ${formatMinutes(state.duration)}`, esc(euroRange(ev.cost)));
  if (ev.zoneName) row('Zone', esc(ev.zoneName));
  if (ev.feeTimes) row('Gebührenpflichtig', esc(ev.feeTimes));
  if (ev.tariffText) row('Tarif', esc(ev.tariffText));
  if (ev.openingHours) row('Geöffnet', esc(ev.openingHours));
  row('Höchstparkdauer', esc(ev.maxstay ?? 'keine Angabe'));
  const notes = ev.notes.map((n) => `<li>${esc(n)}</li>`).join('');
  const sources = ev.sources
    .map((s) => {
      const badge = s.verified === true ? '<span class="badge ok">belegt</span>' : s.verified === false ? '<span class="badge warn">unbestätigt</span>' : '';
      const text = s.url ? `<a href="${esc(s.url)}" target="_blank" rel="noopener">${esc(s.text)}</a>` : esc(s.text);
      return `<li>${badge} ${text}</li>`;
    })
    .join('');
  const osmLink = `https://www.openstreetmap.org/${esc(String(item.feature.id).split('#')[0])}`;
  el.innerHTML = `
    <header class="detail-head">
      <i class="dot ${ev.category}"></i>
      <h2>${esc(info.title)}</h2>
      <button type="button" class="btn btn-icon close" aria-label="Details schließen">✕</button>
    </header>
    ${ev.unverified.length ? `<p class="unverified-note"><span class="badge warn">unbestätigt</span> Nicht sicher belegt: ${esc(ev.unverified.join(', '))}. Bitte vor Ort prüfen.</p>` : ''}
    <dl>${rows.join('')}</dl>
    ${notes ? `<ul class="notes">${notes}</ul>` : ''}
    <h3>Datenquellen</h3>
    <ul class="sources">${sources}<li><a href="${osmLink}" target="_blank" rel="noopener">Objekt auf openstreetmap.org ansehen</a></li></ul>
    <a class="btn nav" href="https://www.openstreetmap.org/directions?to=${info.point[1]}%2C${info.point[0]}" target="_blank" rel="noopener">Route (OSM)</a>`;
  el.hidden = false;
  el.dataset.category = ev.category;
  el.querySelector('.close').addEventListener('click', closeDetail);
}

function closeDetail() {
  state.selected = null;
  $('#detail').hidden = true;
  if (highlightLayer) layers.pins.removeLayer(highlightLayer);
}

// ---------------------------------------------------------------- Suche & Standort
let targetMarker = null;
let meMarker = null;

function setTarget(place) {
  state.target = place;
  if (targetMarker) layers.pins.removeLayer(targetMarker);
  targetMarker = L.marker([place.point[1], place.point[0]], {
    icon: L.divIcon({ className: 'target-icon', html: '<div class="target-pin" data-testid="target-marker"></div>', iconSize: [26, 26], iconAnchor: [13, 26] }),
    title: place.name,
  }).bindTooltip(place.name);
  layers.pins.addLayer(targetMarker);
  map.flyTo([place.point[1], place.point[0]], 17, { duration: 0.6 });
  $('#search-results').hidden = true;
  renderList();
}

function showToast(msg) {
  const el = $('#toast');
  el.textContent = msg;
  el.hidden = false;
  clearTimeout(showToast.t);
  showToast.t = setTimeout(() => (el.hidden = true), 4000);
}

function bindUI() {
  document.querySelectorAll('.chip').forEach((b) =>
    b.addEventListener('click', () => {
      state.filter = b.dataset.filter;
      document.querySelectorAll('.chip').forEach((x) => x.setAttribute('aria-checked', String(x === b)));
      refresh();
    }),
  );
  const setMode = (mode) => {
    state.mode = mode;
    $('#mode-now').setAttribute('aria-checked', String(mode === 'now'));
    $('#mode-plan').setAttribute('aria-checked', String(mode === 'plan'));
    $('#plan-fields').hidden = mode !== 'plan';
    refresh();
  };
  $('#mode-now').addEventListener('click', () => setMode('now'));
  $('#mode-plan').addEventListener('click', () => {
    fillDaySelect();
    setMode('plan');
  });
  $('#plan-day').addEventListener('change', (e) => {
    state.planDay = +e.target.value;
    refresh();
  });
  $('#plan-time').addEventListener('change', (e) => {
    if (/^\d{2}:\d{2}/.test(e.target.value)) state.planTime = e.target.value.slice(0, 5);
    refresh();
  });
  $('#duration').addEventListener('change', (e) => {
    state.duration = +e.target.value;
    refresh();
  });
  $('#show-customers').addEventListener('change', (e) => {
    state.showCustomers = e.target.checked;
    refresh();
  });
  $('#list').addEventListener('click', (e) => {
    const btn = e.target.closest('.item');
    if (!btn) return;
    const item = state.items.find((i) => i.feature.id === btn.dataset.id);
    if (item) select(item);
  });
  $('#panel-toggle').addEventListener('click', () => {
    const panel = $('#panel');
    const collapsed = panel.classList.toggle('collapsed');
    $('#panel-toggle').setAttribute('aria-expanded', String(!collapsed));
    setTimeout(() => map.invalidateSize(), 250);
  });
  map.on('moveend', () => {
    if (!state.target && !state.me) renderList();
  });

  $('#search').addEventListener('submit', async (e) => {
    e.preventDefault();
    const q = $('#search-input').value.trim();
    if (q.length < 3) return showToast('Bitte mindestens 3 Zeichen eingeben.');
    const ul = $('#search-results');
    ul.hidden = false;
    ul.innerHTML = '<li class="muted">Suche …</li>';
    try {
      const results = await searchPlaces(q);
      if (!results.length) {
        ul.innerHTML = '<li class="muted">Nichts gefunden in Oldenburg.</li>';
        return;
      }
      ul.innerHTML = results.map((r, i) => `<li><button type="button" data-i="${i}">${esc(r.name)}<small>${esc(r.detail)}</small></button></li>`).join('') + '<li class="attrib">Suche: <a href="https://nominatim.openstreetmap.org/" target="_blank" rel="noopener">Nominatim</a> / OpenStreetMap</li>';
      ul.querySelectorAll('button').forEach((b) => b.addEventListener('click', () => setTarget(results[+b.dataset.i])));
      if (results.length === 1) setTarget(results[0]);
    } catch (err) {
      ul.innerHTML = `<li class="muted">${esc(err.message)}</li>`;
    }
  });
  $('#search-input').addEventListener('input', (e) => {
    if (!e.target.value) {
      state.target = null;
      if (targetMarker) layers.pins.removeLayer(targetMarker);
      $('#search-results').hidden = true;
      renderList();
    }
  });

  $('#locate').addEventListener('click', () => {
    if (!('geolocation' in navigator)) return showToast('Standortbestimmung wird nicht unterstützt.');
    $('#locate').classList.add('busy');
    navigator.geolocation.getCurrentPosition(
      (pos) => {
        $('#locate').classList.remove('busy');
        state.me = [pos.coords.longitude, pos.coords.latitude];
        if (meMarker) layers.pins.removeLayer(meMarker);
        meMarker = L.circleMarker([pos.coords.latitude, pos.coords.longitude], { radius: 9, color: '#fff', weight: 3, fillColor: '#2a7de1', fillOpacity: 1 });
        layers.pins.addLayer(meMarker);
        map.flyTo([pos.coords.latitude, pos.coords.longitude], 17, { duration: 0.6 });
        renderList();
      },
      (err) => {
        $('#locate').classList.remove('busy');
        showToast(err.code === 1 ? 'Standortzugriff nicht erlaubt.' : 'Standort konnte nicht bestimmt werden.');
      },
      { enableHighAccuracy: true, timeout: 10_000, maximumAge: 60_000 },
    );
  });
}

init().catch((err) => {
  console.error(err);
  $('#summary').textContent = 'Daten konnten nicht geladen werden.';
});

if ('serviceWorker' in navigator && import.meta.env.PROD) {
  window.addEventListener('load', () => navigator.serviceWorker.register('sw.js').catch(() => {}));
}

