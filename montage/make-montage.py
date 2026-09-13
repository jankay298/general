#!/usr/bin/env python3
"""Build a chronological montage video from a folder or zip archive of photos and videos.

The archive is unpacked, every photo and video inside it is ordered by the time it
was taken, and each one is rendered into a segment of identical resolution, frame
rate and pixel format. The segments are then joined -- with crossfades when asked
for -- and an optional music track is laid over the result.

Photos are normalised through Pillow first (EXIF rotation applied, HEIC decoded,
oversized images shrunk), which is what makes iPhone exports work at all: ffmpeg
on Ubuntu cannot decode HEIC, and it ignores the EXIF orientation flag on JPEGs.

Usage:
    ./montage/make-montage.py --input Lissabon.zip --output lissabon.mp4
    ./montage/make-montage.py --input Lissabon.zip --output lissabon.mp4 \
        --max-minutes 6 --music soundtrack.mp3
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import random
import shutil
import subprocess
import sys
import tempfile
import zipfile
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from datetime import datetime, timedelta
from pathlib import Path

try:
    # iPhones export HEIC, which neither ffmpeg nor stock Pillow can read.
    import pillow_heif

    pillow_heif.register_heif_opener()
except ImportError:  # handled per-file, with a clear message, when a HEIC shows up
    pillow_heif = None

PHOTO_SUFFIXES = {".jpg", ".jpeg", ".png", ".heic", ".heif", ".tif", ".tiff", ".webp", ".bmp", ".gif"}
VIDEO_SUFFIXES = {".mp4", ".mov", ".m4v", ".avi", ".mkv", ".3gp", ".mts", ".m2ts", ".webm", ".mpg", ".mpeg"}

# Junk that photo exports are full of: macOS resource forks, Windows thumbnail
# databases, and the sidecar files iPhones write next to an edited photo.
JUNK_NAMES = {".ds_store", "thumbs.db", "desktop.ini"}
JUNK_SUFFIXES = {".aae", ".xmp", ".thm", ".json", ".txt", ".plist"}

# Timestamps are gathered from all of these and the EARLIEST plausible one wins.
# A capture is the first thing that ever happens to a file; exporting, copying and
# zipping only ever stamp it with a later time. On a freshly exported phone album
# CreateDate, MediaCreateDate and TrackCreateDate all read "export day", while the
# true time survives in QuickTime's CreationDate, in DateTimeOriginal, or -- for
# files carrying no capture tag at all -- in the archive's own modification time.
# Taking the earliest picks the real one without having to know which tag was
# rewritten this time.
DATE_TAGS = [
    "SubSecDateTimeOriginal",
    "DateTimeOriginal",
    "CreationDate",
    "CreateDate",
    "MediaCreateDate",
    "TrackCreateDate",
    "FileModifyDate",
]

# Digital cameras did not exist before this, so anything earlier is a broken tag
# (QuickTime's 1904 epoch, or a camera whose clock was never set) rather than a date.
EARLIEST_PLAUSIBLE = datetime(1990, 1, 1)


@dataclass
class Item:
    """One photo or video, with the moment it was taken and its place in the story."""

    path: Path
    kind: str  # "photo" or "video"
    taken: datetime
    dated: bool  # False when the timestamp is a guess rather than real metadata
    duration: float = 0.0  # source duration, videos only


def log(msg: str) -> None:
    print(f"==> {msg}", flush=True)


def run(cmd: list[str], **kwargs) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, check=True, capture_output=True, text=True, **kwargs)


# --------------------------------------------------------------------------- input

def unpack(source: Path, workdir: Path) -> Path:
    """Return a directory holding the media, unpacking source first if it is an archive."""
    if source.is_dir():
        return source
    if not zipfile.is_zipfile(source):
        sys.exit(f"{source} is neither a directory nor a zip archive")

    dest = workdir / "unpacked"
    dest.mkdir(parents=True, exist_ok=True)
    log(f"Unpacking {source.name} ({source.stat().st_size / 1e9:.2f} GB)")
    with zipfile.ZipFile(source) as zf:
        members = [m for m in zf.infolist() if not m.is_dir()]
        zf.extractall(dest, members=members)
        # extractall() stamps every file with the current time, which would erase
        # the only ordering hint undated files have left.
        for member in members:
            try:
                stamp = datetime(*member.date_time).timestamp()
                os.utime(dest / member.filename, (stamp, stamp))
            except (ValueError, OSError):
                pass
    log(f"Unpacked {len(members)} entries")
    return dest


def collect(root: Path) -> tuple[list[Path], list[Path]]:
    """Find every usable photo and video below root, skipping export junk."""
    photos, videos = [], []
    for path in sorted(root.rglob("*")):
        if not path.is_file():
            continue
        # __MACOSX holds AppleDouble copies of every file; they are not media.
        if any(part == "__MACOSX" or part.startswith("._") for part in path.parts):
            continue
        name, suffix = path.name.lower(), path.suffix.lower()
        if name in JUNK_NAMES or suffix in JUNK_SUFFIXES:
            continue
        if suffix in PHOTO_SUFFIXES:
            photos.append(path)
        elif suffix in VIDEO_SUFFIXES:
            videos.append(path)
    return photos, videos


def drop_live_photo_videos(photos: list[Path], videos: list[Path]) -> list[Path]:
    """Drop the 1-3s clips iPhones save beside a Live Photo of the same name.

    Without this a Lisbon album turns into hundreds of near-identical stutters of
    the photo that follows it.
    """
    photo_stems = {(p.parent, p.stem.lower()) for p in photos}
    kept = []
    for v in videos:
        if (v.parent, v.stem.lower()) in photo_stems and probe_duration(v) <= 4.0:
            continue
        kept.append(v)
    dropped = len(videos) - len(kept)
    if dropped:
        log(f"Skipped {dropped} Live Photo clips")
    return kept


# ----------------------------------------------------------------------- metadata

def has_audio(path: Path) -> bool:
    """True when the file carries at least one audio stream."""
    try:
        out = run([
            "ffprobe", "-v", "error", "-select_streams", "a", "-show_entries",
            "stream=index", "-of", "csv=p=0", str(path),
        ]).stdout.strip()
        return bool(out)
    except subprocess.CalledProcessError:
        return False


def probe_duration(path: Path) -> float:
    try:
        out = run([
            "ffprobe", "-v", "error", "-show_entries", "format=duration",
            "-of", "default=noprint_wrappers=1:nokey=1", str(path),
        ]).stdout.strip()
        return float(out)
    except (subprocess.CalledProcessError, ValueError):
        return 0.0


def parse_exif_date(value: str) -> datetime | None:
    """Parse an EXIF timestamp such as '2025:04:18 09:31:07+01:00'."""
    if not value or value.startswith("0000"):
        return None
    text = value.strip()
    # Drop a trailing timezone; the album is one trip, so a single clock is fine.
    for marker in ("+", "-"):
        idx = text.find(marker, 11)
        if idx != -1:
            text = text[:idx]
            break
    text = text.replace("Z", "").strip()
    for fmt in ("%Y:%m:%d %H:%M:%S.%f", "%Y:%m:%d %H:%M:%S", "%Y-%m-%d %H:%M:%S"):
        try:
            return datetime.strptime(text, fmt)
        except ValueError:
            continue
    return None


def read_timestamps(paths: list[Path], workdir: Path) -> dict[Path, tuple[datetime | None, float]]:
    """Read capture time (and video duration) for every path in one exiftool pass."""
    if not paths:
        return {}
    args_file = workdir / "exiftool-args.txt"
    args_file.write_text("\n".join(str(p) for p in paths), encoding="utf-8")

    cmd = ["exiftool", "-j", "-n", "-q", "-fast2", "-Duration"]
    cmd += [f"-{tag}" for tag in DATE_TAGS]
    cmd += ["-@", str(args_file)]
    # exiftool exits non-zero when any single file is unreadable, but still emits
    # JSON for the rest, so the output matters more than the status code.
    proc = subprocess.run(cmd, capture_output=True, text=True)
    try:
        records = json.loads(proc.stdout or "[]")
    except json.JSONDecodeError:
        log("exiftool returned no usable metadata; falling back to file order")
        return {}

    result: dict[Path, tuple[datetime | None, float]] = {}
    for record in records:
        path = Path(record.get("SourceFile", ""))
        stamps = [parse_exif_date(str(record.get(tag, ""))) for tag in DATE_TAGS]
        plausible = [t for t in stamps if t and t >= EARLIEST_PLAUSIBLE]
        taken = min(plausible) if plausible else None
        try:
            duration = float(record.get("Duration", 0) or 0)
        except (TypeError, ValueError):
            duration = 0.0
        result[path] = (taken, duration)
    return result


def infer_missing_dates(items: list[Item]) -> None:
    """Give undated files a time taken from their neighbours in filename order.

    Screenshots, downloads and edited copies often carry no EXIF date. Dropping
    them to the end of the film would scatter them out of context, so each one
    inherits a time between the dated files it sits between on disk -- IMG_0412
    belongs with IMG_0411 and IMG_0413, whatever its metadata says.
    """
    ordered = sorted(items, key=lambda i: (str(i.path.parent), i.path.name))

    # Nearest dated file before each position, then the nearest one after it.
    before: list[datetime | None] = []
    last: datetime | None = None
    for item in ordered:
        before.append(last)
        if item.dated:
            last = item.taken

    after: list[datetime | None] = [None] * len(ordered)
    nxt: datetime | None = None
    for idx in range(len(ordered) - 1, -1, -1):
        after[idx] = nxt
        if ordered[idx].dated:
            nxt = ordered[idx].taken

    for idx, item in enumerate(ordered):
        if item.dated:
            continue
        prev_t, next_t = before[idx], after[idx]
        if prev_t and next_t:
            item.taken = prev_t + (next_t - prev_t) / 2
        elif prev_t:
            item.taken = prev_t + timedelta(seconds=1)
        elif next_t:
            item.taken = next_t - timedelta(seconds=1)
        else:
            item.taken = datetime.fromtimestamp(item.path.stat().st_mtime)


def build_items(photos: list[Path], videos: list[Path], workdir: Path) -> list[Item]:
    """Order every file chronologically, keeping undated files next to their neighbours."""
    meta = read_timestamps(photos + videos, workdir)
    items: list[Item] = []
    for path, kind in [(p, "photo") for p in photos] + [(v, "video") for v in videos]:
        taken, duration = meta.get(path, (None, 0.0))
        if kind == "video" and duration <= 0:
            duration = probe_duration(path)
        items.append(Item(
            path=path,
            kind=kind,
            taken=taken or datetime.fromtimestamp(path.stat().st_mtime),
            dated=taken is not None,
            duration=duration,
        ))

    undated = sum(1 for i in items if not i.dated)
    if undated:
        log(f"{undated} file(s) carried no timestamp at all; placed by filename order")
        infer_missing_dates(items)
    # Sort by name within the same second so burst shots keep their shot order.
    items.sort(key=lambda i: (i.taken, i.path.name))
    return items


def select(
    items: list[Item], max_minutes: float, photo_dur: float, video_max: float, transition: float
) -> list[Item]:
    """Thin the album down to a target runtime, spreading the cuts across the trip."""
    if max_minutes <= 0:
        return items

    def length(item: Item) -> float:
        return photo_dur if item.kind == "photo" else min(item.duration or video_max, video_max)

    # Every clip but the first overlaps its predecessor by the crossfade, so it
    # only adds length - transition to the film. Ignoring that overshoots the
    # count and lands a "six minute" film at barely five.
    def cost(item: Item) -> float:
        return max(0.1, length(item) - transition)

    budget = max_minutes * 60 - transition
    total = sum(cost(i) for i in items)
    if total <= budget:
        return items

    # Keep every n-th item so the result still runs from the first morning to the
    # last evening rather than stopping halfway through the trip.
    keep = max(1, int(len(items) * budget / total))
    step = len(items) / keep
    chosen = [items[min(len(items) - 1, int(i * step))] for i in range(keep)]
    runtime = sum(cost(i) for i in chosen) + transition
    log(f"Trimmed {len(items)} clips to {len(chosen)}: about {runtime / 60:.1f} minutes of film")
    return chosen



# --------------------------------------------------------------------- duplicates

def perceptual_hash(path: Path, size: int = 8) -> int | None:
    """A 64-bit difference hash: brightness compared left-to-right across a grid.

    Robust to the re-compression that a shared or re-imported copy goes through,
    which is exactly what byte-level comparison misses -- those files differ in
    every byte while showing the identical picture.
    """
    from PIL import Image

    try:
        with Image.open(path) as img:
            small = img.convert("L").resize((size + 1, size), Image.LANCZOS)
            px = list(small.tobytes())
    except Exception:  # noqa: BLE001 - an unreadable file simply is not compared
        return None
    bits = 0
    for row in range(size):
        base = row * (size + 1)
        for col in range(size):
            bits = (bits << 1) | int(px[base + col] > px[base + col + 1])
    return bits


def drop_duplicates(items: list[Item], jobs: int, threshold: int) -> list[Item]:
    """Remove re-imported copies and near-identical frames, keeping one of each.

    Two things produce doubles in a phone album: a copy re-imported beside the
    original ("IMG_9442 2.MP4"), and bursts of nearly the same shot. Both would
    otherwise play twice in a row.
    """
    import re

    names = {i.path.name for i in items}
    kept: list[Item] = []
    copies = 0
    for item in items:
        # Apple appends " 2" to a file re-imported next to one of the same name.
        original = re.sub(r" \d+(\.[A-Za-z0-9]+)$", r"\1", item.path.name)
        if original != item.path.name and original in names:
            copies += 1
            continue
        kept.append(item)
    if copies:
        log(f"Dropped {copies} re-imported copies")

    photos = [i for i in kept if i.kind == "photo"]
    if threshold < 0 or len(photos) < 2:
        return kept

    with ThreadPoolExecutor(max_workers=jobs) as pool:
        hashes = list(pool.map(lambda i: perceptual_hash(i.path), photos))

    # Compare each photo with the recent ones only: near-duplicates are bursts
    # taken seconds apart, and this keeps an 800-photo album from turning into
    # 320,000 comparisons.
    window = 12
    drop: set[int] = set()
    for idx in range(len(photos)):
        if hashes[idx] is None or idx in drop:
            continue
        for other in range(idx + 1, min(idx + 1 + window, len(photos))):
            if hashes[other] is None or other in drop:
                continue
            if bin(hashes[idx] ^ hashes[other]).count("1") <= threshold:
                # Keep whichever file is larger; a re-compressed copy is smaller
                # than the original it was made from.
                loser = other if photos[other].path.stat().st_size <= photos[idx].path.stat().st_size else idx
                drop.add(loser)
                if loser == idx:
                    break
    if drop:
        log(f"Dropped {len(drop)} near-identical photos")
    dropped_paths = {photos[i].path for i in drop}
    return [i for i in kept if i.path not in dropped_paths]


# ------------------------------------------------------------------------ rendering

def prepare_photo(item: Item, cache: Path, width: int, height: int) -> Path | None:
    """Decode a photo to a plain JPEG that ffmpeg can read, upright and right-sized.

    This is where HEIC is decoded and the EXIF orientation flag is baked into the
    pixels -- ffmpeg does neither for still images.
    """
    from PIL import Image, ImageOps

    digest = hashlib.md5(str(item.path).encode("utf-8")).hexdigest()[:16]
    out = cache / f"{digest}.jpg"
    if out.exists():
        return out
    try:
        with Image.open(item.path) as img:
            img = ImageOps.exif_transpose(img)
            if img.mode not in ("RGB", "L"):
                img = img.convert("RGB")
            # Twice the output size is enough headroom for the Ken Burns zoom, and
            # keeps a 48MP photo from costing a second of scaling per frame.
            img.thumbnail((width * 2, height * 2), Image.LANCZOS)
            img.save(out, "JPEG", quality=95, subsampling=0)
        return out
    except Exception as exc:  # noqa: BLE001 - one bad file must not stop the album
        log(f"Skipping unreadable photo {item.path.name}: {exc}")
        return None


def fit_filter(width: int, height: int, fit: str) -> str:
    """Filter chain that puts any aspect ratio onto the output canvas.

    Portrait phone shots are the reason this exists: 'blur' fills the sides with a
    blurred copy of the shot itself, 'pad' with black bars, 'cover' crops to fill.
    """
    if fit == "cover":
        return (
            f"scale={width}:{height}:force_original_aspect_ratio=increase:flags=lanczos,"
            f"crop={width}:{height}"
        )
    if fit == "pad":
        return (
            f"scale={width}:{height}:force_original_aspect_ratio=decrease:flags=lanczos,"
            f"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:black"
        )
    return (
        f"split[bg][fg];"
        f"[bg]scale={width}:{height}:force_original_aspect_ratio=increase:flags=fast_bilinear,"
        f"crop={width}:{height},boxblur=luma_radius=min(h\\,w)/20:luma_power=1,"
        f"eq=brightness=-0.12[bgout];"
        f"[fg]scale={width}:{height}:force_original_aspect_ratio=decrease:flags=lanczos[fgout];"
        f"[bgout][fgout]overlay=(W-w)/2:(H-h)/2"
    )


def ken_burns(item: Item, duration: float, fps: int, width: int, height: int, zoom: float) -> str:
    """A slow push or pull across the still, so photos do not sit dead on screen.

    The direction is derived from the filename, so re-running the script produces
    the same film rather than a different one each time.
    """
    if zoom <= 1.0:
        return f"fps={fps}"
    frames = max(1, int(round(duration * fps)))
    rng = random.Random(item.path.name)
    zoom_in = rng.random() < 0.5
    # Corner to drift towards, as a fraction of the available travel.
    x_bias, y_bias = rng.choice([(0, 0), (1, 0), (0, 1), (1, 1), (0.5, 0), (0.5, 1)])

    if zoom_in:
        z = f"min(zoom+{(zoom - 1) / frames:.8f},{zoom})"
        progress = "on/max(1," + str(frames - 1) + ")"
    else:
        z = f"max({zoom}-(on/max(1,{frames - 1}))*{zoom - 1:.8f},1.0)"
        progress = f"1-on/max(1,{frames - 1})"

    # zoompan works on the scaled-up image, so the pan targets are expressed in
    # terms of the crop window it reports back as iw/zoom.
    x = f"({x_bias}*({progress})+{(1 - x_bias) * 0.5:.4f}*(1-({progress})))*(iw-iw/zoom)"
    y = f"({y_bias}*({progress})+{(1 - y_bias) * 0.5:.4f}*(1-({progress})))*(ih-ih/zoom)"
    return f"zoompan=z='{z}':x='{x}':y='{y}':d={frames}:s={width}x{height}:fps={fps}"


def render_segment(
    item: Item, index: int, segdir: Path, cache: Path, opts: argparse.Namespace
) -> tuple[int, Path, float] | None:
    """Render one item into a segment with the exact same format as every other."""
    width, height, fps = opts.width, opts.height, opts.fps
    out = segdir / f"seg-{index:05d}.mp4"
    cmd = ["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-nostdin"]

    if item.kind == "photo":
        source = prepare_photo(item, cache, width, height)
        if source is None:
            return None
        duration = opts.photo_duration
        motion = ken_burns(item, duration, fps, width, height, opts.zoom)
        chain = f"{fit_filter(width, height, opts.fit)},{motion}"
        cmd += ["-loop", "1", "-t", f"{duration:.3f}", "-i", str(source)]
        with_audio = False
    else:
        duration = min(item.duration or opts.video_max, opts.video_max)
        if duration <= 0.2:
            return None
        chain = f"{fit_filter(width, height, opts.fit)},fps={fps}"
        cmd += ["-t", f"{duration:.3f}", "-i", str(item.path)]
        with_audio = opts.keep_audio and has_audio(item.path)

    # setsar and yuv420p keep concat and xfade from rejecting a segment later on.
    chain += ",setsar=1,format=yuv420p"

    # Everything goes through a single filter_complex: ffmpeg keeps only the last
    # one it is given, so video and audio have to share the same graph.
    graph = f"[0:v]{chain}[v]"
    maps = ["-map", "[v]"]
    if opts.keep_audio:
        if with_audio:
            graph += (
                f";[0:a]aresample={opts.audio_rate}:async=1,"
                f"aformat=sample_fmts=fltp:channel_layouts=stereo[a]"
            )
        else:
            # A photo, or a clip filmed with the mic off: give it silence so every
            # segment has the same streams and the joins downstream still line up.
            cmd += silent_audio_args(opts)
            graph += f";[1:a]atrim=duration={duration:.3f},asetpts=PTS-STARTPTS[a]"
        maps += ["-map", "[a]"]

    cmd += ["-filter_complex", graph, *maps]
    cmd += [
        "-c:v", "libx264", "-preset", opts.preset, "-crf", str(opts.segment_crf),
        "-pix_fmt", "yuv420p", "-r", str(fps), "-g", str(fps * 2),
        "-t", f"{duration:.3f}",
    ]
    if opts.keep_audio:
        cmd += ["-c:a", "aac", "-b:a", "192k", "-ar", str(opts.audio_rate), "-ac", "2"]
    else:
        cmd += ["-an"]
    cmd += ["-movflags", "+faststart", str(out)]

    try:
        run(cmd)
    except subprocess.CalledProcessError as exc:
        detail = (exc.stderr or "").strip().splitlines()
        log(f"Skipping {item.path.name}: ffmpeg failed ({detail[-1] if detail else 'no output'})")
        return None
    if not out.exists() or out.stat().st_size == 0:
        return None
    return index, out, duration


def silent_audio_args(opts: argparse.Namespace) -> list[str]:
    return ["-f", "lavfi", "-i", f"anullsrc=channel_layout=stereo:sample_rate={opts.audio_rate}"]


# -------------------------------------------------------------------------- joining

def concat_hard(segments: list[Path], out: Path, workdir: Path) -> None:
    """Join segments back to back without re-encoding -- fast, hard cuts."""
    listing = workdir / "concat.txt"
    listing.write_text("".join(f"file '{p}'\n" for p in segments), encoding="utf-8")
    run([
        "ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-nostdin",
        "-f", "concat", "-safe", "0", "-i", str(listing),
        "-c", "copy", "-movflags", "+faststart", str(out),
    ])


def xfade_group(
    segments: list[tuple[Path, float]], out: Path, opts: argparse.Namespace, final: bool = False
) -> float:
    """Crossfade a handful of segments into one clip, returning its duration."""
    if len(segments) == 1:
        shutil.copy2(segments[0][0], out)
        return segments[0][1]

    fade = opts.transition
    cmd = ["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-nostdin"]
    for path, _ in segments:
        cmd += ["-i", str(path)]

    video_parts = []
    offset = segments[0][1] - fade
    total = segments[0][1]
    label = "0:v"
    # Where each clip starts on the finished timeline, reused for the audio below.
    starts = [0.0]
    for i in range(1, len(segments)):
        nxt = f"v{i}"
        video_parts.append(
            f"[{label}][{i}:v]xfade=transition={opts.transition_style}:"
            f"duration={fade:.3f}:offset={offset:.3f}[{nxt}]"
        )
        label = nxt
        starts.append(total - fade)
        total += segments[i][1] - fade
        offset = total - fade

    audio_parts = []
    if opts.keep_audio:
        # Chaining acrossfade would be the obvious way to do this and is a trap:
        # each link buffers its first input, so by the end of a 25-clip chain one
        # filter is holding the whole group's audio and ffmpeg is killed for it.
        # Delaying each track to its own start and summing them streams instead,
        # and overlapping fades sound the same as a crossfade.
        mix_labels = []
        for i, (_, dur) in enumerate(segments):
            chain = []
            if i > 0:  # no fade-in on the first clip: the group is faded as a whole later
                chain.append(f"afade=t=in:st=0:d={fade:.3f}")
            if i < len(segments) - 1:
                chain.append(f"afade=t=out:st={max(0.0, dur - fade):.3f}:d={fade:.3f}")
            delay = int(round(starts[i] * 1000))
            chain.append(f"adelay={delay}|{delay}")
            audio_parts.append(f"[{i}:a]{','.join(chain)}[m{i}]")
            mix_labels.append(f"[m{i}]")
        audio_parts.append(
            f"{''.join(mix_labels)}amix=inputs={len(segments)}:"
            f"normalize=0:dropout_transition=0[aout]"
        )

    graph = ";".join(video_parts + audio_parts)
    cmd += ["-filter_complex", graph, "-map", f"[{label}]"]
    if opts.keep_audio:
        cmd += ["-map", "[aout]", "-c:a", "aac", "-b:a", "192k", "-ar", str(opts.audio_rate)]
    else:
        cmd += ["-an"]
    # Intermediates are re-encoded again later, so they trade size for speed at a
    # quality high enough to leave no trace in the final pass.
    preset = opts.preset if final else opts.intermediate_preset
    crf = opts.crf if final else opts.segment_crf
    cmd += [
        "-c:v", "libx264", "-preset", preset, "-crf", str(crf),
        "-pix_fmt", "yuv420p", "-r", str(opts.fps), "-movflags", "+faststart", str(out),
    ]
    run(cmd)
    return total


def join_with_transitions(
    segments: list[tuple[Path, float]], out: Path, workdir: Path, opts: argparse.Namespace
) -> None:
    """Crossfade hundreds of segments by folding them in batches.

    A single xfade chain over 800 inputs exhausts ffmpeg's file handles and filter
    graph, so groups are faded first and the group results faded together.
    """
    # Every stage re-encodes the whole film, so the group size is widened until two
    # stages are enough: 820 clips in groups of 25 would cost three passes.
    group_size = max(opts.group_size, math.ceil(math.sqrt(len(segments))))
    if group_size != opts.group_size:
        log(f"Using groups of {group_size} to fold {len(segments)} clips in two passes")

    stage, current = 0, segments
    while len(current) > 1:
        stage += 1
        stagedir = workdir / f"stage{stage}"
        stagedir.mkdir(exist_ok=True)
        groups = [current[i:i + group_size] for i in range(0, len(current), group_size)]
        final = len(groups) == 1
        log(f"Crossfade stage {stage}: {len(current)} clips -> {len(groups)} group(s)")

        results: list[tuple[Path, float] | None] = [None] * len(groups)

        def work(args):
            gi, group = args
            target = stagedir / f"g-{gi:04d}.mp4"
            return gi, target, xfade_group(group, target, opts, final=final)

        # The final pass is one long job; giving it the whole machine beats running
        # it on a single worker while three sit idle. Earlier passes are capped
        # well below the core count because each one holds a whole group's worth
        # of decoded video: four at once is what the OOM killer came for.
        workers = 1 if final else max(1, min(opts.jobs, opts.join_jobs))
        with ThreadPoolExecutor(max_workers=workers) as pool:
            for gi, target, dur in pool.map(work, list(enumerate(groups))):
                results[gi] = (target, dur)
        current = [r for r in results if r]
        # Free the previous stage: 800 clips of 1080p intermediates fill a disk.
        if stage > 1:
            shutil.rmtree(workdir / f"stage{stage - 1}", ignore_errors=True)

    shutil.move(str(current[0][0]), str(out))


# ---------------------------------------------------------------------------- music

def add_music(video: Path, music: Path, out: Path, opts: argparse.Namespace) -> None:
    """Lay a soundtrack over the cut, looped to length and faded out at the end."""
    duration = probe_duration(video)
    fade_out = min(4.0, duration / 4)
    filters = [
        f"afade=t=in:st=0:d=2",
        f"afade=t=out:st={max(0.0, duration - fade_out):.3f}:d={fade_out:.3f}",
    ]
    cmd = [
        "ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-nostdin",
        "-i", str(video),
        "-stream_loop", "-1", "-i", str(music),
    ]
    if opts.keep_audio:
        # Duck the music so the clips' own sound stays audible underneath it.
        graph = (
            f"[1:a]volume={opts.music_volume},{','.join(filters)}[music];"
            f"[0:a][music]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[aout]"
        )
    else:
        graph = f"[1:a]volume={opts.music_volume},{','.join(filters)}[aout]"
    cmd += [
        "-filter_complex", graph,
        "-map", "0:v", "-map", "[aout]",
        "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-ar", str(opts.audio_rate),
        "-shortest", "-movflags", "+faststart", str(out),
    ]
    run(cmd)


# ----------------------------------------------------------------------------- main

def parse_args(argv: list[str]) -> argparse.Namespace:
    p = argparse.ArgumentParser(
        description="Build a chronological montage video from a zip or folder of photos and videos.",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter,
    )
    p.add_argument("--input", required=True, type=Path,
                   help="zip archive or folder of media, or a finished video to add music to")
    p.add_argument("--output", required=True, type=Path, help="output video file (.mp4)")
    p.add_argument("--photo-duration", type=float, default=2.5, help="seconds each photo is on screen")
    p.add_argument("--video-max", type=float, default=6.0, help="seconds to keep from each video clip")
    p.add_argument("--max-minutes", type=float, default=0.0,
                   help="thin the album out to roughly this runtime (0 = use everything)")
    p.add_argument("--resolution", default="1920x1080", help="output resolution, WxH")
    p.add_argument("--fps", type=int, default=30, help="output frame rate")
    p.add_argument("--fit", choices=["blur", "pad", "cover"], default="blur",
                   help="how portrait shots fill a landscape frame")
    p.add_argument("--zoom", type=float, default=1.12,
                   help="Ken Burns zoom factor for photos (1.0 disables the movement)")
    p.add_argument("--transition", type=float, default=0.5,
                   help="crossfade length in seconds (0 = hard cuts, much faster)")
    p.add_argument("--transition-style", default="fade", help="ffmpeg xfade transition name")
    p.add_argument("--music", type=Path, help="audio file to lay under the montage")
    p.add_argument("--music-volume", type=float, default=0.6, help="music gain, 1.0 = unchanged")
    p.add_argument("--keep-audio", action="store_true", help="keep the original sound of video clips")
    p.add_argument("--jobs", type=int, default=os.cpu_count() or 4, help="parallel ffmpeg jobs")
    p.add_argument("--preset", default="medium", help="libx264 preset")
    p.add_argument("--crf", type=int, default=20, help="quality of the final file, lower is better")
    p.add_argument("--segment-crf", type=int, default=16,
                   help="quality of intermediate segments, kept high to survive re-encoding")
    p.add_argument("--group-size", type=int, default=25,
                   help="minimum segments crossfaded per batch (raised automatically for big albums)")
    p.add_argument("--join-jobs", type=int, default=2,
                   help="parallel ffmpeg jobs during crossfade passes (memory-bound)")
    p.add_argument("--intermediate-preset", default="veryfast",
                   help="libx264 preset for intermediate crossfade passes")
    p.add_argument("--audio-rate", type=int, default=48000, help="audio sample rate")
    p.add_argument("--workdir", type=Path, help="scratch directory (default: a temporary one)")
    p.add_argument("--keep-workdir", action="store_true", help="do not delete the scratch directory")
    p.add_argument("--keep-duplicates", action="store_true",
                   help="keep re-imported copies and near-identical shots")
    p.add_argument("--similarity", type=int, default=10,
                   help="how alike two photos must be to count as duplicates "
                        "(0-64, lower is stricter; -1 compares copies by name only)")
    p.add_argument("--limit", type=int, default=0, help="only use the first N files (for a quick test)")
    return p.parse_args(argv)


def main(argv: list[str]) -> int:
    opts = parse_args(argv)
    for tool in ("ffmpeg", "ffprobe", "exiftool"):
        if shutil.which(tool) is None:
            sys.exit(f"{tool} is required but not installed")

    try:
        opts.width, opts.height = (int(v) for v in opts.resolution.lower().split("x"))
    except ValueError:
        sys.exit(f"--resolution must look like 1920x1080, got {opts.resolution}")
    # With hard cuts the segments are copied into the result untouched, so they
    # must already carry the requested quality rather than the headroom that only
    # exists to survive a crossfade re-encode.
    if opts.transition <= 0:
        opts.segment_crf = opts.crf

    # xfade needs both clips to be longer than the transition it draws between them.
    shortest = min(opts.photo_duration, opts.video_max)
    if opts.transition > 0 and opts.transition >= shortest:
        opts.transition = max(0.0, shortest / 3)
        log(f"Transition shortened to {opts.transition:.2f}s to fit the shortest clip")

    owns_workdir = opts.workdir is None
    workdir = Path(tempfile.mkdtemp(prefix="montage-")) if owns_workdir else opts.workdir
    workdir.mkdir(parents=True, exist_ok=True)
    opts.output.parent.mkdir(parents=True, exist_ok=True)

    try:
        # Handed a finished film rather than an archive: just lay the music over
        # it. Swapping soundtracks, or adding one later, should cost a minute
        # rather than a re-render of every clip.
        if opts.input.is_file() and opts.input.suffix.lower() in VIDEO_SUFFIXES:
            if not opts.music:
                sys.exit("--input is a video, so --music is required to have anything to do")
            if not opts.music.exists():
                sys.exit(f"Music file not found: {opts.music}")
            log(f"Laying {opts.music.name} over {opts.input.name} without re-rendering")
            add_music(opts.input, opts.music, opts.output, opts)
            log(f"Done: {opts.output}")
            return 0

        media_root = unpack(opts.input, workdir)
        photos, videos = collect(media_root)
        log(f"Found {len(photos)} photos and {len(videos)} videos")
        if not photos and not videos:
            sys.exit("No photos or videos found in the input")
        videos = drop_live_photo_videos(photos, videos)

        items = build_items(photos, videos, workdir)
        if not opts.keep_duplicates:
            items = drop_duplicates(items, opts.jobs, opts.similarity)
        if opts.limit:
            items = items[:opts.limit]
        items = select(items, opts.max_minutes, opts.photo_duration, opts.video_max, opts.transition)
        span = f"{items[0].taken:%d %b %Y} to {items[-1].taken:%d %b %Y}"
        log(f"{len(items)} clips spanning {span}")

        segdir = workdir / "segments"
        cache = workdir / "photo-cache"
        segdir.mkdir(exist_ok=True)
        cache.mkdir(exist_ok=True)

        log(f"Rendering {len(items)} segments on {opts.jobs} workers")
        rendered: list[tuple[int, Path, float]] = []
        done = 0
        with ThreadPoolExecutor(max_workers=opts.jobs) as pool:
            futures = [
                pool.submit(render_segment, item, i, segdir, cache, opts)
                for i, item in enumerate(items)
            ]
            for fut in futures:
                result = fut.result()
                done += 1
                if result:
                    rendered.append(result)
                if done % 50 == 0 or done == len(items):
                    log(f"  {done}/{len(items)} segments")

        if not rendered:
            sys.exit("Every segment failed to render")
        rendered.sort(key=lambda r: r[0])
        skipped = len(items) - len(rendered)
        if skipped:
            log(f"{skipped} file(s) could not be rendered and were left out")

        silent = workdir / "silent.mp4"
        if opts.transition > 0:
            log(f"Joining with {opts.transition:.2f}s crossfades")
            join_with_transitions([(p, d) for _, p, d in rendered], silent, workdir, opts)
        else:
            log("Joining with hard cuts")
            concat_hard([p for _, p, _ in rendered], silent, workdir)

        if opts.music:
            if not opts.music.exists():
                sys.exit(f"Music file not found: {opts.music}")
            log(f"Adding soundtrack {opts.music.name}")
            add_music(silent, opts.music, opts.output, opts)
        else:
            shutil.move(str(silent), str(opts.output))

        length = probe_duration(opts.output)
        size = opts.output.stat().st_size / 1e6
        log(f"Done: {opts.output} -- {int(length // 60)}m {int(length % 60)}s, {size:.0f} MB")
        return 0
    except BaseException:
        # A failure in the final join should not throw away every rendered
        # segment -- that is the expensive part and it is still perfectly good.
        log(f"Failed; leaving the work in {workdir}")
        owns_workdir = False
        raise
    finally:
        if owns_workdir and not opts.keep_workdir:
            shutil.rmtree(workdir, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
