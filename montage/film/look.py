"""Framing and grade: how a source shot becomes one cinematic 9:16 frame."""
W, H = 1080, 1920
TARGET_AR = W / H
FONT_DISPLAY = "/mnt/skills/examples/canvas-design/canvas-fonts/Italiana-Regular.ttf"
FONT_TEXT = "/mnt/skills/examples/canvas-design/canvas-fonts/InstrumentSans-Regular.ttf"

# What separates film from a phone clip with a filter is mostly light behaviour:
# bright areas bleed a soft red halo into their surroundings (halation, from light
# scattering back off the film base), highlights bloom rather than clip, shadows
# never reach pure black, and colour separates -- cool shadows, warm highlights.
#
# The glow is built at quarter resolution: a 25px blur on a 1080x1920 frame costs
# more than the whole rest of the chain, and after blurring nobody can tell.
HALATION = (
    "split=2[hbase][hglow];"
    # isolate the brightest areas only
    "[hglow]scale=iw/4:ih/4,"
    "curves=r='0/0 0.62/0.03 0.80/0.45 1/1':"
    "g='0/0 0.66/0.02 0.86/0.34 1/0.9':"
    "b='0/0 0.72/0.01 0.92/0.2 1/0.72',"
    "gblur=sigma=7,scale=iw*4:ih*4,"
    # halation is red-dominant: that is the physical signature
    "colorchannelmixer=rr=1.0:gg=0.55:bb=0.38[hg];"
    "[hbase][hg]blend=all_mode=screen:all_opacity=0.30"
)

# Filmic contrast: an S-curve that lifts the toe (no crushed blacks) and rolls the
# shoulder off gently, with the channels pulled apart for colour separation.
FILM_CURVE = (
    "curves="
    "r='0/0.045 0.18/0.17 0.5/0.52 0.82/0.86 1/0.985':"
    "g='0/0.042 0.18/0.165 0.5/0.505 0.82/0.84 1/0.965':"
    "b='0/0.062 0.18/0.19 0.5/0.485 0.82/0.79 1/0.925'"
)

GRADE = (
    HALATION + ","
    + FILM_CURVE + ","
    "eq=saturation=0.96:contrast=1.06:gamma=1.01,"
    # sharpen after scaling: film reads crisp without looking digitally over-sharpened
    "unsharp=5:5:0.55:3:3:0.0,"
    # fine grain, finer than the default and animated so it never freezes
    "noise=alls=3:allf=t+u,"
    "vignette=angle=PI/6:mode=backward"
)

def frame_filter(src_w, src_h, cx, cy, zoom=1.0, dur=None, fps=30, move=None):
    """Scale and crop any source into 9:16, keeping cx/cy in view.

    cx/cy are 0..1 coordinates of what must stay visible -- the faces, when the
    detector found any. Cropping blind to that is what puts people's heads
    outside the frame when a landscape shot becomes vertical.
    """
    src_ar = src_w / src_h
    pad = max(1.0, zoom)
    tw, th = int(W * pad), int(H * pad)
    if src_ar > TARGET_AR:            # wider than 9:16 -> fill height, crop width
        sw = int(round(th * src_ar / 2) * 2); sh = th
    else:                              # taller -> fill width, crop height
        sw = tw; sh = int(round(tw / src_ar / 2) * 2)
    max_x, max_y = max(0, sw - tw), max(0, sh - th)
    x = min(max(cx * sw - tw / 2, 0), max_x)
    y = min(max(cy * sh - th / 2, 0), max_y)
    chain = f"scale={sw}:{sh}:flags=lanczos,crop={tw}:{th}:{int(x)}:{int(y)}"
    if zoom > 1.0 and dur:
        frames = max(1, int(round(dur * fps)))
        # A slow push or pull; direction alternates per shot so a montage does not
        # feel like one long mechanical zoom.
        if move == "out":
            z = f"'max({zoom}-(on/{frames})*{zoom-1:.5f},1.0)'"
        else:
            z = f"'min(1+(on/{frames})*{zoom-1:.5f},{zoom})'"
        px = {"left": "0", "right": "(iw-iw/zoom)", None: "(iw-iw/zoom)/2"}.get(move, "(iw-iw/zoom)/2")
        py = {"up": "0", "down": "(ih-ih/zoom)"}.get(move, "(ih-ih/zoom)/2")
        chain += (f",zoompan=z={z}:d={frames}:x='{px}':y='{py}':s={W}x{H}:fps={fps}")
    else:
        chain += f",scale={W}:{H}:flags=lanczos"
    return chain
