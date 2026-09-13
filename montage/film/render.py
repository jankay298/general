"""Render the film: picture, dialogue with J/L cuts, score, and the final mix."""
import json, subprocess, sys, os, hashlib
import numpy as np
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor
sys.path.insert(0, "/tmp/claude-0/-home-user-general/06cb9692-f1ed-541f-8ed7-e9c306cec4f3/scratchpad/edit")
from look import frame_filter, GRADE, W, H, FONT_DISPLAY, FONT_TEXT
import music

S = Path("/tmp/claude-0/-home-user-general/06cb9692-f1ed-541f-8ed7-e9c306cec4f3/scratchpad")
B = S/"edit/build"; SEG = B/"seg"; SEG.mkdir(parents=True, exist_ok=True)
OUT = S/"output"; OUT.mkdir(exist_ok=True)
FPS, SR = 30, 44100
LEAD, TAIL = 0.40, 0.30          # J-cut / L-cut overlap for speech

pm = json.load(open(S/"edit/data/photos.json"))
vm = json.load(open(S/"edit/data/videoframes.json"))

MOOD = {
 "arrival":"wistful","morning":"warm","parking":"playful","belem":"warm","alfama":"warm",
 "cat":"playful","tacos_pre":"playful","tacos_post":"playful","orange":"playful",
 "orange2":"playful","drive":"city","wine":"playful","d3_start":"warm","sintra":"wistful",
 "cave":"playful","regaleira":"wistful","bbq_pre":"city","bbq":"city","bbq_post":"city",
 "night":"tender","phase":"tender","lastday":"farewell","nata":"tender","bridge":"farewell",
 "home":"warm","outro":"farewell",
}
MOVES = [None, "in", "out", "up", "down", "left", "right"]

def run(cmd, **kw):
    r = subprocess.run(cmd, capture_output=True, text=True, **kw)
    if r.returncode != 0:
        raise RuntimeError(f"failed: {' '.join(str(c) for c in cmd[:10])}\n{r.stderr[-900:]}")
    return r

def dims(path):
    m = pm.get(path)
    if m: return m["w"], m["h"]
    fr = vm.get(path) or []
    if fr: return fr[0]["w"], fr[0]["h"]
    o = run(["ffprobe","-v","error","-select_streams","v:0","-show_entries",
             "stream=width,height","-of","csv=p=0",path]).stdout.strip().split(",")
    return int(o[0]), int(o[1])

# ------------------------------------------------------------------ picture
def seg_duration(p):
    r = subprocess.run(["ffprobe","-v","error","-show_entries","format=duration",
                        "-of","csv=p=0",str(p)], capture_output=True, text=True)
    try: return float(r.stdout.strip())
    except ValueError: return 0.0

def playable(p, want=None):
    """A segment counts as finished only if it is readable AND the right length.

    Two ways a segment lies about being done: a killed ffmpeg leaves frames with
    no moov atom (unreadable), or -- worse -- it catches the signal and finalises
    cleanly, leaving a perfectly valid file that is simply too short. Checking
    readability alone accepts the second kind, and concat then quietly drops the
    missing seconds while the soundtrack keeps its original timing.
    """
    d = seg_duration(p)
    if d <= 0.05: return False
    if want is not None and abs(d - want) > 0.25: return False
    return True

def render_shot(args):
    i, s = args
    out = SEG/f"{i:04d}.mp4"
    if out.exists() and playable(out, s["dur"]): return str(out)
    if out.exists(): out.unlink()
    w, h = dims(s["path"])
    dur = s["dur"]
    if s["kind"] == "photo":
        # vary the move per shot, deterministically, so montages breathe
        seed = int(hashlib.md5(s["path"].encode()).hexdigest()[:8], 16)
        move = MOVES[seed % len(MOVES)] or "in"
        zoom = 1.06
    else:
        move, zoom = None, 1.0
    vf = frame_filter(w, h, s["cx"], s["cy"], zoom=zoom, dur=dur, fps=FPS, move=move)
    vf += "," + GRADE + f",fps={FPS},setsar=1,format=yuv420p"
    if s.get("fade_in"):  vf += f",fade=t=in:st=0:d={s['fade_in']}"
    if s.get("fade_out"): vf += f",fade=t=out:st={max(0,dur-s['fade_out']):.2f}:d={s['fade_out']}"
    cmd = ["ffmpeg","-hide_banner","-loglevel","error","-y","-nostdin"]
    if s["kind"] == "photo":
        cmd += ["-loop","1","-t",f"{dur:.3f}","-i",s["path"]]
    else:
        cmd += ["-ss",f"{s.get('tin',0):.3f}","-t",f"{dur:.3f}","-i",s["path"]]
    cmd += ["-an","-vf",vf,"-c:v","libx264","-preset","medium","-crf","17",
            "-pix_fmt","yuv420p","-r",str(FPS),"-t",f"{dur:.3f}",str(out)]
    run(cmd)
    return str(out)

def title_card(i, main, sub, dur, hold_black=0.0):
    out = SEG/f"{i:04d}.mp4"
    m = main.replace("'", r"\'"); sb = sub.replace("'", r"\'")
    fade = 0.9
    vf = (f"drawtext=fontfile={FONT_DISPLAY}:text='{m}':fontcolor=0xf2ece0:fontsize=120:"
          f"x=(w-text_w)/2:y=(h-text_h)/2-70:"
          f"alpha='min(min(max(t-{hold_black},0)/{fade},1),max(0,({dur}-t)/0.7))',"
          f"drawtext=fontfile={FONT_TEXT}:text='{sb}':fontcolor=0xbfb5a6:fontsize=40:"
          f"x=(w-text_w)/2:y=(h-text_h)/2+60:"
          f"alpha='min(min(max(t-{hold_black+0.35},0)/{fade},1),max(0,({dur}-t)/0.7))',"
          f"noise=alls=4:allf=t+u,vignette=angle=PI/6,fps={FPS},setsar=1,format=yuv420p")
    run(["ffmpeg","-hide_banner","-loglevel","error","-y","-f","lavfi",
         "-i",f"color=c=0x0a0a0c:s={W}x{H}:d={dur}","-vf",vf,
         "-c:v","libx264","-preset","medium","-crf","17","-pix_fmt","yuv420p",str(out)])
    return str(out)

# -------------------------------------------------------------------- score
def build_score(beats_timeline, total):
    """One continuous piece of music per mood-run, crossfaded at the seams."""
    track = np.zeros(int(total*SR)+SR, dtype=np.float32)
    runs = []
    for b in beats_timeline:
        mood = MOOD.get(b["beat"], "warm")
        if runs and runs[-1][0] == mood and abs(runs[-1][2] - b["start"]) < 0.05:
            runs[-1][2] = b["end"]
        else:
            runs.append([mood, b["start"], b["end"]])
    for n, (mood, a, b) in enumerate(runs):
        dur = b - a + 2.0
        piece = music.render(mood, dur, seed=n*13+7)
        fi, fo = int(1.2*SR), int(1.6*SR)
        if len(piece) > fi+fo:
            piece[:fi] *= np.linspace(0,1,fi); piece[-fo:] *= np.linspace(1,0,fo)
        p = int(a*SR); e = min(len(track), p+len(piece))
        track[p:e] += piece[:e-p]
    peak = np.abs(track).max()
    if peak > 0: track = track/peak*0.82
    return track[:int(total*SR)]

if __name__ == "__main__":
    seq = json.load(open(S/"edit/data/sequence.json"))
    # ---- assemble the timeline, inserting title cards and chapter fades
    timeline, idx = [], 0
    CHAPTERS = {"ANKUNFT": ("LISSABON","Januar 2026"), "TAG 2": None,
                "TAG 3": None, "TAG 4": None}
    shots = []
    for s in seq:
        if s["kind"] == "chapter":
            if s["text"] == "ANKUNFT":
                shots.append(dict(kind="title", main="LISSABON", sub="Januar 2026",
                                  dur=3.6, beat="arrival", hold_black=0.3))
            continue
        shots.append(s)
    # fade the very last shot out, and dip in from black at the start
    shots[0]["fade_in"] = 0.8
    shots[-1]["fade_out"] = 2.2
    # give each beat's first shot a tiny fade for breathing room at chapter turns
    prev_beat = None
    for s in shots:
        if s["beat"] != prev_beat and s["beat"] in ("d3_start","lastday","morning"):
            s["fade_in"] = 0.5
        prev_beat = s["beat"]

    print(f"{len(shots)} Segmente", flush=True)
    jobs = int(os.environ.get("JOBS","4"))
    paths = [None]*len(shots)
    work = [(i,s) for i,s in enumerate(shots) if s["kind"] != "title"]
    for i,s in enumerate(shots):
        if s["kind"] == "title":
            paths[i] = title_card(i, s["main"], s["sub"], s["dur"], s.get("hold_black",0))
    done = 0
    with ThreadPoolExecutor(max_workers=jobs) as ex:
        for (i,s), p in zip(work, ex.map(render_shot, work)):
            paths[i] = p; done += 1
            if done % 20 == 0: print(f"  {done}/{len(work)}", flush=True)
    print("Bild fertig", flush=True)

    # ---- positions on the finished timeline, from what was actually rendered
    # The picture is the clock. A segment can come out shorter than planned (the
    # source ran out, an encode was cut short), and timing the soundtrack against
    # the plan instead of the result is what slides voices off their pictures.
    t = 0.0
    for i,s in enumerate(shots):
        real = seg_duration(paths[i])
        if abs(real - s["dur"]) > 0.02:
            s["dur"] = round(real,3)
        s["start"] = round(t,3); t += s["dur"]; s["end"] = round(t,3)
    total = t
    json.dump(shots, open(B/"timeline_final.json","w"), indent=1)
    print(f"Gesamtlaenge {total/60:.2f} Min", flush=True)

    # ---- picture: straight cuts, concatenated without re-encoding
    lst = B/"concat.txt"
    lst.write_text("".join(f"file '{p}'\n" for p in paths))
    bad = [(p, sh) for p, sh in zip(paths, shots) if not playable(p, sh["dur"])]
    # A B-roll shot may legitimately be shorter than asked for when the clip ends;
    # a dialogue take that is short means words are missing, which is never ok.
    fatal = [p for p, sh in bad if sh["kind"] == "talk" or seg_duration(p) < 0.4]
    if fatal:
        raise RuntimeError(f"{len(fatal)} unbrauchbare Segmente: {fatal[:5]}")
    if bad:
        print(f"{len(bad)} B-Roll-Segmente kuerzer als geplant (Quelle zu kurz) - Timeline folgt dem Bild", flush=True)
    silent = B/"picture.mp4"
    run(["ffmpeg","-hide_banner","-loglevel","error","-y","-f","concat","-safe","0",
         "-i",str(lst),"-c","copy",str(silent)])
    got = float(subprocess.run(["ffprobe","-v","error","-show_entries","format=duration",
        "-of","csv=p=0",str(silent)], capture_output=True, text=True).stdout.strip())
    if abs(got - total) > 1.5:
        raise RuntimeError(f"Bildspur {got:.1f}s, erwartet {total:.1f}s -- concat hat gekuerzt")
    print(f"Bild und Zeitachse stimmen ueberein ({got:.1f}s)", flush=True)
    print(f"Bildspur montiert: {got/60:.2f} Min", flush=True)
    json.dump({"total": total}, open(B/"meta.json","w"))
