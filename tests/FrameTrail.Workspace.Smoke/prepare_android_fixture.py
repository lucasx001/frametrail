"""Generate real H.264 access units for an in-process scrcpy protocol test peer."""
import json
import pathlib
import re
import subprocess
import sys

root = pathlib.Path(sys.argv[1])
root.mkdir(parents=True, exist_ok=True)
video = root / "phone.h264"
size = sys.argv[2] if len(sys.argv) > 2 else "320x240"
width, height = map(int, size.split("x"))
subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
                f"testsrc2=size={size}:rate=30:duration=1", "-c:v", "libx264", "-preset", "ultrafast",
                "-tune", "zerolatency", "-x264-params", "keyint=30:bframes=0:repeat-headers=1",
                "-pix_fmt", "yuv420p", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-f", "h264", str(video)], check=True)
packets = json.loads(subprocess.check_output(["ffprobe", "-v", "error", "-show_packets",
    "-show_entries", "packet=pos,size,flags", "-of", "json", str(video)]))["packets"]
raw = video.read_bytes()
first = raw[:int(packets[0]["size"])]
starts = list(re.finditer(b"\x00\x00(?:\x00)?\x01", first))
config = b""
for i, match in enumerate(starts):
    if first[match.end()] & 31 in (7, 8):
        end = starts[i+1].start() if i+1 < len(starts) else len(first)
        config += first[match.start():end]
assert config and len(packets) == 30
# MediaCodec sends SPS/PPS in separate configuration packets. Do not let test
# keyframes accidentally carry an in-band configuration that hides this case.
access_units = []
index = []
offset = 0
for packet in packets:
    block = raw[int(packet["pos"]):int(packet["pos"])+int(packet["size"])]
    nals = list(re.finditer(b"\x00\x00(?:\x00)?\x01", block))
    filtered = b""
    for i, nal in enumerate(nals):
        end = nals[i+1].start() if i+1 < len(nals) else len(block)
        if block[nal.end()] & 31 not in (7, 8):
            filtered += block[nal.start():end]
    assert filtered
    access_units.append(filtered)
    index.append({"position": offset, "size": len(filtered), "key": "K" in packet["flags"]})
    offset += len(filtered)
video.write_bytes(b"".join(access_units))
(root / "config.bin").write_bytes(config)
(root / "size.json").write_text(json.dumps({"width": width, "height": height}), encoding="utf-8")
(root / "packets.json").write_text(json.dumps(index), encoding="utf-8")
subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
    "sine=frequency=880:sample_rate=48000:duration=30", "-ac", "2", "-c:a", "pcm_s16le", str(root / "microphone.wav")], check=True)
