"""Measure every photo and video frame: sharpness, exposure, faces, motion."""
import cv2, json, sys, time
import numpy as np
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor
import pillow_heif, PIL.Image as Image, PIL.ImageOps as ImageOps
pillow_heif.register_heif_opener()

S = Path("/tmp/claude-0/-home-user-general/06cb9692-f1ed-541f-8ed7-e9c306cec4f3/scratchpad")
MODEL = str(S/"edit/yunet.onnx")
PHOTO_EXT = {".jpg",".jpeg",".heic",".png"}
VIDEO_EXT = {".mp4",".mov",".m4v"}

def load_bgr(path, maxside=640):
    """Read any photo (HEIC included), upright, scaled down for analysis."""
    try:
        with Image.open(path) as im:
            im = ImageOps.exif_transpose(im).convert("RGB")
            im.thumbnail((maxside, maxside), Image.BILINEAR)
            return cv2.cvtColor(np.array(im), cv2.COLOR_RGB2BGR)
    except Exception:
        return None

def measure(img, det):
    """Sharpness, brightness, contrast and face boxes, all in 0..1 coordinates."""
    h, w = img.shape[:2]
    g = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    sharp = float(cv2.Laplacian(g, cv2.CV_64F).var())
    bright = float(g.mean()) / 255
    contrast = float(g.std()) / 255
    det.setInputSize((w, h))
    try:
        _, faces = det.detect(img)
    except cv2.error:
        faces = None
    boxes = []
    if faces is not None:
        for f in faces:
            x, y, fw, fh = f[:4]
            boxes.append([round(float(x)/w,4), round(float(y)/h,4),
                          round(float(fw)/w,4), round(float(fh)/h,4), round(float(f[-1]),3)])
    return {"sharp": round(sharp,1), "bright": round(bright,3),
            "contrast": round(contrast,3), "faces": boxes, "w": w, "h": h}

def do_photos(paths):
    det = cv2.FaceDetectorYN.create(MODEL, "", (320,320), 0.6, 0.3, 5000)
    out = {}
    for p in paths:
        img = load_bgr(p)
        if img is None: continue
        out[str(p)] = measure(img, det)
    return out

def do_video(path):
    """Sample one frame per second; also track how much the picture moves."""
    det = cv2.FaceDetectorYN.create(MODEL, "", (320,320), 0.6, 0.3, 5000)
    cap = cv2.VideoCapture(str(path))
    fps = cap.get(cv2.CAP_PROP_FPS) or 30
    total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0)
    step = max(1, int(round(fps)))
    frames, prev = [], None
    idx = 0
    while idx < total:
        cap.set(cv2.CAP_PROP_POS_FRAMES, idx)
        ok, fr = cap.read()
        if not ok: break
        h, w = fr.shape[:2]
        sc = 640/max(h,w)
        if sc < 1: fr = cv2.resize(fr, (int(w*sc), int(h*sc)))
        m = measure(fr, det)
        g = cv2.cvtColor(fr, cv2.COLOR_BGR2GRAY)
        small = cv2.resize(g, (64,64))
        m["motion"] = round(float(np.abs(small.astype(np.int16) -
                        prev.astype(np.int16)).mean()),2) if prev is not None else 0.0
        prev = small
        m["t"] = round(idx/fps, 2)
        frames.append(m)
        idx += step
    cap.release()
    return str(path), frames

if __name__ == "__main__":
    files = [p for p in (S/"media").rglob("*") if p.is_file()
             and not p.name.startswith("._") and "__MACOSX" not in p.parts]
    photos = [p for p in files if p.suffix.lower() in PHOTO_EXT]
    videos = [p for p in files if p.suffix.lower() in VIDEO_EXT]
    t0 = time.time()
    chunks = [photos[i::3] for i in range(3)]
    res = {}
    with ThreadPoolExecutor(max_workers=3) as ex:
        for r in ex.map(do_photos, chunks): res.update(r)
    json.dump(res, open(S/"edit/data/photos.json","w"), indent=0)
    print(f"Fotos: {len(res)} analysiert ({time.time()-t0:.0f}s)", flush=True)

    t0 = time.time(); vres = {}
    with ThreadPoolExecutor(max_workers=3) as ex:
        for n, (p, fr) in enumerate(ex.map(do_video, videos), 1):
            vres[p] = fr
            if n % 25 == 0: print(f"Videos {n}/{len(videos)} ({time.time()-t0:.0f}s)", flush=True)
    json.dump(vres, open(S/"edit/data/videoframes.json","w"), indent=0)
    print(f"Videos: {len(vres)} analysiert ({time.time()-t0:.0f}s)")
