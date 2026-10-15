"""Independent visual regression: Python stdlib + ffmpeg/ffprobe, no engine code."""
import json
import math
from pathlib import Path
import subprocess
import struct
import sys

root = Path(sys.argv[1])

def rgb(path, frame=None):
    args = ["ffmpeg", "-v", "error", "-i", str(path)]
    if frame is not None:
        args += ["-vf", f"select=eq(n\\,{frame})"]
    return subprocess.check_output(args + ["-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"])

def metrics(a, b):
    assert len(a) == len(b) == 640*360*3
    mae = sum(abs(x-y) for x, y in zip(a, b))/len(a)
    mse = sum((x-y)**2 for x, y in zip(a, b))/len(a)
    return {"mae": mae, "psnr": 100 if mse == 0 else 10*math.log10(255**2/mse)}

def roi(image, x, y, width, height):
    return b"".join(image[(row*640+x)*3:(row*640+x+width)*3] for row in range(y, y+height))

def difference(a, b):
    return sum(abs(x-y) for x, y in zip(a, b))/len(a)

results = {}
for preview in sorted(root.glob("*-preview.png")):
    name = preview.name.removesuffix("-preview.png")
    movie = root/(name+".mp4")
    assert movie.exists(), name
    info = json.loads(subprocess.check_output(["ffprobe", "-v", "error", "-show_streams", "-of", "json", str(movie)]))
    video = next(s for s in info["streams"] if s["codec_type"] == "video")
    fps = 60 if name == "combined-60" else 30
    assert video["width"] == 640 and video["height"] == 360
    assert int(video["nb_frames"]) == fps*3 and abs(float(video["duration"])-3) < 0.00001, name
    assert all(video[k] == "bt709" for k in ("color_space", "color_transfer", "color_primaries")), name
    subprocess.run(["ffmpeg", "-v", "error", "-i", str(movie), "-f", "null", "-"], check=True, capture_output=True)
    frame = round((2.5 if name.startswith("background-") else 1)*fps)
    result = metrics(rgb(preview), rgb(movie, frame))
    # H.264 4:2:0 discards chroma at saturated test-pattern edges. Bound both
    # average color error and energy; PNG-to-PNG deterministic checks stay exact.
    assert result["mae"] < 3.5 and result["psnr"] > 27, (name, result)
    results[name] = {**result, "frames": fps*3, "fps": fps, "fullDecode": True, "color": "bt709"}

neutral = rgb(root/"neutral-preview.png")
changes = {}
for name in ("zoom", "tilt", "solid", "pixelate", "blur", "focus", "sticker", "text", "intro", "camera", "combined", "canvas-mask"):
    changed = rgb(root/(name+"-preview.png"))
    changes[name] = difference(neutral, changed)
    assert changes[name] > 0.1, ("Effect produced no visible change", name)

solid = rgb(root/"solid-preview.png")
assert max(roi(solid, 210, 130, 160, 80)) == 0, "Solid mask leaked source pixels"
assert max(roi(rgb(root/"privacy-overlap-preview.png"), 210, 130, 160, 80)) == 0, "Overlapping blur revealed opaque privacy mask"
focus = rgb(root/"focus-preview.png")
assert difference(roi(neutral, 210, 130, 100, 60), roi(focus, 210, 130, 100, 60)) < 0.1, "Focus changed its preserved center"
assert difference(roi(neutral, 80, 55, 100, 45), roi(focus, 80, 55, 100, 45)) > 0.5, "Focus failed to blur outside"
canvas_mask = rgb(root/"canvas-mask-preview.png")
assert max(roi(canvas_mask, 470, 240, 120, 75)) == 0, "Canvas mask did not cover the camera layer"
sticker = rgb(root/"sticker-preview.png")
pixel = (127*640+226)*3
expected = [neutral[pixel+c]*0.6 + (255*0.4 if c == 0 else 0) for c in range(3)]
assert max(abs(sticker[pixel+c]-expected[c]) for c in range(3)) < 3, ("Premultiplied sticker alpha differs", list(sticker[pixel:pixel+3]), expected)
assert rgb(root/"slow.png") == rgb(root/"fast.png"), "Preview rate changed motion pixels"
trajectory = metrics(rgb(root/"slow.png"), rgb(root/"combined-60.mp4", 90))
assert trajectory["mae"] < 3.5 and trajectory["psnr"] > 27, ("Preview/export trajectory differs", trajectory)
assert rgb(root/"portable-original.png") == rgb(root/"portable.png"), "Project relocation changed its visual assets"
with_cursor, without_cursor = rgb(root/"combined-preview.png"), rgb(root/"combined-no-cursor-preview.png")
changed = [i for i in range(640*360) if max(abs(with_cursor[i*3+c]-without_cursor[i*3+c]) for c in range(3)) > 8]
assert changed, "Projected pointer disappeared"
bounds = (min(i % 640 for i in changed), min(i // 640 for i in changed), max(i % 640 for i in changed), max(i // 640 for i in changed))
f32 = lambda v: struct.unpack("f", struct.pack("f", v))[0]
weights = [math.exp(-(1-i*.05)/.08) for i in range(14, 21)]
cursor_x = sum(f32(.75+i/600)*w for i, w in zip(range(14, 21), weights))/sum(weights)
pan_x, pan_y = 320-(51.2+.7*537.6)*2, 180-(28.8+.4*302.4)*2
x, y = (51.2+cursor_x*537.6)*2+pan_x-320, (28.8+f32(.38)*302.4)*2+pan_y-180
ax, ay = math.radians(18), math.radians(-24)
z, yy = y*math.sin(ax), y*math.cos(ax)
xx, z = x*math.cos(ay)+z*math.sin(ay), -x*math.sin(ay)+z*math.cos(ay)
w = 1+z/1600
tip = (xx/w+320, yy/w+180)
assert abs(bounds[0]-tip[0]) < 6 and abs(bounds[1]-tip[1]) < 6 and bounds[2]-bounds[0] < 65 and bounds[3]-bounds[1] < 65, ("Zoom/tilt pointer attachment differs", bounds, tip)
report = {"success": True, "comparisons": results, "effectDifferences": changes, "checks": ["Full MP4 decode, duration, frame count and BT.709", "Same-time preview/export pixels", "Solid privacy mask", "Focus center and exterior", "Canvas mask covers camera", "Premultiplied sticker alpha", "Preview-rate determinism", "Portable visual assets"]}
report["previewExportTrajectory"] = trajectory
report["projectedPointer"] = {"differenceBounds": bounds, "expectedTip": tip}
(root/"independent-media-verification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps({"success": True, "comparisons": len(results), "worstMae": max(v["mae"] for v in results.values()), "minimumPsnr": min(v["psnr"] for v in results.values())}))
