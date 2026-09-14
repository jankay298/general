"""The cut: full vlog takes, with pictures that show what is being talked about."""
import json, sys
from pathlib import Path
sys.path.insert(0, "/tmp/claude-0/-home-user-general/06cb9692-f1ed-541f-8ed7-e9c306cec4f3/scratchpad/edit")
import story
from story import S, M, talk, burst, pool, shot_of, beat_len, used, named

W="modern_warm"; B="modern_bright"; D="modern_drive"
N="modern_night"; SO="modern_soft"; C="modern_close"

shots = []
def add(x): shots.extend(x if isinstance(x, list) else [x])

# ============================================================== I — arrival
add(burst(("19.01 21:00","20.01 00:00"), 5, N))

# Morning. The full take: "Guten Morgen aus Portugal ... Pastéis geholt"
add(talk(M/"688B0DF8-BAB4-4D50-9529-8EFB2AB6F091.MP4", 0.0, 13.4, W,
         cover=["streets"], head=3.0))

# The parking saga, complete. Deliberately NOT illustrated with parking signs --
# the story is funny, the photographs of car parks are not.
add(talk(M/"0812C90C-28A7-45A5-AFBE-F0A67C65A92A.MP4", 0.0, 25.4, B,
         cover=["streets","her"], head=3.2))

# ================================================================ II — city
add(talk(M/"IMG_9226.MP4", 0.4, 23.4, W, cover=["belem"], head=3.4))
add(burst(("20.01 12:30","20.01 13:20"), 6, B))
add(burst(("20.01 13:20","20.01 14:30"), 6, B))
add(burst(("20.01 15:05","20.01 15:40"), 4, W))

# Tacos: the anticipation in full, then the verdict over the food itself
add(talk(M/"64850859-AD00-4372-B49C-FC3B4D04AA47.MP4", 0.0, 17.2, B,
         cover_window=("20.01 17:40","20.01 18:20"), head=5.0))
add(talk(M/"0D9AF20A-8D21-480A-98CC-83CD4F77C222.MP4", 0.0, 34.6, B,
         cover=["tacos"], cover_window=("20.01 17:45","20.01 19:05"), head=4.0))
add(talk(M/"0D9AF20A-8D21-480A-98CC-83CD4F77C222.MP4", 44.6, 62.0, B,
         cover=["couple"], head=3.0))

# The orange
add(talk(M/"C6BC9A45-C7D3-42FF-B9BF-674436DD6A7A.MP4", 0.0, 5.4, B, head=5.4))
add(talk(M/"C6BC9A45-C7D3-42FF-B9BF-674436DD6A7A.MP4", 59.0, 68.2, B,
         cover=["orange"], cover_window=("20.01 18:50","20.01 19:15"), head=4.0))

# The drive that went wrong, and the wine
add(talk(M/"9F6232E7-11D8-4455-875D-C2BEDB4E0996.MP4", 0.0, 35.0, D,
         cover=["drive"], head=3.4))
add(talk(M/"9F6232E7-11D8-4455-875D-C2BEDB4E0996.MP4", 73.0, 91.6, B,
         cover=["view_night"], cover_window=("21.01 20:50","21.01 23:00"), head=4.0))

# ============================================================== III — Sintra
add(talk(M/"B3DAB253-B1D7-472B-825F-49A14C703D99.MP4", 0.2, 45.5, W,
         cover=["her","streets"], head=4.0))
add(talk(M/"IMG_3639.MP4", 0.0, 67.4, N,
         cover=["sintra_nature"], head=4.0))
add(burst(("21.01 14:10","21.01 15:00"), 7, N))
add(talk(M/"IMG_9517.MP4", 2.3, 30.0, D, cover=["cave"], cover_window=("21.01 14:45","21.01 15:15"), head=3.6))
add(burst(("21.01 15:05","21.01 15:45"), 6, N))
add(dict(kind="photo", path=story.CATS["sintra_palace"][0], dur=3.0,
         cx=0.5, cy=0.45, whole=True, mood=N))

# =========================================================== IV — night out
add(talk(M/"2BD9BAA8-9F16-4D09-8BD2-BBCAF3107F51.MP4", 3.8, 31.2, D,
         cover=["couple"], head=6.0))
add(talk(M/"ACD2DD38-7C46-4F5A-80CA-C34A0CADC0BB.MP4", 0.0, 21.5, D,
         cover=["bbq"], cover_window=("21.01 19:40","21.01 20:05"), head=4.0))
add(talk(M/"AC8CD6A0-87C1-4E58-99B7-5D261EEB3110.MP4", 0.0, 30.0, D,
         cover=["bbq"], cover_window=("21.01 19:40","21.01 20:05"), head=4.0))
add(talk(M/"AC8CD6A0-87C1-4E58-99B7-5D261EEB3110.MP4", 62.0, 87.8, D,
         cover=["view_night"], cover_window=("21.01 20:50","21.01 23:00"), head=4.0))
add(burst(("21.01 21:00","21.01 22:45"), 4, SO))
add(talk(M/"464ADC30-CBD4-4C2C-A791-A7ED47683351.MP4", 0.0, 55.9, SO,
         cover=["bar"], cover_window=("21.01 21:00","21.01 23:45"), head=6.0))

# ============================================================= V — farewell
add(talk(M/"9DDE9777-3C5B-486E-9DE3-BD3623FA568F.MP4", 1.0, 34.6, C,
         cover=["view_day"], head=4.0))
add(talk(M/"IMG_3720.MP4", 5.8, 39.5, SO, cover=["nata"], cover_window=("22.01 09:40","22.01 10:35"), head=4.0))
add(talk(M/"9DDE9777-3C5B-486E-9DE3-BD3623FA568F.MP4", 44.0, 64.0, C,
         cover=["couple","view_day"], head=3.4))
add(talk(M/"3922EC83-6951-4B90-993B-7CFFAA3B94A2.MP4", 0.0, 21.0, W, head=6.0))
tail = burst(("22.01 10:35","22.01 11:15"), 2, C)
add(tail)
if shots: shots[-1]["dur"] = 4.5

# ===================================================== cold open, built last
# Uses frames the story does NOT use later, so nothing is shown twice.
def unused_best(k):
    cands = [t for t in story.tl
             if t["path"] not in used and t["path"] not in story.BLACK
             and t["kind"] == "photo"]
    cands.sort(key=story.score, reverse=True)
    out, hs = [], []
    for t in cands:
        h = story.phash(t["path"])
        if h is not None and any(bin(h ^ q).count("1") <= 20 for q in hs): continue
        out.append(t); hs.append(h)
        if len(out) >= k: break
    for t in out: used.add(t["path"])
    return out

opener = []
hooks = unused_best(8)
if hooks:
    opener.append(shot_of(hooks[0], 1.6, N))
    step = beat_len(D)/2
    for t in hooks[1:]: opener.append(shot_of(t, step*1.05, D))
    opener[-1]["dur"] = step*1.8
opener.append(dict(kind="title", main="LISSABON", sub="Januar 2026", dur=3.2,
                   hold_black=0.25, mood=N))
if opener:
    opener[0]["audio_path"] = str(M/"9DDE9777-3C5B-486E-9DE3-BD3623FA568F.MP4")
    opener[0]["audio_tin"] = 1.2; opener[0]["audio_dur"] = 4.4
shots = opener + shots

if __name__ == "__main__":
    total = sum(s["dur"] for s in shots)
    json.dump(shots, open(S/"edit/data/sequence2.json","w"), indent=1)
    kinds = {}
    for s in shots: kinds[s["kind"]] = kinds.get(s["kind"],0)+1
    voice = sum(s.get("audio_dur",0) for s in shots)
    durs = sorted(s["dur"] for s in shots)
    print(f"{len(shots)} Einstellungen, {total/60:.2f} Min")
    print("Aufteilung:", kinds)
    print(f"O-Ton gesamt: {voice/60:.1f} Min in {sum(1 for s in shots if s.get('audio_dur'))} Takes")
    print(f"Schnittlaengen: {durs[0]:.2f}s / Median {durs[len(durs)//2]:.2f}s / {durs[-1]:.2f}s")
