"""Angebotskalkulation — Referenzimplementierung von docs/rechenmodell.md.

Dieselben Eingaben müssen hier und in der Excel-Vorlage auf den Cent dasselbe
ergeben. Der Referenzfall ist in tests/test_referenzfall.py festgenagelt.

Gerechnet wird durchgängig mit Decimal und kaufmännischer Rundung
(ROUND_HALF_UP), weil Excel so rundet. Pythons eingebautes round() rundet zur
geraden Zahl (25,625 -> 25,62 statt 25,63) und würde Cent-Abweichungen erzeugen.
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field, replace
from decimal import ROUND_HALF_UP, Decimal
from pathlib import Path

DATA_DIR = Path(__file__).resolve().parent.parent / "data"


def d(value) -> Decimal:
    """Nach Decimal wandeln, ohne Float-Ungenauigkeit einzuschleppen."""
    if isinstance(value, Decimal):
        return value
    return Decimal(str(value))


def rnd(value, stellen: int = 2) -> Decimal:
    """Kaufmännisch runden — wie ROUND() in Excel."""
    quant = Decimal(1).scaleb(-stellen)
    return d(value).quantize(quant, rounding=ROUND_HALF_UP)


# --- Stammdaten -----------------------------------------------------------
@dataclass(frozen=True)
class Stammdaten:
    """Kalkulationsgrößen des Betriebs. Vgl. Blatt 'Stammdaten' der Vorlage."""

    mittellohn: Decimal = d("42.00")          # €/produktive Stunde
    gk_lohn: Decimal = d("0.25")              # Gemeinkostenzuschlag Lohn
    gk_material: Decimal = d("0.10")          # Gemeinkostenzuschlag Material
    wagnis_gewinn: Decimal = d("0.12")
    rabatt: Decimal = d("0.00")
    skonto: Decimal = d("0.02")               # steckt im Einheitspreis
    fahrtkosten_je_km: Decimal = d("0.70")
    umsatzsteuer: Decimal = d("0.19")
    zahlungsziel_tage: int = 14
    gueltig_tage: int = 30

    firma: dict = field(default_factory=lambda: {
        "name": "Muster Malerbetrieb GmbH",
        "strasse": "Handwerkerweg 12",
        "plz_ort": "12345 Musterstadt",
        "telefon": "0123 456789",
        "email": "info@muster-malerbetrieb.de",
        "ust_idnr": "DE123456789",
        "iban": "DE00 0000 0000 0000 0000 00",
    })

    @classmethod
    def aus_datei(cls, pfad: str | Path) -> "Stammdaten":
        """Lädt Stammdaten aus JSON.

        Unbekannte Schlüssel werden übergangen, damit Kommentarfelder wie
        "_hinweis" in der Datei stehen dürfen; nicht genannte Felder behalten
        ihren Vorgabewert.
        """
        roh = json.loads(Path(pfad).read_text(encoding="utf-8"))
        ganzzahlig = {"zahlungsziel_tage", "gueltig_tage"}
        bekannt = set(cls.__dataclass_fields__)

        werte = {}
        for schluessel, wert in roh.items():
            if schluessel not in bekannt:
                continue
            if schluessel == "firma":
                werte[schluessel] = wert
            elif schluessel in ganzzahlig:
                werte[schluessel] = int(wert)
            else:
                werte[schluessel] = d(wert)
        return cls(**werte)

    def mit(self, **aenderungen) -> "Stammdaten":
        return replace(self, **{k: d(v) if isinstance(v, (int, float, str)) and
                                k not in ("firma", "zahlungsziel_tage", "gueltig_tage")
                                else v for k, v in aenderungen.items()})


# --- Leistungskatalog -----------------------------------------------------
@dataclass(frozen=True)
class Leistung:
    nr: str
    gewerk: str
    leistung: str
    einheit: str
    zeit_je_einheit_std: Decimal
    material_je_einheit_eur: Decimal
    hinweis: str = ""


class Katalog:
    """Leistungskatalog — dieselbe JSON-Datei speist auch die Excel-Vorlage."""

    def __init__(self, leistungen: list[Leistung]):
        self._nach_nr = {l.nr: l for l in leistungen}

    @classmethod
    def laden(cls, pfad: str | Path | None = None) -> "Katalog":
        pfad = Path(pfad) if pfad else DATA_DIR / "leistungskatalog.json"
        roh = json.loads(pfad.read_text(encoding="utf-8"))
        return cls([
            Leistung(
                nr=e["nr"], gewerk=e["gewerk"], leistung=e["leistung"],
                einheit=e["einheit"],
                zeit_je_einheit_std=d(e["zeit_je_einheit_std"]),
                material_je_einheit_eur=d(e["material_je_einheit_eur"]),
                hinweis=e.get("hinweis", ""),
            )
            for e in roh["leistungen"]
        ])

    def __getitem__(self, nr: str) -> Leistung:
        try:
            return self._nach_nr[nr]
        except KeyError:
            raise KeyError(f"Katalog-Nr. {nr!r} gibt es nicht") from None

    def __contains__(self, nr: str) -> bool:
        return nr in self._nach_nr

    def alle(self) -> list[Leistung]:
        return list(self._nach_nr.values())

    def suchen(self, begriff: str, gewerk: str | None = None) -> list[Leistung]:
        b = begriff.lower().strip()
        treffer = []
        for l in self._nach_nr.values():
            if gewerk and l.gewerk.lower() != gewerk.lower():
                continue
            if not b or b in l.leistung.lower() or b in l.nr.lower() or b in l.gewerk.lower():
                treffer.append(l)
        return treffer


# --- Positionsrechnung ----------------------------------------------------
@dataclass(frozen=True)
class Position:
    """Eine gerechnete Angebotsposition."""

    pos: int
    katalog_nr: str
    leistung: str
    einheit: str
    menge: Decimal
    zeit_je_einheit_std: Decimal
    stunden: Decimal
    lohnkosten: Decimal
    material_je_einheit_eur: Decimal
    materialkosten: Decimal
    selbstkosten: Decimal
    einzelpreis: Decimal
    gesamtpreis: Decimal


def position_rechnen(pos: int, leistung: Leistung, menge, st: Stammdaten) -> Position:
    """h = q x t | KL = h x L | KM = q x m | SK = KL(1+gkL) + KM(1+gkM)
    EP = SK (1+wg)(1+sk) / q | GP = EP x q

    GP entsteht bewusst aus dem *gerundeten* EP, damit im gedruckten Angebot
    Einzelpreis x Menge exakt die Zeile ergibt.
    """
    q = d(menge)
    stunden = rnd(q * leistung.zeit_je_einheit_std)
    lohn = rnd(stunden * st.mittellohn)
    material = rnd(q * leistung.material_je_einheit_eur)
    selbstkosten = rnd(lohn * (1 + st.gk_lohn) + material * (1 + st.gk_material))
    if q == 0:
        einzelpreis = d(0)
    else:
        einzelpreis = rnd(selbstkosten * (1 + st.wagnis_gewinn) * (1 + st.skonto) / q)
    return Position(
        pos=pos,
        katalog_nr=leistung.nr,
        leistung=leistung.leistung,
        einheit=leistung.einheit,
        menge=q,
        zeit_je_einheit_std=leistung.zeit_je_einheit_std,
        stunden=stunden,
        lohnkosten=lohn,
        material_je_einheit_eur=leistung.material_je_einheit_eur,
        materialkosten=material,
        selbstkosten=selbstkosten,
        einzelpreis=einzelpreis,
        gesamtpreis=rnd(einzelpreis * q),
    )


# --- Angebotssumme --------------------------------------------------------
@dataclass(frozen=True)
class Angebot:
    positionen: list[Position]
    zwischensumme_positionen: Decimal
    fahrtkosten: Decimal
    zwischensumme: Decimal
    rabatt: Decimal
    nettobetrag: Decimal
    umsatzsteuer: Decimal
    bruttobetrag: Decimal
    # Kennzahlen — nur intern, nie im Kundenangebot
    gesamtstunden: Decimal
    materialkosten: Decimal
    selbstkosten: Decimal
    deckungsbeitrag: Decimal
    marge: Decimal
    erloes_je_stunde: Decimal

    def als_dict(self) -> dict:
        def z(v):
            return float(v)
        return {
            "positionen": [
                {
                    "pos": p.pos, "katalog_nr": p.katalog_nr, "leistung": p.leistung,
                    "menge": z(p.menge), "einheit": p.einheit,
                    "stunden": z(p.stunden),
                    "einzelpreis": z(p.einzelpreis), "gesamtpreis": z(p.gesamtpreis),
                }
                for p in self.positionen
            ],
            "summen": {
                "zwischensumme_positionen": z(self.zwischensumme_positionen),
                "fahrtkosten": z(self.fahrtkosten),
                "rabatt": z(self.rabatt),
                "nettobetrag": z(self.nettobetrag),
                "umsatzsteuer": z(self.umsatzsteuer),
                "bruttobetrag": z(self.bruttobetrag),
            },
            "kennzahlen_intern": {
                "gesamtstunden": z(self.gesamtstunden),
                "materialkosten": z(self.materialkosten),
                "selbstkosten": z(self.selbstkosten),
                "deckungsbeitrag": z(self.deckungsbeitrag),
                "marge": z(self.marge),
                "erloes_je_stunde": z(self.erloes_je_stunde),
            },
        }


def angebot_rechnen(positionen: list[Position], st: Stammdaten,
                    fahrstrecke_km=0) -> Angebot:
    km = d(fahrstrecke_km)
    zwischensumme_pos = rnd(sum((p.gesamtpreis for p in positionen), d(0)))
    fahrt = rnd(km * st.fahrtkosten_je_km * (1 + st.skonto))
    zwischensumme = rnd(zwischensumme_pos + fahrt)
    rabatt = -rnd(zwischensumme * st.rabatt)
    netto = rnd(zwischensumme + rabatt)
    ust = rnd(netto * st.umsatzsteuer)

    stunden = rnd(sum((p.stunden for p in positionen), d(0)))
    material = rnd(sum((p.materialkosten for p in positionen), d(0)))
    # Fahrt hier zum reinen Kostensatz, nicht zum Verkaufswert.
    selbstkosten = rnd(sum((p.selbstkosten for p in positionen), d(0))
                       + km * st.fahrtkosten_je_km)
    db = rnd(netto - selbstkosten)

    return Angebot(
        positionen=positionen,
        zwischensumme_positionen=zwischensumme_pos,
        fahrtkosten=fahrt,
        zwischensumme=zwischensumme,
        rabatt=rabatt,
        nettobetrag=netto,
        umsatzsteuer=ust,
        bruttobetrag=rnd(netto + ust),
        gesamtstunden=stunden,
        materialkosten=material,
        selbstkosten=selbstkosten,
        deckungsbeitrag=db,
        # Sechs Stellen, damit die Anzeige selbst rundet: 0,123490 -> 12,3 %.
        # Auf vier Stellen vorgerundet ergäbe 0,1235 -> 12,4 % und wiche von
        # der Excel-Vorlage ab, die den Rohwert formatiert.
        marge=rnd(db / netto, 6) if netto else d(0),
        erloes_je_stunde=rnd(netto / stunden) if stunden else d(0),
    )
