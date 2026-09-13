#!/usr/bin/env bash
#
# Fetch a large archive from a share link so it can be fed to make-montage.py.
#
# Handles the two link formats that need rewriting before a downloader can touch
# them (Dropbox share pages and Google Drive file ids), resumes a transfer that
# was cut off rather than starting again from zero, and refuses to start when
# there is not enough disk space for the file plus its unpacked contents.
#
# Usage: ./montage/fetch-archive.sh <share-url> [output-file]
#
set -euo pipefail

URL="${1:?usage: fetch-archive.sh <share-url> [output-file]}"
OUT="${2:-}"

log() { printf '\n==> %s\n' "$*"; }

# Unpacking needs roughly as much room as the archive itself, and the render
# needs room for the segments on top, so insist on three times the size.
require_space() {
  local need_bytes="$1" dir avail
  dir="$(dirname "$(readlink -f "${OUT}")")"
  avail=$(df -PB1 "$dir" | awk 'NR==2 {print $4}')
  if [ "$need_bytes" -gt 0 ] && [ "$((need_bytes * 3))" -gt "$avail" ]; then
    printf 'Not enough space: archive is %s, only %s free (need ~%s for unpacking and rendering).\n' \
      "$(numfmt --to=iec "$need_bytes")" "$(numfmt --to=iec "$avail")" \
      "$(numfmt --to=iec "$((need_bytes * 3))")" >&2
    exit 1
  fi
}

case "$URL" in
  *drive.google.com*|*docs.google.com*)
    # Drive serves an HTML interstitial for anything large enough to skip the
    # virus scan, so a plain GET saves the warning page instead of the file.
    [ -n "$OUT" ] || OUT="lissabon.zip"
    log "Google Drive link; downloading with gdown"
    # gdown 6 dropped --fuzzy (it parses share URLs by default) and errors out on
    # the flag, so passing it fails before a byte is transferred.
    python3 -m gdown --continue -O "$OUT" "$URL"
    ;;
  *)
    if [[ "$URL" == *dropbox.com* ]]; then
      # A Dropbox share link renders a preview page unless dl=1 is forced.
      URL="${URL/&dl=0/&dl=1}"; URL="${URL/\?dl=0/?dl=1}"
      [[ "$URL" == *dl=1* ]] || URL="${URL}$([[ "$URL" == *\?* ]] && echo "&dl=1" || echo "?dl=1")"
      log "Dropbox link; forcing a direct download"
    fi
    if [ -z "$OUT" ]; then
      OUT="$(basename "${URL%%\?*}")"
      [ -n "$OUT" ] && [ "$OUT" != "/" ] || OUT="lissabon.zip"
    fi

    # Check reachability first: a link that is not shared publicly returns 401/403
    # and will never start working, so failing immediately with the real reason
    # beats five rounds of backoff ending in "download failed".
    status=$(curl -sSIL -o /dev/null -w '%{http_code}' --max-time 60 "$URL" 2>/dev/null || echo 000)
    case "$status" in
      401|403)
        echo "The server refused access (HTTP $status)." >&2
        echo "The share link is probably not set to 'anyone with the link can view'." >&2
        exit 1 ;;
      404)
        echo "Nothing at that link (HTTP 404) -- it may have expired." >&2
        exit 1 ;;
    esac

    size=$(curl -sSIL --max-time 60 "$URL" 2>/dev/null \
             | awk 'BEGIN{IGNORECASE=1} /^content-length:/ {v=$2} END{gsub(/\r/,"",v); print v+0}')
    if [ "${size:-0}" -gt 0 ]; then
      log "Downloading $(numfmt --to=iec "$size") to $OUT"
      require_space "$size"
    else
      log "Downloading to $OUT (server did not report a size)"
    fi

    # Five attempts with -C -: a multi-gigabyte transfer that drops resumes from
    # where it stopped instead of discarding what already arrived.
    delay=2
    for attempt in 1 2 3 4 5; do
      rc=0
      curl -fSL --retry 3 --retry-delay 2 -C - --progress-bar "$URL" -o "$OUT" || rc=$?
      [ "$rc" = 0 ] && break
      # 22 is an HTTP error status. Retrying one only burns time: the link is
      # wrong or private, not flaky. Everything else (18 partial, 28 timeout,
      # 56 recv error) is worth another go with -C - picking up where it stopped.
      if [ "$rc" = 22 ]; then
        echo "The server rejected the request -- check that the link is public and points" >&2
        echo "directly at the file." >&2
        exit 1
      fi
      if [ "$attempt" = 5 ]; then
        echo "Download failed after 5 attempts (curl exit $rc)." >&2
        exit 1
      fi
      log "Transfer interrupted (curl exit $rc); resuming in ${delay}s"
      sleep "$delay"
      delay=$((delay * 2))
    done
    ;;
esac

log "Saved $OUT ($(du -h "$OUT" | cut -f1))"
if file -b "$OUT" | grep -qi "html"; then
  echo "Warning: this looks like a web page, not an archive -- the link probably needs" >&2
  echo "'anyone with the link' sharing enabled." >&2
  exit 1
fi
# A readable zip is reported, but anything else (a tar, a folder exported as
# tar.gz) is still a perfectly good input -- so this never decides the exit
# status, which belongs to the download alone.
if unzip -l "$OUT" >/dev/null 2>&1; then
  log "Archive is readable: $(unzip -l "$OUT" | tail -1 | tr -s ' ')"
else
  log "Not a zip archive -- make-montage.py takes a folder or zip, so unpack it first if needed"
fi

exit 0
