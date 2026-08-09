#!/usr/bin/env bash
#
# Install R and the grafify package (https://grafify.shenoylab.com/).
#
# Tested on Ubuntu 24.04 (noble) with R 4.3.3.
#
# grafify's dependencies are all available as Debian/Ubuntu binary packages,
# so they are installed with apt rather than compiled from source -- this takes
# minutes instead of an hour.
#
# The two packages that apt cannot supply at a new enough version (ggplot2 and
# grafify itself) are installed from CRAN when a CRAN mirror is reachable, and
# from their upstream git repositories otherwise. The git fallback exists
# because sandboxed/proxied environments frequently allow github.com while
# blocking CRAN mirrors.
#
# Usage: ./setup/install-grafify.sh
#
set -euo pipefail

# grafify >= 5.0 calls discrete_scale() with the signature introduced in
# ggplot2 3.5.0, so 3.4.x (what Ubuntu 24.04 ships) is not sufficient even
# though grafify's DESCRIPTION only declares ggplot2 >= 3.4.0.
GGPLOT2_MIN_VERSION="3.5.0"
GGPLOT2_TAG="v3.5.2"
GRAFIFY_TAG="v5.1.0"

SUDO=""
if [ "$(id -u)" -ne 0 ]; then
  SUDO="sudo"
fi

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

log() { printf '\n==> %s\n' "$*"; }

log "Installing R and grafify's dependencies from apt"
export DEBIAN_FRONTEND=noninteractive
$SUDO apt-get update -qq
$SUDO apt-get install -y -qq --no-install-recommends \
  ca-certificates \
  git \
  r-base-core \
  r-base-dev \
  r-cran-car \
  r-cran-dplyr \
  r-cran-emmeans \
  r-cran-ggplot2 \
  r-cran-hmisc \
  r-cran-lme4 \
  r-cran-lmertest \
  r-cran-magrittr \
  r-cran-mgcv \
  r-cran-patchwork \
  r-cran-purrr \
  r-cran-tidyr

# Print the installed version of an R package, or nothing if it is absent.
pkg_version() {
  Rscript -e "cat(tryCatch(as.character(packageVersion('$1')), error = function(e) ''))"
}

# True when the installed version of $1 is at least $2.
pkg_at_least() {
  Rscript -e "q(status = if (tryCatch(packageVersion('$1') >= '$2', error = function(e) FALSE)) 0L else 1L)"
}

# install_pkg <cran-name> <github-owner/repo> <git-tag>
#
# Try CRAN first so the package comes from the canonical source, then fall back
# to a shallow git clone of the upstream repository at a known-good tag.
install_pkg() {
  local cran_name="$1" gh_repo="$2" git_tag="$3"

  log "Installing $cran_name from CRAN"
  # install.packages() only warns when a repository is unreachable, so promote
  # warnings to errors to make an unreachable mirror a non-zero exit status.
  if $SUDO Rscript -e "options(warn = 2); install.packages('$cran_name', repos = 'https://cloud.r-project.org')"; then
    return 0
  fi

  log "CRAN unavailable; installing $cran_name from github.com/$gh_repo@$git_tag"
  git clone --depth 1 --branch "$git_tag" "https://github.com/$gh_repo.git" \
    "$WORKDIR/$cran_name"
  $SUDO R CMD INSTALL --no-staged-install "$WORKDIR/$cran_name"
}

if pkg_at_least ggplot2 "$GGPLOT2_MIN_VERSION"; then
  log "ggplot2 $(pkg_version ggplot2) already satisfies >= $GGPLOT2_MIN_VERSION"
else
  log "ggplot2 $(pkg_version ggplot2) is older than $GGPLOT2_MIN_VERSION, upgrading"
  install_pkg ggplot2 tidyverse/ggplot2 "$GGPLOT2_TAG"
fi

install_pkg grafify ashenoy-cmbi/grafify "$GRAFIFY_TAG"

log "Verifying the installation"
Rscript -e '
  suppressPackageStartupMessages(library(grafify))
  cat(R.version.string, "\n")
  cat("ggplot2:", as.character(packageVersion("ggplot2")), "\n")
  cat("grafify:", as.character(packageVersion("grafify")), "\n")

  # Draw a real graph and fit a real model, so that a missing dependency or an
  # incompatible ggplot2 fails here rather than in the user first session.
  p <- plot_scatterbar_sd(data_cholesterol, Treatment, Cholesterol)
  out <- file.path(tempdir(), "grafify-check.png")
  ggplot2::ggsave(out, p, width = 6, height = 4, dpi = 110)
  stopifnot(file.exists(out))

  # Plotting functions take bare column names; the model functions take them
  # as strings.
  invisible(simple_anova(data_cholesterol, "Cholesterol", "Treatment"))
  cat("\ngrafify is installed and working.\n")
'
