#!/usr/bin/env python3
"""Kommandozeile für den Angebotskalkulator.

    python3 cli.py demo              Referenzfall rechnen, Angebot schreiben (ohne API)
    python3 cli.py katalog [suche]   Leistungskatalog anzeigen (ohne API)
    python3 cli.py chat              Gespräch mit dem Assistenten (braucht API-Zugang)
"""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from engine import Aufmass, Katalog, Stammdaten, Vorgang, d  # noqa: E402
from engine.angebot_html import angebot_schreiben  # noqa: E402

AUSGABE = Path(__file__).resolve().parent / "ausgabe"


def eur(v) -> str:
    return f"{v:>10,.2f} €".replace(",", "X").replace(".", ",").replace("X", ".")


def referenzvorgang() -> Vorgang:
    v = Vorgang(katalog=Katalog.laden(), stammdaten=Stammdaten())
    v.aufmass = Aufmass(wohnflaeche=d(45), abzug_fenster_tueren=d(12), tueren=4,
                        fahrstrecke_km=d(40))
    v.kunde.update({"name": "Familie Beispiel", "strasse": "Beispielstraße 7",
                    "plz_ort": "12345 Musterstadt",
                    "objekt": "Wohnung 45 m², komplett streichen"})
    v.angebotsnummer = "2026-0001"
    v.paket_hinzufuegen("wohnung_streichen")
    return v


def befehl_demo() -> int:
    v = referenzvorgang()
    a = v.angebot()

    print("Aufmaß")
    am = v.aufmass
    print(f"  Wohnfläche {am.wohnflaeche} m² × Faktor {am.faktor_wand}"
          f" − {am.abzug_fenster_tueren} m² Abzug")
    print(f"  Wand {am.wandflaeche} m² · Decke {am.deckenflaeche} m²"
          f" · Spachtel {am.spachtelflaeche} m² · {am.tueren} Türen\n")

    print(f"{'Pos':>3}  {'Leistung':<44}{'Menge':>8} {'Einh':<8}"
          f"{'EP':>11}{'Gesamt':>13}")
    for p in a.positionen:
        print(f"{p.pos:>3}  {p.leistung[:42]:<44}{p.menge:>8}"
              f" {p.einheit:<8}{eur(p.einzelpreis)}{eur(p.gesamtpreis)}")

    print(f"\n{'Zwischensumme Positionen':>62}{eur(a.zwischensumme_positionen)}")
    print(f"{'An- und Abfahrt':>62}{eur(a.fahrtkosten)}")
    if a.rabatt:
        print(f"{'Rabatt':>62}{eur(a.rabatt)}")
    print(f"{'Nettobetrag':>62}{eur(a.nettobetrag)}")
    print(f"{'zzgl. Umsatzsteuer':>62}{eur(a.umsatzsteuer)}")
    print(f"{'Gesamtbetrag (brutto)':>62}{eur(a.bruttobetrag)}")

    print("\nIntern (nicht Teil des Angebots)")
    print(f"  Gesamtstunden      {a.gesamtstunden}")
    print(f"  Selbstkosten      {eur(a.selbstkosten)}")
    print(f"  Deckungsbeitrag   {eur(a.deckungsbeitrag)}")
    print(f"  Marge              {a.marge * 100:.1f} %".replace(".", ","))
    print(f"  Erlös je Stunde   {eur(a.erloes_je_stunde)}")

    pfad = angebot_schreiben(v, AUSGABE / "angebot-demo.html")
    print(f"\nAngebot geschrieben: {pfad}")
    return 0


def befehl_katalog(suche: str = "") -> int:
    katalog = Katalog.laden()
    treffer = katalog.suchen(suche)
    print(f"{'Nr':<6}{'Gewerk':<13}{'Leistung':<48}{'Einh':<9}"
          f"{'Std/Einh':>9}{'Mat/Einh':>10}")
    for l in treffer:
        print(f"{l.nr:<6}{l.gewerk:<13}{l.leistung[:46]:<48}{l.einheit:<9}"
              f"{l.zeit_je_einheit_std:>9}{l.material_je_einheit_eur:>10}")
    print(f"\n{len(treffer)} von {len(katalog.alle())} Leistungen")
    return 0


def befehl_chat() -> int:
    try:
        from assistant import Angebotsassistent
    except ImportError:
        print("Das anthropic-SDK fehlt:  pip install anthropic", file=sys.stderr)
        return 1

    assistent = Angebotsassistent(ausgabe_dir=AUSGABE)
    print("Angebotsassistent — beenden mit Strg-D oder 'ende'.\n")
    while True:
        try:
            eingabe = input("Du: ").strip()
        except (EOFError, KeyboardInterrupt):
            print()
            return 0
        if not eingabe:
            continue
        if eingabe.lower() in ("ende", "exit", "quit"):
            return 0
        try:
            print(f"\nAssistent: {assistent.antworten(eingabe)}\n")
        except Exception as fehler:  # noqa: BLE001 — im Dialog nicht abstürzen
            print(f"\n[Fehler] {type(fehler).__name__}: {fehler}\n", file=sys.stderr)


def main(argv: list[str]) -> int:
    befehl = argv[1] if len(argv) > 1 else "demo"
    if befehl == "demo":
        return befehl_demo()
    if befehl == "katalog":
        return befehl_katalog(argv[2] if len(argv) > 2 else "")
    if befehl == "chat":
        return befehl_chat()
    print(__doc__)
    return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
