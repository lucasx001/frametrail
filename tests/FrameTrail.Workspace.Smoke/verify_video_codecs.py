"""Independently verify real capture, encoder identity, AAC muxing and decode."""
import json
import pathlib
import subprocess
import sys

root = pathlib.Path(sys.argv[1])
report = json.loads((root / "codec-validation.json").read_text(encoding="utf-8-sig"))
assert report["success"], report
checks = []
for capability in report["capabilities"]:
    codec = capability["Codec"]
    if not capability["CanRecord"]:
        continue
    project = root / f"{codec}.frametrailproject"
    manifest = json.loads((project / "project.json").read_text(encoding="utf-8-sig"))
    duration = manifest["sourceDuration"]["ticks"] / manifest["sourceDuration"]["timescale"]
    assert manifest["export"]["videoCodec"] == codec
    has_audio = any(m["kind"] == "systemAudio" for m in manifest["media"])
    for item in manifest["media"]:
        if item["kind"] == "screen":
            path = project / item["path"]
            subprocess.run(["ffmpeg", "-v", "error", "-i", str(path), "-f", "null", "-"], check=True)
            info = json.loads(subprocess.check_output(["ffprobe", "-v", "error", "-show_streams", "-of", "json", str(path)]))
            video = next(s for s in info["streams"] if s["codec_type"] == "video")
            assert video["codec_name"] == codec and (video["width"], video["height"]) == (640, 360), video
    diagnostics = list((project / "recovery").glob("*.capture.json"))
    assert len(diagnostics) == 2, diagnostics
    for path in diagnostics:
        stats = json.loads(path.read_text(encoding="utf-8-sig"))["screen"]
        assert stats["FramesWritten"] > 0 and stats["ErrorCode"] == 0, stats
        if capability["HardwareCount"]:
            assert stats["HardwareActive"], stats
    for fps, suffix, dimensions in [(30, "", (640, 360)), (60, "-60", (1920, 1080))]:
        path = root / f"{codec}-export{suffix}.mp4"
        subprocess.run(["ffmpeg", "-v", "error", "-i", str(path), "-f", "null", "-"], check=True)
        info = json.loads(subprocess.check_output(["ffprobe", "-v", "error", "-count_frames", "-show_streams", "-of", "json", str(path)]))
        video = next(s for s in info["streams"] if s["codec_type"] == "video")
        assert video["codec_name"] == codec and (video["width"], video["height"]) == dimensions, video
        assert int(video["nb_read_frames"]) == int(-(-duration * fps // 1)), video
        assert abs(float(video["duration"]) - duration) < 0.04, video
        if has_audio:
            audio = next(s for s in info["streams"] if s["codec_type"] == "audio")
            assert audio["codec_name"] == "aac" and abs(float(audio["duration"]) - duration) < 0.08, audio
        checks.append({"codec": codec, "fps": fps, "size": dimensions, "frames": int(video["nb_read_frames"]), "aac": has_audio})
    raw = lambda path: subprocess.check_output(["ffmpeg", "-v", "error", "-i", str(path), "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", "-"])
    a, b = raw(root / f"{codec}-preview.png"), raw(root / f"{codec}-export.mp4")
    assert len(a) == len(b) == 640 * 360 * 3
    mae = sum(abs(x-y) for x, y in zip(a, b)) / len(a)
    assert mae < 5, (codec, mae)
    checks.append({"codec": codec, "preview_export_mae": mae})
(root / "independent-codec-verification.json").write_text(json.dumps({"success": True, "checks": checks}, indent=2), encoding="utf-8")
print(json.dumps({"success": True, "checks": checks}))
