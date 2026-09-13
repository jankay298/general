# general

Small, reproducible tools.

| Tool | What it does |
| --- | --- |
| [`bin/make-video`](#make-video) | Turn a folder of photos and videos into one video |
| [`setup/install-grafify.sh`](#grafify-setup) | Install R and grafify for publication-ready graphs |

---

# make-video

Points at a folder, orders everything by the time it was taken, gives every
photo a slow Ken Burns move and every video a trimmed clip, cross-fades between
them, and lays music underneath.

```bash
./setup/install-video-deps.sh                       # ffmpeg and friends, once

./bin/make-video ~/Pictures/holiday -r --plan       # look at the running order
./bin/make-video ~/Pictures/holiday -r \
    --target-duration 8:00 --music ~/Music/bed.mp3 \
    --title "Sommer 2024" -o holiday.mp4
```

Everything happens on your own machine — nothing is uploaded anywhere. Only
ffmpeg and ffprobe are required, and there are no Python packages to install.

## Start with `--plan`

`--plan` prints the whole running order and the total length without encoding a
single frame. It takes seconds, and it is the cheapest way to find out that half
your videos sort into 1970 because they lost their metadata.

```
    1      0:00    3.5s  photo  2024-07-02 10:14  IMG_0421.HEIC
    2      0:03    3.5s  photo  2024-07-02 10:15  IMG_0422.HEIC
    3      0:06    8.2s  video  2024-07-02 11:02  IMG_0431.MOV
```

## 800 photos is 45 minutes, and nobody watches that

At the default 3.5 seconds per photo, 820 items run about three quarters of an
hour. Two options, and they combine:

```bash
--target-duration 8:00     # thin the selection out until it fits 8 minutes
--photo-duration 2.5       # or just hold each photo for less time
```

`--target-duration` keeps the selection spread evenly over the whole period, so
a two-week holiday still reads as a two-week holiday — it does not simply cut
the tail off. `--limit 200` does the same by count instead of by time.

To cut by hand instead, `--since` / `--until` narrow the date range, and
`--photos-only` / `--videos-only` drop one kind entirely.

## Options worth knowing

| Option | Default | What it does |
| --- | --- | --- |
| `-r`, `--recursive` | off | also search subfolders |
| `--target-duration M:SS` | — | thin the selection out to this running time |
| `--photo-duration SEC` | 3.5 | how long a photo stays on screen |
| `--max-clip SEC` | 10 | longest piece taken out of one video |
| `--transition SEC` | 0.6 | cross-fade length |
| `--size` | 1080p | `720p`, `4k`, `portrait`, `square` or `1920x1080` |
| `--fit` | blur | how off-shape material fills the frame: `blur`, `pad`, `crop` |
| `--zoom` / `--no-motion` | 1.10 | Ken Burns strength, or hold the photos still |
| `--music PATH` | — | a file or a folder of them; looped and faded to fit |
| `--duck` | off | dip the music whenever a video has its own sound |
| `--title` / `--end-title` | — | opening and closing cards |
| `--order` | date | `date`, `name` or `shuffle` |
| `--jobs` | cores | how many clips to render at once |

`--help` lists the rest, including quality and encoder settings.

## Two things worth knowing

**Portrait photos do not have to mean black bars.** `--fit blur` (the default)
puts a blurred, darkened copy of the same picture behind it, so a phone photo
fills a 16:9 frame without being cropped. `--fit crop` fills the frame by
cutting the edges off, `--fit pad` gives you the plain black bars. If most of
the material is from a phone, `--size portrait` is usually the better answer
than any of them.

**Capture dates come from several places, and one of them is a guess.**
exiftool is asked first, then the file's own EXIF block, then a date in the
filename, and only then the file's modification time — which is when the file
was *copied*, not when the picture was taken, and is wrong as often as not. The
run prints how many files ended up on that last fallback; if the number is
large, `--plan` will show you the damage before you spend an hour encoding.

## How it works

Every photo and every video clip is first rendered on its own, in parallel, to
an intermediate file with identical resolution, frame rate and audio format.
Those are then cross-faded together in groups of 25, the groups are joined
without re-encoding, and the music is mixed in at the very end. The video is
therefore encoded exactly twice, whatever the number of input files, and one
enormous ffmpeg filter graph — which is where the naive version of this falls
over at a few hundred inputs — is never built.

The one visible cost is that the borders between groups are hard cuts rather
than cross-fades: 33 of them in an 820-clip film, each where a cut would have
been perfectly normal anyway. `--group-size 0` cross-fades everything in a
single pass if you would rather not have them, at the price of a filter graph
with one input per clip.

Rendered clips are cached next to the output and keyed by content, so a run that
is interrupted — or repeated with different music — picks up where it left off
instead of starting over. The cache is deleted when the run finishes; `--clean`
empties it first, `--keep-cache` keeps it.

## Practicalities

- **HEIC**: most ffmpeg builds cannot decode iPhone photos. `make-video`
  notices, converts them once with `heif-convert` or ImageMagick, and caches the
  result. `setup/install-video-deps.sh` installs one of them.
- **Disk**: budget roughly 10 GB of free space for a 45-minute film. The cache
  is the bulk of it and is deleted at the end.
- **Time**: on a 4-core machine the test material rendered at about 1.5× the
  finished running time with `--preset veryfast`; the default `--preset medium`
  is slower and smaller. `--jobs` defaults to the core count.
- **Rotation**: photos are read with `-noautorotate` and straightened from their
  own EXIF tag, because whether ffmpeg does this for you changed between
  versions (6.1 does, 5.x does not) and doing it twice puts them on their side.

---

# grafify setup

Reproducible install of [grafify](https://grafify.shenoylab.com/) — the ggplot2
wrapper package for publication-ready graphs and ANOVA / mixed-effects linear
models.

## Install

```bash
./setup/install-grafify.sh
```

Tested on Ubuntu 24.04 (noble). The script installs R plus grafify's
dependencies, then grafify itself, and finishes by drawing a graph and fitting a
model to prove the installation works.

Installed by the script:

| Package | Version | Source |
| --- | --- | --- |
| R | 4.3.3 | apt (`r-base-core`) |
| grafify | 5.1.0 | CRAN, falling back to GitHub |
| ggplot2 | 3.5.2 | CRAN, falling back to GitHub |
| car, dplyr, emmeans, Hmisc, lme4, lmerTest, magrittr, mgcv, patchwork, purrr, tidyr | Ubuntu versions | apt (`r-cran-*`) |

## Two things worth knowing

**ggplot2 must be 3.5.0 or newer.** grafify's `DESCRIPTION` declares
`ggplot2 (>= 3.4.0)`, but from version 5.0.0 its palette functions call
`discrete_scale()` using the signature introduced in ggplot2 3.5.0. Ubuntu 24.04
ships ggplot2 3.4.4, so every grafify plot fails on a stock install with:

```
Error in discrete_scale("fill", palette = pal, ...) :
  argument "scale_name" is missing, with no default
```

The script therefore upgrades ggplot2 when the installed version is too old.

**Dependencies come from apt, not CRAN.** Every one of grafify's imports is
packaged for Ubuntu as `r-cran-*`, so the script installs those binaries instead
of compiling `lme4`, `Hmisc` and friends from source — minutes instead of the
better part of an hour. Only ggplot2 and grafify are installed as R packages.

If no CRAN mirror is reachable — common in sandboxed or proxied environments
that permit github.com but not `cloud.r-project.org` — the script falls back to a
shallow clone of each package's upstream repository at a pinned tag.

## Usage

Plotting functions take bare column names; the model functions take them as
strings.

```r
library(grafify)

# Scatter + bar with SD error bars, in a colourblind-friendly palette
plot_scatterbar_sd(data_cholesterol, Treatment, Cholesterol)

# One-way ANOVA as an ordinary linear model
simple_anova(data_cholesterol, "Cholesterol", "Treatment")

# Mixed-effects model with a random factor, then post-hoc comparisons
mod <- mixed_model(data_doubling_time, "Doubling_time", "Student", "Experiment")
posthoc_Pairwise(mod, "Student")
```

grafify ships 8 practice datasets (`data_cholesterol`, `data_doubling_time`,
…) and 60 exported functions. Run `help(package = "grafify")` for the full list.
