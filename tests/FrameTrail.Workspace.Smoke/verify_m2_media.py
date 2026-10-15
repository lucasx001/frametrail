"""Independent image/audio checks. Python stdlib + ffmpeg; no product dependency."""
import array
import json
import math
from pathlib import Path
import subprocess
import sys
import wave

output, fixture = map(Path, sys.argv[1:3])

def rgb(path, extra=()):
    return subprocess.check_output(["ffmpeg", "-v", "error", "-i", str(path), *extra,
                                    "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"])

def image_metrics(a, b):
    assert len(a) == len(b), "Image dimensions differ"
    mae = sum(abs(x-y) for x, y in zip(a, b))/len(a)
    mse = sum((x-y)**2 for x, y in zip(a, b))/len(a)
    return {"mae": mae, "psnr": 100 if mse == 0 else 10*math.log10(255**2/mse)}

comparison = image_metrics(rgb(output/"edited-preview.png"), rgb(output/"edited-30.mp4"))
assert comparison["mae"] <= 3.5 and comparison["psnr"] >= 29, comparison
def probe(path):
    return json.loads(subprocess.check_output(["ffprobe", "-v", "error", "-show_streams", "-of", "json", str(path)]))
stream_durations = {s["codec_type"]: float(s["duration"]) for s in probe(output/"edited-30.mp4")["streams"]}
assert abs(stream_durations["video"]-2.25) < 0.00001
assert abs(stream_durations["audio"]-2.25) <= 1024/48000
assert abs(float(probe(output/"edited.m4a")["streams"][0]["duration"])-2.25) <= 1024/48000
native_frame = rgb(output/"exact-source-frame60.png")
expected_frame = rgb(fixture/"media/screen.mp4", ("-vf", r"select=eq(n\,60)"))
exact = image_metrics(native_frame, expected_frame)
# GPU chroma reconstruction and ffmpeg's software scaler differ at saturated
# high-frequency edges. Check color tolerance separately from temporal identity.
assert exact["mae"] <= 3.5 and exact["psnr"] >= 25, ("Source color conversion differs", exact)
def clock_crop(image):
    return b"".join(image[y*640*3:y*640*3+108*3] for y in range(38))
clock_scores = {}
for frame in range(58, 63):
    expected = rgb(fixture/"media/screen.mp4", ("-vf", f"select=eq(n\\,{frame})"))
    clock_scores[frame] = image_metrics(clock_crop(native_frame), clock_crop(expected))["mae"]
assert min(clock_scores, key=clock_scores.get) == 60, ("B-frame seek is inaccurate", clock_scores)

def samples(path):
    with wave.open(str(path)) as f:
        assert f.getsampwidth() == 2 and f.getframerate() == 48000 and f.getnchannels() == 2
        values = array.array("h", f.readframes(f.getnframes()))
    if sys.byteorder != "little": values.byteswap()
    return [v/32768 for v in values[::2]]

def power(values, hz):
    # Goertzel over a Hann window. Also test octave frequencies so a pitch shift
    # cannot pass merely because the expected 440 Hz bin has some energy.
    coefficient = 2*math.cos(2*math.pi*hz/48000)
    a = b = 0.0
    for i, v in enumerate(values):
        value = v*(0.5-0.5*math.cos(2*math.pi*i/max(1, len(values)-1))) + coefficient*a-b
        b, a = a, value
    return a*a+b*b-coefficient*a*b

audio = samples(output/"mixed.wav")
def rms(start, end):
    values = audio[int(start*48000):int(end*48000)]
    return math.sqrt(sum(v*v for v in values)/len(values))

silent, normal, boosted = rms(0, 0.9), rms(1.05, 1.6), rms(1.85, 2.2)
assert silent == 0 and abs(boosted/normal-2) < 0.03, (silent, normal, boosted)
pitch = []
both = samples(output/"both-tracks.wav")[9600:28800]
system_power, microphone_power = power(both, 440), power(both, 880)
assert system_power > 1 and abs(system_power/microphone_power-1) < 0.03, (system_power, microphone_power)
for numerator, denominator in [(1, 4), (1, 2), (1, 1), (3, 2), (2, 1), (4, 1)]:
    values = samples(output/f"pitch-{numerator}-{denominator}.wav")
    expected = int(3*denominator/numerator*48000)
    assert len(values) == expected, (numerator, denominator, len(values), expected)
    section = values[9600:28800]
    frequencies = [110, 220, 330, *range(430, 451), 660, 880, 1760]
    peak = max(frequencies, key=lambda hz: power(section, hz))
    assert abs(peak-440) <= 2, ("Pitch changed", numerator, denominator, peak)
    pitch.append({"rate": numerator/denominator, "frames": len(values), "peakHz": peak})

result = {"success": True, "previewVsH264": comparison, "streamDurations": stream_durations, "nativeSeekVsSourceFrame60": exact, "burnedFrameNumberScores": clock_scores, "twoTrackPowerRatio": system_power/microphone_power,
          "audioRms": {"muted": silent, "globalHalfGain": normal, "clipDoubleGain": boosted}, "pitch": pitch}
(output/"image-audio-metrics.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
print(json.dumps(result))
