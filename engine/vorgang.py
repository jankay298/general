"""Vorgang — der veränderliche Zustand eines Angebotsgesprächs.

Die Sprach-App hält je Gespräch genau einen Vorgang und ruft darauf die
Werkzeuge auf. Alles Rechnen liegt in kalkulation.py; hier wird nur gesammelt.
"""

from __future__ import annotations

from dataclasses import dataclass, field

from .aufmass import Aufmass
from .kalkulation import (Angebot, Katalog, Position, Stammdaten,
                          angebot_rechnen, d, position_rechnen)

# Standardpaket "Wohnung komplett streichen": Katalog-Nr. -> Mengenbezug.
# Der Bezug wird gegen das Aufmaß aufgelöst, damit eine geänderte Wohnfläche
# alle Mengen mitzieht.
PAKET_WOHNUNG_STREICHEN = [
    ("M12", "pauschal"),
    ("M01", "bodenflaeche"),
    ("M02", "streichflaeche"),
    ("M03", "spachtelflaeche"),
    ("M04", "streichflaeche"),
    ("M05", "wandflaeche"),
    ("M06", "deckenflaeche"),
    ("M09", "tueren"),
    ("M08", "tueren"),
    ("M10", "bodenflaeche"),
]


def menge_aus_bezug(bezug: str, aufmass: Aufmass):
    if bezug == "pauschal":
        return d(1)
    if bezug == "tueren":
        return d(aufmass.tueren)
    return getattr(aufmass, bezug)


@dataclass
class Vorgang:
    katalog: Katalog
    stammdaten: Stammdaten
    aufmass: Aufmass = field(default_factory=Aufmass)
    kunde: dict = field(default_factory=lambda: {
        "name": "", "strasse": "", "plz_ort": "", "objekt": "",
    })
    angebotsnummer: str = ""
    # (katalog_nr, menge oder Bezugsname)
    _eintraege: list[tuple[str, object]] = field(default_factory=list)

    # --- Bearbeiten ---
    def position_hinzufuegen(self, katalog_nr: str, menge=None, bezug: str | None = None):
        if katalog_nr not in self.katalog:
            raise KeyError(katalog_nr)
        if bezug is None and menge is None:
            raise ValueError("menge oder bezug angeben")
        self._eintraege.append((katalog_nr, bezug if bezug else d(menge)))
        return self.positionen()[-1]

    def paket_hinzufuegen(self, name: str = "wohnung_streichen") -> list[Position]:
        if name != "wohnung_streichen":
            raise ValueError(f"unbekanntes Paket: {name}")
        vorher = len(self._eintraege)
        for nr, bezug in PAKET_WOHNUNG_STREICHEN:
            self._eintraege.append((nr, bezug))
        return self.positionen()[vorher:]

    def position_entfernen(self, pos: int) -> None:
        if not 1 <= pos <= len(self._eintraege):
            raise IndexError(pos)
        del self._eintraege[pos - 1]

    def leeren(self) -> None:
        self._eintraege.clear()

    # --- Rechnen ---
    def positionen(self) -> list[Position]:
        ergebnis = []
        for i, (nr, menge_oder_bezug) in enumerate(self._eintraege, start=1):
            menge = (menge_aus_bezug(menge_oder_bezug, self.aufmass)
                     if isinstance(menge_oder_bezug, str) else menge_oder_bezug)
            ergebnis.append(position_rechnen(i, self.katalog[nr], menge, self.stammdaten))
        return ergebnis

    def angebot(self) -> Angebot:
        return angebot_rechnen(self.positionen(), self.stammdaten,
                               self.aufmass.fahrstrecke_km)
