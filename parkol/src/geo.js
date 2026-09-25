// Kleine Geometrie-Helfer (ohne Abhängigkeiten). Koordinaten in GeoJSON-Reihenfolge [lon, lat].

const R = 6371000;
const rad = (d) => (d * Math.PI) / 180;

/** Entfernung in Metern zwischen zwei Punkten [lon, lat]. */
export function distance(a, b) {
  const dLat = rad(b[1] - a[1]);
  const dLon = rad(b[0] - a[0]);
  const h = Math.sin(dLat / 2) ** 2 + Math.cos(rad(a[1])) * Math.cos(rad(b[1])) * Math.sin(dLon / 2) ** 2;
  return 2 * R * Math.asin(Math.sqrt(h));
}

/** Repräsentativer Punkt einer Geometrie (Punkt, Linienmitte, Flächenschwerpunkt der Stützpunkte). */
export function anchor(geometry) {
  switch (geometry.type) {
    case 'Point':
      return geometry.coordinates;
    case 'LineString':
      return lineMidpoint(geometry.coordinates);
    case 'Polygon':
      return mean(geometry.coordinates[0].slice(0, -1));
    case 'MultiPolygon':
      return mean(geometry.coordinates.flatMap((p) => p[0].slice(0, -1)));
    default:
      return null;
  }
}

function mean(pts) {
  const n = pts.length || 1;
  return [pts.reduce((s, p) => s + p[0], 0) / n, pts.reduce((s, p) => s + p[1], 0) / n];
}

function lineMidpoint(coords) {
  let total = 0;
  const seg = [];
  for (let i = 1; i < coords.length; i++) {
    const d = distance(coords[i - 1], coords[i]);
    seg.push(d);
    total += d;
  }
  let half = total / 2;
  for (let i = 0; i < seg.length; i++) {
    if (half <= seg[i] && seg[i] > 0) {
      const f = half / seg[i];
      return [coords[i][0] + (coords[i + 1][0] - coords[i][0]) * f, coords[i][1] + (coords[i + 1][1] - coords[i][1]) * f];
    }
    half -= seg[i];
  }
  return coords[0];
}

/** Punkt-in-Polygon (Ray casting). ring: [[lon, lat], …] */
export function pointInRing(p, ring) {
  let inside = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const [xi, yi] = ring[i];
    const [xj, yj] = ring[j];
    if (yi > p[1] !== yj > p[1] && p[0] < ((xj - xi) * (p[1] - yi)) / (yj - yi) + xi) inside = !inside;
  }
  return inside;
}

/** Kürzeste Entfernung (Meter) von Punkt p zum Rand des Rings. */
export function distanceToRing(p, ring) {
  // lokale äquirektanguläre Projektion um p
  const kx = Math.cos(rad(p[1])) * 111320;
  const ky = 110540;
  const proj = (q) => [(q[0] - p[0]) * kx, (q[1] - p[1]) * ky];
  let best = Infinity;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const a = proj(ring[j]);
    const b = proj(ring[i]);
    const dx = b[0] - a[0];
    const dy = b[1] - a[1];
    const len2 = dx * dx + dy * dy;
    let t = len2 ? -(a[0] * dx + a[1] * dy) / len2 : 0;
    t = Math.max(0, Math.min(1, t));
    const x = a[0] + t * dx;
    const y = a[1] + t * dy;
    best = Math.min(best, Math.hypot(x, y));
  }
  return best;
}

/** [lat, lon]-Liste aus tarife.json → GeoJSON-Ring [lon, lat] (geschlossen). */
export function ringFromLatLon(points) {
  const ring = points.map(([lat, lon]) => [lon, lat]);
  const [f, l] = [ring[0], ring.at(-1)];
  if (f[0] !== l[0] || f[1] !== l[1]) ring.push(f);
  return ring;
}

export function formatDistance(m) {
  if (m < 1000) return `${Math.round(m / 10) * 10} m`;
  return `${(m / 1000).toFixed(1).replace('.', ',')} km`;
}
