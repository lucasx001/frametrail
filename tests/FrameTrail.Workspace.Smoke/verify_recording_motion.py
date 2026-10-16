"""Independent decode and pixel checks for --recording-motion; requires ffmpeg/ffprobe."""
import json
import math
import pathlib
import subprocess
import sys

root = pathlib.Path(sys.argv[1])


def rgb(path, seconds=None):
    args = ["ffmpeg", "-v", "error"]
    if seconds is not None:
        args += ["-ss", str(seconds)]
    return subprocess.check_output(args + ["-i", str(path), "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", "-"])


def differences(a, b):
    assert len(a) == len(b) == 640 * 360 * 3
    delta = [abs(x-y) for x, y in zip(a, b)]
    mse = sum(x*x for x in delta) / len(delta)
    return {"mae": sum(delta) / len(delta), "max": max(delta), "psnr": 10 * math.log10(255**2 / max(mse, 1e-12))}


results = {}
for name in ("on", "off"):
    project = json.loads((root / f"{name}.frametrailproject" / "project.json").read_text(encoding="utf-8"))
    assert len(project["effects"]) == (1 if name == "on" else 0)
    if name == "on":
        effect = project["effects"][0]
        assert effect["parameters"]["origin"] == "automatic"
        assert effect["sourceStart"]["ticks"] + effect["sourceDuration"]["ticks"] < 25_000_000
    movie = root / f"{name}.mp4"
    subprocess.check_call(["ffmpeg", "-v", "error", "-i", str(movie), "-f", "null", "-"])
    probe = json.loads(subprocess.check_output(["ffprobe", "-v", "error", "-count_frames", "-show_streams", "-of", "json", str(movie)]))
    video = next(s for s in probe["streams"] if s["codec_type"] == "video")
    assert int(video["nb_read_frames"]) == 90 and abs(float(video["duration"]) - 3) < .04
    for label, seconds in (("before", .2), ("during", 1.5), ("after", 2.8)):
        preview = rgb(root / f"{name}-{label}.png")
        neutral = rgb(root / f"{name}-{label}-neutral.png")
        camera = differences(preview, neutral)
        # Independent GPU decoders can differ by a few color levels; a zoom
        # must change geometry substantially, while the idle frames stay full.
        if name == "on" and label == "during":
            assert camera["mae"] > 10, (name, label, camera)
        else:
            assert camera["mae"] < .5 and camera["max"] < 8, (name, label, camera)
        exported = differences(preview, rgb(movie, seconds))
        assert exported["mae"] < 8 and exported["psnr"] > 25, (name, label, exported)
        results[f"{name}-{label}"] = {"cameraDifference": camera, "previewExport": exported}

report = {"success": True, "checks": ["Recorder switch persists automatic motion or leaves effects empty", "Stationary cursor returns to full view", "GPU preview and exported MP4 share the camera trajectory", "Both MP4 files fully decode with 90 frames over 3 seconds"], "comparisons": results}
(root / "independent-verification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps({"success": True, "comparisons": len(results), "worstPreviewExportMae": max(v["previewExport"]["mae"] for v in results.values())}))
