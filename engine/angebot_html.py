"""Druckfertiges Angebot als HTML (A4, per Browser als PDF speicherbar)."""

from __future__ import annotations

import html
from datetime import date, timedelta
from decimal import Decimal
from pathlib import Path

from .vorgang import Vorgang


def eur(betrag: Decimal) -> str:
    s = f"{betrag:,.2f}".replace(",", " ").replace(".", ",").replace(" ", ".")
    return f"{s}&nbsp;€"


def zahl(wert: Decimal) -> str:
    s = f"{wert:.2f}".rstrip("0").rstrip(".")
    return (s or "0").replace(".", ",")


def _esc(v) -> str:
    return html.escape(str(v or ""))


CSS = """
@page { size: A4; margin: 18mm 16mm; }
* { box-sizing: border-box; }
body { font: 10.5pt/1.5 Arial, Helvetica, sans-serif; color: #1a1a1a;
       max-width: 178mm; margin: 0 auto; padding: 12px; background: #fff; }
header { display: flex; justify-content: space-between; align-items: flex-start;
         gap: 24px; border-bottom: 2px solid #1F3864; padding-bottom: 10px; }
.firma { font-size: 14pt; font-weight: bold; color: #1F3864; }
.klein { font-size: 9pt; color: #555; }
h1 { font-size: 15pt; color: #1F3864; margin: 0; text-align: right; }
.meta { margin-top: 6px; font-size: 9.5pt; text-align: right; }
.meta div { display: flex; justify-content: flex-end; gap: 10px; }
.empfaenger { margin: 22px 0 6px; }
.empfaenger .name { font-weight: bold; }
.objekt { font-weight: bold; margin: 14px 0 4px; }
table { width: 100%; border-collapse: collapse; margin-top: 12px; }
thead th { background: #1F3864; color: #fff; font-size: 9.5pt; padding: 6px 7px;
           text-align: left; }
td, th { border: 1px solid #c9c9c9; padding: 5px 7px; vertical-align: top; }
tbody tr:nth-child(even) td { background: #f4f7fb; }
.num { text-align: right; white-space: nowrap; }
.mid { text-align: center; white-space: nowrap; }
tfoot td { border: none; padding: 3px 7px; }
tfoot .label { text-align: right; }
tfoot .summe td { font-weight: bold; border-top: 1px solid #1F3864; }
tfoot .brutto td { font-weight: bold; font-size: 11.5pt; background: #e8edf5;
                   border-top: 2px solid #1F3864; }
footer { margin-top: 26px; padding-top: 8px; border-top: 1px solid #c9c9c9;
         font-size: 8.5pt; color: #555; }
footer p { margin: 2px 0; }
.gruss { margin-top: 22px; font-size: 10pt; }
@media print { body { padding: 0; } }
"""


def angebot_html(v: Vorgang, angebotsdatum: date | None = None) -> str:
    a = v.angebot()
    st = v.stammdaten
    f = st.firma
    heute = angebotsdatum or date.today()
    gueltig = heute + timedelta(days=st.gueltig_tage)
    nummer = v.angebotsnummer or heute.strftime("%Y-%m-%d")

    zeilen = "\n".join(
        f"""      <tr>
        <td class="mid">{p.pos}</td>
        <td>{_esc(p.leistung)}</td>
        <td class="num">{zahl(p.menge)}</td>
        <td class="mid">{_esc(p.einheit)}</td>
        <td class="num">{eur(p.einzelpreis)}</td>
        <td class="num">{eur(p.gesamtpreis)}</td>
      </tr>"""
        for p in a.positionen
    )

    rabattzeile = ""
    if a.rabatt != 0:
        rabattzeile = (f'      <tr><td colspan="5" class="label">Rabatt</td>'
                       f'<td class="num">{eur(a.rabatt)}</td></tr>\n')
    fahrtzeile = ""
    if a.fahrtkosten != 0:
        fahrtzeile = (f'      <tr><td colspan="5" class="label">An- und Abfahrt</td>'
                      f'<td class="num">{eur(a.fahrtkosten)}</td></tr>\n')

    ust_prozent = zahl(st.umsatzsteuer * 100)
    ustzeile = (f'      <tr><td colspan="5" class="label">zzgl. {ust_prozent}&nbsp;% '
                f'Umsatzsteuer</td><td class="num">{eur(a.umsatzsteuer)}</td></tr>\n'
                if st.umsatzsteuer else
                '      <tr><td colspan="5" class="label">Kein Ausweis der Umsatzsteuer '
                'nach § 19 UStG</td><td class="num">&mdash;</td></tr>\n')

    return f"""<!doctype html>
<html lang="de">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Angebot {_esc(nummer)}</title>
<style>{CSS}</style>
</head>
<body>
<header>
  <div>
    <div class="firma">{_esc(f['name'])}</div>
    <div class="klein">{_esc(f['strasse'])}<br>{_esc(f['plz_ort'])}<br>
      Tel. {_esc(f['telefon'])}<br>{_esc(f['email'])}</div>
  </div>
  <div>
    <h1>ANGEBOT</h1>
    <div class="meta">
      <div><span>Angebotsnr.</span><strong>{_esc(nummer)}</strong></div>
      <div><span>Datum</span><strong>{heute.strftime('%d.%m.%Y')}</strong></div>
      <div><span>Gültig bis</span><strong>{gueltig.strftime('%d.%m.%Y')}</strong></div>
    </div>
  </div>
</header>

<div class="empfaenger">
  <div class="name">{_esc(v.kunde.get('name'))}</div>
  <div>{_esc(v.kunde.get('strasse'))}</div>
  <div>{_esc(v.kunde.get('plz_ort'))}</div>
</div>

<div class="objekt">Bauvorhaben: {_esc(v.kunde.get('objekt'))}</div>
<p>Sehr geehrte Damen und Herren, vielen Dank für Ihre Anfrage.
   Für die genannten Leistungen bieten wir Ihnen an:</p>

<table>
  <thead>
    <tr><th style="width:6%">Pos</th><th>Leistung</th><th style="width:10%">Menge</th>
        <th style="width:9%">Einheit</th><th style="width:13%">EP</th>
        <th style="width:15%">Gesamt</th></tr>
  </thead>
  <tbody>
{zeilen}
  </tbody>
  <tfoot>
      <tr><td colspan="5" class="label">Zwischensumme Positionen</td>
          <td class="num">{eur(a.zwischensumme_positionen)}</td></tr>
{fahrtzeile}{rabattzeile}      <tr class="summe"><td colspan="5" class="label">Nettobetrag</td>
          <td class="num">{eur(a.nettobetrag)}</td></tr>
{ustzeile}      <tr class="brutto"><td colspan="5" class="label">Gesamtbetrag</td>
          <td class="num">{eur(a.bruttobetrag)}</td></tr>
  </tfoot>
</table>

<div class="gruss">Mit freundlichen Grüßen<br>{_esc(f['name'])}</div>

<footer>
  <p>Zahlungsbedingungen: {st.zahlungsziel_tage} Tage netto, bei Zahlung innerhalb
     von 8 Tagen {zahl(st.skonto * 100)}&nbsp;% Skonto.</p>
  <p>Dieses Angebot ist {st.gueltig_tage} Tage ab Angebotsdatum gültig.
     Ausführungstermin nach Absprache.</p>
  <p>USt-IdNr.: {_esc(f['ust_idnr'])} &nbsp;|&nbsp; IBAN: {_esc(f['iban'])}</p>
</footer>
</body>
</html>
"""


def angebot_schreiben(v: Vorgang, pfad: str | Path) -> Path:
    p = Path(pfad)
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(angebot_html(v), encoding="utf-8")
    return p
