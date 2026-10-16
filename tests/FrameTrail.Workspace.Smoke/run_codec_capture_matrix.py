"""Capture only the benchmark's own animation; never records user applications."""
import json
import pathlib
import subprocess
import sys

benchmark, root = pathlib.Path(sys.argv[1]).resolve(), pathlib.Path(sys.argv[2]).resolve()
root.mkdir(parents=True, exist_ok=False)
checks = []
for codec in ("h264", "hevc", "av1"):
    for width, height in ((1920, 1080), (3840, 2160)):
        for fps in (30, 60):
            path = root / f"{codec}-{height}-{fps}.mp4"
            job = subprocess.run([str(benchmark), "3", str(path), "--codec", codec, "--fps", str(fps), "--size", str(width), str(height)], capture_output=True, timeout=60)
            output = job.stdout.decode("utf-8", errors="replace")
            error = job.stderr.decode("utf-8", errors="replace")
            (root / f"{codec}-{height}-{fps}.log").write_text(output + "\n" + error, encoding="utf-8")
            assert job.returncode == 0, (codec, height, fps, output, error)
            stats = json.loads(next(line for line in output.splitlines() if line.startswith("{")))
            assert stats["hr"] == 0 and stats["hardware"] and stats["written"] > 0, stats
            assert (stats["width"], stats["height"]) == (width, height), stats
            subprocess.run(["ffmpeg", "-v", "error", "-i", str(path), "-f", "null", "-"], check=True)
            probe = json.loads(subprocess.check_output(["ffprobe", "-v", "error", "-count_frames", "-show_streams", "-of", "json", str(path)]))
            video = next(s for s in probe["streams"] if s["codec_type"] == "video")
            assert video["codec_name"] == codec and int(video["nb_read_frames"]) == stats["written"], video
            assert abs(float(video["duration"]) - stats["durationSeconds"]) < .05, (video, stats)
            stats["height"] = height
            stats["meets90PercentRequestedFps"] = stats["fps"] >= fps * .9
            checks.append(stats)
            (root / "capture-matrix.json").write_text(json.dumps({"success": len(checks) == 12, "checks": checks}, indent=2), encoding="utf-8")
            print(json.dumps({"codec": codec, "height": height, "requestedFps": fps, "actualFps": stats["fps"], "dropped": stats["dropped"]}), flush=True)
print(json.dumps({"success": True, "captures": len(checks)}))
