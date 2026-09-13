"""Build the soundtrack: speech, room tone, score, and a mix that respects voices."""
import json, subprocess, sys, os
import numpy as np
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor
sys.path.insert(0, "/tmp/claude-0/-home-user-general/06cb9692-f1ed-541f-8ed7-e9c306cec4f3/scratchpad/edit")
import music

S = Path("/tmp/claude-0/-home-user-general/06cb9692-f1ed-541f-8ed7-e9c306cec4f3/scratchpad")
B = S/"edit/build"; AUD = B/"aud"; AUD.mkdir(parents=True, exist_ok=True)
SR = 44100
LEAD, TAIL = 0.40, 0.30

def run(cmd, **kw):
    # Binary input (the raw mix on stdin) must not go through text mode.
    text = not isinstance(kw.get("input"), (bytes, bytearray))
    r = subprocess.run(cmd, capture_output=True, text=text, **kw)
    if r.returncode != 0:
        err = r.stderr if isinstance(r.stderr, str) else r.stderr.decode("utf-8","replace")
        raise RuntimeError(f"failed: {' '.join(str(c) for c in cmd[:10])}\n{err[-700:]}")
    return r

def extract(path, tin, dur, out, speech=True):
    """Pull one clip's audio, level it, and clean it up if it carries speech."""
    if Path(out).exists() and Path(out).stat().st_size > 1000:
        return out
    af = []
    if speech:
        # Roll off the rumble a phone mic picks up from wind and handling, tame
        # sibilance slightly, then normalise so every take sits at the same level.
        af += ["highpass=f=95", "lowpass=f=11000",
               "afftdn=nr=10:nf=-32",            # gentle broadband noise reduction
               "loudnorm=I=-16:TP=-1.5:LRA=11"]
    else:
        af += ["highpass=f=120", "loudnorm=I=-30:TP=-6:LRA=14"]
    af += [f"afade=t=in:st=0:d=0.12", f"afade=t=out:st={max(0,dur-0.18):.3f}:d=0.18",
           "aformat=sample_fmts=fltp:sample_rates=44100:channel_layouts=stereo"]
    run(["ffmpeg","-hide_banner","-loglevel","error","-y","-nostdin",
         "-ss",f"{max(0,tin):.3f}","-t",f"{dur:.3f}","-i",str(path),
         "-vn","-af",",".join(af),"-ar",str(SR),"-ac","2",str(out)])
    return out

def load_np(path):
    p = subprocess.run(["ffmpeg","-hide_banner","-loglevel","error","-i",str(path),
                        "-f","f32le","-ar",str(SR),"-ac","2","pipe:1"],
                       capture_output=True)
    a = np.frombuffer(p.stdout, dtype=np.float32)
    return a.reshape(-1,2) if a.size else np.zeros((0,2), dtype=np.float32)

def place(track, clip, at):
    p = int(at*SR)
    if p < 0: clip = clip[-p:]; p = 0
    e = min(len(track), p+len(clip))
    if e > p: track[p:e] += clip[:e-p]

if __name__ == "__main__":
    shots = json.load(open(B/"timeline_final.json"))
    total = json.load(open(B/"meta.json"))["total"]
    n = int(total*SR)+SR
    speech = np.zeros((n,2), dtype=np.float32)
    ambi   = np.zeros((n,2), dtype=np.float32)

    talks = [(i,s) for i,s in enumerate(shots) if s["kind"]=="talk"]
    brolls= [(i,s) for i,s in enumerate(shots) if s["kind"]=="bvideo"]
    print(f"{len(talks)} O-Ton-Takes, {len(brolls)} Atmo-Clips", flush=True)

    def do_talk(arg):
        i,s = arg
        lead = min(LEAD, s["tin"])
        dur = s["dur"] + lead + TAIL
        f = AUD/f"t{i:04d}.wav"
        extract(s["path"], s["tin"]-lead, dur, f, speech=True)
        return i, s["start"]-lead, f

    def do_amb(arg):
        i,s = arg
        f = AUD/f"a{i:04d}.wav"
        extract(s["path"], s.get("tin",0), s["dur"], f, speech=False)
        return i, s["start"], f

    with ThreadPoolExecutor(max_workers=4) as ex:
        for i, at, f in ex.map(do_talk, talks):
            place(speech, load_np(f), at)
        for i, at, f in ex.map(do_amb, brolls):
            place(ambi, load_np(f), at)
    print("Sprache und Atmo montiert", flush=True)

    # ---- score
    import render
    sc = render.build_score([{"beat":s["beat"],"start":s["start"],"end":s["end"]} for s in shots], total)
    score = np.stack([sc, sc], axis=1)[:n]
    if len(score) < n:
        score = np.vstack([score, np.zeros((n-len(score),2), dtype=np.float32)])

    # ---- duck the music wherever anyone speaks
    # envelope of the speech track, smoothed, drives the music level
    env = np.abs(speech).max(axis=1)
    win = int(0.05*SR)
    kern = np.ones(win)/win
    env = np.convolve(env, kern, mode="same")
    # attack fast, release slow, so music does not pump between words
    thr, depth = 0.02, 0.26          # music drops to 26% under speech
    target = np.where(env > thr, depth, 1.0).astype(np.float32)
    # The follower is recursive, so it cannot be vectorised -- but ducking does not
    # need sample accuracy. Running it at 200 Hz and interpolating back up turns
    # 24 million Python iterations into a hundred thousand.
    CR = 200
    hop = SR // CR
    coarse = target[::hop]
    a_coef, r_coef = np.exp(-1/(0.03*CR)), np.exp(-1/(0.55*CR))
    out = np.empty_like(coarse)
    cur = 1.0
    for j, tv in enumerate(coarse):
        c = a_coef if tv < cur else r_coef   # fast to duck, slow to come back
        cur = tv + (cur - tv)*c
        out[j] = cur
    duck = np.interp(np.arange(n), np.arange(len(out))*hop, out).astype(np.float32)
    score *= duck[:,None]

    mix = speech*1.0 + ambi*0.55 + score*0.85
    # final limiter
    peak = np.abs(mix).max()
    if peak > 0.99: mix = mix/peak*0.99
    mix = np.tanh(mix*1.05)*0.96
    raw = (np.clip(mix,-1,1)*32767).astype(np.int16).tobytes()
    run(["ffmpeg","-hide_banner","-loglevel","error","-y","-f","s16le","-ar",str(SR),
         "-ac","2","-i","pipe:0","-c:a","aac","-b:a","256k",str(B/"mix.m4a")], input=raw)
    print("Mix fertig", flush=True)
