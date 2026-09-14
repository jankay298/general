"""Render the screenplay: picture, voice carried over cutaways, score, mix."""
import json, subprocess, sys, os, hashlib
import numpy as np
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor
sys.path.insert(0, "/tmp/claude-0/-home-user-general/06cb9692-f1ed-541f-8ed7-e9c306cec4f3/scratchpad/edit")
from look import frame_filter, fit_whole, GRADE, W, H, FONT_DISPLAY, FONT_TEXT
import music

S = Path("/tmp/claude-0/-home-user-general/06cb9692-f1ed-541f-8ed7-e9c306cec4f3/scratchpad")
B = S/"edit/build2"; SEG = B/"seg"; AUD = B/"aud"; PC = B/"photos"
for d in (SEG, AUD, PC): d.mkdir(parents=True, exist_ok=True)
FPS, SR = 30, 44100
LEAD, TAIL = 0.35, 0.30
MOVES = [None, "in", "out", "up", "down", "left", "right"]

def run(cmd, **kw):
    text = not isinstance(kw.get("input"), (bytes, bytearray))
    r = subprocess.run(cmd, capture_output=True, text=text, **kw)
    if r.returncode:
        err = r.stderr if isinstance(r.stderr, str) else r.stderr.decode("utf-8","replace")
        raise RuntimeError(f"failed: {' '.join(str(c) for c in cmd[:9])}\n{err[-700:]}")
    return r

def dur_of(p):
    r = subprocess.run(["ffprobe","-v","error","-show_entries","format=duration",
                        "-of","csv=p=0",str(p)], capture_output=True, text=True)
    try: return float(r.stdout.strip())
    except ValueError: return 0.0

def prepared(path):
    """Upright JPEG: ffmpeg cannot read HEIC and ignores EXIF rotation on stills."""
    import pillow_heif, PIL.Image as Image, PIL.ImageOps as ImageOps
    pillow_heif.register_heif_opener()
    out = PC/f"{hashlib.md5(path.encode()).hexdigest()[:16]}.jpg"
    if out.exists() and out.stat().st_size > 1000: return str(out)
    with Image.open(path) as im:
        im = ImageOps.exif_transpose(im)
        if im.mode != "RGB": im = im.convert("RGB")
        im.thumbnail((W*2, H*2), Image.LANCZOS)
        im.save(out, "JPEG", quality=96, subsampling=0)
    return str(out)

def vdims(path):
    """Dimensions as ffmpeg will produce them, rotation matrix applied."""
    r = subprocess.run(["ffprobe","-v","error","-select_streams","v:0","-show_entries",
        "stream=width,height:stream_side_data=rotation","-of","json",str(path)],
        capture_output=True, text=True)
    st = json.loads(r.stdout)["streams"][0]
    w, h = int(st["width"]), int(st["height"])
    rot = 0
    for sd in st.get("side_data_list", []) or []:
        if "rotation" in sd: rot = abs(int(sd["rotation"])) % 180
    return (h, w) if rot == 90 else (w, h)

def render_shot(args):
    i, s = args
    out = SEG/f"{i:04d}.mp4"
    if out.exists() and abs(dur_of(out) - s["dur"]) < 0.2: return str(out)
    if out.exists(): out.unlink()
    dur = s["dur"]

    if s["kind"] == "title":
        m = s["main"].replace("'", r"\'"); sb = s["sub"].replace("'", r"\'")
        hb = s.get("hold_black", 0.0)
        vf = (f"drawtext=fontfile={FONT_DISPLAY}:text='{m}':fontcolor=0xf4efe4:fontsize=118:"
              f"x=(w-text_w)/2:y=(h-text_h)/2-66:"
              f"alpha='min(min(max(t-{hb},0)/0.8,1),max(0,({dur}-t)/0.6))',"
              f"drawtext=fontfile={FONT_TEXT}:text='{sb}':fontcolor=0xc3b9a8:fontsize=40:"
              f"x=(w-text_w)/2:y=(h-text_h)/2+62:"
              f"alpha='min(min(max(t-{hb+0.3},0)/0.8,1),max(0,({dur}-t)/0.6))',"
              f"noise=alls=4:allf=t+u,vignette=angle=PI/6,fps={FPS},setsar=1,format=yuv420p")
        run(["ffmpeg","-hide_banner","-loglevel","error","-y","-f","lavfi",
             "-i",f"color=c=0x090909:s={W}x{H}:d={dur}","-vf",vf,
             "-c:v","libx264","-preset","medium","-crf","17","-pix_fmt","yuv420p",str(out)])
        return str(out)

    if s["kind"] == "photo":
        src = prepared(s["path"])
        from PIL import Image as _I
        with _I.open(src) as im: w, h = im.size
        seed = int(hashlib.md5(s["path"].encode()).hexdigest()[:8], 16)
        move = MOVES[seed % len(MOVES)] or "in"
        # a quick cut has no time for a move; a held frame does
        zoom = 1.075 if dur > 1.2 else 1.03
    else:
        src = s["path"]; w, h = vdims(src); move, zoom = None, 1.0

    if s.get("whole"):
        vf = fit_whole(w, h, dur=dur, fps=FPS, zoom=1.03 if s["kind"]=="photo" else 1.0)
    else:
        vf = frame_filter(w, h, s["cx"], s["cy"], zoom=zoom, dur=dur, fps=FPS, move=move)
    vf += "," + GRADE + f",fps={FPS},setsar=1,format=yuv420p"
    if s.get("fade_in"):  vf += f",fade=t=in:st=0:d={s['fade_in']}"
    if s.get("fade_out"): vf += f",fade=t=out:st={max(0,dur-s['fade_out']):.2f}:d={s['fade_out']}"

    cmd = ["ffmpeg","-hide_banner","-loglevel","error","-y","-nostdin"]
    if s["kind"] == "photo":
        cmd += ["-loop","1","-t",f"{dur:.3f}","-i",src]
    else:
        cmd += ["-ss",f"{s.get('tin',0):.3f}","-t",f"{dur:.3f}","-i",src]
    cmd += ["-an","-vf",vf,"-c:v","libx264","-preset","medium","-crf","17",
            "-pix_fmt","yuv420p","-r",str(FPS),"-t",f"{dur:.3f}",str(out)]
    run(cmd)
    return str(out)

if __name__ == "__main__":
    shots = json.load(open(S/"edit/data/sequence2.json"))
    limit = int(os.environ.get("LIMIT","0"))
    if limit: shots = shots[:limit]
    shots[0]["fade_in"] = 0.6
    shots[-1]["fade_out"] = 2.0
    jobs = int(os.environ.get("JOBS","3"))
    print(f"{len(shots)} Einstellungen", flush=True)
    work = list(enumerate(shots))
    paths = [None]*len(shots)
    done = 0
    with ThreadPoolExecutor(max_workers=jobs) as ex:
        for (i, s), p in zip(work, ex.map(render_shot, work)):
            paths[i] = p; done += 1
            if done % 25 == 0: print(f"  {done}/{len(shots)}", flush=True)

    t = 0.0
    for i, s in enumerate(shots):
        real = dur_of(paths[i])
        if real <= 0.05: raise RuntimeError(f"Segment {i} leer")
        s["dur"] = round(real, 3); s["start"] = round(t, 3); t += s["dur"]; s["end"] = round(t, 3)
    total = t
    json.dump(shots, open(B/"timeline.json","w"), indent=1)
    lst = B/"concat.txt"; lst.write_text("".join(f"file '{p}'\n" for p in paths))
    run(["ffmpeg","-hide_banner","-loglevel","error","-y","-f","concat","-safe","0",
         "-i",str(lst),"-c","copy",str(B/"picture.mp4")])
    got = dur_of(B/"picture.mp4")
    if abs(got-total) > 1.5: raise RuntimeError(f"Bildspur {got:.1f}s statt {total:.1f}s")
    print(f"Bild fertig: {got/60:.2f} Min", flush=True)
    json.dump({"total": total}, open(B/"meta.json","w"))
