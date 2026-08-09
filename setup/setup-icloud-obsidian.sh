#!/usr/bin/env bash
#
# Set up, or check, an Obsidian vault stored in iCloud Drive on macOS.
#
# The default location is a plain folder in iCloud Drive:
#
#   ~/Library/Mobile Documents/com~apple~CloudDocs/obsidian/
#
# That syncs between Macs, but the Obsidian apps on iPhone and iPad cannot
# open it -- they only list vaults inside Obsidian's own iCloud container.
# Pass --mobile to use that container instead:
#
#   ~/Library/Mobile Documents/iCloud~md~obsidian/Documents/obsidian/
#
# The script is idempotent and never deletes or rewrites notes: it creates the
# vault directory, forces iCloud to download anything it has evicted, and then
# reports on the two things that quietly break iCloud-hosted vaults -- files
# left as .icloud placeholder stubs, and sync conflict copies.
#
# Written for bash 3.2, the version macOS ships.
#
set -euo pipefail

ICLOUD_DRIVE="$HOME/Library/Mobile Documents/com~apple~CloudDocs"
OBSIDIAN_CONTAINER="$HOME/Library/Mobile Documents/iCloud~md~obsidian/Documents"

log()  { printf '\n==> %s\n' "$*"; }
note() { printf '    %s\n' "$*"; }
warn() { printf '    warning: %s\n' "$*" >&2; }
die()  { printf 'error: %s\n' "$*" >&2; exit 1; }

usage() {
  cat <<'EOF'
Usage: setup/setup-icloud-obsidian.sh [options] [vault-path]

Set up, or check, an Obsidian vault stored in iCloud Drive on macOS.

Options:
  --mobile     put the vault in Obsidian's own iCloud container, so that the
               iOS and iPadOS apps can open it as well as macOS
  --check      report on an existing vault; change nothing
  --open       open the vault in Obsidian when finished
  -h, --help   show this message

With no vault-path the vault is

  ~/Library/Mobile Documents/com~apple~CloudDocs/obsidian

or, with --mobile,

  ~/Library/Mobile Documents/iCloud~md~obsidian/Documents/obsidian

An explicit vault-path overrides both.
EOF
}

MOBILE=0
CHECK_ONLY=0
OPEN_VAULT=0
VAULT=""

while [ $# -gt 0 ]; do
  case "$1" in
    --mobile)  MOBILE=1 ;;
    --check)   CHECK_ONLY=1 ;;
    --open)    OPEN_VAULT=1 ;;
    -h|--help) usage; exit 0 ;;
    -*)        usage >&2; die "unknown option: $1" ;;
    *)
      [ -z "$VAULT" ] || die "more than one vault path given"
      VAULT="$1"
      ;;
  esac
  shift
done

[ "$(uname -s)" = "Darwin" ] ||
  die "this only runs on macOS; iCloud Drive lives under ~/Library/Mobile Documents"

if [ -z "$VAULT" ]; then
  if [ "$MOBILE" -eq 1 ]; then
    VAULT="$OBSIDIAN_CONTAINER/obsidian"
  else
    VAULT="$ICLOUD_DRIVE/obsidian"
  fi
fi

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

# Percent-encode $1 for use in an obsidian:// URL. Byte-wise, so that a vault
# path containing non-ASCII characters survives.
urlencode() {
  local LC_ALL=C
  local s="$1" out="" i c
  for (( i = 0; i < ${#s}; i++ )); do
    c="${s:i:1}"
    case "$c" in
      [a-zA-Z0-9._~-]) out="$out$c" ;;
      *) printf -v c '%%%02X' "'$c"; out="$out$c" ;;
    esac
  done
  printf '%s' "$out"
}

count() { wc -l < "$1" | tr -d ' '; }

# Print at most 10 paths from the file $1, relative to the vault.
sample() {
  local n=0 line
  while IFS= read -r line; do
    n=$(( n + 1 ))
    if [ "$n" -gt 10 ]; then
      note "... and $(( $(count "$1") - 10 )) more"
      break
    fi
    note "  ${line#"$VAULT"/}"
  done < "$1"
}

# ---------------------------------------------------------------- iCloud Drive

[ -d "$ICLOUD_DRIVE" ] ||
  die "iCloud Drive is not enabled on this Mac ($ICLOUD_DRIVE does not exist).
       Turn it on in System Settings > [your name] > iCloud > iCloud Drive."

case "$VAULT" in
  "$OBSIDIAN_CONTAINER"/*) IN_CONTAINER=1 ;;
  *)                       IN_CONTAINER=0 ;;
esac

if [ "$IN_CONTAINER" -eq 1 ] && [ ! -d "$OBSIDIAN_CONTAINER" ] && [ "$CHECK_ONLY" -eq 0 ]; then
  mkdir -p "$OBSIDIAN_CONTAINER" 2>/dev/null ||
    die "cannot create Obsidian's iCloud container at $OBSIDIAN_CONTAINER.
       Launch Obsidian once (on this Mac, or on an iPhone or iPad signed in to
       the same Apple Account) so that macOS provisions it, then re-run this."
fi

# --------------------------------------------------------------- the vault dir

if [ "$CHECK_ONLY" -eq 1 ]; then
  [ -d "$VAULT" ] || die "no such vault: $VAULT"
  log "Checking $VAULT"
else
  if [ -d "$VAULT" ]; then
    log "Vault already exists at $VAULT"
  else
    log "Creating vault at $VAULT"
    mkdir -p "$VAULT"
  fi
  # Marks the folder as a vault. Obsidian fills this directory in on first
  # launch; iCloud does not reliably sync empty directories, so do not expect
  # it to appear on other devices before then.
  mkdir -p "$VAULT/.obsidian"
fi

# ------------------------------------------------------------------- Obsidian

if [ "$CHECK_ONLY" -eq 0 ]; then
  if [ -d "/Applications/Obsidian.app" ] || [ -d "$HOME/Applications/Obsidian.app" ]; then
    log "Obsidian is already installed"
  elif command -v brew >/dev/null 2>&1; then
    log "Installing Obsidian with Homebrew"
    brew install --cask obsidian
  else
    log "Obsidian is not installed"
    warn "Homebrew is not available either; download Obsidian from https://obsidian.md/download"
  fi
fi

# ------------------------------------------------------- materialise the files

# With "Optimize Mac Storage" on, iCloud evicts the contents of files it thinks
# you are not using and leaves a hidden .<name>.icloud placeholder behind.
# Obsidian cannot read those: the notes look empty, or vanish from search.
if [ "$CHECK_ONLY" -eq 0 ]; then
  log "Asking iCloud to download everything in the vault"
  if command -v brctl >/dev/null 2>&1; then
    brctl download "$VAULT" 2>/dev/null || warn "brctl download did not succeed"
  else
    warn "brctl is not available, so evicted files cannot be pulled down here"
  fi
fi

# ----------------------------------------------------------------- diagnostics

log "Vault report"

find "$VAULT" -type f -name '*.icloud'  > "$WORKDIR/stubs"   2>/dev/null || true
find "$VAULT" -type f -name '.DS_Store' > "$WORKDIR/dsstore" 2>/dev/null || true
find "$VAULT" -type f -name '*.md'      > "$WORKDIR/notes"   2>/dev/null || true
find "$VAULT" -type f \
  \( -name '* [0-9].*' -o -name '* [0-9][0-9].*' \) > "$WORKDIR/conflicts" 2>/dev/null || true

note "path:      $VAULT"
note "notes:     $(count "$WORKDIR/notes") markdown files"
note "size:      $(du -sh "$VAULT" 2>/dev/null | cut -f1)"

if [ "$IN_CONTAINER" -eq 1 ]; then
  note "mobile:    yes -- this vault is in Obsidian's iCloud container, so the"
  note "           iOS and iPadOS apps can open it"
else
  note "mobile:    no -- this is a plain iCloud Drive folder. It syncs between"
  note "           Macs, but Obsidian on iPhone and iPad will not list it."
  note "           Re-run with --mobile for a vault those apps can open."
fi

STUBS="$(count "$WORKDIR/stubs")"
if [ "$STUBS" -gt 0 ]; then
  warn "$STUBS file(s) are still iCloud placeholders, not real content."
  note "brctl download works in the background, so give it a minute and re-run"
  note "with --check. If they persist, turn off System Settings > [your name] >"
  note "iCloud > iCloud Drive > Optimize Mac Storage."
  sample "$WORKDIR/stubs"
fi

CONFLICTS="$(count "$WORKDIR/conflicts")"
if [ "$CONFLICTS" -gt 0 ]; then
  warn "$CONFLICTS file(s) look like iCloud conflict copies."
  note 'iCloud resolves a conflict by keeping both sides, naming the loser'
  note '"note 2.md". Names that legitimately end in a number match too, so'
  note 'check before deleting anything.'
  sample "$WORKDIR/conflicts"
fi

DSSTORE="$(count "$WORKDIR/dsstore")"
if [ "$DSSTORE" -gt 0 ]; then
  note "$DSSTORE .DS_Store file(s) are syncing along with the notes; harmless."
fi

if [ "$STUBS" -eq 0 ] && [ "$CONFLICTS" -eq 0 ]; then
  note "no placeholder stubs and no conflict copies"
fi

# ------------------------------------------------------------------- finishing

URL="obsidian://open?path=$(urlencode "$VAULT")"

if [ "$OPEN_VAULT" -eq 1 ]; then
  log "Opening the vault in Obsidian"
  open "$URL"
else
  log "Open the vault with"
  note "open '$URL'"
  note "Opening it once adds it to Obsidian's vault switcher."
fi
