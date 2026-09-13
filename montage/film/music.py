"""A small synthesiser: warm nylon-string arpeggios over a soft pad.

Not a replacement for a recorded track, but the aim is a score that breathes with
the film -- plucked notes with a real decay, a pad underneath, and tape wobble so
nothing sits perfectly in tune the way only a machine can.
"""
import numpy as np

SR = 44100

def note_hz(semitones_from_a4: float) -> float:
    return 440.0 * 2 ** (semitones_from_a4 / 12)

# Chord voicings as semitone offsets from A4, low to high.
CHORDS = {
    "Am":  [-24, -12, -9, -5, 0, 3],
    "F":   [-28, -16, -13, -9, -4, 0],
    "C":   [-21, -9, -5, -2, 3, 7],
    "G":   [-26, -14, -10, -7, -2, 2],
    "Dm":  [-19, -7, -4, 0, 5, 8],
    "E7":  [-17, -5, -1, 2, 5, 8],
    "Bb":  [-26, -14, -10, -7, -2, 1],
    "Em":  [-17, -5, -2, 2, 7, 10],
}

MOODS = {
    # progression, bpm, arpeggio pattern, pad level, brightness
    "warm":      (["Am","F","C","G"],      82, [0,2,4,3,2,4,3,2], 0.34, 1.0),
    "wistful":   (["Am","Em","F","C"],     72, [0,3,2,4,1,3,2,4], 0.40, 0.85),
    "playful":   (["C","G","Am","F"],      100, [0,4,2,5,3,4,2,5], 0.24, 1.15),
    "city":      (["Dm","Bb","F","C"],     108, [0,3,5,3,2,4,5,4], 0.22, 1.1),
    "tender":    (["F","C","Dm","Bb"],     66, [0,2,3,4,3,2,1,2],  0.46, 0.8),
    "farewell":  (["Am","F","C","E7"],     60, [0,2,4,5,4,3,2,1],  0.50, 0.75),
}

def _pluck(freq, dur, sr=SR, bright=1.0, amp=1.0):
    """A plucked string: sharp attack, long decay, a few inharmonic partials."""
    n = int(dur * sr)
    if n <= 0: return np.zeros(0, dtype=np.float32)
    t = np.arange(n) / sr
    env = np.exp(-t * (3.2 / max(dur, 0.35))) * (1 - np.exp(-t * 420))
    out = np.zeros(n)
    # Upper partials carry the "string" in a plucked sound; damping them as hard
    # as a naive envelope does leaves only a dull thud, so they stay present and
    # simply decay faster than the fundamental.
    for k, w in enumerate([1.0, 0.55, 0.38, 0.26, 0.17, 0.11, 0.07, 0.045], start=1):
        detune = 1 + (k - 1) * 0.0009          # slight inharmonicity, like a real string
        kdecay = np.exp(-t * (1.1 * (k - 1)))  # highs fade first, as on a real string
        out += w * (bright ** (0.45 * (k - 1))) * kdecay * np.sin(2*np.pi*freq*k*detune*t)
    # body resonance: a touch of the octave below, filtered soft
    out += 0.18 * np.sin(2 * np.pi * freq * 0.5 * t) * np.exp(-t * 4)
    # the click of nail on string: a few milliseconds of filtered noise
    nl = min(n, int(0.006 * sr))
    if nl > 4:
        rng = np.random.default_rng(int(freq) % 9973)
        click = rng.standard_normal(nl) * np.exp(-np.arange(nl) / (nl / 3))
        out[:nl] += click * 0.22
    return (out * env * amp).astype(np.float32)

def _pad(freqs, dur, sr=SR, amp=0.3):
    """A slow breathing pad: detuned saw-ish tones, soft attack."""
    n = int(dur * sr); t = np.arange(n) / sr
    out = np.zeros(n)
    for f in freqs:
        for det in (-0.14, 0.0, 0.15):
            ph = 2 * np.pi * (f + det) * t
            out += np.sin(ph) + 0.28 * np.sin(2 * ph) + 0.1 * np.sin(3 * ph)
    out /= max(1, len(freqs) * 3)
    atk = np.minimum(t / 0.9, 1.0)
    rel = np.minimum((dur - t) / 1.2, 1.0).clip(0, 1)
    lfo = 1 + 0.06 * np.sin(2 * np.pi * 0.13 * t)
    return (out * atk * rel * lfo * amp).astype(np.float32)

def _reverb(x, sr=SR, amount=0.26, decay=1.5):
    """Cheap convolution reverb: exponentially decaying noise."""
    n = int(decay * sr)
    ir = (np.random.default_rng(7).standard_normal(n) * np.exp(-np.arange(n) / (0.28 * sr)))
    ir[0] = 1.0
    ir /= np.abs(ir).sum() / 3
    wet = np.convolve(x, ir)[:len(x)]
    return ((1 - amount) * x + amount * wet).astype(np.float32)

def _wobble(x, sr=SR, cents=5.0, rate=0.7):
    """Tape wobble: a wandering read head, so the pitch never sits perfectly still."""
    n = len(x); t = np.arange(n) / sr
    drift = np.sin(2*np.pi*rate*t) * 0.6 + np.sin(2*np.pi*rate*0.37*t + 1.1) * 0.4
    shift = (2 ** (cents * drift / 1200) - 1)
    idx = np.clip(np.cumsum(1 + shift), 0, n - 1)
    return np.interp(idx, np.arange(n), x).astype(np.float32)

def render(mood: str, seconds: float, seed: int = 0) -> np.ndarray:
    """Render one continuous piece of the score."""
    prog, bpm, pattern, padlvl, bright = MOODS[mood]
    rng = np.random.default_rng(seed)
    beat = 60.0 / bpm
    step = beat / 2                     # eighth notes
    total = int(seconds * SR) + SR
    arp = np.zeros(total, dtype=np.float32)
    pad = np.zeros(total, dtype=np.float32)
    bars = int(np.ceil(seconds / (beat * 4))) + 1
    for bar in range(bars):
        chord = CHORDS[prog[bar % len(prog)]]
        t0 = bar * beat * 4
        pos = int(t0 * SR)
        if pos >= total: break
        pd = _pad([note_hz(s) for s in chord[:3]], beat * 4 + 0.6, amp=padlvl)
        pad[pos:pos+len(pd)] += pd[:max(0, total - pos)]
        for i, deg in enumerate(pattern):
            st = t0 + i * step
            p = int(st * SR)
            if p >= total: break
            f = note_hz(chord[min(deg, len(chord)-1)])
            # humanised timing and touch
            amp = 0.30 * (1.0 + rng.normal(0, 0.09)) * (1.18 if i % 4 == 0 else 0.9)
            p += int(rng.normal(0, 0.006) * SR)
            p = max(0, p)
            if p >= total:
                continue
            nt = _pluck(f, step * 3.0, bright=0.82*bright, amp=amp)
            end = min(total, p + len(nt))
            # Guard the slice: with end <= p the destination is empty while nt[:end-p]
            # would index from the end and hand back nearly the whole note.
            if end > p:
                arp[p:end] += nt[:end-p]
    mix = arp + pad
    mix = _wobble(mix, cents=4.5 + rng.random()*2)
    mix = _reverb(mix, amount=0.30 if mood in ("tender","farewell","wistful") else 0.22)
    # gentle tape saturation
    mix = np.tanh(mix * 1.25) * 0.8
    peak = np.abs(mix).max()
    if peak > 0: mix = mix / peak * 0.85
    return mix[:int(seconds * SR)].astype(np.float32)
