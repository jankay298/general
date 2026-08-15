"""Aufmaß-Näherung für Malerarbeiten.

Aus der Wohnfläche werden Wand-, Decken- und Spachtelfläche geschätzt. Die
Faktoren sind Eingaben, keine versteckten Konstanten — die App soll sie
aussprechen, nicht stillschweigend setzen (siehe docs/rechenmodell.md).
"""

from __future__ import annotations

from dataclasses import dataclass, replace

from .kalkulation import Decimal, d, rnd


@dataclass(frozen=True)
class Aufmass:
    wohnflaeche: Decimal = d(0)
    raumhoehe: Decimal = d("2.50")
    faktor_wand: Decimal = d("2.50")        # m² Wand je m² Wohnfläche
    abzug_fenster_tueren: Decimal = d(0)
    tueren: int = 0
    spachtel_anteil: Decimal = d("0.30")
    fahrstrecke_km: Decimal = d(0)

    # --- abgeleitete Größen ---
    @property
    def wandflaeche_brutto(self) -> Decimal:
        return rnd(self.wohnflaeche * self.faktor_wand)

    @property
    def wandflaeche(self) -> Decimal:
        return rnd(max(d(0), self.wandflaeche_brutto - self.abzug_fenster_tueren))

    @property
    def deckenflaeche(self) -> Decimal:
        return rnd(self.wohnflaeche)

    @property
    def bodenflaeche(self) -> Decimal:
        return rnd(self.wohnflaeche)

    @property
    def streichflaeche(self) -> Decimal:
        """Wand netto + Decke — die Fläche, die zweimal gestrichen wird."""
        return rnd(self.wandflaeche + self.deckenflaeche)

    @property
    def spachtelflaeche(self) -> Decimal:
        return rnd(self.streichflaeche * self.spachtel_anteil, 1)

    def mit(self, **aenderungen) -> "Aufmass":
        umgewandelt = {}
        for k, v in aenderungen.items():
            if v is None:
                continue
            umgewandelt[k] = int(v) if k == "tueren" else d(v)
        return replace(self, **umgewandelt)

    def als_dict(self) -> dict:
        return {
            "wohnflaeche_m2": float(self.wohnflaeche),
            "raumhoehe_m": float(self.raumhoehe),
            "faktor_wand": float(self.faktor_wand),
            "wandflaeche_brutto_m2": float(self.wandflaeche_brutto),
            "abzug_fenster_tueren_m2": float(self.abzug_fenster_tueren),
            "wandflaeche_netto_m2": float(self.wandflaeche),
            "deckenflaeche_m2": float(self.deckenflaeche),
            "bodenflaeche_m2": float(self.bodenflaeche),
            "streichflaeche_m2": float(self.streichflaeche),
            "spachtelflaeche_m2": float(self.spachtelflaeche),
            "tueren": self.tueren,
            "fahrstrecke_km": float(self.fahrstrecke_km),
        }
