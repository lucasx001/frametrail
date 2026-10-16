"""Compare hot-edited WinUI preview pixels with fresh native evaluations."""
import json
import pathlib
import subprocess
import sys

root = pathlib.Path(sys.argv[1])
results = {}
for label in ("split", "trim", "crop", "frame", "canvas", "zoom", "undo", "redo"):
    def rgb(kind):
        return subprocess.check_output(["ffmpeg", "-v", "error", "-i", str(root / f"{label}-{kind}.png"), "-pix_fmt", "rgb24", "-f", "rawvideo", "-"])
    live, fresh = rgb("live"), rgb("fresh")
    assert len(live) == len(fresh) and live
    delta = [abs(a-b) for a, b in zip(live, fresh)]
    mae = sum(delta) / len(delta)
    assert mae < .6 and max(delta) < 12, (label, mae, max(delta))
    results[label] = {"mae": mae, "maximum": max(delta)}
report = {"success": True, "comparisons": results}
movie = root / "live-edited.mp4"
subprocess.check_call(["ffmpeg", "-v", "error", "-i", str(movie), "-f", "null", "-"])
probe = json.loads(subprocess.check_output(["ffprobe", "-v", "error", "-count_frames", "-show_streams", "-of", "json", str(movie)]))
video = next(s for s in probe["streams"] if s["codec_type"] == "video")
assert int(video["nb_read_frames"]) == 84 and abs(float(video["duration"]) - 2.8) < .04
report["editedMp4"] = {"frames": 84, "duration": float(video["duration"])}
(root / "independent-preview-verification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps({"success": True, "comparisons": len(results), "worstMae": max(v["mae"] for v in results.values())}))
