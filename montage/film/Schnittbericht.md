# Lissabon — Januar 2026
## Schnittbericht (Fassung 2)

**Film:** 7:58 Min, 1080×1920 (9:16), 30 fps
**Ausgeliefert:** 6 Teile à ~1:20, je ~28,7 MiB, verlustfrei zusammenfügbar

---

## Material

| | |
|---|---|
| Archiv | 1641 Einträge (820 Medien + 821 `__MACOSX`-Systemdateien) |
| Analysiert | **820** — 674 Fotos, 146 Videos, 52,3 Min Videomaterial |
| Nach Dublettenfilter | 599 eigenständige Aufnahmen |
| Im Film | **114** Quelldateien in 120 Einstellungen |
| davon als Ganzbild gezeigt | 11 (statt auf 9:16 beschnitten) |

### Aussortiert
- **63 re-importierte Kopien** (Apples „ 2"-Muster). Keine byte-identisch — beim
  Re-Import neu komprimiert, also in jedem Byte verschieden bei gleichem Bild.
- **158 Serien-/Beinahe-Dubletten** über einen 64-Bit-Differenzhash.
- Zusätzlich eine Wiederholungssperre **innerhalb jeder Montage**: dasselbe Motiv
  kann nicht zweimal in einer Sequenz landen, auch nicht in anderer Belichtung.

---

## Chronologie

Die Aufnahmezeiten waren nicht direkt verwendbar:

- **124 Videos** trugen ein `CreateDate` vom Exporttag — der Export überschreibt
  es. Die echte Zeit steckt in QuickTimes `CreationDate`.
- **22 geteilte Videos** hatten gar kein Aufnahme-Tag, nur den Moment des
  Speicherns ins Album: alle 22 in einem 17-Minuten-Fenster drei Tage nach der Reise.

Regel: Von allen Zeitstempeln gewinnt der **früheste plausible**. Eine Aufnahme ist
das erste Ereignis im Leben einer Datei; Export, Kopie und Zip stempeln nur später.

Ergebnis: **19.01. 21:22 bis 22.01. 20:23**, vier Tage, keine Ausreißer.

---

## Erzählung

Grundlage: vollständige Transkription aller 146 Videos (Whisper small, deutsch) —
**65 Videos mit Sprache, 3890 Wörter**. 20 Takes
im Film, 4:56 Min gesprochen.

| Kapitel | Inhalt |
|---|---|
| Ankunft (19.01.) | Nachtgassen, Elevador da Bica, Pizza |
| Tag 2 (20.01.) | „Guten Morgen aus Portugal" → Parkplatz-Odyssee → Belém → Alfama → 1-€-Tacos → „Operation Maus pflückt eine Orange" → 27× die Ausfahrt verpasst |
| Tag 3 (21.01.) | „another day, another vlog" → Sintra, „wie ein Märchenland" → Höhle: „wir sind hier illegal" → Korean BBQ 10/10 → „Wir sind nicht mehr in der Kennenlernphase" |
| Tag 4 (22.01.) | „unser letzter Tag" → „das letzte Mal Pastel de Nata" → die Brücke → gelandet in Deutschland |

Pastéis de Nata am ersten Morgen und am letzten Tag bilden die Klammer.
B-Roll stammt jeweils aus dem Zeitfenster um die Aussage.

---

## Bild

- **Ganzbild statt Beschnitt** bei allem breiter als 1,15:1 — das komplette Bild
  mittig über einer unscharfen, abgedunkelten Vergrößerung seiner selbst. Nur wenn
  Gesichter die Breite füllen, wird die nähere Einstellung genommen.
- **9:16-Zuschnitt nach Gesichtern** (YuNet) mit Kopffreiheit.
- **Halation in RGB** — helle Stellen strahlen einen weichen roten Saum ab.
  *Korrigierter Fehler:* Zuvor wurde im YUV-Raum geblendet; „Screen" auf den
  Farbdifferenzkanälen verschiebt Farben statt aufzuhellen und färbte helle
  Szenen magenta (Index +26 → jetzt +1).
- **Ausrichtung:** Fotos werden vor dem Rendern aufgerichtet (Pillow), Videomaße
  kommen inklusive Rotationsmatrix von ffprobe. Zuvor rechnete der Filter bei
  Hochformat mit vertauschten Seiten.
- **Filmische S-Kurve**, Korn, dezente Vignette, Schärfung nach der Skalierung.
- **Bewegung:** Push, Pull oder Schwenk je Foto, deterministisch aus dem Dateinamen.
- **Tempo:** Bildlängen liegen auf dem Takt der jeweiligen Musik; starke Aufnahmen
  werden gehalten, der Rest schnell geschnitten.
- **Schnitt:** hart innerhalb einer Szene, Schwarzblende an Szenengrenzen.

## Ton

- **J-Cuts**: Sprache setzt 0,4 s vor dem Bildschnitt ein, läuft 0,3 s nach.
- Jeder Take auf **−16 LUFS** normalisiert, Hochpass 95 Hz, sanfte Rauschunterdrückung.
- **Ducking** per Hüllkurvenfolger: Musik unter Sprache auf 26 %, 30 ms Ansprechzeit,
  550 ms Rückkehr.
- Original-Atmo der B-Roll bei −30 LUFS darunter.
- Endmix **−16,1 LUFS**.

## Musik

Selbst synthetisiert (`music.py`), kein Stockmaterial, keine externen Dienste.
Beat-Satz: Kick mit Tonhöhenverlauf, Clap aus drei Bursts, Sub-Bass auf den
Akkordgrundtönen, Pad und gezupfte Oberstimme, Bandlaufschwankung und -sättigung.
Sechs Cues von 76 bis 112 bpm, szenenweise zugeordnet.

*Nicht umgesetzt:* bekannte Titel (Bruno Mars, Taylor Swift o. ä.) — urheberrechtlich
geschützt, nicht verwendbar. Eigene Tracks können nachträglich über den fertigen
Film gelegt werden, ohne neu zu rendern.

## Technik

| | |
|---|---|
| Video | H.264, 1080×1920, 30 fps; Teile mit `-tune grain` bei ~3000 kbit/s |
| Ton | AAC, 44,1 kHz stereo |
| Werkzeuge | ffmpeg 6.1.1, faster-whisper (small), OpenCV YuNet, Pillow/pillow-heif, NumPy |
| Schrift | Italiana (Titel), Instrument Sans (Untertitel) |
