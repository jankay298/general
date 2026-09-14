"""Build a cut with shape: a hook, speech carried over pictures, and rhythm.

The previous version put one picture per moment in chronological order and laid
sound underneath. That reads as a slideshow no matter how good the grade is.
Three things change here:

- A talking take is not one shot. The speaker establishes for a beat or two, then
  the picture cuts away to what is being talked about while the voice keeps
  running. That single move is most of what separates a vlog from a slideshow.
- Shot lengths are dramatic, not uniform: bursts of six to twelve quick frames on
  the beat, then one held image to land on.
- The film opens on its strongest moment, not on day one.
"""
import json, sys
from pathlib import Path
from datetime import datetime
sys.path.insert(0, "/tmp/claude-0/-home-user-general/06cb9692-f1ed-541f-8ed7-e9c306cec4f3/scratchpad/edit")
from look import needs_whole

S = Path("/tmp/claude-0/-home-user-general/06cb9692-f1ed-541f-8ed7-e9c306cec4f3/scratchpad")
M = S/"media/Lissabon "
tl = json.load(open(S/"edit/data/timeline.json"))
pmeta = json.load(open(S/"edit/data/photos.json"))
vmeta = json.load(open(S/"edit/data/videoframes.json"))
for t in tl: t["dt"] = datetime.fromisoformat(t["taken"])

BPM = {"modern_warm":92,"modern_bright":104,"modern_drive":112,"modern_soft":84,
       "modern_night":96,"modern_close":76}

def beat_len(mood): return 60.0/BPM[mood]

# ---------------------------------------------------------------- scoring
def score(t):
    m = pmeta.get(t["path"])
    if m is None:
        fr = vmeta.get(t["path"]) or []
        if not fr: return 0.0
        m = max(fr, key=lambda f: f["sharp"])
    sharp = min(m["sharp"]/400.0, 1.5)
    faces = min(len(m["faces"]), 2)*0.45
    b = m["bright"]
    expo = 1.0 - min(abs(b-0.45)/0.45, 1.0)*0.6
    return sharp + faces + expo + min(m["contrast"]/0.25, 1.0)*0.25

def face_span(t):
    m = pmeta.get(t["path"]); frames = vmeta.get(t["path"]) or []
    boxes = m["faces"] if m else [b for fr in frames for b in fr["faces"]]
    if not boxes: return 0.0
    lo = min(b[0] for b in boxes); hi = max(b[0]+b[2] for b in boxes)
    return max(0.0, min(1.0, hi-lo))

def focus(t):
    m = pmeta.get(t["path"]); frames = vmeta.get(t["path"]) or []
    boxes = m["faces"] if m else [b for fr in frames for b in fr["faces"]]
    if not boxes: return 0.5, 0.45
    cx = sum(b[0]+b[2]/2 for b in boxes)/len(boxes)
    cy = sum(b[1]+b[3]/2 for b in boxes)/len(boxes)
    return cx, min(0.85, cy+0.10)

def dims(t):
    m = pmeta.get(t["path"])
    if m: return m["w"], m["h"]
    fr = vmeta.get(t["path"]) or []
    if fr: return fr[0]["w"], fr[0]["h"]
    return 1080, 1920

_PH = {}
def phash(path):
    if path not in _PH:
        try:
            import importlib.util
            if "mm" not in globals():
                sp = importlib.util.spec_from_file_location("mm", "/home/user/general/montage/make-montage.py")
                mod = importlib.util.module_from_spec(sp); sys.modules["mm"] = mod
                sp.loader.exec_module(mod); globals()["mm"] = mod
            _PH[path] = mm.perceptual_hash(Path(path))
        except Exception: _PH[path] = None
    return _PH[path]

def parse(ts):
    d, hm = ts.split(" "); day, mon = d.split(".")
    return datetime(2026, int(mon), int(day), int(hm[:2]), int(hm[3:]))

BLACK = set(json.load(open(S/"edit/data/blacklist.json"))) if (S/"edit/data/blacklist.json").exists() else set()
CATS = json.load(open(S/"edit/data/categories.json")) if (S/"edit/data/categories.json").exists() else {}
BY_PATH = {t["path"]: t for t in tl}

used = set()

def named(cat, n=None, keep=False):
    """Shots chosen by hand for what they show, not by when they were taken.

    Cutting away from a voice only works if the picture shows what is being
    talked about -- tacos over the taco verdict, the cave over "we went in
    illegally". Time-window picking gets that roughly right at best, and filled
    the parking story with photographs of parking signs.
    """
    out = []
    for p in CATS.get(cat, []):
        if p in BLACK: continue
        if p in used and not keep: continue
        t = BY_PATH.get(p)
        if t: out.append(t)
        if n and len(out) >= n: break
    if not keep:
        for t in out: used.add(t["path"])
    return out

def pool(window, n, want_faces=None):
    """Best unused shots from a time window, spread out and free of near-twins."""
    a, b = parse(window[0]), parse(window[1])
    cand = [t for t in tl if a <= t["dt"] <= b and t["path"] not in used
            and t["path"] not in BLACK]
    if want_faces is True:
        cand = [t for t in cand if face_span(t) > 0.05] or cand
    cand.sort(key=score, reverse=True)
    out, mins, hashes = [], [], []
    for t in cand:
        mm_ = t["dt"].timestamp()//60
        if any(abs(mm_-x) < 1.0 for x in mins) and len(cand) > n*2: continue
        h = phash(t["path"])
        if h is not None and any(bin(h ^ q).count("1") <= 16 for q in hashes): continue
        out.append(t); mins.append(mm_)
        if h is not None: hashes.append(h)
        if len(out) >= n: break
    for t in out: used.add(t["path"])
    return sorted(out, key=lambda x: x["dt"])

def shot_of(t, dur, mood):
    cx, cy = focus(t)
    w, h = dims(t)
    base = dict(cx=cx, cy=cy, whole=needs_whole(w, h, face_span(t)), mood=mood)
    if t["kind"] == "photo":
        return dict(kind="photo", path=t["path"], dur=round(dur,3), **base)
    fr = vmeta.get(t["path"]) or []
    usable = [f for f in fr if f["t"] > 0.6] or fr
    tin = max(0.0, (max(usable, key=lambda f: f["sharp"])["t"] - 0.3) if usable else 0.6)
    return dict(kind="bvideo", path=t["path"], tin=round(tin,2), dur=round(dur,3), **base)

def burst(window, count, mood, hold_last=True):
    """A run of quick cuts on the beat, optionally landing on a held frame."""
    step = beat_len(mood)/2
    items = pool(window, count)
    out = []
    for i, t in enumerate(items):
        last = hold_last and i == len(items)-1
        out.append(shot_of(t, step*(3.0 if last else 1.0), mood))
    return out

def talk(path, tin, tout, mood, cover_window=None, cover_n=0, head=3.0, tail=0.0,
         cover=None, target=2.7, speaker_every=3):
    """A spoken take: the speaker and the pictures alternate while the voice runs.

    Two failure modes this avoids. Staying on the speaker for a 45-second take is
    deadly, and so is stretching three photographs across it -- both happened when
    the cutaway material simply ran out. Instead the take is divided into blocks
    of roughly `target` seconds: the speaker returns every few blocks, and the
    pictures fill the rest, drawn first from the named categories and then from
    the surrounding minutes.
    """
    total = tout - tin
    head = min(head, total)
    p = str(path)
    cx, cy = focus({"path": p}); w, h = dims({"path": p})
    whole = needs_whole(w, h, face_span({"path": p}))
    def speaker(at, dur, first=False):
        sh = dict(kind="talk", path=p, tin=round(at,2), dur=round(dur,3),
                  cx=cx, cy=cy, whole=whole, mood=mood)
        if first:
            sh.update(audio_tin=round(tin,2), audio_dur=round(total,3))
        else:
            sh["silent"] = True
        return sh

    shots = [speaker(tin, head, first=True)]
    rest = total - head - tail
    if rest <= 0.4:
        return shots

    blocks = max(1, int(round(rest/target)))
    # gather enough cutaway material for the blocks that are not the speaker
    want = blocks - blocks//(speaker_every+1)
    items = []
    for c in (cover or []):
        items += named(c)
    if cover_window and len(items) < want:
        items += pool(cover_window, want - len(items))
    # Never show the same picture twice inside one take. When material is short,
    # hold each shot longer and come back to the speaker more often instead --
    # cycling through three photographs for forty seconds is exactly the repeat
    # that makes a cut look careless.
    seen, uniq = set(), []
    for t in items:
        if t["path"] in seen: continue
        seen.add(t["path"]); uniq.append(t)
    items = uniq
    if items:
        n_pic = len(items)
        n_speaker = max(1, n_pic // max(1, speaker_every))
        blocks = n_pic + n_speaker
        # With little material this would stretch each block over half a minute.
        # Cap the block length and let the surplus go back to the speaker: a face
        # returning every few seconds reads as an edit, a 25-second still does not.
        if rest/blocks > 5.0:
            blocks = max(blocks, int(round(rest/4.5)))
    if not items:
        # nothing to cut to: at least break the speaker into readable chunks
        n = max(1, int(round(rest/6.0)))
        for i in range(n):
            shots.append(speaker(tin+head+i*rest/n, rest/n))
        return shots

    each = rest/blocks
    pos, i = tin+head, 0
    for b in range(blocks):
        if (b and b % speaker_every == 0) or i >= len(items):
            shots.append(speaker(pos, each))     # come back to the face
        else:
            shots.append(shot_of(items[i], each, mood))
            i += 1
        pos += each
    return shots
