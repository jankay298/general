/**
 * Automatisierte Vorprüfung einer Website nach WCAG 2.1 AA (BFSG).
 *
 * Aufruf:
 *   node scan.mjs https://example.de              → prüft Startseite + bis zu 5 Unterseiten
 *   node scan.mjs https://example.de --seiten 10  → mehr Unterseiten
 *   node scan.mjs https://example.de --nur-start  → nur die Startseite
 *
 * Ergebnis: bericht/<domain>-<datum>.json  und  bericht/<domain>-<datum>.html
 *
 * WICHTIG: Automatische Prüfung findet erfahrungsgemäß nur einen Teil der
 * Barrieren. Der Rest braucht manuelle Prüfung. Siehe README.md.
 */
import { chromium } from "playwright";
import { readFileSync, mkdirSync, writeFileSync, existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { buildReport } from "./report.mjs";

const HERE = dirname(fileURLToPath(import.meta.url));
const AXE_PATH = join(HERE, "node_modules/axe-core/axe.min.js");
const LOCALE_PATH = join(HERE, "node_modules/axe-core/locales/de.json");

const WCAG_TAGS = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];

function parseArgs(argv) {
  const args = argv.slice(2);
  const url = args.find((a) => !a.startsWith("--"));
  if (!url) {
    console.error("Bitte eine URL angeben, z. B.:  node scan.mjs https://example.de");
    process.exit(1);
  }
  const idx = args.indexOf("--seiten");
  const maxPages = args.includes("--nur-start")
    ? 1
    : idx !== -1 && args[idx + 1]
      ? Math.max(1, parseInt(args[idx + 1], 10) || 6)
      : 6;
  return { url: url.startsWith("http") ? url : `https://${url}`, maxPages };
}

/** Interne Links der Startseite einsammeln, damit nicht nur die Startseite geprüft wird. */
async function collectLinks(page, origin, limit) {
  const hrefs = await page.$$eval("a[href]", (as) => as.map((a) => a.href));
  const seen = new Set();
  const out = [];
  for (const h of hrefs) {
    let u;
    try {
      u = new URL(h);
    } catch {
      continue;
    }
    if (u.origin !== origin) continue;
    if (/\.(pdf|jpe?g|png|gif|svg|zip|docx?|xlsx?|mp4|mp3)$/i.test(u.pathname)) continue;
    u.hash = "";
    const key = u.href;
    if (seen.has(key)) continue;
    seen.add(key);
    out.push(key);
    if (out.length >= limit) break;
  }
  return out;
}

async function auditPage(page, url, axeSource, locale) {
  await page.goto(url, { waitUntil: "domcontentloaded", timeout: 45000 });
  await page.waitForTimeout(1200); // Zeit für nachgeladene Inhalte

  await page.addScriptTag({ content: axeSource });
  const result = await page.evaluate(
    async ([tags, loc]) => {
      window.axe.configure({ locale: loc });
      const r = await window.axe.run(document, {
        runOnly: { type: "tag", values: [...tags, "best-practice"] },
        resultTypes: ["violations", "incomplete"],
      });
      const trim = (nodes) =>
        nodes.slice(0, 8).map((n) => ({
          ziel: Array.isArray(n.target) ? n.target.join(" ") : String(n.target),
          auszug: (n.html || "").slice(0, 220),
        }));
      const form = (v) => ({
        id: v.id,
        schwere: v.impact || "moderate",
        beschreibung: v.help,
        details: v.description,
        hilfe: v.helpUrl,
        wcag: v.tags.filter((t) => t.startsWith("wcag")),
        anzahl: v.nodes.length,
        stellen: trim(v.nodes),
      });
      // Verstoß gegen eine WCAG-Erfolgskriterium vs. reine Empfehlung sauber
      // trennen — im Bericht dürfen sie nicht vermischt werden, weil nur die
      // erste Gruppe rechtlich relevant ist.
      const istWcag = (v) => v.tags.some((t) => /^wcag\d/.test(t));
      return {
        titel: document.title || "",
        sprache: document.documentElement.getAttribute("lang") || "",
        verstoesse: r.violations.filter(istWcag).map(form),
        empfehlungen: r.violations.filter((v) => !istWcag(v)).map(form),
        unklar: r.incomplete.map((v) => ({
          id: v.id,
          beschreibung: v.help,
          anzahl: v.nodes.length,
        })),
      };
    },
    [WCAG_TAGS, locale],
  );
  return { url, ...result };
}

async function main() {
  const { url, maxPages } = parseArgs(process.argv);
  const origin = new URL(url).origin;
  const domain = new URL(url).hostname.replace(/^www\./, "");

  if (!existsSync(AXE_PATH)) {
    console.error("axe-core fehlt. Bitte zuerst  npm install  ausführen.");
    process.exit(1);
  }
  const axeSource = readFileSync(AXE_PATH, "utf8");
  const locale = JSON.parse(readFileSync(LOCALE_PATH, "utf8"));

  // Läuft der Rechner hinter einem Firmen- oder Entwicklungs-Proxy, wird er
  // aus der Umgebung übernommen. Auf einem normalen Rechner ist das leer.
  const proxyUrl = process.env.HTTPS_PROXY || process.env.https_proxy;
  const browser = await chromium.launch({
    executablePath: process.env.CHROMIUM_PATH || undefined,
    ...(proxyUrl ? { proxy: { server: proxyUrl } } : {}),
  });
  const context = await browser.newContext({
    viewport: { width: 1280, height: 900 },
    locale: "de-DE",
  });
  const page = await context.newPage();

  console.log(`Prüfe ${url} …`);
  const seiten = [];
  const start = await auditPage(page, url, axeSource, locale);
  seiten.push(start);
  console.log(`  ✓ Startseite — ${start.verstoesse.length} Regelverstöße`);

  if (maxPages > 1) {
    const links = await collectLinks(page, origin, maxPages - 1);
    for (const link of links) {
      try {
        const r = await auditPage(page, link, axeSource, locale);
        seiten.push(r);
        console.log(`  ✓ ${new URL(link).pathname} — ${r.verstoesse.length} Regelverstöße`);
      } catch (err) {
        console.log(`  ✗ ${link} — nicht erreichbar (${err.message.split("\n")[0]})`);
      }
    }
  }

  await browser.close();

  const daten = {
    domain,
    startUrl: url,
    geprueftAm: new Date().toISOString(),
    pruefstandard: "WCAG 2.1 Stufe AA",
    werkzeug: "axe-core 4.13",
    seiten,
  };

  const outDir = join(HERE, "bericht");
  mkdirSync(outDir, { recursive: true });
  const stamp = new Date().toISOString().slice(0, 10);
  const base = join(outDir, `${domain}-${stamp}`);

  writeFileSync(`${base}.json`, JSON.stringify(daten, null, 2), "utf8");
  writeFileSync(`${base}.html`, buildReport(daten), "utf8");

  const gesamt = seiten.reduce((n, s) => n + s.verstoesse.length, 0);
  console.log(`\nFertig. ${seiten.length} Seiten geprüft, ${gesamt} Regelverstöße gefunden.`);
  console.log(`Bericht:  ${base}.html`);
}

main().catch((err) => {
  console.error("Abbruch:", err.message);
  process.exit(1);
});
