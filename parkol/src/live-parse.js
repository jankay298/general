// Liest die HTML-Seite des Oldenburger Parkleitsystems (oldenburg-service.de/pls.php)
// in eine schlanke JSON-Struktur. Wird im GitHub-Actions-Job (Node) genutzt und
// ist ohne DOM lauffähig.

const decode = (s) =>
  s
    .replace(/<[^>]*>/g, ' ')
    .replace(/&nbsp;/g, ' ')
    .replace(/&auml;/g, 'ä').replace(/&ouml;/g, 'ö').replace(/&uuml;/g, 'ü')
    .replace(/&Auml;/g, 'Ä').replace(/&Ouml;/g, 'Ö').replace(/&Uuml;/g, 'Ü')
    .replace(/&szlig;/g, 'ß').replace(/&amp;/g, '&')
    .replace(/\s+/g, ' ')
    .trim();

/** "25.09.2026 10:48:00" (Berliner Zeit) → ISO-String mit korrektem Offset. */
export function berlinToIso(s) {
  const m = /(\d{2})\.(\d{2})\.(\d{4})\s+(\d{2}):(\d{2})(?::(\d{2}))?/.exec(s);
  if (!m) return null;
  const [, d, mo, y, h, mi, se = '00'] = m;
  // Offset für Europe/Berlin zu diesem Zeitpunkt bestimmen
  const guess = new Date(Date.UTC(+y, +mo - 1, +d, +h, +mi, +se));
  for (const offset of [1, 2]) {
    const utc = new Date(guess.getTime() - offset * 3600_000);
    const local = new Intl.DateTimeFormat('en-GB', { timeZone: 'Europe/Berlin', hour: '2-digit', hourCycle: 'h23' }).format(utc);
    if (+local === +h) return utc.toISOString();
  }
  return new Date(guess.getTime() - 3600_000).toISOString();
}

export function parsePls(html) {
  const standMatch = /Letzte Aktualisierung:\s*([\d.]+\s+[\d:]+)/.exec(decode(html));
  const eintraege = [];
  const rows = html.split(/<tr[^>]*>/i).slice(1);
  for (const row of rows) {
    const cells = [...row.matchAll(/<td[^>]*>([\s\S]*?)<\/td>/gi)];
    if (cells.length < 5) continue;
    const name = decode(cells[0][1]);
    const gesamt = parseInt(decode(cells[1][1]), 10);
    const frei = parseInt(decode(cells[2][1]), 10);
    if (!name || !Number.isFinite(gesamt) || !Number.isFinite(frei)) continue;
    const trendAlt = /alt="([^"]*)"/.exec(cells[3][1])?.[1] ?? null;
    const status = decode(cells[4][1]).replace(/!$/, '');
    eintraege.push({ name, gesamt, frei, trend: trendAlt, status });
  }
  return { stand: standMatch ? berlinToIso(standMatch[1]) : null, eintraege };
}
