import { expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';

// 1×1 PNG für gestubbte Kartenkacheln (keine Last auf tile.openstreetmap.org in Tests)
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=', 'base64');

// Dienstag, 22.09.2026, 11:00 Uhr in Oldenburg (MESZ)
const TUESDAY_11 = new Date('2026-09-22T09:00:00Z');

function liveFixture(standIso) {
  return {
    status: 'ok',
    quelle: 'test',
    stand: standIso,
    eintraege: [
      { name: 'Parkhaus Am Waffenplatz', gesamt: 568, frei: 231, status: 'Offen' },
      { name: 'Parkhaus Theatergarage', gesamt: 0, frei: 0, status: 'Störung' },
      { name: 'Parkhaus Schlosshöfe', gesamt: 430, frei: 233, status: 'Offen' },
    ],
  };
}

let consoleErrors;

test.beforeEach(async ({ page }) => {
  consoleErrors = [];
  page.on('console', (m) => {
    if (m.type() === 'error') consoleErrors.push(m.text());
  });
  page.on('pageerror', (e) => consoleErrors.push(e.message));
  await page.route('https://tile.openstreetmap.org/**', (r) => r.fulfill({ contentType: 'image/png', body: PNG }));
  await page.route('https://nominatim.openstreetmap.org/**', (r) =>
    r.fulfill({
      contentType: 'application/json',
      body: JSON.stringify([{ name: 'Lappan', display_name: 'Lappan, Innenstadt, Oldenburg, Niedersachsen, Deutschland', lat: '53.14322', lon: '8.21264' }]),
    }),
  );
  await page.clock.setFixedTime(TUESDAY_11);
});

test.afterEach(async () => {
  expect(consoleErrors, 'keine Konsolenfehler').toEqual([]);
});

async function open(page, live = null) {
  if (live) await page.route('**/live.json*', (r) => r.fulfill({ contentType: 'application/json', body: JSON.stringify(live) }));
  await page.goto('./');
  await page.waitForFunction(() => window.__parkol?.ready === true);
}

const counts = (page) => page.evaluate(() => window.__parkol.counts);

test('Karte lädt mit OSM-Kacheln, Quellenangabe und Parkflächen', async ({ page }) => {
  await open(page);
  await expect(page.locator('.leaflet-container')).toBeVisible();
  await expect(page.locator('.leaflet-tile-loaded').first()).toBeAttached();
  await expect(page.locator('.leaflet-control-attribution')).toContainText('OpenStreetMap');
  await expect(page.locator('[data-testid=garage-marker]').first()).toBeVisible();
  expect(await page.locator('[data-testid=garage-marker]').count()).toBeGreaterThanOrEqual(8);
  const c = await counts(page);
  expect(c.visible).toBeGreaterThan(200);
  expect(c.paid).toBeGreaterThan(0);
  expect(c.free).toBeGreaterThan(0);
  await expect(page.locator('#summary')).toContainText('Jetzt: Dienstag, 11:00 Uhr');
  await expect(page.locator('#list .item').first()).toBeVisible();
});

test('kein horizontales Scrollen', async ({ page }) => {
  await open(page);
  const { sw, iw } = await page.evaluate(() => ({ sw: document.documentElement.scrollWidth, iw: window.innerWidth }));
  expect(sw).toBeLessThanOrEqual(iw);
  // auch mit geöffneter Detailansicht
  await page.locator('#list .item').first().click();
  await expect(page.locator('#detail')).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(await page.evaluate(() => window.innerWidth));
});

test('Filter "Jetzt kostenlos", "Parkhäuser" und "Straße/Parkplatz"', async ({ page }) => {
  await open(page);
  const all = await counts(page);

  await page.getByRole('radio', { name: 'Jetzt kostenlos' }).click();
  let c = await counts(page);
  expect(c.paid).toBe(0);
  expect(c.unknown).toBe(0);
  expect(c.free).toBeGreaterThan(0);
  expect(c.visible).toBeLessThan(all.visible);
  await expect(page.locator('#list .item .dot.paid')).toHaveCount(0);
  await expect(page.locator('#list .item .dot.free').first()).toBeVisible();

  await page.getByRole('radio', { name: 'Parkhäuser' }).click();
  c = await counts(page);
  expect(c.visible).toBe(c.garage);
  expect(c.garage).toBeGreaterThanOrEqual(8);
  await expect(page.locator('#list .item .dot:not(.garage)')).toHaveCount(0);

  await page.getByRole('radio', { name: 'Straße/Parkplatz' }).click();
  c = await counts(page);
  expect(c.garage).toBe(0);
  expect(c.visible).toBeGreaterThan(100);
  await expect(page.locator('[data-testid=garage-marker]')).toHaveCount(0);

  await page.getByRole('radio', { name: 'Alle' }).click();
  expect((await counts(page)).visible).toBe(all.visible);
});

test('Zeitauswahl: Samstag 14 Uhr kostenpflichtig, Sonntag und ab 19 Uhr kostenlos', async ({ page }) => {
  await open(page);
  await page.getByRole('radio', { name: 'Planen' }).click();
  await expect(page.locator('#plan-fields')).toBeVisible();

  // Tag-Auswahl: Samstag
  const satIndex = await page.locator('#plan-day option[data-day="Sa"]').getAttribute('value');
  await page.selectOption('#plan-day', satIndex);
  await page.fill('#plan-time', '14:00');
  await page.locator('#plan-time').dispatchEvent('change');
  await expect(page.locator('#summary')).toContainText('Geplant: Samstag, 14:00 Uhr');
  expect((await counts(page)).paid).toBeGreaterThan(0);

  // 18:59 noch kostenpflichtig, 19:00 kostenlos
  await page.fill('#plan-time', '18:59');
  await page.locator('#plan-time').dispatchEvent('change');
  const at1859 = await counts(page);
  expect(at1859.paid).toBeGreaterThan(0);
  await page.fill('#plan-time', '19:00');
  await page.locator('#plan-time').dispatchEvent('change');
  const at1900 = await counts(page);
  expect(at1900.free).toBeGreaterThan(at1859.free);

  // Sonntag
  const sunIndex = await page.locator('#plan-day option[data-day="So"]').getAttribute('value');
  await page.selectOption('#plan-day', sunIndex);
  await page.fill('#plan-time', '14:00');
  await page.locator('#plan-time').dispatchEvent('change');
  await expect(page.locator('#summary')).toContainText('Geplant: Sonntag, 14:00 Uhr');
  const sun = await counts(page);
  expect(sun.free).toBeGreaterThan(at1859.free);

  // zurück auf "Jetzt"
  await page.getByRole('radio', { name: 'Jetzt', exact: true }).click();
  await expect(page.locator('#summary')).toContainText('Jetzt: Dienstag, 11:00 Uhr');
});

test('Detailansicht zeigt Preis, Kostenlos-ab, Quelle und "unbestätigt"', async ({ page }) => {
  await open(page);
  await page.getByRole('radio', { name: 'Straße/Parkplatz' }).click();
  const paidItem = page.locator('#list .item:has(.dot.paid)').first();
  await paidItem.click();
  const detail = page.locator('#detail');
  await expect(detail).toBeVisible();
  await expect(detail).toContainText('€/h');
  await expect(detail).toContainText('Kostenlos ab');
  await expect(detail).toContainText('19:00');
  await expect(detail).toContainText('Höchstparkdauer');
  await expect(detail).toContainText('Datenquellen');

  // Parkhaus mit unbestätigtem Tarif
  await page.getByRole('radio', { name: 'Parkhäuser' }).click();
  await page.locator('#list .item:has(.badge.warn)').first().click();
  await expect(detail.locator('.unverified-note')).toContainText('unbestätigt');
  await detail.getByRole('button', { name: 'Details schließen' }).click();
  await expect(detail).toBeHidden();
});

test('Live-Belegung wird angezeigt, wenn aktuelle Daten vorliegen', async ({ page }) => {
  await open(page, liveFixture('2026-09-22T08:55:00Z'));
  await expect(page.locator('#live-status')).toContainText('Stand 10:55 Uhr');
  await expect(page.locator('[data-testid=garage-marker]', { hasText: '231 frei' })).toBeVisible();
  await page.getByRole('radio', { name: 'Parkhäuser' }).click();
  await page.locator('#list .item', { hasText: 'Am Waffenplatz' }).click();
  await expect(page.locator('#detail')).toContainText('231 von 568');
});

test('veraltete Live-Daten werden als veraltet gekennzeichnet', async ({ page }) => {
  await open(page, liveFixture('2026-09-22T06:00:00Z'));
  await expect(page.locator('#live-status')).toContainText('veraltet');
});

test('ohne Live-Daten: Hinweis "keine Live-Daten"', async ({ page }) => {
  await open(page); // Platzhalter-live.json aus dem Build
  await expect(page.locator('#live-status')).toContainText('Keine Live-Daten');
  await page.getByRole('radio', { name: 'Parkhäuser' }).click();
  await page.locator('#list .item').first().click();
  await expect(page.locator('#detail')).toContainText('keine Live-Daten');
});

test('Adresssuche (Nominatim) setzt Ziel und sortiert nach Entfernung', async ({ page }) => {
  await open(page);
  await page.fill('#search-input', 'Lappan');
  await page.getByRole('button', { name: 'Suchen', exact: true }).click();
  await expect(page.locator('[data-testid=target-marker]')).toBeVisible();
  await expect(page.locator('#list-title')).toContainText('Nähe Lappan');
  const dists = await page.locator('#list .dist').allTextContents();
  const meters = dists.map((d) => (d.includes('km') ? parseFloat(d.replace(',', '.')) * 1000 : parseFloat(d)));
  expect(meters).toEqual([...meters].sort((a, b) => a - b));
});

test('Mein Standort', async ({ page, context }) => {
  await context.grantPermissions(['geolocation']);
  await context.setGeolocation({ latitude: 53.1392, longitude: 8.2171 });
  await open(page);
  await page.getByRole('button', { name: 'Mein Standort' }).click();
  await expect(page.locator('#list-title')).toContainText('Standort');
});

test('PWA: Manifest und Icons vorhanden', async ({ page, request }) => {
  await open(page);
  const href = await page.locator('link[rel=manifest]').getAttribute('href');
  const res = await request.get(href);
  expect(res.ok()).toBe(true);
  const manifest = await res.json();
  expect(manifest.display).toBe('standalone');
  for (const icon of manifest.icons) expect((await request.get(icon.src)).ok()).toBe(true);
  expect((await request.get('sw.js')).ok()).toBe(true);
});

test('Screenshots für die Sichtprüfung', async ({ page }, info) => {
  mkdirSync('screenshots', { recursive: true });
  await open(page, liveFixture('2026-09-22T08:55:00Z'));
  await page.screenshot({ path: `screenshots/${info.project.name}-start.png` });
  await page.getByRole('radio', { name: 'Straße/Parkplatz' }).click();
  await page.locator('#list .item').first().click();
  await page.screenshot({ path: `screenshots/${info.project.name}-detail.png` });
});
