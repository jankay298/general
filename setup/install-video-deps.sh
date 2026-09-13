#!/usr/bin/env bash
#
# Install what bin/make-video needs.
#
#   ffmpeg + ffprobe   required -- all the decoding, scaling and encoding
#   exiftool           optional -- better capture dates, so holiday photos from
#                                  two cameras still end up in the right order
#   heif-convert       optional -- iPhone HEIC photos; most ffmpeg builds
#                                  cannot decode them on their own
#   DejaVu fonts       optional -- only needed for --title / --end-title
#
# Usage: ./setup/install-video-deps.sh
#
set -euo pipefail

SUDO=""
if [ "$(id -u)" -ne 0 ] && command -v sudo >/dev/null 2>&1; then
  SUDO="sudo"
fi

log() { printf '\n==> %s\n' "$*"; }

if command -v apt-get >/dev/null 2>&1; then
  log "Installing with apt"
  export DEBIAN_FRONTEND=noninteractive
  $SUDO apt-get update -qq
  $SUDO apt-get install -y ffmpeg libimage-exiftool-perl libheif-examples fonts-dejavu-core
elif command -v dnf >/dev/null 2>&1; then
  log "Installing with dnf"
  $SUDO dnf install -y ffmpeg-free perl-Image-ExifTool libheif-tools dejavu-sans-fonts
elif command -v pacman >/dev/null 2>&1; then
  log "Installing with pacman"
  $SUDO pacman -Sy --needed --noconfirm ffmpeg perl-image-exiftool libheif ttf-dejavu
elif command -v zypper >/dev/null 2>&1; then
  log "Installing with zypper"
  $SUDO zypper --non-interactive install ffmpeg exiftool libheif-tools dejavu-fonts
elif command -v brew >/dev/null 2>&1; then
  log "Installing with Homebrew"
  brew install ffmpeg exiftool libheif
else
  cat >&2 <<'MSG'
No supported package manager found.

  macOS    install Homebrew from https://brew.sh, then: brew install ffmpeg exiftool libheif
  Windows  winget install Gyan.FFmpeg  (make-video itself runs under WSL or Git Bash)
  other    install ffmpeg and exiftool with whatever your distribution uses
MSG
  exit 1
fi

log "Checking the result"
missing=0
for tool in ffmpeg ffprobe; do
  if command -v "$tool" >/dev/null 2>&1; then
    printf '  %-14s %s\n' "$tool" "$("$tool" -version | head -1 | cut -d' ' -f1-3)"
  else
    printf '  %-14s MISSING (required)\n' "$tool"
    missing=1
  fi
done
for tool in exiftool heif-convert; do
  if command -v "$tool" >/dev/null 2>&1; then
    printf '  %-14s installed\n' "$tool"
  else
    printf '  %-14s not installed (optional)\n' "$tool"
  fi
done

# libx264 does the encoding; a build without it cannot produce the video.
if ! ffmpeg -hide_banner -encoders 2>/dev/null | grep -q ' libx264'; then
  echo "  WARNING: this ffmpeg has no libx264 encoder -- make-video needs it" >&2
  missing=1
fi

[ "$missing" -eq 0 ] || exit 1

log "Ready. Try: ./bin/make-video ~/Pictures --plan"
