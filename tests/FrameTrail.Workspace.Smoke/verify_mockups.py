"""Compare native mockup previews with independently decoded MP4 frames."""
import json
import pathlib
import subprocess
import sys

root = pathlib.Path(sys.argv[1])
report = json.loads((root / "mockup-validation.json").read_text(encoding="utf-8-sig"))
assert report["success"], report["failure"]
checks, images = [], {}
for item in report["renders"]:
    video = item["video"]
    subprocess.run(["ffmpeg", "-v", "error", "-i", video, "-f", "null", "-"], check=True)
    info = json.loads(subprocess.check_output(["ffprobe", "-v", "error", "-count_frames", "-show_streams", "-of", "json", video]))
    stream = next(s for s in info["streams"] if s["codec_type"] == "video")
    assert (stream["width"], stream["height"], stream["r_frame_rate"]) == (640, 360, "30/1"), stream
    assert int(stream["nb_read_frames"]) > 0
    def rgb(path):
        return subprocess.check_output(["ffmpeg", "-v", "error", "-i", str(path), "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", "-"])
    preview, decoded = rgb(item["preview"]), rgb(video)
    assert len(preview) == len(decoded) == 640 * 360 * 3
    error = sum(abs(a - b) for a, b in zip(preview, decoded)) / len(preview)
    assert error < 5, (item["style"], error)
    images[item["style"]] = preview
    checks.append({"style": item["style"], "frames": int(stream["nb_read_frames"]), "preview_export_mae": error})
for first, second in [("none", "windowLight"), ("windowLight", "windowDark"), ("browserLight", "browserDark"), ("none", "devicePhone"), ("browserDark", "browser-custom")]:
    changed = sum(max(abs(a-b) for a,b in zip(images[first][i:i+3], images[second][i:i+3])) > 12 for i in range(0, len(images[first]), 3))
    assert changed > 400, (first, second, changed)
    checks.append({"pair": [first, second], "changed_pixels": changed})
(root / "independent-mockup-verification.json").write_text(json.dumps({"success": True, "checks": checks}, indent=2), encoding="utf-8")
print(json.dumps({"success": True, "checks": checks}))
