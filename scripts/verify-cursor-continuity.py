"""Exercise native preview and export with real video and checkpoint pointer files.

Requires ffmpeg on PATH and a built FrameTrail.Media.dll. No desktop capture or
Python packages are used. Outputs are retained in a new validation directory.
"""
import argparse
import ctypes
import json
from pathlib import Path
import subprocess


def run(*args):
    return subprocess.run(args, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE).stdout


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--native-bin", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    root = output / "project"
    root.mkdir()
    (root / "media").mkdir()
    (root / "events").mkdir()
    run("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "color=c=black:s=160x90:r=30:d=60",
        "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-profile:v", "baseline",
        "-pix_fmt", "yuv420p", "-g", "30", str(root / "media/black.mp4"))
    dll = ctypes.CDLL(str(args.native_bin.resolve() / "FrameTrail.Media.dll"))
    dll.ft_media_create.argtypes = [ctypes.c_char_p, ctypes.c_uint32, ctypes.c_wchar_p, ctypes.POINTER(ctypes.c_uint64)]
    dll.ft_media_destroy.argtypes = [ctypes.c_uint64]
    dll.ft_media_render.argtypes = [ctypes.c_uint64, ctypes.c_int64, ctypes.c_uint64, ctypes.c_wchar_p]
    dll.ft_media_export.argtypes = [ctypes.c_uint64, ctypes.c_wchar_p, ctypes.c_uint32]
    dll.ft_media_error.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32]

    def check(code):
        if code < 0:
            error = ctypes.create_unicode_buffer(4096)
            dll.ft_media_error(error, len(error))
            raise RuntimeError(f"native HRESULT {code & 0xffffffff:08x}: {error.value}")

    def time(value):
        return {"ticks": round(value * 10_000_000), "timescale": 10_000_000}

    def write_tracks(left_x, right_x, click=False):
        for index, x in enumerate((left_x, right_x)):
            rows = [{"type": "pointer", "ticks": sample * 1_000_000, "timescale": 10_000_000,
                     "x": x, "y": 0.5, "inside": True, "visible": True,
                     "kind": 1 if click and index == 0 and sample == 599 else 0}
                    for sample in range(600)]
            (root / f"events/pointer-{index}.jsonl").write_text(
                "\n".join(json.dumps(row) for row in rows) + "\n", encoding="utf-8")

    def project(offset=0, hide=False, start=59, duration=2):
        media = []
        for index in range(2):
            media.extend([
                {"id": f"screen-{index}", "kind": "screen", "path": "media/black.mp4",
                 "duration": time(60), "startOffset": time(60 * index), "width": 160, "height": 90},
                {"id": f"pointer-{index}", "kind": "pointerEvents", "path": f"events/pointer-{index}.jsonl",
                 "duration": time(60), "startOffset": time(60 * index)}])
        return {"schemaVersion": 1, "projectId": "cursor-continuity-validation", "revision": 1,
                "sourceDuration": time(120), "media": media,
                "segments": [{"id": "take", "sourceStart": time(start), "sourceDuration": time(duration),
                              "rate": {"numerator": 1, "denominator": 1}}],
                "canvas": {"width": 160, "height": 90, "padding": 0,
                           "background": {"r": 0, "g": 0, "b": 0, "a": 1},
                           "screen": {"x": 0, "y": 0, "width": 1, "height": 1},
                           "camera": {"enabled": False, "x": 0, "y": 0, "width": 1, "height": 1, "mirror": False}},
                "effects": [], "markers": [],
                "editor": {"cursorStyle": "dot", "cursorScale": 1, "cursorSmoothing": 0,
                           "cursorTimeOffset": offset, "cursorHideWhenIdle": hide, "cursorIdleDelay": 8,
                           "cursorClickStyle": "ripple", "cornerRadius": 0, "shadow": 0},
                "export": {"width": 160, "height": 90, "fps": {"numerator": 30, "denominator": 1},
                           "videoCodec": "h264", "audioCodec": "aac", "container": "mp4"}}

    def render(settings, output_time, name, export=False):
        encoded = json.dumps(settings).encode()
        handle = ctypes.c_uint64()
        check(dll.ft_media_create(encoded, len(encoded), str(root), ctypes.byref(handle)))
        try:
            image = root / f"{name}.png"
            check(dll.ft_media_render(handle, round(output_time * 10_000_000), 0, str(image)))
            if export:
                check(dll.ft_media_export(handle, str(output / f"{name}.mp4"), 0))
            return image
        finally:
            dll.ft_media_destroy(handle)

    def rgb(path, frame=None):
        args = ["ffmpeg", "-v", "error", "-i", str(path)]
        if frame is not None:
            args.extend(["-vf", f"select=eq(n\\,{frame})"])
        return run(*args, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "-")

    def bright_at(data, center):
        return sum(1 for y in range(38, 53) for x in range(center - 7, center + 8)
                   if min(data[(y * 160 + x) * 3:(y * 160 + x) * 3 + 3]) > 150)

    write_tracks(0.25, 0.75)
    positive = render(project(offset=0.2), 0.9, "positive", export=True)
    negative = render(project(offset=-0.2), 1.1, "negative")
    checks = {"positive_preview_white_pixels": bright_at(rgb(positive), 120),
              "negative_preview_white_pixels": bright_at(rgb(negative), 40),
              "positive_export_white_pixels": bright_at(rgb(output / "positive.mp4", frame=27), 120)}
    assert min(checks.values()) > 20, checks
    write_tracks(0.5, 0.5)
    idle = render(project(hide=True, start=0, duration=120), 60.1, "idle")
    checks["idle_preview_white_pixels"] = bright_at(rgb(idle), 80)
    assert checks["idle_preview_white_pixels"] == 0, checks
    write_tracks(0.25, 0.75, click=True)
    click_image = rgb(render(project(), 1.1, "click"))
    # The previous file's ripple remains blue at x=40 after the checkpoint;
    # the current dot is at x=120 and cannot satisfy this region.
    checks["prior_checkpoint_ripple_pixels"] = sum(
        1 for y in range(20, 70) for x in range(15, 65)
        if click_image[(y * 160 + x) * 3 + 2] > click_image[(y * 160 + x) * 3] + 20)
    assert checks["prior_checkpoint_ripple_pixels"] > 10, checks
    (output / "validation.json").write_text(json.dumps(checks, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(checks))


if __name__ == "__main__":
    main()
