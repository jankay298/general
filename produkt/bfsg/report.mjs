/**
 * Erzeugt aus den Prüfdaten den Bericht, der an den Kunden geht.
 * Reines HTML ohne externe Abhängigkeiten — im Browser öffnen und über
 * "Drucken → Als PDF sichern" ausliefern.
 */

const SCHWERE = {
  critical: { rang: 0, text: "Kritisch", farbe: "krit" },
  serious: { rang: 1, text: "Schwer", farbe: "schwer" },
  moderate: { rang: 2, text: "Mittel", farbe: "mittel" },
  minor: { rang: 3, text: "Gering", farbe: "gering" },
};

/**
 * Konkrete Handlungsempfehlungen auf Deutsch. axe-core liefert nur die
 * Regelbeschreibung — der Wert des Berichts liegt darin, dem Kunden zu sagen,
 * was er tatsächlich tun muss.
 */
const MASSNAHMEN = {
  "image-alt":
    "Jedem Bild ein alt-Attribut geben, das den Bildinhalt beschreibt. Rein dekorative Bilder bekommen alt=\"\" (leer, aber vorhanden), damit Screenreader sie überspringen.",
  "input-image-alt":
    "Grafische Absende-Buttons brauchen ein alt-Attribut mit der Beschriftung, z. B. alt=\"Suchen\".",
  "area-alt": "Verweissensitive Flächen einer Bildkarte brauchen je ein beschreibendes alt-Attribut.",
  "color-contrast":
    "Text- und Hintergrundfarbe so anpassen, dass das Kontrastverhältnis mindestens 4,5:1 beträgt (3:1 bei Schrift ab 18,7 pt bzw. 14 pt fett). Betrifft häufig helle Grautöne, Platzhaltertexte und Buttons in der Markenfarbe.",
  "link-name":
    "Jeder Link braucht einen erkennbaren Namen. Bei reinen Icon-Links ein aria-label ergänzen, z. B. aria-label=\"Zum Warenkorb\".",
  "button-name":
    "Jeder Button braucht eine Beschriftung — sichtbar als Text oder über aria-label, wenn nur ein Symbol zu sehen ist.",
  "label":
    "Jedes Formularfeld braucht ein verknüpftes <label for=\"…\">. Ein Platzhaltertext ersetzt kein Label, weil er beim Tippen verschwindet.",
  "form-field-multiple-labels": "Pro Formularfeld nur ein Label verwenden, sonst lesen Screenreader widersprüchlich vor.",
  "select-name": "Auswahlfelder brauchen ein zugeordnetes Label.",
  "html-has-lang":
    "Im <html>-Tag die Sprache setzen: <html lang=\"de\">. Ohne diese Angabe liest der Screenreader deutsche Texte mit englischer Aussprache vor.",
  "html-lang-valid": "Der Wert im lang-Attribut muss ein gültiger Sprachcode sein, für Deutsch also \"de\".",
  "valid-lang": "Anderssprachige Textpassagen mit eigenem lang-Attribut auszeichnen.",
  "document-title": "Jede Seite braucht einen aussagekräftigen <title>, der den Seiteninhalt benennt.",
  "heading-order":
    "Überschriftenebenen dürfen nicht springen. Nach h2 folgt h3, nicht h4. Überschriften sind Struktur, nicht Schriftgröße — für die Optik CSS verwenden.",
  "empty-heading": "Leere Überschriften entfernen oder mit Text füllen.",
  "landmark-one-main": "Den Hauptinhalt in ein <main>-Element setzen, damit Screenreader direkt dorthin springen können.",
  "region": "Alle Inhalte in Landmarken einordnen (header, nav, main, footer), damit die Seite navigierbar wird.",
  "bypass":
    "Einen \"Zum Inhalt springen\"-Link als erstes fokussierbares Element einbauen, damit Tastaturnutzer die Navigation überspringen können.",
  "skip-link": "Der Sprunglink muss auf ein tatsächlich vorhandenes Ziel auf der Seite verweisen.",
  "list": "<ul> und <ol> dürfen als direkte Kinder nur <li> enthalten.",
  "listitem": "<li> nur innerhalb von <ul> oder <ol> verwenden.",
  "definition-list": "<dl> darf direkt nur <dt>, <dd> und <div> enthalten.",
  "duplicate-id": "IDs müssen pro Seite eindeutig sein, sonst gehen Label-Zuordnungen ins Leere.",
  "duplicate-id-active": "IDs bedienbarer Elemente müssen eindeutig sein.",
  "duplicate-id-aria": "IDs, auf die per ARIA verwiesen wird, müssen eindeutig sein.",
  "aria-required-attr": "Das verwendete ARIA-Muster verlangt zusätzliche Attribute, die fehlen.",
  "aria-required-children": "Die ARIA-Rolle verlangt bestimmte Kindelemente, die fehlen.",
  "aria-required-parent": "Die ARIA-Rolle verlangt ein bestimmtes Elternelement, das fehlt.",
  "aria-valid-attr-value": "Ein ARIA-Attribut enthält einen ungültigen Wert oder verweist auf eine nicht vorhandene ID.",
  "aria-hidden-focus": "Elemente mit aria-hidden=\"true\" dürfen nicht per Tastatur fokussierbar sein.",
  "aria-allowed-attr": "Das ARIA-Attribut ist für diese Rolle nicht zulässig.",
  "aria-roles": "Die verwendete ARIA-Rolle existiert nicht oder ist falsch geschrieben.",
  "frame-title": "Jedes <iframe> braucht ein title-Attribut, das seinen Inhalt benennt — betrifft oft eingebettete Karten und Videos.",
  "meta-viewport":
    "user-scalable=no und maximum-scale entfernen. Nutzer müssen die Seite auf bis zu 200 % vergrößern können.",
  "target-size": "Bedienelemente brauchen eine Mindestgröße von 24 × 24 CSS-Pixeln oder ausreichenden Abstand zueinander.",
  "scrollable-region-focusable": "Scrollbare Bereiche müssen per Tastatur erreichbar sein.",
  "nested-interactive": "Bedienbare Elemente nicht ineinander verschachteln, z. B. kein Button innerhalb eines Links.",
  "td-headers-attr": "Tabellenzellen dürfen nur auf Kopfzellen derselben Tabelle verweisen.",
  "th-has-data-cells": "Kopfzellen brauchen zugehörige Datenzellen, sonst ist die Tabelle nicht auswertbar.",
  "table-duplicate-name": "summary-Attribut und <caption> dürfen nicht denselben Text enthalten.",
  "video-caption": "Videos mit Ton brauchen Untertitel.",
  "object-alt": "<object>-Elemente brauchen einen Alternativtext.",
  "svg-img-alt": "SVG-Grafiken mit Bildbedeutung brauchen einen zugänglichen Namen, z. B. über ein <title>-Element.",
  "page-has-heading-one":
    "Jede Seite braucht genau eine <h1>, die den Seiteninhalt benennt. Sie ist für Screenreader-Nutzer der Einstiegspunkt in die Seite.",
  "landmark-unique": "Mehrfach vorkommende Landmarken gleicher Rolle brauchen unterscheidbare Namen.",
  "landmark-complementary-is-top-level": "<aside> gehört auf die oberste Ebene, nicht in andere Landmarken verschachtelt.",
  "landmark-no-duplicate-banner": "Pro Seite nur einen <header> als banner-Landmarke verwenden.",
  "landmark-no-duplicate-contentinfo": "Pro Seite nur einen <footer> als contentinfo-Landmarke verwenden.",
  "landmark-banner-is-top-level": "Der Seitenkopf gehört auf die oberste Ebene der Seitenstruktur.",
  "landmark-contentinfo-is-top-level": "Der Seitenfuß gehört auf die oberste Ebene der Seitenstruktur.",
  "image-redundant-alt": "Der Alternativtext soll nicht wiederholen, was ohnehin schon als Text danebensteht.",
  "link-in-text-block": "Links im Fließtext müssen sich nicht nur durch die Farbe vom Text unterscheiden — Unterstreichung ergänzen.",
  "meta-viewport-large": "Nutzer müssen die Seite auf mindestens 500 % vergrößern können.",
  "tabindex": "Kein tabindex größer als 0 verwenden — das zerstört die natürliche Fokusreihenfolge.",
  "focus-order-semantics": "Fokussierbare Elemente brauchen eine passende Rolle, damit klar ist, was sie tun.",
  "autocomplete-valid": "Das autocomplete-Attribut muss einen gültigen Wert haben, damit Formulare automatisch ausgefüllt werden können.",
};

const esc = (s) =>
  String(s ?? "").replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c],
  );

/** Gleiche Regelverstöße über alle geprüften Seiten zusammenfassen. */
function aggregate(seiten, feld = "verstoesse") {
  const map = new Map();
  for (const s of seiten) {
    for (const v of s[feld] || []) {
      const e = map.get(v.id) || { ...v, anzahl: 0, seiten: [], stellen: [] };
      e.anzahl += v.anzahl;
      e.seiten.push({ url: s.url, anzahl: v.anzahl });
      for (const st of v.stellen) if (e.stellen.length < 6) e.stellen.push(st);
      map.set(v.id, e);
    }
  }
  return [...map.values()].sort(
    (a, b) =>
      (SCHWERE[a.schwere]?.rang ?? 9) - (SCHWERE[b.schwere]?.rang ?? 9) || b.anzahl - a.anzahl,
  );
}

function ampel(kritisch, schwer) {
  if (kritisch > 0) return { stufe: "rot", text: "Erheblicher Handlungsbedarf" };
  if (schwer > 0) return { stufe: "gelb", text: "Handlungsbedarf" };
  return { stufe: "gruen", text: "Keine automatisch erkennbaren schweren Mängel" };
}

export function buildReport(daten) {
  const befunde = aggregate(daten.seiten);
  const empfehlungen = aggregate(daten.seiten, "empfehlungen");
  const zaehler = { critical: 0, serious: 0, moderate: 0, minor: 0 };
  for (const b of befunde) zaehler[b.schwere] = (zaehler[b.schwere] || 0) + 1;
  const stellenGesamt = befunde.reduce((n, b) => n + b.anzahl, 0);
  const status = ampel(zaehler.critical, zaehler.serious);
  const datum = new Date(daten.geprueftAm).toLocaleDateString("de-DE", {
    day: "2-digit", month: "long", year: "numeric",
  });

  const renderBefunde = (liste) =>
    liste
    .map((b, i) => {
      const sw = SCHWERE[b.schwere] || SCHWERE.moderate;
      const wcag = b.wcag
        .filter((t) => /^wcag\d/.test(t))
        .map((t) => t.replace(/^wcag/, "").replace(/^(\d)(\d)(\d)$/, "$1.$2.$3"))
        .join(", ");
      return `
      <article class="befund">
        <header>
          <span class="nr">${String(i + 1).padStart(2, "0")}</span>
          <div>
            <h3>${esc(b.beschreibung)}</h3>
            <p class="meta">
              <span class="pill ${sw.farbe}">${sw.text}</span>
              <span>${b.anzahl} betroffene ${b.anzahl === 1 ? "Stelle" : "Stellen"}</span>
              ${wcag ? `<span>WCAG ${esc(wcag)}</span>` : ""}
              <span class="regel">${esc(b.id)}</span>
            </p>
          </div>
        </header>
        <p class="was">${esc(b.details)}</p>
        <div class="tun">
          <b>Was zu tun ist</b>
          <p>${esc(MASSNAHMEN[b.id] || "Siehe technische Referenz zu dieser Regel.")}</p>
        </div>
        <details>
          <summary>Betroffene Stellen (${b.stellen.length} von ${b.anzahl} gezeigt)</summary>
          <ul class="stellen">
            ${b.stellen.map((s) => `<li><code>${esc(s.ziel)}</code><pre>${esc(s.auszug)}</pre></li>`).join("")}
          </ul>
          <p class="seitenliste">Gefunden auf: ${b.seiten.map((s) => `${esc(new URL(s.url).pathname)} (${s.anzahl})`).join(" · ")}</p>
        </details>
      </article>`;
    })
    .join("");

  const befundHtml = renderBefunde(befunde);
  const empfehlungHtml = renderBefunde(empfehlungen);

  return `<!doctype html>
<html lang="de">
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Barrierefreiheits-Prüfbericht ${esc(daten.domain)}</title>
<style>
:root{
  --paper:#FFFFFF; --ground:#F1F3F2; --ink:#15242A; --soft:#3A4C52; --muted:#62757A;
  --rule:#C9D3D1; --rule-soft:#E1E7E5; --accent:#0D6B61; --accent-wash:#DCEBE8;
  --krit:#A32319; --krit-w:#F6E0DD; --schwer:#A85715; --schwer-w:#F8E9D8;
  --mittel:#7A6412; --mittel-w:#F5EED4; --gering:#4A5D63; --gering-w:#E6EBEA;
  --gruen:#226E45;
}
*{box-sizing:border-box}
body{margin:0;background:var(--ground);color:var(--ink);
  font:15px/1.6 ui-sans-serif,system-ui,-apple-system,"Segoe UI",Roboto,Arial,sans-serif}
.blatt{max-width:900px;margin:0 auto;background:var(--paper);padding:clamp(1.5rem,5vw,3.5rem)}
h1,h2,h3{margin:0;line-height:1.2;text-wrap:balance}
h1{font-size:clamp(1.6rem,4vw,2.3rem);letter-spacing:-.02em}
h2{font-size:1.3rem;margin:2.5rem 0 1rem;padding-bottom:.4rem;border-bottom:2px solid var(--ink)}
h3{font-size:1.05rem}
.kopf{border-bottom:3px solid var(--ink);padding-bottom:1.25rem}
.eyebrow{font-size:.75rem;letter-spacing:.14em;text-transform:uppercase;color:var(--muted);font-weight:600;margin:0 0 .5rem}
.kopfdaten{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:.75rem 1.5rem;margin-top:1.25rem;font-size:.85rem}
.kopfdaten div{display:flex;flex-direction:column}
.kopfdaten dt,.kopfdaten .l{color:var(--muted);font-size:.75rem;text-transform:uppercase;letter-spacing:.08em}
.kopfdaten .v{font-weight:600}
.status{display:flex;align-items:center;gap:1rem;margin:1.75rem 0;padding:1rem 1.25rem;border-left:5px solid;border-radius:2px}
.status.rot{border-color:var(--krit);background:var(--krit-w)}
.status.gelb{border-color:var(--schwer);background:var(--schwer-w)}
.status.gruen{border-color:var(--gruen);background:#DCEDE2}
.status b{display:block;font-size:1.15rem}
.status p{margin:.2rem 0 0;font-size:.9rem;color:var(--soft)}
.kacheln{display:grid;grid-template-columns:repeat(auto-fit,minmax(120px,1fr));gap:.75rem;margin:1.5rem 0}
.kachel{border:1px solid var(--rule);border-radius:3px;padding:.85rem}
.kachel .z{font-size:1.9rem;font-weight:700;line-height:1;font-variant-numeric:tabular-nums}
.kachel .b{font-size:.75rem;color:var(--muted);text-transform:uppercase;letter-spacing:.07em;margin-top:.3rem}
.kachel.krit .z{color:var(--krit)} .kachel.schwer .z{color:var(--schwer)}
.kachel.mittel .z{color:var(--mittel)} .kachel.gering .z{color:var(--gering)}
.befund{border:1px solid var(--rule);border-radius:3px;padding:1.25rem;margin-bottom:1rem;break-inside:avoid}
.befund header{display:flex;gap:1rem;align-items:flex-start}
.nr{font-variant-numeric:tabular-nums;font-weight:700;color:var(--accent);font-size:1.1rem;padding-top:.1rem}
.meta{display:flex;flex-wrap:wrap;gap:.5rem .9rem;align-items:center;margin:.4rem 0 0;font-size:.8rem;color:var(--muted)}
.pill{padding:.1rem .5rem;border-radius:2px;font-weight:700;font-size:.72rem;text-transform:uppercase;letter-spacing:.05em}
.pill.krit{background:var(--krit-w);color:var(--krit)}
.pill.schwer{background:var(--schwer-w);color:var(--schwer)}
.pill.mittel{background:var(--mittel-w);color:var(--mittel)}
.pill.gering{background:var(--gering-w);color:var(--gering)}
.regel{font-family:ui-monospace,Menlo,Consolas,monospace;font-size:.75rem}
.was{margin:.9rem 0 0;color:var(--soft);font-size:.92rem}
.tun{margin-top:.9rem;padding:.85rem 1rem;background:var(--accent-wash);border-radius:2px}
.tun b{font-size:.75rem;text-transform:uppercase;letter-spacing:.08em;color:var(--accent)}
.tun p{margin:.35rem 0 0;font-size:.92rem}
details{margin-top:.85rem;font-size:.85rem}
summary{cursor:pointer;color:var(--muted)}
.stellen{list-style:none;padding:0;margin:.7rem 0 0;display:grid;gap:.6rem}
.stellen code{font-size:.78rem;color:var(--accent);word-break:break-all}
.stellen pre{margin:.25rem 0 0;padding:.5rem .6rem;background:var(--ground);border-radius:2px;
  font-size:.75rem;white-space:pre-wrap;word-break:break-all;color:var(--soft)}
.seitenliste{color:var(--muted);font-size:.78rem;margin:.7rem 0 0}
.hinweis{border:1px solid var(--rule);border-left:4px solid var(--accent);
  border-radius:2px;padding:1rem 1.25rem;margin:1.25rem 0;font-size:.9rem;color:var(--soft)}
.hinweis b{color:var(--ink)}
table{border-collapse:collapse;width:100%;font-size:.88rem;margin-top:.5rem}
th,td{text-align:left;padding:.5rem .6rem;border-bottom:1px solid var(--rule-soft)}
th{font-size:.75rem;text-transform:uppercase;letter-spacing:.07em;color:var(--muted)}
td:last-child,th:last-child{text-align:right;font-variant-numeric:tabular-nums}
footer{margin-top:3rem;padding-top:1.25rem;border-top:1px solid var(--rule);
  font-size:.78rem;color:var(--muted)}
@media print{body{background:#fff}.blatt{max-width:none;padding:0}details{display:none}}
</style>
<div class="blatt">
  <header class="kopf">
    <p class="eyebrow">Prüfbericht Barrierefreiheit · BFSG</p>
    <h1>${esc(daten.domain)}</h1>
    <div class="kopfdaten">
      <div><span class="l">Geprüft am</span><span class="v">${esc(datum)}</span></div>
      <div><span class="l">Prüfstandard</span><span class="v">${esc(daten.pruefstandard)}</span></div>
      <div><span class="l">Geprüfte Seiten</span><span class="v">${daten.seiten.length}</span></div>
      <div><span class="l">Verfahren</span><span class="v">Automatisierte Vorprüfung</span></div>
    </div>
  </header>

  <div class="status ${status.stufe}">
    <div>
      <b>${esc(status.text)}</b>
      <p>${zaehler.critical} kritische und ${zaehler.serious} schwere Regelverstöße an insgesamt ${stellenGesamt} Stellen.</p>
    </div>
  </div>

  <div class="kacheln">
    <div class="kachel krit"><div class="z">${zaehler.critical}</div><div class="b">Kritisch</div></div>
    <div class="kachel schwer"><div class="z">${zaehler.serious}</div><div class="b">Schwer</div></div>
    <div class="kachel mittel"><div class="z">${zaehler.moderate}</div><div class="b">Mittel</div></div>
    <div class="kachel gering"><div class="z">${zaehler.minor}</div><div class="b">Gering</div></div>
  </div>

  <div class="hinweis">
    <b>Was dieser Bericht ist.</b> Eine automatisierte technische Vorprüfung nach WCAG 2.1
    Stufe AA mit axe-core. Automatische Prüfungen erfassen erfahrungsgemäß nur einen Teil der
    tatsächlichen Barrieren — typischerweise rund ein Drittel. Punkte wie Tastaturbedienung,
    Sinnhaftigkeit von Alternativtexten, Fokusreihenfolge und Verständlichkeit lassen sich nur
    manuell beurteilen und sind hier <b>nicht</b> abgedeckt. Dieser Bericht ist keine
    Rechtsberatung und kein Konformitätsnachweis.
  </div>

  <h2>Geprüfte Seiten</h2>
  <table>
    <thead><tr><th>Seite</th><th>Titel</th><th>Verstöße</th></tr></thead>
    <tbody>
      ${daten.seiten
        .map(
          (s) =>
            `<tr><td>${esc(new URL(s.url).pathname || "/")}</td><td>${esc((s.titel || "—").slice(0, 60))}</td><td>${s.verstoesse.length}</td></tr>`,
        )
        .join("")}
    </tbody>
  </table>

  <h2>Befunde nach Schweregrad</h2>
  ${befunde.length ? befundHtml : "<p>Die automatisierte Prüfung hat keine Regelverstöße gefunden. Eine manuelle Prüfung ist trotzdem erforderlich.</p>"}

  ${
    empfehlungen.length
      ? `<h2>Weitere Empfehlungen</h2>
  <div class="hinweis">
    Diese Punkte verstoßen gegen <b>kein</b> WCAG-Erfolgskriterium und sind rechtlich nicht
    zwingend. Sie verbessern die Bedienbarkeit aber deutlich — besonders die Überschriften-
    struktur und die Landmarken, an denen sich Screenreader-Nutzer durch die Seite hangeln.
  </div>
  ${empfehlungHtml}`
      : ""
  }

  <h2>Empfohlenes Vorgehen</h2>
  <ol>
    <li><b>Kritische und schwere Befunde zuerst.</b> Sie blockieren die Nutzung ganzer Seitenbereiche und wiegen bei einer Abmahnung am schwersten.</li>
    <li><b>Wiederkehrende Muster im Vorlagensystem beheben.</b> Ein Fehler im Seitenkopf betrifft jede Unterseite — eine Korrektur an der richtigen Stelle beseitigt hunderte Fundstellen.</li>
    <li><b>Manuelle Prüfung anschließen.</b> Tastaturbedienung, Fokusreihenfolge, Formularfehlermeldungen und Alternativtexte auf Sinnhaftigkeit.</li>
    <li><b>Barrierefreiheitserklärung veröffentlichen</b> und einen Rückmeldeweg für Nutzer einrichten.</li>
    <li><b>Nachprüfung</b> nach Umsetzung, um die Korrekturen zu belegen.</li>
  </ol>

  <footer>
    Erstellt mit ${esc(daten.werkzeug)} · Prüfstandard ${esc(daten.pruefstandard)} ·
    Startadresse ${esc(daten.startUrl)}<br>
    Dieser Bericht dokumentiert den technischen Befund zum Prüfzeitpunkt. Er stellt keine
    Rechtsberatung dar und garantiert keine Rechtssicherheit. Für eine rechtliche Bewertung
    wenden Sie sich an eine Rechtsanwältin oder einen Rechtsanwalt.
  </footer>
</div>
</html>`;
}
