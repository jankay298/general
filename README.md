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
