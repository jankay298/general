"""Soundtrack for the screenplay cut: voices that outlast their pictures."""
import json, subprocess, sys, os, hashlib
import numpy as np
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor
sys.path.insert(0, "/tmp/claude-0/-home-user-general/06cb9692-f1ed-541f-8ed7-e9c306cec4f3/scratchpad/edit")
import music

S = Path("/tmp/claude-0/-home-user-general/06cb9692-f1ed-541f-8ed7-e9c306cec4f3/scratchpad")
B = S/"edit/build2"; AUD = B/"aud"; AUD.mkdir(parents=True, exist_ok=True)
SR = 44100; LEAD = 0.35

def run(cmd, **kw):
    text = not isinstance(kw.get("input"), (bytes, bytearray))
    r = subprocess.run(cmd, capture_output=True, text=text, **kw)
    if r.returncode:
        err = r.stderr if isinstance(r.stderr,str) else r.stderr.decode("utf-8","replace")
        raise RuntimeError(f"failed: {' '.join(str(c) for c in cmd[:9])}\n{err[-600:]}")
    return r

def grab(path, tin, dur, speech):
    key = hashlib.md5(f"{path}|{tin}|{dur}|{speech}".encode()).hexdigest()[:16]
    out = AUD/f"{key}.wav"
    if out.exists() and out.stat().st_size > 1000: return out
    af = (["highpass=f=95","lowpass=f=11000","afftdn=nr=10:nf=-32",
           "loudnorm=I=-16:TP=-1.5:LRA=11"] if speech else
          ["highpass=f=120","loudnorm=I=-30:TP=-6:LRA=14"])
    af += [f"afade=t=in:st=0:d=0.10",
           f"afade=t=out:st={max(0,dur-0.16):.3f}:d=0.16",
           "aformat=sample_fmts=fltp:sample_rates=44100:channel_layouts=stereo"]
    run(["ffmpeg","-hide_banner","-loglevel","error","-y","-nostdin",
         "-ss",f"{max(0,tin):.3f}","-t",f"{dur:.3f}","-i",str(path),
         "-vn","-af",",".join(af),"-ar",str(SR),"-ac","2",str(out)])
    return out

def load(p):
    r = subprocess.run(["ffmpeg","-hide_banner","-loglevel","error","-i",str(p),
                        "-f","f32le","-ar",str(SR),"-ac","2","pipe:1"], capture_output=True)
    a = np.frombuffer(r.stdout, dtype=np.float32)
    return a.reshape(-1,2) if a.size else np.zeros((0,2), np.float32)

def place(track, clip, at):
    p = int(at*SR)
    if p < 0: clip = clip[-p:]; p = 0
    e = min(len(track), p+len(clip))
    if e > p: track[p:e] += clip[:e-p]

if __name__ == "__main__":
    shots = json.load(open(B/"timeline.json"))
    total = json.load(open(B/"meta.json"))["total"]
    n = int(total*SR)+SR
    speech = np.zeros((n,2), np.float32)
    ambi = np.zeros((n,2), np.float32)

    # Voices: each take runs its full length from where its first shot starts,
    # regardless of how many cutaways the picture makes in the meantime.
    voice_jobs, amb_jobs = [], []
    for s in shots:
        if s.get("audio_dur"):
            src = s.get("audio_path", s.get("path"))
            voice_jobs.append((src, s.get("audio_tin", s.get("tin",0)), s["audio_dur"],
                               s["start"] - min(LEAD, s.get("audio_tin",0))))
        elif s["kind"] == "bvideo":
            amb_jobs.append((s["path"], s.get("tin",0), s["dur"], s["start"]))
    print(f"{len(voice_jobs)} Stimmen, {len(amb_jobs)} Atmo", flush=True)

    with ThreadPoolExecutor(max_workers=4) as ex:
        for (src,tin,dur,at), f in zip(voice_jobs, ex.map(lambda j: grab(j[0],j[1],j[2],True), voice_jobs)):
            place(speech, load(f), at)
        for (src,tin,dur,at), f in zip(amb_jobs, ex.map(lambda j: grab(j[0],j[1],j[2],False), amb_jobs)):
            place(ambi, load(f), at)

    # Score: one continuous cue per run of shots sharing a mood
    runs = []
    for s in shots:
        m = s.get("mood","modern_warm")
        if runs and runs[-1][0] == m: runs[-1][2] = s["end"]
        else: runs.append([m, s["start"], s["end"]])
    sc = np.zeros(n, np.float32)
    for i,(m,a,b) in enumerate(runs):
        piece = music.render_modern(m, b-a+1.6, seed=i*13+5)
        fi, fo = int(0.9*SR), int(1.3*SR)
        if len(piece) > fi+fo:
            piece[:fi] *= np.linspace(0,1,fi); piece[-fo:] *= np.linspace(1,0,fo)
        p = int(a*SR); e = min(n, p+len(piece))
        if e > p: sc[p:e] += piece[:e-p]
    pk = np.abs(sc).max()
    if pk: sc = sc/pk*0.82
    score = np.stack([sc,sc], axis=1)

    # Duck under speech
    env = np.abs(speech).max(axis=1)
    env = np.convolve(env, np.ones(int(0.05*SR))/int(0.05*SR), mode="same")
    target = np.where(env > 0.02, 0.24, 1.0).astype(np.float32)
    CR = 200; hop = SR//CR
    a_c, r_c = np.exp(-1/(0.03*CR)), np.exp(-1/(0.55*CR))
    coarse = target[::hop]; outv = np.empty_like(coarse); cur = 1.0
    for j,tv in enumerate(coarse):
        cur = tv + (cur-tv)*(a_c if tv < cur else r_c); outv[j] = cur
    duck = np.interp(np.arange(n), np.arange(len(outv))*hop, outv).astype(np.float32)
    score *= duck[:,None]

    mix = speech*1.0 + ambi*0.5 + score*0.9
    pk = np.abs(mix).max()
    if pk > 0.99: mix = mix/pk*0.99
    mix = np.tanh(mix*1.05)*0.96
    raw = (np.clip(mix,-1,1)*32767).astype(np.int16).tobytes()
    run(["ffmpeg","-hide_banner","-loglevel","error","-y","-f","s16le","-ar",str(SR),
         "-ac","2","-i","pipe:0","-c:a","aac","-b:a","256k",str(B/"mix.m4a")], input=raw)
    print("Mix fertig", flush=True)
