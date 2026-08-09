# setup

Reproducible setup scripts, one per topic.

| Topic | Script |
| --- | --- |
| [grafify](#grafify) — publication-ready graphs and linear models in R | `setup/install-grafify.sh` |
| [Obsidian in iCloud Drive](#obsidian-in-icloud-drive) — a vault that syncs across devices | `setup/setup-icloud-obsidian.sh` |

## grafify

Reproducible install of [grafify](https://grafify.shenoylab.com/) — the ggplot2
wrapper package for publication-ready graphs and ANOVA / mixed-effects linear
models.

### Install

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

### Two things worth knowing

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

### Usage

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

## Obsidian in iCloud Drive

Sets up an [Obsidian](https://obsidian.md/) vault that lives in iCloud Drive, at

```
~/Library/Mobile Documents/com~apple~CloudDocs/obsidian/
```

```bash
./setup/setup-icloud-obsidian.sh            # create, or adopt, the vault
./setup/setup-icloud-obsidian.sh --mobile   # ...somewhere iPhone and iPad can see it
./setup/setup-icloud-obsidian.sh --check    # report on an existing vault, change nothing
```

macOS only. The script installs Obsidian with Homebrew if it is missing, creates
the vault directory, forces iCloud to download anything it has evicted, and then
reports on the two things below. It never deletes or rewrites a note, and
re-running it is safe.

### Two things worth knowing

**iPhone and iPad cannot open a vault in a plain iCloud Drive folder.** The
mobile apps only list vaults held in Obsidian's own iCloud container — the
folder Finder and Files show as *iCloud Drive → Obsidian*, which on disk is:

```
~/Library/Mobile Documents/iCloud~md~obsidian/Documents/
```

A vault under `com~apple~CloudDocs/obsidian/` syncs between Macs perfectly well,
but it will never appear on a phone. `--mobile` puts the vault in the container
instead. Relocating an existing vault is just moving the folder: quit Obsidian
on every device, wait for sync to settle, drag it across, then open it from the
new location.

**"Optimize Mac Storage" empties notes you have not touched lately.** When
iCloud reclaims disk space it replaces a file's contents with a hidden
`.<name>.icloud` placeholder. Obsidian does not know what those are, so the
notes go missing from search or open blank. The script runs `brctl download`
across the vault and lists any placeholders still standing afterwards —
downloads happen in the background, so re-run with `--check` a minute later. The
durable fix is System Settings → *[your name]* → iCloud → iCloud Drive → turn
**Optimize Mac Storage** off.

### Conflicts

iCloud does not merge. When two devices edit a note before syncing, it keeps
both sides and renames one to `note 2.md`. `--check` lists files matching that
shape, though names that legitimately end in a digit match too, so read the list
before deleting anything. Two habits prevent most conflicts:

- Let sync finish before opening the vault on a second device. The iCloud Drive
  entry in Finder's sidebar shows progress.
- Expect `.obsidian/workspace.json` to conflict anyway — it stores the current
  pane layout, and every device rewrites it constantly. Losing a copy costs
  nothing.

iCloud is also unhurried with large numbers of small files, which is precisely
the shape of a mature vault. If latency rather than conflicts becomes the
problem, Obsidian Sync and a git remote are the usual alternatives.
