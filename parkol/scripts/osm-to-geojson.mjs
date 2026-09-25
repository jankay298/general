// Wandelt eine Overpass-Antwort (out tags geom) in schlankes GeoJSON um.
// Jedes Feature erhält normalisierte Eigenschaften, die die App direkt
// auswerten kann. Die Original-Tags bleiben unter `tags` erhalten.

const SIDES = ['left', 'right'];
const NO_PARKING = new Set(['no', 'separate', 'no_parking', 'no_stopping', 'fire_lane', 'none']);
const GARAGE_TYPES = new Set(['multi-storey', 'underground', 'rooftop']);
const STREET_TYPES = new Set(['street_side', 'lane', 'on_kerb', 'half_on_kerb', 'shoulder']);

const round = (n) => Math.round(n * 1e6) / 1e6;
const coords = (geom) => geom.map((p) => [round(p.lon), round(p.lat)]);

/** Liest einen Tag für eine Straßenseite, mit Fallback auf `both` und ohne Seitenangabe. */
function sideTag(tags, side, suffix, prefix = 'parking') {
  const s = suffix ? `:${suffix}` : '';
  return tags[`${prefix}:${side}${s}`] ?? tags[`${prefix}:both${s}`] ?? (suffix ? tags[`${prefix}${s}`] : undefined);
}

/** Normalisiert das Straßenparken einer Seite (neues und altes OSM-Schema). */
export function streetSide(tags, side) {
  // Neues Schema: parking:<side>=lane|street_side|on_kerb|…
  const type = sideTag(tags, side, '');
  if (type !== undefined) {
    if (NO_PARKING.has(type)) return null;
    const restriction = sideTag(tags, side, 'restriction');
    if (restriction && /no_parking|no_stopping|no_standing|loading_only|charging_only/.test(restriction)) return null;
    const access = sideTag(tags, side, 'access');
    if (access === 'private' || access === 'no') return null;
    return {
      side,
      orientation: sideTag(tags, side, 'orientation') ?? null,
      fee: sideTag(tags, side, 'fee') ?? null,
      feeConditional: sideTag(tags, side, 'fee:conditional') ?? null,
      maxstay: sideTag(tags, side, 'maxstay') ?? null,
      maxstayConditional: sideTag(tags, side, 'maxstay:conditional') ?? null,
      restrictionConditional: sideTag(tags, side, 'restriction:conditional') ?? null,
      access: access ?? sideTag(tags, side, 'access:conditional') ?? null,
      residents: sideTag(tags, side, 'zone') ?? null,
      disc: sideTag(tags, side, 'authentication:disc') === 'yes' ? 'yes' : null,
      capacity: toInt(sideTag(tags, side, 'capacity')),
    };
  }
  // Altes Schema: parking:lane:<side>=parallel|diagonal|… + parking:condition:<side>
  const lane = sideTag(tags, side, '', 'parking:lane');
  if (lane === undefined || NO_PARKING.has(lane)) return null;
  const cond = sideTag(tags, side, '', 'parking:condition');
  if (cond && NO_PARKING.has(cond)) return null;
  if (cond === 'private') return null;
  const interval = sideTag(tags, side, 'time_interval', 'parking:condition');
  const deflt = sideTag(tags, side, 'default', 'parking:condition');
  let fee = null;
  let feeConditional = null;
  if (cond === 'ticket') {
    fee = interval ? (deflt === 'free' ? 'no' : null) : 'yes';
    if (interval) feeConditional = `yes @ (${interval})`;
  } else if (cond === 'free') fee = 'no';
  return {
    side,
    orientation: lane,
    fee,
    feeConditional,
    maxstay: sideTag(tags, side, 'maxstay', 'parking:condition') ?? null,
    maxstayConditional: null,
    restrictionConditional: null,
    access: cond === 'customers' ? 'customers' : null,
    residents: cond === 'residents' ? (sideTag(tags, side, 'residents', 'parking:condition') ?? 'yes') : null,
    disc: cond === 'disc' ? 'yes' : null,
    capacity: toInt(sideTag(tags, side, 'capacity', 'parking:lane')),
  };
}

function toInt(v) {
  const n = parseInt(v, 10);
  return Number.isFinite(n) ? n : null;
}

function lotProps(tags) {
  const parking = tags.parking ?? 'surface';
  return {
    kind: GARAGE_TYPES.has(parking) ? 'garage' : STREET_TYPES.has(parking) ? 'street' : 'lot',
    name: tags.name ?? null,
    parking,
    capacity: toInt(tags.capacity),
    fee: tags.fee ?? null,
    feeConditional: tags['fee:conditional'] ?? null,
    maxstay: tags.maxstay ?? null,
    maxstayConditional: tags['maxstay:conditional'] ?? null,
    access: tags.access ?? null,
    operator: tags.operator ?? null,
    openingHours: tags.opening_hours ?? null,
    residents: tags['parking:zone'] ?? tags.zone ?? null,
    disc: tags['authentication:disc'] === 'yes' ? 'yes' : null,
    charge: tags.charge ?? null,
  };
}

function geometryOf(el) {
  if (el.type === 'node') return { type: 'Point', coordinates: [round(el.lon), round(el.lat)] };
  if (el.type === 'way') {
    const c = coords(el.geometry ?? []);
    if (c.length < 2) return null;
    const closed = c.length > 3 && c[0][0] === c.at(-1)[0] && c[0][1] === c.at(-1)[1];
    if (closed && (el.tags?.amenity === 'parking' || el.tags?.area === 'yes')) {
      return { type: 'Polygon', coordinates: [c] };
    }
    return { type: 'LineString', coordinates: c };
  }
  if (el.type === 'relation') {
    const outers = (el.members ?? [])
      .filter((m) => m.type === 'way' && m.role !== 'inner' && m.geometry?.length > 3)
      .map((m) => [coords(m.geometry)]);
    if (!outers.length) return null;
    return { type: 'MultiPolygon', coordinates: outers };
  }
  return null;
}

/** Entfernt Tags, die für die App irrelevant sind (spart Bytes). */
function slimTags(tags) {
  const keep = {};
  for (const [k, v] of Object.entries(tags)) {
    if (/^(name|amenity|parking|capacity|fee|access|operator|opening_hours|maxstay|charge|highway|zone|website|surface|park_ride|supervised)/.test(k)) {
      keep[k] = v;
    }
  }
  return keep;
}

export function osmToGeoJSON(osm) {
  const features = [];
  for (const el of osm.elements ?? []) {
    const tags = el.tags ?? {};
    const geometry = geometryOf(el);
    if (!geometry) continue;
    const id = `${el.type}/${el.id}`;

    if (tags.amenity === 'parking') {
      if (tags.access === 'private' || tags.access === 'no') continue;
      features.push({ type: 'Feature', id, geometry, properties: { id, ...lotProps(tags), tags: slimTags(tags) } });
      continue;
    }
    if (tags.highway && el.type === 'way') {
      const sides = SIDES.map((s) => streetSide(tags, s)).filter(Boolean);
      if (!sides.length) continue;
      const same = sides.length === 2 && JSON.stringify({ ...sides[0], side: 0, capacity: 0 }) === JSON.stringify({ ...sides[1], side: 0, capacity: 0 });
      const groups = same ? [{ ...sides[0], side: 'both', capacity: sumCap(sides) }] : sides;
      for (const s of groups) {
        features.push({
          type: 'Feature',
          id: `${id}#${s.side}`,
          geometry,
          properties: {
            id: `${id}#${s.side}`,
            kind: 'street',
            name: tags.name ?? null,
            parking: 'street_side',
            ...s,
            operator: null,
            openingHours: null,
            charge: null,
            tags: slimTags(tags),
          },
        });
      }
    }
  }
  return { type: 'FeatureCollection', features };
}

function sumCap(sides) {
  const caps = sides.map((s) => s.capacity);
  return caps.every((c) => c != null) ? caps.reduce((a, b) => a + b, 0) : null;
}
