"""Turn the beat list into a concrete shot sequence with crops and durations."""
import json, sys
from pathlib import Path
from datetime import datetime
sys.path.insert(0, "/tmp/claude-0/-home-user-general/06cb9692-f1ed-541f-8ed7-e9c306cec4f3/scratchpad/edit")
import edl

S = Path("/tmp/claude-0/-home-user-general/06cb9692-f1ed-541f-8ed7-e9c306cec4f3/scratchpad")
tl = json.load(open(S/"edit/data/timeline.json"))
pmeta = json.load(open(S/"edit/data/photos.json"))
vmeta = json.load(open(S/"edit/data/videoframes.json"))
for t in tl: t["dt"] = datetime.fromisoformat(t["taken"])

def parse(ts):
    d, hm = ts.split(" ")
    day, mon = d.split(".")
    return datetime(2026, int(mon), int(day), int(hm[:2]), int(hm[3:]))

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

used = set()
shots = []

def pick_broll(window, n, exclude_talk=None):
    if not window or n <= 0: return []
    a, b = parse(window[0]), parse(window[1])
    pool = [t for t in tl if a <= t["dt"] <= b and t["path"] not in used
            and t["path"] != exclude_talk]
    pool.sort(key=score, reverse=True)
    chosen, seen_min = [], []
    for t in pool:
        # spread the picks across the window rather than clustering on one minute
        mins = t["dt"].timestamp() // 60
        if any(abs(mins - s) < 1.5 for s in seen_min) and len(pool) > n * 2:
            continue
        chosen.append(t); seen_min.append(mins)
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
        shots.append(dict(kind="talk", path=talk_path, tin=a, tout=b, dur=round(b-a,2),
                          cx=cx, cy=cy, beat=beat["id"], note=beat["note"]))
        used.add(talk_path)
    for t in pick_broll(beat.get("broll"), beat.get("broll_n", 0), talk_path):
        cx, cy = crop_focus(t)
        if t["kind"] == "photo":
            shots.append(dict(kind="photo", path=t["path"], dur=beat["pace"],
                              cx=cx, cy=cy, beat=beat["id"], score=round(score(t),2)))
        else:
            (tin, d), = best_frames(t["path"], beat["pace"] + 1.0)
            shots.append(dict(kind="bvideo", path=t["path"], tin=round(tin,2),
                              dur=round(min(d, 5.0),2), cx=cx, cy=cy, beat=beat["id"],
                              score=round(score(t),2)))

json.dump(shots, open(S/"edit/data/sequence.json","w"), indent=1)
total = sum(s["dur"] for s in shots)
kinds = {}
for s in shots: kinds[s["kind"]] = kinds.get(s["kind"],0)+1
print(f"{len(shots)} Shots, {total/60:.1f} Min")
print("Aufteilung:", kinds)
print(f"verwendete Quelldateien: {len(used)}")
