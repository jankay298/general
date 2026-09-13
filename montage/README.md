# montage

Turns a zip archive (or folder) of holiday photos and videos into a single
chronological film — the sort of thing an 820-file phone album is good for and
otherwise never gets used as.

## Usage

```bash
./montage/make-montage.py --input Lissabon.zip --output lissabon.mp4
```

That reads every photo and video in the archive, orders them by the moment they
were taken, gives each photo 2.5 seconds with a slow Ken Burns drift, trims each
video to 6 seconds, and crossfades the whole lot together at 1080p30.

A six-minute cut with music, from the same archive:

```bash
./montage/make-montage.py --input Lissabon.zip --output lissabon.mp4 \
    --max-minutes 6 --music soundtrack.mp3
```

`--max-minutes` keeps every n-th clip rather than the first n, so a shortened
film still runs from the first morning to the last evening.

## Requirements

```bash
sudo apt-get install ffmpeg libimage-exiftool-perl libheif-examples
pip install pillow pillow-heif
```

## Options worth knowing

| Option | Default | What it does |
| --- | --- | --- |
| `--photo-duration` | 2.5 | Seconds per photo |
| `--video-max` | 6.0 | Seconds kept from each video clip |
| `--max-minutes` | 0 | Thin the album down to roughly this runtime (0 = keep everything) |
| `--transition` | 0.5 | Crossfade length; `0` gives hard cuts and runs far faster |
| `--fit` | blur | How portrait shots fill a landscape frame: `blur`, `pad` or `cover` |
| `--zoom` | 1.12 | Ken Burns zoom factor; `1.0` leaves photos still |
| `--music` | — | Audio file laid under the film, looped and faded out |
| `--keep-audio` | off | Keep the original sound of video clips |
| `--resolution` | 1920x1080 | Output size |
| `--limit` | 0 | Only use the first N files — useful for a two-minute test run |

## Three things worth knowing

**HEIC is handled outside ffmpeg.** iPhones export HEIC, which Ubuntu's ffmpeg
cannot decode at all. Every photo is therefore decoded through Pillow (with
`pillow-heif` registered) and written out as a plain JPEG before ffmpeg sees it.
That same step applies the EXIF orientation flag, which ffmpeg ignores on still
images — without it roughly half of a phone album ends up on its side.

**File modification time is not a capture date.** Zipping and unpacking rewrite
it, so trusting it would date every file to the moment the archive was unpacked.
Only real capture tags (`DateTimeOriginal`, `CreateDate`, …) count as dates; a
file that has none — a screenshot, a download, an edited copy — inherits a time
between the dated files it sits between in filename order, so it stays in
context instead of piling up at the end.

**Crossfading hundreds of clips takes two passes, not one.** A single ffmpeg
`xfade` chain over 800 inputs exhausts the filter graph, so segments are faded in
groups and the group results faded together. Each pass re-encodes the whole film,
so the group size is widened automatically to keep it at two passes, and the
intermediates are encoded fast and at high quality so only the final pass spends
time on compression. `--transition 0` skips all of this: the segments are
concatenated without re-encoding, which is several times quicker.

## Live Photos

iPhones save a 1–3 second video next to every Live Photo, with the same name as
the still. Left in, those turn a montage into hundreds of near-identical
stutters, so a video is dropped when it shares its name with a photo beside it
and runs no longer than four seconds.
