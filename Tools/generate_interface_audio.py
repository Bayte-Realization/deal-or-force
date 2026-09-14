"""Deterministic original UI earcons. Standard library only; no samples or AI model.
Run from the project root. Only writes its declared sound/audio/interface/*.wav files.
"""
import math
import random
import struct
import wave
from pathlib import Path

RATE = 44100
ROOT = Path(__file__).resolve().parents[1] / "game/sound/audio/interface"
# Semitones above A3; delay, note, duration, strength. Ascending = accepted, descending = release.
SCORES = {
    "country_selected": [(0, 7, .16, .8), (.075, 12, .22, .65)],
    "network_connected": [(0, 0, .3, .7), (.12, 7, .35, .7), (.24, 12, .5, .65)],
    "network_disconnected": [(0, 7, .3, .6), (.15, 0, .4, .65)],
    "player_ready": [(0, 7, .18, .65), (.10, 16, .32, .75)],
    "player_not_ready": [(0, 12, .18, .6), (.09, 7, .24, .6)],
    "action_denied": [(0, -5, .13, .65), (.14, -5, .2, .5)],
    "campaign_start": [(0, -12, .8, .5), (.05, 0, .7, .45), (.20, 7, .7, .5), (.4, 12, .9, .5)],
    "game_pause": [(0, 12, .18, .6), (.065, 7, .2, .5)],
    "game_resume": [(0, 7, .18, .5), (.065, 12, .22, .6)],
    "construction_queued": [(0, -5, .10, .7), (.06, 2, .18, .55)],
    "construction_complete": [(0, 0, .32, .7), (.14, 4, .34, .65), (.28, 7, .55, .7)],
    "policy_changed": [(0, -5, .3, .65), (.12, 2, .38, .6), (.23, 7, .5, .6)],
    "nation_formed": [(0, -12, 1.2, .4), (0, 0, 1.2, .35), (.25, 7, 1, .4), (.5, 12, 1.3, .5), (.65, 16, 1.1, .3)],
    "save_complete": [(0, 12, .16, .55), (.09, 19, .26, .55)],
}

def render(name, score):
    rng = random.Random(name)
    duration = max(start + length for start, _, length, _ in score) + .10
    samples = [0.0] * int(duration * RATE)
    for start, note, length, strength in score:
        frequency = 220 * 2 ** (note / 12)
        count = int(length * RATE)
        origin = int(start * RATE)
        for i in range(count):
            t = i / RATE
            edge = min(1, t / .008, (length - t) / .025)
            envelope = max(0, edge) * math.exp(-3.8 * t / length)
            phase = math.tau * frequency * t
            # Soft felt/mallet body; transient has no harsh high-frequency click.
            tone = math.sin(phase) + .20 * math.sin(2 * phase) + .07 * math.sin(3 * phase)
            transient = rng.uniform(-1, 1) * .025 * math.exp(-t * 100)
            samples[origin + i] += strength * envelope * (tone + transient)
    # A very short dark reflection adds space without masking the next interaction.
    dry = samples.copy()
    for delay, gain in [(.027, .09), (.047, .045)]:
        offset = int(delay * RATE)
        for i in range(offset, len(samples)):
            samples[i] += dry[i - offset] * gain
    peak = max(abs(v) for v in samples)
    gain = .50 / max(peak, .001)  # -6 dBFS ceiling, with in-game trims applied separately.
    pcm = [round(max(-.99, min(.99, v * gain)) * 32767) for v in samples]
    assert pcm[0] == 0 and pcm[-1] == 0 and max(map(abs, pcm)) < 32767
    ROOT.mkdir(parents=True, exist_ok=True)
    with wave.open(str(ROOT / (name + ".wav")), "wb") as out:
        out.setparams((1, 2, RATE, 0, "NONE", "not compressed"))
        out.writeframes(struct.pack("<" + "h" * len(pcm), *pcm))
    rms = math.sqrt(sum((p / 32768) ** 2 for p in pcm) / len(pcm))
    print(f"{name}: {len(pcm)/RATE:.2f}s, peak -6.0 dBFS, RMS {20*math.log10(rms):.1f} dBFS")

if __name__ == "__main__":
    for name, score in SCORES.items():
        render(name, score)
