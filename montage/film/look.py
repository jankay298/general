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
    # Blend in RGB, not YUV. ffmpeg's blend works on whatever planes it is given,
    # and "screen" applied to the U/V chroma planes shifts colour instead of only
    # lifting brightness -- a neutral test blend that way turned a neutral frame
    # 26 points magenta. Converting to gbrp first makes screen mean what it says.
    f"format=gbrp,split=2[hbase][hglow];"
    # Isolate only genuinely bright areas: a lower threshold pulls most of a
    # daylit frame into the glow and tints the whole picture rather than blooming
    # its highlights. Built at quarter size because a wide blur is expensive and
    # nobody can tell afterwards.
    f"[hglow]scale={W//4}:{H//4},"
    "curves=r='0/0 0.78/0 0.90/0.22 1/0.85':"
    "g='0/0 0.80/0 0.92/0.16 1/0.72':"
    "b='0/0 0.84/0 0.95/0.09 1/0.55',"
    f"gblur=sigma=7,scale={W}:{H},"
    # halation is red-dominant, but only mildly: more separation reads as a cast
    "colorchannelmixer=rr=1.0:gg=0.80:bb=0.66[hg];"
    "[hbase][hg]blend=all_mode=screen:all_opacity=0.22,format=yuv420p"
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
    pad = max(1.0, zoom)
    tw, th = int(W * pad), int(H * pad)
    # Scale so BOTH sides cover the crop window, rounding UP to even numbers.
    # Deriving one side from the other and rounding to nearest can land a pixel
    # short: a source already at exactly 9:16 came out 2034 high against a 2035
    # crop, and ffmpeg rejects the filter outright rather than clamping.
    factor = max(tw / src_w, th / src_h)
    sw = max(tw, int(-(-src_w * factor // 2) * 2))
    sh = max(th, int(-(-src_h * factor // 2) * 2))
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


def fit_whole(src_w, src_h, dur=None, fps=30, zoom=1.0, seed=0):
    """Show the entire frame inside 9:16, filling the rest with a blurred copy.

    Cropping a landscape shot to 9:16 throws away two thirds of the picture. When
    what is in the frame matters -- a view, a table of food, both people side by
    side -- the whole image goes in the middle and its own blurred, darkened
    enlargement fills above and below, so nothing is lost and the frame still
    reads as one picture rather than a photo on black bars.
    """
    src_ar = src_w / src_h
    # foreground: whole image, as wide as the frame allows
    fw = W
    fh = int(round(W / src_ar / 2) * 2)
    if fh > H:                       # already tall: fall back to filling height
        fh = H; fw = int(round(H * src_ar / 2) * 2)
    chain = (
        f"split=2[wbg][wfg];"
        f"[wbg]scale={W}:{H}:force_original_aspect_ratio=increase:flags=fast_bilinear,"
        f"crop={W}:{H},gblur=sigma=42,eq=brightness=-0.16:saturation=0.72[wbgo];"
        f"[wfg]scale={fw}:{fh}:flags=lanczos[wfgo];"
        f"[wbgo][wfgo]overlay=(W-w)/2:(H-h)/2"
    )
    if zoom > 1.0 and dur:
        frames = max(1, int(round(dur * fps)))
        z = f"'min(1+(on/{frames})*{zoom-1:.5f},{zoom})'"
        chain += (f",zoompan=z={z}:d={frames}:x='(iw-iw/zoom)/2':y='(ih-ih/zoom)/2'"
                  f":s={W}x{H}:fps={fps}")
    return chain


def needs_whole(src_w, src_h, faces_span=None):
    """True when cropping to 9:16 would cost too much of the picture.

    A 4:3 frame loses about half its width; 16:9 loses two thirds. Anything wider
    than roughly 1.2:1 is shown whole unless the subject is a face filling the
    middle, where a crop is the better, closer framing.
    """
    ar = src_w / src_h
    if ar <= 1.15:
        return False
    if faces_span is not None and faces_span > 0.42:
        return False      # faces already fill the width: crop in on them
    return True
