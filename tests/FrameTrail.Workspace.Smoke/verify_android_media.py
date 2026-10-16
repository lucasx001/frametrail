"""Independent demux/decode checks: no dependency on FrameTrail's parser."""
import json
import math
import pathlib
import subprocess
import sys
import wave
import struct

root = pathlib.Path(sys.argv[1])
checks = []
excluded = []
for name, size in (("phone-live.png", (320, 240)), ("portrait-live.png", (360, 800))):
    pixels = subprocess.check_output(["ffmpeg", "-v", "error", "-i", str(root/name), "-pix_fmt", "rgb24", "-f", "rawvideo", "-"])
    assert len(pixels) == size[0]*size[1]*3 and max(pixels)-min(pixels) > 100
    assert sum(p > 32 for p in pixels) > len(pixels)*0.15, f"Black native preview: {name}"
    checks.append(name + ": live native decoder/GPU output contains the expected nonblack test pattern")
for path in sorted(root.glob("**/*.mp4")):
    if "segment-" in path.name:
        project = path.parent.parent
        manifest = json.loads((project / "project.json").read_text(encoding="utf-8-sig"))
        if path.relative_to(project).as_posix() not in {m["path"] for m in manifest["media"]}:
            excluded.append(str(path.relative_to(root)))
            continue
    info = json.loads(subprocess.check_output(["ffprobe", "-v", "error", "-show_streams", "-of", "json", str(path)]))
    video = next(s for s in info["streams"] if s["codec_type"] == "video")
    expected = (360, 800) if "portrait.frametrailproject" in path.parts else (320, 240)
    assert video["codec_name"] == "h264" and (video["width"], video["height"]) == expected
    packets = json.loads(subprocess.check_output(["ffprobe", "-v", "error", "-select_streams", "v", "-show_packets", "-show_entries", "packet=pts_time,flags", "-of", "json", str(path)]))["packets"]
    assert packets and "K" in packets[0]["flags"], str(path)
    pts = [float(p["pts_time"]) for p in packets]
    assert abs(pts[0]) < 0.001 and all(b > a for a, b in zip(pts, pts[1:])), str(path)
    # Packet-arrival jitter must not alter the media clock's 30 Hz cadence.
    if "segment-" in path.name:
        assert all(abs((b-a)-1/30) < 0.0001 for a,b in zip(pts,pts[1:])), str(path)
    subprocess.run(["ffmpeg", "-v", "error", "-xerror", "-i", str(path), "-f", "null", "-"], check=True, capture_output=True)
    checks.append(str(path.relative_to(root)) + ": H.264, keyframe at zero, increasing PTS, full decode")
with wave.open(str(root / "phone-mix.wav")) as audio:
    assert audio.getnframes() > 48000 and audio.getnchannels() == 2
    samples = audio.readframes(audio.getnframes())
    assert any(samples), "Phone audio was silently dropped by the mixer"
    pcm = struct.unpack("<" + "h" * (len(samples)//2), samples)
    channel = pcm[0:48000:2]
    for frequency in (440, 880):
        amplitude = 2 * abs(sum(value * complex(math.cos(-2*math.pi*frequency*i/48000), math.sin(-2*math.pi*frequency*i/48000)) for i,value in enumerate(channel))) / (32768*len(channel))
        assert amplitude > 0.025, f"Missing independent {frequency}Hz phone/microphone contribution: {amplitude}"
for name in ("phone-export.mp4", "phone-export.m4a"):
    info = json.loads(subprocess.check_output(["ffprobe", "-v", "error", "-show_streams", "-of", "json", str(root/name)]))
    assert any(s["codec_type"] == "audio" and s["codec_name"] == "aac" for s in info["streams"])
assert any("process-crash" in path for path in excluded), "Unfinished crash segment was not excluded"
(root / "independent-media-validation.json").write_text(json.dumps({"success": True, "checks": checks, "excludedPending": excluded, "phoneAudioMixedAndExported": True}, indent=2), encoding="utf-8")
print(f"Independent Android validation passed: {len(checks)-2} MP4 files, 2 live native images, phone PCM mix and AAC export.")
