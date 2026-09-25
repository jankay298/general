#!/usr/bin/env node
// Erzeugt die PNG-Icons der PWA aus public/icons/favicon.svg (einmalig, Ergebnis ist eingecheckt).
import { chromium } from '@playwright/test';
import { readFile } from 'node:fs/promises';

const svg = await readFile(new URL('../public/icons/favicon.svg', import.meta.url), 'utf8');
const browser = await chromium.launch();
const page = await browser.newPage();
const sizes = [
  ['icon-192.png', 192, 0],
  ['icon-512.png', 512, 0],
  ['apple-touch-icon.png', 180, 0],
  ['icon-maskable-512.png', 512, 0.12],
];
for (const [name, size, pad] of sizes) {
  await page.setViewportSize({ width: size, height: size });
  const inner = Math.round(size * (1 - 2 * pad));
  await page.setContent(`<html><body style="margin:0;background:#1f5fbf;display:grid;place-items:center;width:${size}px;height:${size}px">
    <div style="width:${inner}px;height:${inner}px">${svg.replace('<svg ', `<svg width="${inner}" height="${inner}" `).replace(pad ? 'rx="14"' : '__', 'rx="0"')}</div></body></html>`);
  await page.screenshot({ path: new URL(`../public/icons/${name}`, import.meta.url).pathname, omitBackground: false });
}
await browser.close();
console.log('Icons erzeugt.');
