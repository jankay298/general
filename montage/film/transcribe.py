import json, time
from pathlib import Path
from faster_whisper import WhisperModel
S = Path("/tmp/claude-0/-home-user-general/06cb9692-f1ed-541f-8ed7-e9c306cec4f3/scratchpad")
info = json.load(open(S/"edit/data/videos.json"))
model = WhisperModel("small", device="cpu", compute_type="int8", cpu_threads=4)
out = {}
t0 = time.time()
for n, c in enumerate(info, 1):
    try:
        segs, _ = model.transcribe(c["path"], language="de", vad_filter=True,
                                   vad_parameters=dict(min_silence_duration_ms=500),
                                   condition_on_previous_text=False)
        out[c["path"]] = [{"start": round(s.start,2), "end": round(s.end,2),
                           "text": s.text.strip(),
                           "logprob": round(s.avg_logprob,3),
                           "nospeech": round(s.no_speech_prob,3)} for s in segs]
    except Exception as e:
        out[c["path"]] = []
        print(f"FEHLER {Path(c['path']).name}: {e}", flush=True)
    if n % 20 == 0:
        print(f"{n}/{len(info)} ({time.time()-t0:.0f}s)", flush=True)
json.dump(out, open(S/"edit/data/transcripts.json","w"), ensure_ascii=False, indent=1)
spoken = sum(1 for v in out.values() if v)
words = sum(len(s["text"].split()) for v in out.values() for s in v)
print(f"\nfertig: {spoken}/{len(info)} Videos mit Sprache, {words} Wörter, {time.time()-t0:.0f}s")
