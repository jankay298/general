"""Angebotsassistent — Claude führt das Gespräch, die Engine rechnet.

Text rein, Text raus. Eine Sprachschicht (STT vor `antworten`, TTS auf das
Ergebnis) kann das unverändert umschließen; deshalb kennt dieses Modul weder
Mikrofon noch Lautsprecher.
"""

from __future__ import annotations

import os
from pathlib import Path

import anthropic

from engine import Katalog, Stammdaten, Vorgang

from .tools import werkzeuge_bauen

MODELL = "claude-opus-5"

# Thinking ist auf Opus 5 standardmäßig an und zählt gegen max_tokens — deshalb
# großzügig, obwohl die gesprochenen Antworten kurz sind.
MAX_TOKENS = 16000

# Sprachdialog lebt von kurzer Latenz. "medium" reicht für Tool-Auswahl und
# Gesprächsführung; auf "high" hochdrehen, wenn Fälle komplexer werden.
EFFORT = os.environ.get("ANGEBOT_EFFORT", "medium")

SYSTEM = """\
Du bist der Angebotsassistent eines Handwerksbetriebs. Der Nutzer ist der \
Handwerker oder die Handwerkerin — meist unterwegs, oft auf der Baustelle, \
häufig per Sprache. Aus dem Gespräch entsteht ein druckfertiges Angebot.

## Wie du sprichst
Antworte kurz und in ganzen Sätzen, so wie du es am Telefon sagen würdest. \
Keine Aufzählungszeichen, keine Tabellen, keine Überschriften — das wird \
vorgelesen. Nenne Beträge gerundet und in Worten lesbar ("dreitausend­­\
zweihundert netto"), Details nur auf Nachfrage.

## Wie du arbeitest
Schätze fehlende Angaben und sprich die Schätzung aus, statt zu blockieren: \
"Ich rechne mit 2,5 Quadratmeter Wand je Quadratmeter Wohnfläche, macht 112 — \
passt das?" Frag nur nach, wenn eine andere Antwort das Angebot deutlich \
verändern würde. Zehn Pflichtfelder abzufragen bricht das Gespräch ab.

Eine Ausnahme: den Mittellohn rätst du nie. Er entscheidet über Gewinn oder \
Verlust und ist eine Betriebseinstellung; ist er offensichtlich falsch \
gesetzt, sag das, statt es zu überspielen.

Rechne nie selbst im Kopf. Jede Zahl, die du nennst, kommt aus \
angebot_berechnen — auch nach kleinen Änderungen neu.

Halte dich an das, was gefragt wurde. Füg keine Leistungen hinzu, die der \
Nutzer nicht genannt hat; schlag sie vor und lass ihn entscheiden.

Die internen Kennzahlen gehören dem Handwerker, nicht dem Kunden. Sag aber \
von dir aus Bescheid, wenn der Erlös je Stunde auffällig niedrig ist — das \
ist der Moment, in dem ein Angebot zu billig rausgeht.
"""


def vorgang_starten(stammdaten_datei: str | Path | None = None) -> Vorgang:
    st = (Stammdaten.aus_datei(stammdaten_datei) if stammdaten_datei
          else Stammdaten())
    return Vorgang(katalog=Katalog.laden(), stammdaten=st)


class Angebotsassistent:
    """Hält Vorgang und Gesprächsverlauf über mehrere Turns."""

    def __init__(self, vorgang: Vorgang | None = None,
                 ausgabe_dir: str | Path = "ausgabe",
                 client: anthropic.Anthropic | None = None):
        self.vorgang = vorgang or vorgang_starten()
        self.ausgabe_dir = Path(ausgabe_dir)
        self.client = client or anthropic.Anthropic()
        self.werkzeuge = werkzeuge_bauen(self.vorgang, self.ausgabe_dir)
        self.verlauf: list[dict] = []

    def antworten(self, eingabe: str) -> str:
        """Eine Gesprächsrunde. Gibt zurück, was vorgelesen werden soll."""
        self.verlauf.append({"role": "user", "content": eingabe})

        runner = self._runner_bauen()
        letzte = None
        for nachricht in runner:
            letzte = nachricht
            self.verlauf.append({"role": "assistant", "content": nachricht.content})
            antwort = runner.generate_tool_call_response()
            if antwort is not None:
                self.verlauf.append(antwort)

        if letzte is None:
            return "Da ist nichts zurückgekommen — bitte noch einmal."
        # stop_reason vor dem Inhalt prüfen: bei einer Ablehnung ist content
        # leer oder abgeschnitten, ein blinder content[0] würde hier krachen.
        if letzte.stop_reason == "refusal":
            return ("Diese Anfrage habe ich abgelehnt. Formuliere sie bitte "
                    "anders oder frag mich etwas zum Angebot.")
        return "".join(b.text for b in letzte.content if b.type == "text").strip()

    def _runner_bauen(self):
        parameter = dict(
            model=MODELL,
            max_tokens=MAX_TOKENS,
            system=SYSTEM,
            thinking={"type": "adaptive"},
            output_config={"effort": EFFORT},
            tools=self.werkzeuge,
            messages=self.verlauf,
        )
        if os.environ.get("ANGEBOT_FALLBACKS", "an") != "aus":
            try:
                return self.client.beta.messages.tool_runner(
                    **parameter,
                    betas=["server-side-fallback-2026-07-01"],
                    fallbacks="default",
                )
            except anthropic.BadRequestError as fehler:
                if "fallback" not in str(fehler).lower():
                    raise
                # Beta für dieses Konto nicht freigeschaltet — ohne weiter.
        return self.client.beta.messages.tool_runner(**parameter)
