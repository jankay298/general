"""Der Referenzfall aus docs/rechenmodell.md.

Weicht die Engine hier ab, stimmt sie nicht mehr mit der Excel-Vorlage überein.
Die Sollwerte stammen aus der von LibreOffice durchgerechneten .xlsx.
"""

import sys
import unittest
from decimal import Decimal
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from engine import Aufmass, Katalog, Stammdaten, Vorgang, d, rnd  # noqa: E402


def referenzvorgang() -> Vorgang:
    v = Vorgang(katalog=Katalog.laden(), stammdaten=Stammdaten())
    v.aufmass = Aufmass(
        wohnflaeche=d(45), raumhoehe=d("2.50"), faktor_wand=d("2.50"),
        abzug_fenster_tueren=d(12), tueren=4, spachtel_anteil=d("0.30"),
        fahrstrecke_km=d(40),
    )
    v.paket_hinzufuegen("wohnung_streichen")
    return v


class TestAufmass(unittest.TestCase):
    def test_flaechen(self):
        a = referenzvorgang().aufmass
        self.assertEqual(a.wandflaeche_brutto, Decimal("112.50"))
        self.assertEqual(a.wandflaeche, Decimal("100.50"))
        self.assertEqual(a.deckenflaeche, Decimal("45.00"))
        self.assertEqual(a.streichflaeche, Decimal("145.50"))
        # 145,5 x 0,3 = 43,65 -> kaufmännisch auf eine Stelle = 43,7
        self.assertEqual(a.spachtelflaeche, Decimal("43.7"))


class TestPositionen(unittest.TestCase):
    def setUp(self):
        self.positionen = referenzvorgang().positionen()

    def test_anzahl(self):
        self.assertEqual(len(self.positionen), 10)

    def test_waende_streichen(self):
        p = next(p for p in self.positionen if p.katalog_nr == "M05")
        self.assertEqual(p.menge, Decimal("100.50"))
        self.assertEqual(p.stunden, Decimal("12.06"))
        self.assertEqual(p.lohnkosten, Decimal("506.52"))
        self.assertEqual(p.materialkosten, Decimal("125.63"))
        self.assertEqual(p.selbstkosten, Decimal("771.34"))
        self.assertEqual(p.einzelpreis, Decimal("8.77"))
        self.assertEqual(p.gesamtpreis, Decimal("881.39"))

    def test_spachteln_rundet_kaufmaennisch(self):
        p = next(p for p in self.positionen if p.katalog_nr == "M03")
        self.assertEqual(p.menge, Decimal("43.7"))
        self.assertEqual(p.materialkosten, Decimal("19.67"))  # 19,665 -> 19,67
        self.assertEqual(p.gesamtpreis, Decimal("181.79"))

    def test_einzelpreis_mal_menge_ergibt_gesamtpreis(self):
        """Sonst geht das gedruckte Angebot für den Kunden nicht auf.

        Gegengerechnet wird mit rnd() — kaufmännisch, wie Excel. Decimals
        eigenes quantize() rundet zur geraden Zahl und liefert bei M05
        881,38 statt der 881,39 aus der Vorlage.
        """
        for p in self.positionen:
            with self.subTest(pos=p.katalog_nr):
                self.assertEqual(p.gesamtpreis, rnd(p.einzelpreis * p.menge))


class TestAngebotssumme(unittest.TestCase):
    def setUp(self):
        self.a = referenzvorgang().angebot()

    def test_summen(self):
        self.assertEqual(self.a.zwischensumme_positionen, Decimal("3212.34"))
        self.assertEqual(self.a.fahrtkosten, Decimal("28.56"))
        self.assertEqual(self.a.rabatt, Decimal("0.00"))
        self.assertEqual(self.a.nettobetrag, Decimal("3240.90"))
        self.assertEqual(self.a.umsatzsteuer, Decimal("615.77"))
        self.assertEqual(self.a.bruttobetrag, Decimal("3856.67"))

    def test_angebot_geht_auf(self):
        summe = (self.a.zwischensumme_positionen + self.a.fahrtkosten + self.a.rabatt)
        self.assertEqual(summe, self.a.nettobetrag)
        self.assertEqual(self.a.nettobetrag + self.a.umsatzsteuer, self.a.bruttobetrag)

    def test_kennzahlen(self):
        self.assertEqual(self.a.gesamtstunden, Decimal("45.17"))
        self.assertEqual(self.a.materialkosten, Decimal("401.13"))
        self.assertEqual(self.a.selbstkosten, Decimal("2840.68"))
        self.assertEqual(self.a.deckungsbeitrag, Decimal("400.22"))
        self.assertEqual(self.a.marge, Decimal("0.123490"))  # Anzeige: 12,3 %
        self.assertEqual(self.a.erloes_je_stunde, Decimal("71.75"))


class TestStellschrauben(unittest.TestCase):
    """Die Kalkulation muss auf ihre Treiber reagieren."""

    def test_mittellohn_hebt_den_preis(self):
        v = referenzvorgang()
        basis = v.angebot().nettobetrag
        v.stammdaten = v.stammdaten.mit(mittellohn=d(50))
        self.assertGreater(v.angebot().nettobetrag, basis)

    def test_wohnflaeche_zieht_alle_mengen_mit(self):
        v = referenzvorgang()
        basis = v.angebot().nettobetrag
        v.aufmass = v.aufmass.mit(wohnflaeche=90)
        self.assertGreater(v.angebot().nettobetrag, basis * Decimal("1.5"))

    def test_rabatt_senkt_den_nettobetrag(self):
        v = referenzvorgang()
        basis = v.angebot().nettobetrag
        v.stammdaten = v.stammdaten.mit(rabatt=d("0.10"))
        self.assertEqual(v.angebot().rabatt, -Decimal("324.09"))
        self.assertLess(v.angebot().nettobetrag, basis)

    def test_kleinunternehmer_ohne_umsatzsteuer(self):
        v = referenzvorgang()
        v.stammdaten = v.stammdaten.mit(umsatzsteuer=d(0))
        a = v.angebot()
        self.assertEqual(a.umsatzsteuer, Decimal("0.00"))
        self.assertEqual(a.bruttobetrag, a.nettobetrag)

    def test_leerer_vorgang_rechnet_ohne_division_durch_null(self):
        v = Vorgang(katalog=Katalog.laden(), stammdaten=Stammdaten())
        a = v.angebot()
        self.assertEqual(a.nettobetrag, Decimal("0.00"))
        self.assertEqual(a.erloes_je_stunde, Decimal(0))
        self.assertEqual(a.marge, Decimal(0))


if __name__ == "__main__":
    unittest.main(verbosity=2)


class TestStammdatenLaden(unittest.TestCase):
    def test_beispieldatei_laedt_und_ignoriert_kommentare(self):
        pfad = Path(__file__).resolve().parent.parent / "data" / "stammdaten.example.json"
        st = Stammdaten.aus_datei(pfad)
        self.assertEqual(st.mittellohn, Decimal("42.00"))
        self.assertEqual(st.zahlungsziel_tage, 14)
        self.assertEqual(st.firma["name"], "Muster Malerbetrieb GmbH")

    def test_referenzfall_aus_datei_ist_identisch(self):
        pfad = Path(__file__).resolve().parent.parent / "data" / "stammdaten.example.json"
        v = referenzvorgang()
        v.stammdaten = Stammdaten.aus_datei(pfad)
        self.assertEqual(v.angebot().bruttobetrag, Decimal("3856.67"))
