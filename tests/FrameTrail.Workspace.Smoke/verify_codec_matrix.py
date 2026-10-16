"""Verify bounded codec/export/capture matrices independently with FFmpeg."""
import json
import pathlib
import subprocess
import sys

root = pathlib.Path(sys.argv[1])
report = json.loads((root / "export-matrix.json").read_text(encoding="utf-8-sig"))
assert report["success"], report
checks = []
for item in report["results"]:
    if item.get("faultProtection"):
        continue
    codec, height, fps = item["codec"], item["height"], item["fps"]
    path = root / f"{codec}-{height}-{fps}.mp4"
    subprocess.run(["ffmpeg", "-v", "error", "-i", str(path), "-f", "null", "-"], check=True)
    probe = json.loads(subprocess.check_output(["ffprobe", "-v", "error", "-count_frames", "-show_streams", "-of", "json", str(path)]))
    video = next(s for s in probe["streams"] if s["codec_type"] == "video")
    audio = next(s for s in probe["streams"] if s["codec_type"] == "audio")
    assert video["codec_name"] == codec and (video["width"], video["height"]) == (height * 16 // 9, height), video
    assert int(video["nb_read_frames"]) == fps // 2 and abs(float(video["duration"]) - .5) < .04, video
    assert audio["codec_name"] == "aac" and abs(float(audio["duration"]) - .5) < .08, audio
    checks.append({"codec": codec, "height": height, "fps": fps, "frames": int(video["nb_read_frames"]), "bytes": path.stat().st_size})
assert len(checks) == 18, checks
captures = []
if len(sys.argv) > 2:
    capture_root = pathlib.Path(sys.argv[2])
    captured = json.loads((capture_root / "capture-matrix.json").read_text(encoding="utf-8-sig"))
    assert captured["success"] and len(captured["checks"]) == 12, captured
    for item in captured["checks"]:
        path = capture_root / f"{item['codec']}-{item['height']}-{item['requestedFps']}.mp4"
        def small_frame(seconds):
            return subprocess.check_output(["ffmpeg", "-v", "error", "-ss", str(seconds), "-i", str(path), "-vf", "scale=160:90", "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", "-"])
        first, later = small_frame(.3), small_frame(1.7)
        assert len(first) == len(later) == 160 * 90 * 3
        motion = sum(abs(a-b) for a, b in zip(first, later)) / len(first)
        assert motion > 1, (path, motion)
        captures.append({"codec": item["codec"], "height": item["height"], "requestedFps": item["requestedFps"], "actualFps": item["fps"], "dropped": item["dropped"], "motionDifference": motion})
(root / "independent-matrix-verification.json").write_text(json.dumps({"success": True, "checks": checks, "captures": captures}, indent=2), encoding="utf-8")
print(json.dumps({"success": True, "exports": len(checks), "captures": len(captures)}))
