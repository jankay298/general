"""Werkzeuge, die Claude im Angebotsgespräch aufrufen kann.

Die Beschreibungen sagen bewusst, *wann* ein Werkzeug zu rufen ist, nicht nur
was es tut — daran entscheidet das Modell, ob es greift.
"""

from __future__ import annotations

import json
from pathlib import Path

from anthropic import beta_tool

from engine import Vorgang
from engine.angebot_html import angebot_schreiben


def _json(obj) -> str:
    return json.dumps(obj, ensure_ascii=False, indent=1)


def werkzeuge_bauen(vorgang: Vorgang, ausgabe_dir: Path) -> list:
    """Baut die Werkzeugliste für genau einen Vorgang."""

    def positionsliste() -> list[dict]:
        return [
            {"pos": p.pos, "nr": p.katalog_nr, "leistung": p.leistung,
             "menge": float(p.menge), "einheit": p.einheit,
             "einzelpreis": float(p.einzelpreis), "gesamt": float(p.gesamtpreis)}
            for p in vorgang.positionen()
        ]

    @beta_tool
    def leistungen_suchen(suchbegriff: str, gewerk: str = "") -> str:
        """Durchsucht den Leistungskatalog des Betriebs.

        Rufe das auf, bevor du eine Position hinzufügst und die Katalog-Nummer
        noch nicht kennst, oder wenn der Kunde eine Arbeit nennt und du prüfen
        willst, ob der Betrieb sie im Katalog führt. Leerer Suchbegriff listet
        alles.

        Args:
            suchbegriff: Teil der Leistungsbezeichnung, z. B. "streichen", "Tür".
            gewerk: Optional auf ein Gewerk einschränken (Maler, Trockenbau,
                Boden, Elektro, Logistik).
        """
        treffer = vorgang.katalog.suchen(suchbegriff, gewerk or None)
        return _json([
            {"nr": l.nr, "gewerk": l.gewerk, "leistung": l.leistung,
             "einheit": l.einheit, "zeit_je_einheit_std": float(l.zeit_je_einheit_std),
             "material_je_einheit_eur": float(l.material_je_einheit_eur),
             "hinweis": l.hinweis}
            for l in treffer
        ])

    @beta_tool
    def aufmass_setzen(wohnflaeche: float, tueren: int, fahrstrecke_km: float,
                       abzug_fenster_tueren: float = 0.0, faktor_wand: float = 2.5,
                       raumhoehe: float = 2.5, spachtel_anteil: float = 0.3) -> str:
        """Setzt das Aufmaß und gibt die daraus geschätzten Flächen zurück.

        Rufe das auf, sobald du die Wohnfläche kennst — alle Mengen der
        Positionen hängen daran und ziehen automatisch mit. Ersetzt das
        bisherige Aufmaß vollständig, gib also immer alle bekannten Werte an.

        Sprich die angenommenen Faktoren danach aus, statt sie stillschweigend
        zu setzen ("ich rechne mit 2,5 m² Wand je m² Wohnfläche, macht 112 m²").

        Args:
            wohnflaeche: Grundfläche der zu bearbeitenden Räume in m².
            tueren: Anzahl Türen (für Lackierarbeiten).
            fahrstrecke_km: Fahrstrecke hin und zurück in km.
            abzug_fenster_tueren: Nicht zu streichende Fläche in m².
            faktor_wand: m² Wandfläche je m² Wohnfläche. 2,3–2,8 bei mehreren
                Räumen, kleiner bei einem einzelnen großen Raum.
            raumhoehe: Raumhöhe in m (nur informativ).
            spachtel_anteil: Anteil der Fläche, der gespachtelt werden muss
                (0,3 = 30 %).
        """
        vorgang.aufmass = vorgang.aufmass.mit(
            wohnflaeche=wohnflaeche, tueren=tueren, fahrstrecke_km=fahrstrecke_km,
            abzug_fenster_tueren=abzug_fenster_tueren, faktor_wand=faktor_wand,
            raumhoehe=raumhoehe, spachtel_anteil=spachtel_anteil,
        )
        return _json(vorgang.aufmass.als_dict())

    @beta_tool
    def paket_wohnung_streichen() -> str:
        """Fügt das Standardpaket "Wohnung komplett streichen" hinzu.

        Rufe das, wenn der Kunde eine komplette Wohnung streichen lassen will,
        statt zehn Positionen einzeln zu setzen. Enthält An-/Abfahrt, Abdecken,
        Untergrund, Spachteln, Grundierung, Wände, Decken, Türen und
        Endreinigung. Das Aufmaß muss vorher gesetzt sein.
        """
        neu = vorgang.paket_hinzufuegen("wohnung_streichen")
        return _json({"hinzugefuegt": len(neu), "positionen": positionsliste()})

    @beta_tool
    def position_hinzufuegen(katalog_nr: str, menge: float) -> str:
        """Fügt eine einzelne Position mit fester Menge hinzu.

        Für Arbeiten außerhalb des Standardpakets, oder wenn der Kunde eine
        konkrete Menge nennt ("noch drei Steckdosen tauschen").

        Args:
            katalog_nr: Nummer aus dem Leistungskatalog, z. B. "M05", "E01".
            menge: Menge in der Einheit der Leistung.
        """
        try:
            p = vorgang.position_hinzufuegen(katalog_nr, menge=menge)
        except KeyError:
            return _json({"fehler": f"Katalog-Nr. {katalog_nr} gibt es nicht. "
                                    "Erst leistungen_suchen aufrufen."})
        return _json({"pos": p.pos, "leistung": p.leistung,
                      "menge": float(p.menge), "einheit": p.einheit,
                      "einzelpreis": float(p.einzelpreis),
                      "gesamt": float(p.gesamtpreis)})

    @beta_tool
    def position_entfernen(pos: int) -> str:
        """Entfernt eine Position aus dem Angebot.

        Rufe das, wenn der Kunde eine Leistung streicht. Achtung: die
        Positionsnummern der folgenden Zeilen rutschen danach nach oben.

        Args:
            pos: Positionsnummer aus der aktuellen Liste.
        """
        try:
            vorgang.position_entfernen(pos)
        except IndexError:
            return _json({"fehler": f"Position {pos} gibt es nicht."})
        return _json({"positionen": positionsliste()})

    @beta_tool
    def kunde_setzen(name: str, objekt: str, strasse: str = "",
                     plz_ort: str = "") -> str:
        """Hinterlegt Kundendaten und das Bauvorhaben für den Angebotskopf.

        Rufe das, sobald der Name des Kunden fällt. Adresse kann nachgereicht
        werden — ohne sie ist das Angebot rechnerisch trotzdem vollständig.

        Args:
            name: Name des Kunden oder Auftraggebers.
            objekt: Kurzbeschreibung, z. B. "Wohnung 45 m², komplett streichen".
            strasse: Straße und Hausnummer.
            plz_ort: PLZ und Ort.
        """
        vorgang.kunde.update({"name": name, "objekt": objekt,
                              "strasse": strasse, "plz_ort": plz_ort})
        return _json(vorgang.kunde)

    @beta_tool
    def angebot_berechnen() -> str:
        """Rechnet das Angebot durch und gibt Summen und interne Kennzahlen zurück.

        Rufe das, bevor du dem Nutzer einen Preis nennst, und noch einmal nach
        jeder Änderung. Die Kennzahlen (Deckungsbeitrag, Marge, Erlös je
        Stunde) sind nur für den Handwerker — nenne sie dem Kunden nicht
        ungefragt, aber warne den Handwerker, wenn der Erlös je Stunde unter
        seinem Mittellohn plus Zuschlägen liegt.
        """
        a = vorgang.angebot()
        return _json({"aufmass": vorgang.aufmass.als_dict(), **a.als_dict()})

    @beta_tool
    def angebot_speichern(dateiname: str = "angebot.html") -> str:
        """Schreibt das druckfertige Angebot als HTML-Datei (A4, PDF-fähig).

        Rufe das erst, wenn Positionen und Kundendaten stehen und der Nutzer
        das Angebot haben will.

        Args:
            dateiname: Dateiname, z. B. "angebot-mueller.html".
        """
        if not vorgang.positionen():
            return _json({"fehler": "Noch keine Positionen im Angebot."})
        pfad = angebot_schreiben(vorgang, ausgabe_dir / Path(dateiname).name)
        a = vorgang.angebot()
        return _json({"datei": str(pfad),
                      "nettobetrag": float(a.nettobetrag),
                      "bruttobetrag": float(a.bruttobetrag)})

    @beta_tool
    def stammdaten_lesen() -> str:
        """Liest die Kalkulationsgrößen des Betriebs (Mittellohn, Zuschläge, USt).

        Rufe das, wenn der Nutzer fragt, womit gerechnet wird, oder wenn ein
        Preis unerwartet ausfällt und du die Ursache erklären sollst. Diese
        Werte sind Betriebseinstellungen und werden nicht im Gespräch geändert.
        """
        st = vorgang.stammdaten
        return _json({
            "mittellohn_eur_je_std": float(st.mittellohn),
            "gemeinkosten_lohn": float(st.gk_lohn),
            "gemeinkosten_material": float(st.gk_material),
            "wagnis_gewinn": float(st.wagnis_gewinn),
            "skonto_im_preis": float(st.skonto),
            "rabatt": float(st.rabatt),
            "fahrtkosten_je_km": float(st.fahrtkosten_je_km),
            "umsatzsteuer": float(st.umsatzsteuer),
            "firma": st.firma["name"],
        })

    return [leistungen_suchen, aufmass_setzen, paket_wohnung_streichen,
            position_hinzufuegen, position_entfernen, kunde_setzen,
            angebot_berechnen, angebot_speichern, stammdaten_lesen]
