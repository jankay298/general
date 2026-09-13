"""Turn the beat list into a concrete shot sequence with crops and durations."""
import json, sys
from pathlib import Path
from datetime import datetime
sys.path.insert(0, "/tmp/claude-0/-home-user-general/06cb9692-f1ed-541f-8ed7-e9c306cec4f3/scratchpad/edit")
import edl
from look import needs_whole

S = Path("/tmp/claude-0/-home-user-general/06cb9692-f1ed-541f-8ed7-e9c306cec4f3/scratchpad")
tl = json.load(open(S/"edit/data/timeline.json"))
pmeta = json.load(open(S/"edit/data/photos.json"))
vmeta = json.load(open(S/"edit/data/videoframes.json"))
for t in tl: t["dt"] = datetime.fromisoformat(t["taken"])

def parse(ts):
    d, hm = ts.split(" ")
    day, mon = d.split(".")
    return datetime(2026, int(mon), int(day), int(hm[:2]), int(hm[3:]))

_PH = {}
def phash_of(path):
    """Cached perceptual hash, used to keep repeats out of a single montage."""
    if path not in _PH:
        try:
            import importlib.util
            if "mm" not in globals():
                spec = importlib.util.spec_from_file_location("mm", "/home/user/general/montage/make-montage.py")
                mod = importlib.util.module_from_spec(spec); sys.modules["mm"] = mod
                spec.loader.exec_module(mod); globals()["mm"] = mod
            _PH[path] = mm.perceptual_hash(Path(path))
        except Exception:
            _PH[path] = None
    return _PH[path]

def source_dims(t):
    m = pmeta.get(t["path"])
    if m: return m["w"], m["h"]
    fr = vmeta.get(t["path"]) or []
    if fr: return fr[0]["w"], fr[0]["h"]
    return 1080, 1920

def score(t):
    """How good is this shot? Sharpness first, then people, then exposure."""
    m = pmeta.get(t["path"])
    if m is None:
        fr = vmeta.get(t["path"]) or []
        if not fr: return 0.0
        m = max(fr, key=lambda f: f["sharp"])
    sharp = min(m["sharp"] / 400.0, 1.5)           # diminishing above 400
    faces = min(len(m["faces"]), 2) * 0.45          # people matter in a couple's film
    # punish the badly exposed: near-black or blown out
    b = m["bright"]
    expo = 1.0 - min(abs(b - 0.45) / 0.45, 1.0) * 0.6
    contrast = min(m["contrast"] / 0.25, 1.0) * 0.25
    return sharp + faces + expo + contrast

def face_span(t):
    """How much of the width the faces occupy, 0..1."""
    m = pmeta.get(t["path"]); frames = vmeta.get(t["path"]) or []
    boxes = m["faces"] if m else [b for fr in frames for b in fr["faces"]]
    if not boxes: return 0.0
    lo = min(b[0] for b in boxes); hi = max(b[0]+b[2] for b in boxes)
    return max(0.0, min(1.0, hi-lo))

def crop_focus(t):
    """Where the 9:16 window should sit: on the faces, else slightly above centre."""
    m = pmeta.get(t["path"])
    frames = vmeta.get(t["path"]) or []
    boxes = []
    if m: boxes = m["faces"]
    elif frames:
        for f in frames: boxes.extend(f["faces"])
    if not boxes:
        return 0.5, 0.45
    cx = sum(b[0] + b[2]/2 for b in boxes) / len(boxes)
    cy = sum(b[1] + b[3]/2 for b in boxes) / len(boxes)
    # leave headroom: pull the window down a little from the face centre
    return cx, min(0.85, cy + 0.10)

def best_frames(path, want, minlen=1.6):
    """For a silent video: the liveliest stretch, avoiding the shaky first second."""
    fr = vmeta.get(path) or []
    if not fr: return [(0.8, 0.8 + want)]
    usable = [f for f in fr if f["t"] > 0.7]
    if not usable: usable = fr
    best = max(usable, key=lambda f: f["sharp"] * (1 + min(len(f["faces"]), 2) * 0.5))
    return [(max(0.0, best["t"] - 0.4), max(minlen, want))]

# Beats per minute of the cue under each beat, so shot lengths can land on the
# musical grid instead of an arbitrary 2.5 seconds.
BPM = {"modern_warm":92,"modern_bright":104,"modern_drive":112,"modern_soft":84,
       "modern_night":96,"modern_close":76}
MOOD_OF = {
 "arrival":"modern_night","morning":"modern_warm","parking":"modern_bright",
 "belem":"modern_warm","alfama":"modern_warm","cat":"modern_bright",
 "tacos_pre":"modern_bright","tacos_post":"modern_bright","orange":"modern_bright",
 "orange2":"modern_bright","drive":"modern_drive","wine":"modern_bright",
 "d3_start":"modern_warm","sintra":"modern_night","cave":"modern_drive",
 "regaleira":"modern_night","bbq_pre":"modern_drive","bbq":"modern_drive",
 "bbq_post":"modern_drive","night":"modern_soft","phase":"modern_soft",
 "lastday":"modern_close","nata":"modern_soft","bridge":"modern_close",
 "home":"modern_warm","outro":"modern_close",
}

def grid(beat_id, units):
    """Round a shot length onto the musical grid of its cue.

    Cutting on the beat is most of what makes a montage feel edited rather than
    stepped through; units are in half-bars.
    """
    bpm = BPM[MOOD_OF.get(beat_id, "modern_warm")]
    half_bar = 60.0/bpm*2
    return round(half_bar*units, 3)

used = set()
shots = []

def pick_broll(window, n, exclude_talk=None):
    if not window or n <= 0: return []
    a, b = parse(window[0]), parse(window[1])
    pool = [t for t in tl if a <= t["dt"] <= b and t["path"] not in used
            and t["path"] != exclude_talk]
    pool.sort(key=score, reverse=True)
    # Reject near-twins inside the pool itself: the global pass runs at a loose
    # threshold to avoid throwing away genuinely different shots, but two frames
    # of the same view landing minutes apart in one montage still read as a
    # repeat -- especially when they differ only in exposure.
    picked_hashes = []
    chosen, seen_min = [], []
    for t in pool:
        # spread the picks across the window rather than clustering on one minute
        mins = t["dt"].timestamp() // 60
        if any(abs(mins - s) < 1.5 for s in seen_min) and len(pool) > n * 2:
            continue
        h = phash_of(t["path"])
        if h is not None and any(bin(h ^ q).count("1") <= 16 for q in picked_hashes):
            continue
        chosen.append(t); seen_min.append(mins)
        if h is not None: picked_hashes.append(h)
        if len(chosen) >= n: break
    for t in chosen: used.add(t["path"])
    return sorted(chosen, key=lambda t: t["dt"])

for beat in edl.BEATS:
    if beat["chapter"]:
        shots.append(dict(kind="chapter", text=beat["chapter"], dur=0.0, beat=beat["id"]))
    talk_path = None
    if beat["talk"]:
        p, a, b = beat["talk"]
        talk_path = str(p)
        cx, cy = crop_focus({"path": talk_path})
        tw, th = source_dims({"path": talk_path})
        shots.append(dict(kind="talk", path=talk_path, tin=a, tout=b, dur=round(b-a,2),
                          cx=cx, cy=cy, beat=beat["id"], note=beat["note"],
                          whole=needs_whole(tw, th, face_span({"path": talk_path}))))
        used.add(talk_path)
    for t in pick_broll(beat.get("broll"), beat.get("broll_n", 0), talk_path):
        cx, cy = crop_focus(t)
        dw, dh = source_dims(t)
        whole = needs_whole(dw, dh, face_span(t))
        if t["kind"] == "photo":
            # Vary the rhythm: a strong shot is held, the ones around it are quick.
            sc = score(t)
            n_in_beat = sum(1 for x in shots if x.get("beat")==beat["id"] and x["kind"]=="photo")
            if sc > 2.2:      units = 1.0        # a keeper: let it breathe
            elif n_in_beat % 3 == 2: units = 0.75
            else:             units = 0.5        # quick cut
            if beat["pace"] > 3.0: units *= 1.6  # the outro is deliberately slow
            shots.append(dict(kind="photo", path=t["path"], dur=grid(beat["id"], units),
                              cx=cx, cy=cy, beat=beat["id"], score=round(sc,2), whole=whole))
        else:
            (tin, d), = best_frames(t["path"], beat["pace"] + 1.0)
            shots.append(dict(kind="bvideo", path=t["path"], tin=round(tin,2),
                              dur=round(min(d, 5.0),2), cx=cx, cy=cy, beat=beat["id"],
                              score=round(score(t),2), whole=whole))

json.dump(shots, open(S/"edit/data/sequence.json","w"), indent=1)
total = sum(s["dur"] for s in shots)
kinds = {}
for s in shots: kinds[s["kind"]] = kinds.get(s["kind"],0)+1
print(f"{len(shots)} Shots, {total/60:.1f} Min")
print("Aufteilung:", kinds)
print(f"verwendete Quelldateien: {len(used)}")
