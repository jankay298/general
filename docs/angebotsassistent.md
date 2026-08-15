# Angebotsassistent — Aufbau und Betrieb

Die Sprach-App zum Angebotskalkulator. Der Handwerker spricht, Claude führt das
Gespräch und ruft Werkzeuge auf, die Engine rechnet, am Ende steht ein
druckfertiges Angebot.

## Aufbau

```
data/leistungskatalog.json   Leistungen — eine Quelle für Excel-Vorlage UND Engine
data/stammdaten.example.json Kalkulationsgrößen des Betriebs (Vorlage zum Kopieren)

engine/kalkulation.py        Positions- und Summenrechnung (= docs/rechenmodell.md)
engine/aufmass.py            Aufmaß-Näherung aus der Wohnfläche
engine/vorgang.py            Zustand eines Angebotsgesprächs
engine/angebot_html.py       druckfertiges Angebot als HTML (A4)

assistant/tools.py           die 9 Werkzeuge, die Claude aufrufen kann
assistant/chat.py            Gesprächsschleife über den Tool-Runner

cli.py                       demo | katalog | chat
tests/                       Referenzfall + Werkzeugkette, laufen ohne API
```

**Die Arbeitsteilung ist der Punkt:** das Modell führt das Gespräch und wählt
Werkzeuge, es rechnet nicht. Jeder Betrag im Angebot kommt aus `engine/`,
deterministisch und getestet. Ein Sprachmodell, das Preise im Kopf ausrechnet,
ist genau die Fehlerquelle, die man in einem Angebot nicht haben will.

## Ohne API-Zugang ausprobieren

```bash
python3 cli.py demo               # Referenzfall rechnen, Angebot schreiben
python3 cli.py katalog streichen  # Leistungskatalog durchsuchen
python3 -m unittest discover -s tests -t .
```

`demo` legt `ausgabe/angebot-demo.html` an — im Browser öffnen und über
„Drucken → als PDF sichern" zum PDF machen.

## Mit API-Zugang

```bash
pip install -r requirements.txt
python3 cli.py chat
```

Anmeldung wie bei jedem Anthropic-SDK: `ANTHROPIC_API_KEY` setzen, oder einmal
`ant auth login` ausführen — der leere Client findet das Profil selbst.

Beispielgespräch:

> **Du:** Ich soll bei Familie Meier eine Wohnung streichen, 62 Quadratmeter,
> fünf Türen, ungefähr 30 Kilometer hin und zurück.
>
> **Assistent:** *(ruft `aufmass_setzen`, `paket_wohnung_streichen`,
> `angebot_berechnen`)* Ich rechne mit 2,5 Quadratmeter Wand je Quadratmeter
> Wohnfläche, macht 155 brutto. Komme auf rund 4.300 Euro netto. Passt der
> Wandfaktor?

## Die neun Werkzeuge

| Werkzeug | wofür |
|---|---|
| `leistungen_suchen` | Katalog durchsuchen, bevor eine Position gesetzt wird |
| `aufmass_setzen` | Wohnfläche & Co. — alle Mengen hängen daran |
| `paket_wohnung_streichen` | die zehn Standardpositionen auf einen Schlag |
| `position_hinzufuegen` | Einzelleistung mit fester Menge |
| `position_entfernen` | Position streichen |
| `kunde_setzen` | Kundendaten für den Angebotskopf |
| `angebot_berechnen` | Summen + interne Kennzahlen |
| `angebot_speichern` | druckfertiges HTML schreiben |
| `stammdaten_lesen` | womit gerechnet wird (Mittellohn, Zuschläge, USt) |

Die Beschreibungen sagen bewusst, **wann** ein Werkzeug zu rufen ist, nicht nur
was es tut — daran entscheidet das Modell, ob es greift.

## Modelleinstellungen

`claude-opus-5`, adaptives Denken, `effort` per `ANGEBOT_EFFORT` (Vorgabe
`medium`). Sprachdialog lebt von kurzer Latenz; bei komplexeren Fällen auf
`high` hochdrehen. `max_tokens` steht bewusst hoch, obwohl die gesprochenen
Antworten kurz sind: Denk-Tokens zählen mit dagegen.

## Was die Sprachschicht noch braucht

`Angebotsassistent.antworten()` nimmt Text und gibt Text zurück — bewusst ohne
Mikrofon und Lautsprecher. Für echten Sprachbetrieb kommt davor eine
Spracherkennung und dahinter eine Sprachausgabe; am Assistenten ändert sich
nichts. Der Systemprompt ist bereits auf Vorlesen ausgelegt: ganze Sätze, keine
Aufzählungen, keine Tabellen.

## Wenn sich die Rechnung ändert

`docs/rechenmodell.md` ist die verbindliche Referenz. Ändert sich dort etwas,
müssen **beide** Implementierungen nachziehen — `engine/kalkulation.py` und
`tools/build_angebotskalkulator.py` — und `tests/test_referenzfall.py` fängt es
ab, wenn nur eine von beiden nachgezogen wurde.
