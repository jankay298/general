# Lissabon — Januar 2026
## Schnittbericht

**Fertiger Film:** `Lissabon_Film_1080x1920.mp4` — 8:58 Min, 1080×1920 (9:16), 30 fps

---

## Material

| | |
|---|---|
| Dateien im Archiv | 1641 Einträge (820 Medien + 821 `__MACOSX`-Systemdateien) |
| Analysierte Medien | **820** (674 Fotos, 146 Videos, 52,3 Min Videomaterial) |
| Nach Dublettenfilter | 599 eigenständige Aufnahmen |
| Im Film verwendet | **114** Quelldateien in 120 Einstellungen |

### Aussortiert (221 Dateien)
- **63 re-importierte Kopien** — Apples „ 2"-Muster (`IMG_9442 2.MP4` neben `IMG_9442.MP4`).
  Keine davon war byte-identisch: beim Re-Import neu komprimiert, also in jedem Byte
  verschieden bei gleichem Bild. Ein Prüfsummenvergleich findet sie nicht.
- **158 Serien-/Beinahe-Dubletten** — erkannt über einen 64-Bit-Differenzhash des
  Bildinhalts, Schwelle 6/64. Aus jeder Gruppe blieb die schärfste Aufnahme.
- Live-Photo-Begleitclips wurden verworfen.

---

## Chronologie

Die Aufnahmezeiten waren **nicht** direkt verwendbar:

- **124 Videos** trugen ein `CreateDate` vom Exporttag (13.09.2026) — der Export
  überschreibt es. Die echte Zeit steckt in QuickTimes `CreationDate`.
- **22 geteilte Videos** hatten überhaupt kein Aufnahme-Tag, nur den Moment des
  Speicherns ins Album — alle 22 in einem 17-Minuten-Fenster am 25.01., drei Tage
  nach der Reise.

Regel im Skript: Von allen Zeitstempeln gewinnt der **früheste plausible**. Eine
Aufnahme ist das erste Ereignis im Leben einer Datei; Export, Kopie und Zip können
sie nur später stempeln.

Ergebnis: **19.01. 21:22 bis 22.01. 20:23**, vier Tage, keine Ausreißer.

---

## Erzählung

26 Story-Beats, 20 davon mit Originalton
(4:56 Min gesprochen).

Grundlage war die vollständige Transkription aller 146 Videos (Whisper small,
deutsch): **65 Videos mit Sprache, 3890 Wörter**. Daraus die Struktur:

| Kapitel | Inhalt |
|---|---|
| Ankunft (19.01.) | Nachtgassen, Elevador da Bica, Pizza |
| Tag 2 (20.01.) | „Guten Morgen aus Portugal" → Parkplatz-Odyssee (26 € Garage, 1,5 h Suche) → Belém, „Experience 10 von 10" → Alfama-Aussichtspunkte → 1-€-Tacos mit Fazit → „Operation Maus pflückt eine Orange" → Storytime: 27× die Ausfahrt verpasst |
| Tag 3 (21.01.) | „another day, another vlog" → Sintra im Nebel, „wie ein Märchenland" → Quinta da Regaleira → die Höhle: „wir sind hier illegal" → Korean BBQ, 10/10 → nachts: „Wir sind nicht mehr in der Kennenlernphase" |
| Tag 4 (22.01.) | „unser letzter Tag" → „das letzte Mal Pastel de Nata" → die Brücke: „man ist in San Francisco … wir sind in Lisbon" → gelandet in Deutschland |

Die Pastéis de Nata am ersten Morgen und am letzten Tag bilden die Klammer.

B-Roll wurde jeweils aus dem Zeitfenster um die Aussage gewählt — die Tacos-Bilder
(18:11) liegen beim Taco-Gespräch, die Höhle (15:03) bei „wir sind illegal
reingegangen", die Brücke beim San-Francisco-Vergleich.

---

## Bild

- **9:16-Zuschnitt nach Gesichtern** (YuNet). Der Ausschnitt folgt dem Schwerpunkt
  der erkannten Gesichter statt blind der Bildmitte, mit Kopffreiheit nach oben.
- **Halation** — helle Stellen strahlen einen weichen rötlichen Saum ab, wie Licht,
  das von der Filmschicht zurückgestreut wird. Bei Quarter-Resolution gerechnet.
- **S-Kurve mit Farbtrennung**: angehobene Schwarzwerte, kühle Schatten, warme
  Lichter, Sättigung 0,96, feines animiertes Korn, dezente Vignette.
- **Bewegung**: jedes Foto bekommt Push, Pull oder Schwenk — deterministisch aus dem
  Dateinamen, damit Montagen nicht mechanisch wirken. Zoom max. 6 %.
- **Harte Schnitte** als Regel; Schwarzblenden nur an Kapitelgrenzen und am Ende.

## Ton

- **J-Cuts**: Sprache setzt 0,4 s vor dem Bildschnitt ein, läuft 0,3 s nach.
- Jeder Take einzeln auf **−16 LUFS** normalisiert, Hochpass 95 Hz gegen
  Windrumpeln, sanfte Breitband-Rauschunterdrückung.
- **Musik-Ducking** über einen Hüllkurvenfolger: unter Sprache auf 26 %, schnelles
  Einsetzen (30 ms), langsame Rückkehr (550 ms), damit nichts zwischen Wörtern pumpt.
- Original-Atmo der B-Roll liegt bei −30 LUFS darunter.
- Endmix: **−15,5 LUFS integriert**, keine Stillephase über 3 s.

## Musik

Selbst synthetisiert (`edit/music.py`) — kein Stockmaterial, keine externen Dienste:
gezupfte Nylonsaiten mit acht Obertönen, saitenrichtiger Höhenabfall und
Anschlagsgeräusch, darunter eine Pad-Fläche, dazu Bandlaufschwankung (±5 Cent),
Faltungshall und leichte Bandsättigung.

Sechs Stimmungen, szenenweise zugeordnet: `warm`, `wistful`, `playful`, `city`,
`tender`, `farewell` — Akkordfolgen von Am–F–C–G bis Am–F–C–E7, 60–108 bpm.

## Technik

| | |
|---|---|
| Auflösung / Bildrate | 1080×1920, 30 fps |
| Video | H.264, CRF 21, preset slow |
| Ton | AAC 256 kbit/s, 44,1 kHz stereo |
| Werkzeuge | ffmpeg 6.1.1, faster-whisper (small), OpenCV YuNet, Pillow/pillow-heif, NumPy |
| Schrift | Italiana (Titel), Instrument Sans (Untertitel) |
