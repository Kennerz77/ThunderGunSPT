"""Trims a 16-bit PCM WAV to a length with a linear fade-out at the end.

The BO1 port's shots run 9-10 s of rumble; in game the Thundergun's blast should be about 3 s.
Usage: python -I trim_wav.py in.wav out.wav seconds fade_seconds
"""
import array
import sys
import wave

src, dst, seconds, fade = sys.argv[1], sys.argv[2], float(sys.argv[3]), float(sys.argv[4])
with wave.open(src) as w:
    channels, width, rate = w.getnchannels(), w.getsampwidth(), w.getframerate()
    if width != 2:
        raise SystemExit(f"{src}: only 16-bit PCM is supported")
    frames = min(w.getnframes(), int(seconds * rate))
    samples = array.array('h', w.readframes(frames))

fade_frames = int(fade * rate)
start = frames - fade_frames
for f in range(max(0, start), frames):
    gain = (frames - f) / fade_frames
    for c in range(channels):
        i = f * channels + c
        samples[i] = int(samples[i] * gain)

with wave.open(dst, 'wb') as w:
    w.setnchannels(channels)
    w.setsampwidth(width)
    w.setframerate(rate)
    w.writeframes(samples.tobytes())
print(f"{dst}: {frames / rate:.2f} s")
