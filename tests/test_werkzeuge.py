"""Die Werkzeuge des Assistenten, ohne API durchgespielt.

Ruft sie in der Reihenfolge auf, in der ein Gespräch sie aufrufen würde, und
prüft, dass am Ende derselbe Referenzfall herauskommt wie in der Excel-Vorlage.
Damit ist die Werkzeugschicht getestet, ohne dass ein Modell laufen muss.
"""

import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from engine import Katalog, Stammdaten, Vorgang  # noqa: E402

try:
    from assistant.tools import werkzeuge_bauen
except ImportError:  # anthropic-SDK nicht installiert
    werkzeuge_bauen = None


@unittest.skipIf(werkzeuge_bauen is None, "anthropic-SDK nicht installiert")
class TestWerkzeuge(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.vorgang = Vorgang(katalog=Katalog.laden(), stammdaten=Stammdaten())
        self.werkzeuge = {t.to_dict()["name"]: t
                          for t in werkzeuge_bauen(self.vorgang, Path(self.tmp.name))}

    def ruf(self, werkzeug, /, **argumente):
        """Ruft ein Werkzeug so auf, wie der Tool-Runner es täte.

        Der Werkzeugname steht positionsgebunden, damit ein Tool-Argument
        namens "name" (kunde_setzen) nicht mit ihm kollidiert.
        """
        return json.loads(self.werkzeuge[werkzeug].call(argumente))

    # --- einzelne Werkzeuge ---
    def test_suche_findet_und_filtert(self):
        alle = self.ruf("leistungen_suchen", suchbegriff="")
        self.assertEqual(len(alle), 24)
        streichen = self.ruf("leistungen_suchen", suchbegriff="streichen")
        self.assertTrue(all("streichen" in l["leistung"].lower() for l in streichen))
        logistik = self.ruf("leistungen_suchen", suchbegriff="", gewerk="Logistik")
        self.assertTrue(all(l["gewerk"] == "Logistik" for l in logistik))

    def test_unbekannte_katalognummer_meldet_statt_zu_stuerzen(self):
        antwort = self.ruf("position_hinzufuegen", katalog_nr="XX99", menge=1)
        self.assertIn("fehler", antwort)
        self.assertIn("leistungen_suchen", antwort["fehler"])

    def test_speichern_ohne_positionen_meldet_fehler(self):
        self.assertIn("fehler", self.ruf("angebot_speichern"))

    def test_aufmass_gibt_geschaetzte_flaechen_zurueck(self):
        a = self.ruf("aufmass_setzen", wohnflaeche=45, tueren=4,
                     fahrstrecke_km=40, abzug_fenster_tueren=12)
        self.assertEqual(a["wandflaeche_brutto_m2"], 112.5)
        self.assertEqual(a["wandflaeche_netto_m2"], 100.5)
        self.assertEqual(a["streichflaeche_m2"], 145.5)

    # --- kompletter Gesprächsablauf ---
    def test_gespraech_ergibt_den_referenzfall(self):
        self.ruf("kunde_setzen", name="Familie Beispiel",
                 objekt="Wohnung 45 m², komplett streichen",
                 strasse="Beispielstraße 7", plz_ort="12345 Musterstadt")
        self.ruf("aufmass_setzen", wohnflaeche=45, tueren=4, fahrstrecke_km=40,
                 abzug_fenster_tueren=12)
        paket = self.ruf("paket_wohnung_streichen")
        self.assertEqual(paket["hinzugefuegt"], 10)

        angebot = self.ruf("angebot_berechnen")
        summen = angebot["summen"]
        self.assertEqual(summen["nettobetrag"], 3240.90)
        self.assertEqual(summen["umsatzsteuer"], 615.77)
        self.assertEqual(summen["bruttobetrag"], 3856.67)
        self.assertEqual(angebot["kennzahlen_intern"]["gesamtstunden"], 45.17)

        gespeichert = self.ruf("angebot_speichern", dateiname="test.html")
        pfad = Path(gespeichert["datei"])
        self.assertTrue(pfad.exists())
        inhalt = pfad.read_text(encoding="utf-8")
        self.assertIn("Familie Beispiel", inhalt)
        self.assertIn("3.856,67", inhalt)
        # Interne Kennzahlen dürfen nie im Kundenangebot landen.
        for verboten in ("Deckungsbeitrag", "Selbstkosten", "Marge", "Mittellohn"):
            self.assertNotIn(verboten, inhalt)

    def test_aenderung_zieht_die_mengen_mit(self):
        self.ruf("aufmass_setzen", wohnflaeche=45, tueren=4, fahrstrecke_km=40,
                 abzug_fenster_tueren=12)
        self.ruf("paket_wohnung_streichen")
        vorher = self.ruf("angebot_berechnen")["summen"]["nettobetrag"]

        # Kunde korrigiert die Fläche mitten im Gespräch.
        self.ruf("aufmass_setzen", wohnflaeche=70, tueren=4, fahrstrecke_km=40,
                 abzug_fenster_tueren=12)
        nachher = self.ruf("angebot_berechnen")["summen"]["nettobetrag"]
        self.assertGreater(nachher, vorher)

    def test_position_entfernen_nummeriert_neu(self):
        self.ruf("aufmass_setzen", wohnflaeche=45, tueren=4, fahrstrecke_km=40)
        self.ruf("paket_wohnung_streichen")
        rest = self.ruf("position_entfernen", pos=1)["positionen"]
        self.assertEqual(len(rest), 9)
        self.assertEqual([p["pos"] for p in rest], list(range(1, 10)))

    def test_einzelposition_neben_dem_paket(self):
        self.ruf("aufmass_setzen", wohnflaeche=45, tueren=4, fahrstrecke_km=40)
        self.ruf("paket_wohnung_streichen")
        zusatz = self.ruf("position_hinzufuegen", katalog_nr="E01", menge=3)
        self.assertEqual(zusatz["menge"], 3.0)
        self.assertEqual(zusatz["pos"], 11)


if __name__ == "__main__":
    unittest.main(verbosity=2)
