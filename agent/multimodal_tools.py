"""Phase 2 multimodal video perception tools for the FrameTrail AI editing agent.

Provides:
- transcribe_project_audio: ASR transcription (faster-whisper / OpenAI Whisper / offline VAD fallback)
- detect_silence_and_pauses: Pure-Python WAV RMS energy silence detection + FrameTrail batch cut generator
- search_transcript_and_clicks: Correlates ASR transcript segments with pointer clicks & dwell hotspots
- inspect_video_frames: Extracts keyframes via ffmpeg and optionally queries a Vision LLM for UI NormalizedRects
"""

from __future__ import annotations

import base64
import json
import math
import mimetypes
import os
import re
import shutil
import struct
import subprocess
import tempfile
import urllib.error
import urllib.request
import uuid
import wave
from pathlib import Path
from typing import Any

try:
    from langchain_core.tools import tool
except ImportError:  # pragma: no cover - fallback when langchain_core is absent

    def tool(fn=None, *, return_direct: bool = False):  # type: ignore[misc]
        """Minimal @tool fallback preserving callability and LangChain-like metadata."""

        def _decorate(func):
            func.name = func.__name__
            func.description = (func.__doc__ or "").strip()
            func.return_direct = return_direct
            func.invoke = lambda args: func(**args) if isinstance(args, dict) else func(args)
            return func

        return _decorate(fn) if fn is not None else _decorate


TICKS_PER_SECOND = 10_000_000
MIN_KEEP_TICKS = 1_000_000  # 0.1s minimum kept segment duration
MAX_BATCH_COMMANDS = 100
MAX_SUBTITLE_TEXT_BYTES = 1024
MAX_SUBTITLE_WORDS = 256
MAX_SUBTITLE_CUES = 10_000

_AUDIO_ONLY_EXTENSIONS = {".wav", ".m4a", ".mp3", ".aac", ".flac", ".ogg", ".opus"}
_VIDEO_WITH_AUDIO_EXTENSIONS = {".mp4", ".mov", ".mkv", ".webm", ".m4v", ".avi"}
_ALL_MEDIA_AUDIO_EXTENSIONS = _AUDIO_ONLY_EXTENSIONS | _VIDEO_WITH_AUDIO_EXTENSIONS

VALID_SUBTITLE_PRESETS = (
    "classicOutline",
    "darkPill",
    "karaokePop",
    "bilingualClean",
    "custom",
)
VALID_HIGHLIGHT_MODES = ("none", "wordColor", "wordPill")
VALID_ALIGNMENTS = ("left", "center", "right")

SUBTITLE_STYLE_PRESETS: dict[str, dict[str, Any]] = {
    "classicOutline": {
        "preset": "classicOutline",
        "fontFamily": "Alibaba PuHuiTi 3.0",
        "fontSize": 42.0,
        "textColor": "#FFFFFF",
        "outlineColor": "#000000",
        "outlineWidth": 3.0,
        "shadowEnabled": True,
        "shadowBlur": 6.0,
        "shadowOpacity": 0.55,
        "backgroundColor": "#111114",
        "backgroundOpacity": 0.0,
        "backgroundRadius": 12.0,
        "highlightColor": "#FACC15",
        "highlightMode": "none",
        "positionX": 0.5,
        "positionY": 0.88,
        "alignment": "center",
        "maxWidthRatio": 0.84,
        "maxCharsPerLine": 18,
        "bilingualEnabled": False,
        "secondaryScale": 0.72,
        "fadeSeconds": 0.12,
    },
    "darkPill": {
        "preset": "darkPill",
        "fontFamily": "Alibaba PuHuiTi 3.0",
        "fontSize": 40.0,
        "textColor": "#FFFFFF",
        "outlineColor": "#000000",
        "outlineWidth": 0.0,
        "shadowEnabled": False,
        "shadowBlur": 4.0,
        "shadowOpacity": 0.35,
        "backgroundColor": "#111114",
        "backgroundOpacity": 0.78,
        "backgroundRadius": 14.0,
        "highlightColor": "#FACC15",
        "highlightMode": "none",
        "positionX": 0.5,
        "positionY": 0.88,
        "alignment": "center",
        "maxWidthRatio": 0.84,
        "maxCharsPerLine": 18,
        "bilingualEnabled": False,
        "secondaryScale": 0.72,
        "fadeSeconds": 0.12,
    },
    "karaokePop": {
        "preset": "karaokePop",
        "fontFamily": "Alibaba PuHuiTi 3.0",
        "fontSize": 46.0,
        "textColor": "#FFFFFF",
        "outlineColor": "#000000",
        "outlineWidth": 3.5,
        "shadowEnabled": True,
        "shadowBlur": 8.0,
        "shadowOpacity": 0.6,
        "backgroundColor": "#111114",
        "backgroundOpacity": 0.0,
        "backgroundRadius": 12.0,
        "highlightColor": "#FACC15",
        "highlightMode": "wordPill",
        "positionX": 0.5,
        "positionY": 0.86,
        "alignment": "center",
        "maxWidthRatio": 0.84,
        "maxCharsPerLine": 16,
        "bilingualEnabled": False,
        "secondaryScale": 0.72,
        "fadeSeconds": 0.08,
    },
    "bilingualClean": {
        "preset": "bilingualClean",
        "fontFamily": "Alibaba PuHuiTi 3.0",
        "fontSize": 38.0,
        "textColor": "#FFFFFF",
        "outlineColor": "#000000",
        "outlineWidth": 2.5,
        "shadowEnabled": True,
        "shadowBlur": 6.0,
        "shadowOpacity": 0.5,
        "backgroundColor": "#111114",
        "backgroundOpacity": 0.55,
        "backgroundRadius": 10.0,
        "highlightColor": "#FACC15",
        "highlightMode": "none",
        "positionX": 0.5,
        "positionY": 0.88,
        "alignment": "center",
        "maxWidthRatio": 0.86,
        "maxCharsPerLine": 18,
        "bilingualEnabled": True,
        "secondaryScale": 0.72,
        "fadeSeconds": 0.12,
    },
}

SUBTITLE_STYLE_KEYS = tuple(SUBTITLE_STYLE_PRESETS["classicOutline"].keys())


def build_subtitle_style(
    existing: dict[str, Any] | None = None,
    *,
    preset: str | None = None,
    font_family: str | None = None,
    font_size: float | None = None,
    text_color: str | None = None,
    outline_color: str | None = None,
    outline_width: float | None = None,
    shadow_enabled: bool | None = None,
    shadow_blur: float | None = None,
    shadow_opacity: float | None = None,
    background_color: str | None = None,
    background_opacity: float | None = None,
    background_radius: float | None = None,
    highlight_color: str | None = None,
    highlight_mode: str | None = None,
    position_x: float | None = None,
    position_y: float | None = None,
    alignment: str | None = None,
    max_width_ratio: float | None = None,
    max_chars_per_line: int | None = None,
    bilingual_enabled: bool | None = None,
    secondary_scale: float | None = None,
    fade_seconds: float | None = None,
    canvas: dict[str, Any] | None = None,
) -> dict[str, Any]:
    """Construct a schema-valid editor.subtitleStyle dict with preset defaults and overrides."""
    if preset is not None:
        cleaned_preset = preset.strip()
        if cleaned_preset not in VALID_SUBTITLE_PRESETS:
            raise ValueError(
                f"Unsupported subtitle preset '{preset}'. Expected one of: {', '.join(VALID_SUBTITLE_PRESETS)}."
            )
    else:
        cleaned_preset = None

    if cleaned_preset and cleaned_preset in SUBTITLE_STYLE_PRESETS:
        style = dict(SUBTITLE_STYLE_PRESETS[cleaned_preset])
    else:
        style = dict(SUBTITLE_STYLE_PRESETS["classicOutline"])
        if isinstance(existing, dict):
            for key in SUBTITLE_STYLE_KEYS:
                if key in existing and existing[key] is not None:
                    style[key] = existing[key]
        if cleaned_preset == "custom":
            style["preset"] = "custom"

    # Portrait 9:16 default vertical safe-area awareness when creating fresh style without explicit position_y
    if (
        existing is None
        and position_y is None
        and isinstance(canvas, dict)
        and int(canvas.get("height", 0) or 0) > int(canvas.get("width", 0) or 0)
    ):
        style["positionY"] = 0.78

    any_custom_override = False
    if font_family is not None and font_family.strip():
        style["fontFamily"] = font_family.strip()
        any_custom_override = True
    if font_size is not None:
        style["fontSize"] = round(max(12.0, min(160.0, float(font_size))), 2)
        any_custom_override = True
    if text_color is not None and text_color.strip():
        style["textColor"] = text_color.strip()
        any_custom_override = True
    if outline_color is not None and outline_color.strip():
        style["outlineColor"] = outline_color.strip()
        any_custom_override = True
    if outline_width is not None:
        style["outlineWidth"] = round(max(0.0, min(20.0, float(outline_width))), 2)
        any_custom_override = True
    if shadow_enabled is not None:
        style["shadowEnabled"] = bool(shadow_enabled)
        any_custom_override = True
    if shadow_blur is not None:
        style["shadowBlur"] = round(max(0.0, min(40.0, float(shadow_blur))), 2)
        any_custom_override = True
    if shadow_opacity is not None:
        style["shadowOpacity"] = round(max(0.0, min(1.0, float(shadow_opacity))), 3)
        any_custom_override = True
    if background_color is not None and background_color.strip():
        style["backgroundColor"] = background_color.strip()
        any_custom_override = True
    if background_opacity is not None:
        style["backgroundOpacity"] = round(max(0.0, min(1.0, float(background_opacity))), 3)
        any_custom_override = True
    if background_radius is not None:
        style["backgroundRadius"] = round(max(0.0, min(64.0, float(background_radius))), 2)
        any_custom_override = True
    if highlight_color is not None and highlight_color.strip():
        style["highlightColor"] = highlight_color.strip()
        any_custom_override = True
    if highlight_mode is not None:
        hm = highlight_mode.strip()
        if hm not in VALID_HIGHLIGHT_MODES:
            raise ValueError(
                f"Unsupported highlight_mode '{highlight_mode}'. Expected one of: {', '.join(VALID_HIGHLIGHT_MODES)}."
            )
        style["highlightMode"] = hm
        any_custom_override = True
    if position_x is not None:
        style["positionX"] = round(max(0.05, min(0.95, float(position_x))), 4)
        any_custom_override = True
    if position_y is not None:
        style["positionY"] = round(max(0.05, min(0.95, float(position_y))), 4)
        any_custom_override = True
    if alignment is not None:
        al = alignment.strip().lower()
        if al not in VALID_ALIGNMENTS:
            raise ValueError(
                f"Unsupported alignment '{alignment}'. Expected one of: {', '.join(VALID_ALIGNMENTS)}."
            )
        style["alignment"] = al
        any_custom_override = True
    if max_width_ratio is not None:
        style["maxWidthRatio"] = round(max(0.2, min(1.0, float(max_width_ratio))), 3)
        any_custom_override = True
    if max_chars_per_line is not None:
        style["maxCharsPerLine"] = max(4, min(80, int(max_chars_per_line)))
        any_custom_override = True
    if bilingual_enabled is not None:
        style["bilingualEnabled"] = bool(bilingual_enabled)
        any_custom_override = True
    if secondary_scale is not None:
        style["secondaryScale"] = round(max(0.4, min(1.0, float(secondary_scale))), 3)
        any_custom_override = True
    if fade_seconds is not None:
        style["fadeSeconds"] = round(max(0.0, min(2.0, float(fade_seconds))), 3)
        any_custom_override = True

    if cleaned_preset is None and any_custom_override and existing is not None:
        style["preset"] = "custom"

    return {k: style[k] for k in SUBTITLE_STYLE_KEYS}


def _media_time_to_ticks(value: Any, default: int = 0) -> int:
    if isinstance(value, (int, float)):
        return int(round(value))
    if not isinstance(value, dict):
        return default
    ticks = int(value.get("ticks", 0))
    timescale = int(value.get("timescale", TICKS_PER_SECOND))
    if timescale <= 0:
        return default
    return int(round(ticks * TICKS_PER_SECOND / timescale))


def _ticks_to_sec(ticks: int) -> float:
    return round(ticks / TICKS_PER_SECOND, 4)


def _sec_to_ticks(sec: float) -> int:
    return int(round(sec * TICKS_PER_SECOND))


def _resolve_project_path(project_dir: str | Path, relative_path: str) -> Path:
    root = Path(project_dir).resolve()
    rel = relative_path.replace("\\", "/").lstrip("/")
    candidate = (root / rel).resolve()
    try:
        candidate.relative_to(root)
    except ValueError as exc:
        raise ValueError(f"Path escapes project directory: {relative_path}") from exc
    return candidate


def _load_project_json(project_dir: str | Path) -> dict[str, Any]:
    project_file = Path(project_dir) / "project.json"
    if not project_file.is_file():
        return {}
    return json.loads(project_file.read_text(encoding="utf-8"))


def _speaker_label(kind: str) -> str:
    if kind == "microphone":
        return "mic"
    if kind == "systemAudio":
        return "system"
    if kind == "androidAudio":
        return "android"
    if kind in ("embeddedAudio", "screen", "camera"):
        return "video"
    return kind or "audio"


def _ranges_overlap(a_start: int, a_dur: int, b_start: int, b_dur: int) -> bool:
    if a_dur <= 0 or b_dur <= 0:
        return True
    a_end = a_start + a_dur
    b_end = b_start + b_dur
    return a_start < b_end and b_start < a_end


def _find_audio_assets(project_dir: Path, project_data: dict[str, Any]) -> list[dict[str, Any]]:
    """Discover both .wav recording tracks and imported .mp4/.mov/.m4a clips with audio.

    Returns list of:
        {path: Path, kind: str, speaker: str, startTicks: int, durationTicks: int, needsExtract: bool}
    """
    dedicated_assets: list[dict[str, Any]] = []
    video_candidates: list[dict[str, Any]] = []
    seen_paths: set[Path] = set()

    for item in project_data.get("media", []):
        kind = str(item.get("kind", ""))
        rel = item.get("path", "")
        if not rel:
            continue
        try:
            full = _resolve_project_path(project_dir, rel)
        except ValueError:
            continue
        if not full.is_file():
            continue
        suffix = full.suffix.lower()
        start_ticks = _media_time_to_ticks(item.get("startOffset"), 0)
        dur_ticks = _media_time_to_ticks(item.get("duration"), 0)

        if kind in ("microphone", "systemAudio", "androidAudio"):
            if suffix in _ALL_MEDIA_AUDIO_EXTENSIONS:
                seen_paths.add(full)
                dedicated_assets.append(
                    {
                        "path": full,
                        "kind": kind,
                        "speaker": _speaker_label(kind),
                        "startTicks": start_ticks,
                        "durationTicks": dur_ticks,
                        "needsExtract": suffix != ".wav",
                    }
                )
        elif kind in ("screen", "camera") and suffix in _VIDEO_WITH_AUDIO_EXTENSIONS:
            video_candidates.append(
                {
                    "path": full,
                    "kind": "embeddedAudio",
                    "speaker": _speaker_label(kind),
                    "startTicks": start_ticks,
                    "durationTicks": dur_ticks,
                    "needsExtract": True,
                }
            )

    assets: list[dict[str, Any]] = list(dedicated_assets)

    # Include video files whose time span is not already covered by a dedicated audio track
    # (e.g. imported .mp4/.mov clips or projects without separate mic/system .wav recordings).
    for cand in video_candidates:
        if cand["path"] in seen_paths:
            continue
        covered_by_dedicated = any(
            _ranges_overlap(
                cand["startTicks"],
                cand["durationTicks"],
                ded["startTicks"],
                ded["durationTicks"],
            )
            for ded in dedicated_assets
        )
        if not covered_by_dedicated:
            seen_paths.add(cand["path"])
            assets.append(cand)

    if not assets:
        media_dir = project_dir / "media"
        if media_dir.is_dir():
            # Prefer audio-only files first, then fall back to video containers
            for audio_file in sorted(media_dir.iterdir()):
                if not audio_file.is_file():
                    continue
                suffix = audio_file.suffix.lower()
                if suffix not in _AUDIO_ONLY_EXTENSIONS:
                    continue
                resolved = audio_file.resolve()
                if resolved in seen_paths:
                    continue
                seen_paths.add(resolved)
                name = audio_file.stem.lower()
                kind = "microphone" if "mic" in name else "systemAudio"
                assets.append(
                    {
                        "path": resolved,
                        "kind": kind,
                        "speaker": _speaker_label(kind),
                        "startTicks": 0,
                        "durationTicks": 0,
                        "needsExtract": suffix != ".wav",
                    }
                )
            if not assets:
                for video_file in sorted(media_dir.iterdir()):
                    if not video_file.is_file():
                        continue
                    suffix = video_file.suffix.lower()
                    if suffix not in _VIDEO_WITH_AUDIO_EXTENSIONS:
                        continue
                    resolved = video_file.resolve()
                    if resolved in seen_paths:
                        continue
                    seen_paths.add(resolved)
                    assets.append(
                        {
                            "path": resolved,
                            "kind": "embeddedAudio",
                            "speaker": "video",
                            "startTicks": 0,
                            "durationTicks": 0,
                            "needsExtract": True,
                        }
                    )

    return assets


def _extract_audio_to_wav16k(
    source_path: Path,
    out_wav: Path,
    start_sec: float | None = None,
    duration_sec: float | None = None,
) -> bool:
    """Extract or resample an audio/video file to 16kHz mono 16-bit PCM WAV using ffmpeg."""
    ffmpeg_exe = _locate_ffmpeg()
    if not ffmpeg_exe:
        return False

    cmd: list[str] = [ffmpeg_exe, "-hide_banner", "-loglevel", "error"]
    if start_sec is not None and start_sec > 0.0:
        cmd.extend(["-ss", f"{start_sec:.4f}"])
    cmd.extend(["-i", str(source_path)])
    if duration_sec is not None and duration_sec > 0.0:
        cmd.extend(["-t", f"{duration_sec:.4f}"])
    cmd.extend(
        [
            "-vn",
            "-ac",
            "1",
            "-ar",
            "16000",
            "-c:a",
            "pcm_s16le",
            "-y",
            str(out_wav),
        ]
    )
    try:
        kwargs: dict[str, Any] = {"capture_output": True, "text": True, "timeout": 120}
        if os.name == "nt":
            kwargs["creationflags"] = subprocess.CREATE_NO_WINDOW
        proc = subprocess.run(cmd, **kwargs)
        return proc.returncode == 0 and out_wav.is_file() and out_wav.stat().st_size > 44
    except (OSError, subprocess.SubprocessError):
        return False


def _slice_pcm_wav_pure_python(
    source_wav: Path,
    out_wav: Path,
    start_sec: float,
    duration_sec: float | None,
) -> bool:
    """Slice a standard PCM WAV file using stdlib wave when ffmpeg is unavailable."""
    try:
        with wave.open(str(source_wav), "rb") as src:
            params = src.getparams()
            sr = params.framerate
            total_frames = params.nframes
            start_frame = max(0, min(total_frames, int(round(max(0.0, start_sec) * sr))))
            if duration_sec is None:
                count_frames = total_frames - start_frame
            else:
                count_frames = max(0, min(total_frames - start_frame, int(round(duration_sec * sr))))
            if count_frames <= 0:
                return False
            src.setpos(start_frame)
            frames = src.readframes(count_frames)
        with wave.open(str(out_wav), "wb") as dst:
            dst.setparams(params)
            dst.writeframes(frames)
        return out_wav.is_file() and out_wav.stat().st_size > 44
    except (wave.Error, OSError, EOFError):
        return False


def _find_screen_assets(project_dir: Path, project_data: dict[str, Any]) -> list[dict[str, Any]]:
    assets: list[dict[str, Any]] = []
    for item in project_data.get("media", []):
        if item.get("kind") != "screen":
            continue
        rel = item.get("path", "")
        if not rel:
            continue
        try:
            full = _resolve_project_path(project_dir, rel)
        except ValueError:
            continue
        if full.is_file():
            assets.append(
                {
                    "path": full,
                    "startTicks": _media_time_to_ticks(item.get("startOffset"), 0),
                    "durationTicks": _media_time_to_ticks(item.get("duration"), 0),
                    "width": int(item.get("width", 0) or 0),
                    "height": int(item.get("height", 0) or 0),
                }
            )
    if not assets:
        media_dir = project_dir / "media"
        if media_dir.is_dir():
            for mp4 in sorted(media_dir.glob("screen-*.mp4")) + sorted(media_dir.glob("*.mp4")):
                if mp4.is_file():
                    assets.append(
                        {
                            "path": mp4.resolve(),
                            "startTicks": 0,
                            "durationTicks": 0,
                            "width": 0,
                            "height": 0,
                        }
                    )
                    break
    return assets


def _read_wav_windows_db(wav_path: Path, window_sec: float = 0.05) -> tuple[int, float, list[tuple[int, int, float]]]:
    """Read PCM or IEEE-float WAV file and return (sample_rate, total_sec, [(start_frame, end_frame, db), ...])."""
    with wav_path.open("rb") as f:
        header = f.read(12)
        if len(header) < 12 or header[0:4] not in (b"RIFF", b"RF64") or header[8:12] != b"WAVE":
            raise ValueError(f"Unsupported WAV container: {wav_path}")

        audio_format = 1
        channels = 1
        sample_rate = 16000
        bits_per_sample = 16
        rf64_data_size: int | None = None
        data_offset: int | None = None
        data_size = 0

        while True:
            chunk_hdr = f.read(8)
            if len(chunk_hdr) < 8:
                break
            chunk_id = chunk_hdr[0:4]
            chunk_size = struct.unpack("<I", chunk_hdr[4:8])[0]
            chunk_pos = f.tell()

            if chunk_id == b"ds64" and chunk_size >= 24:
                ds64 = f.read(24)
                _, rf64_data_size, _ = struct.unpack("<QQQ", ds64)
            elif chunk_id == b"fmt " and chunk_size >= 16:
                fmt = f.read(chunk_size)
                audio_format, channels, sample_rate, _, block_align, bits_per_sample = struct.unpack("<HHIIHH", fmt[:16])
                if audio_format == 0xFFFE and len(fmt) >= 40:
                    sub_format = struct.unpack("<H", fmt[24:26])[0]
                    audio_format = sub_format
            elif chunk_id == b"data":
                data_offset = chunk_pos
                data_size = rf64_data_size if (chunk_size == 0xFFFFFFFF and rf64_data_size is not None) else chunk_size
                break

            next_pos = chunk_pos + chunk_size + (chunk_size & 1)
            f.seek(next_pos)

        if data_offset is None or sample_rate <= 0 or channels <= 0 or bits_per_sample <= 0:
            raise ValueError(f"Invalid WAV format/data chunks: {wav_path}")

        bytes_per_sample = bits_per_sample // 8
        frame_bytes = bytes_per_sample * channels
        total_frames = data_size // frame_bytes
        window_frames = max(1, int(round(sample_rate * window_sec)))
        windows: list[tuple[int, int, float]] = []

        # Use stdlib wave when PCM (format 1) for readability, or direct struct decoding for both PCM & IEEE float (format 3)
        f.seek(data_offset)
        frame_cursor = 0
        while frame_cursor < total_frames:
            frames_to_read = min(window_frames, total_frames - frame_cursor)
            raw = f.read(frames_to_read * frame_bytes)
            if not raw:
                break
            actual_frames = len(raw) // frame_bytes
            if actual_frames == 0:
                break
            sample_count = actual_frames * channels
            sum_sq = _compute_sum_squares(raw[: actual_frames * frame_bytes], audio_format, bits_per_sample, sample_count)
            rms = math.sqrt(sum_sq / max(1, sample_count))
            db = -120.0 if rms <= 1e-12 else max(-120.0, 20.0 * math.log10(rms))
            windows.append((frame_cursor, frame_cursor + actual_frames, db))
            frame_cursor += actual_frames

        return sample_rate, total_frames / sample_rate, windows


def _compute_sum_squares(raw: bytes, audio_format: int, bits_per_sample: int, sample_count: int) -> float:
    if audio_format == 3:
        if bits_per_sample == 32:
            vals = struct.unpack(f"<{sample_count}f", raw)
            return sum(v * v for v in vals if math.isfinite(v))
        if bits_per_sample == 64:
            vals = struct.unpack(f"<{sample_count}d", raw)
            return sum(v * v for v in vals if math.isfinite(v))
    if audio_format == 1:
        if bits_per_sample == 16:
            vals = struct.unpack(f"<{sample_count}h", raw)
            inv = 1.0 / 32768.0
            return sum((v * inv) ** 2 for v in vals)
        if bits_per_sample == 8:
            inv = 1.0 / 128.0
            return sum(((b - 128) * inv) ** 2 for b in raw)
        if bits_per_sample == 24:
            inv = 1.0 / 8388608.0
            total = 0.0
            for idx in range(0, sample_count * 3, 3):
                b0, b1, b2 = raw[idx], raw[idx + 1], raw[idx + 2]
                val = b0 | (b1 << 8) | (b2 << 16)
                if val & 0x800000:
                    val -= 0x1000000
                s = val * inv
                total += s * s
            return total
        if bits_per_sample == 32:
            vals = struct.unpack(f"<{sample_count}i", raw)
            inv = 1.0 / 2147483648.0
            return sum((v * inv) ** 2 for v in vals)
    raise ValueError(f"Unsupported WAV encoding: format={audio_format}, bits={bits_per_sample}")


def _verify_pcm_with_wave_module(wav_path: Path) -> None:
    """Validate standard PCM WAV headers via stdlib wave when applicable."""
    try:
        with wave.open(str(wav_path), "rb") as w:
            _ = w.getparams()
    except wave.Error:
        # IEEE float (tag 3) and extensible (0xFFFE) raise wave.Error in stdlib wave; handled by _read_wav_windows_db.
        pass


def _detect_track_silence(
    wav_path: Path,
    offset_ticks: int,
    min_silence_sec: float,
    threshold_db: float,
) -> list[dict[str, Any]]:
    _verify_pcm_with_wave_module(wav_path)
    sample_rate, _, windows = _read_wav_windows_db(wav_path, window_sec=0.05)
    spans: list[dict[str, Any]] = []
    cur_start: int | None = None
    cur_end: int = 0
    db_samples: list[float] = []

    def _flush() -> None:
        nonlocal cur_start, cur_end, db_samples
        if cur_start is None:
            return
        dur_sec = (cur_end - cur_start) / sample_rate
        if dur_sec >= min_silence_sec:
            start_ticks = offset_ticks + int(round(cur_start * TICKS_PER_SECOND / sample_rate))
            end_ticks = offset_ticks + int(round(cur_end * TICKS_PER_SECOND / sample_rate))
            avg_db = sum(db_samples) / len(db_samples) if db_samples else threshold_db
            spans.append(
                {
                    "startSec": round(start_ticks / TICKS_PER_SECOND, 4),
                    "endSec": round(end_ticks / TICKS_PER_SECOND, 4),
                    "durationSec": round((end_ticks - start_ticks) / TICKS_PER_SECOND, 4),
                    "startTicks": start_ticks,
                    "endTicks": end_ticks,
                    "averageDb": round(avg_db, 2),
                }
            )
        cur_start = None
        db_samples = []

    for start_f, end_f, db in windows:
        if db <= threshold_db:
            if cur_start is None:
                cur_start = start_f
            cur_end = end_f
            db_samples.append(db)
        else:
            _flush()
    _flush()
    return spans


def _intersect_silence_tracks(track_spans: list[list[dict[str, Any]]], min_silence_sec: float) -> list[dict[str, Any]]:
    if not track_spans:
        return []
    if len(track_spans) == 1:
        return track_spans[0]
    current = track_spans[0]
    min_ticks = _sec_to_ticks(min_silence_sec)
    for other in track_spans[1:]:
        merged: list[dict[str, Any]] = []
        for a in current:
            for b in other:
                s = max(a["startTicks"], b["startTicks"])
                e = min(a["endTicks"], b["endTicks"])
                if e - s >= min_ticks:
                    merged.append(
                        {
                            "startSec": round(s / TICKS_PER_SECOND, 4),
                            "endSec": round(e / TICKS_PER_SECOND, 4),
                            "durationSec": round((e - s) / TICKS_PER_SECOND, 4),
                            "startTicks": s,
                            "endTicks": e,
                            "averageDb": round((a["averageDb"] + b["averageDb"]) / 2.0, 2),
                        }
                    )
        current = merged
    return current


def _build_remove_silence_batch(project_data: dict[str, Any], silent_intervals: list[dict[str, Any]], padding_sec: float = 0.15) -> dict[str, Any]:
    segments = project_data.get("segments", [])
    if not segments or not silent_intervals:
        return {"op": "batch", "commands": []}

    pad_ticks = max(0, _sec_to_ticks(padding_sec))
    raw_cuts: list[tuple[int, int]] = []
    for iv in silent_intervals:
        s = int(iv["startTicks"]) + pad_ticks
        e = int(iv["endTicks"]) - pad_ticks
        if e - s >= MIN_KEEP_TICKS:
            raw_cuts.append((s, e))

    if not raw_cuts:
        return {"op": "batch", "commands": []}

    raw_cuts.sort()
    cuts: list[tuple[int, int]] = []
    for s, e in raw_cuts:
        if cuts and s <= cuts[-1][1] + MIN_KEEP_TICKS:
            cuts[-1] = (cuts[-1][0], max(cuts[-1][1], e))
        else:
            cuts.append((s, e))

    commands: list[dict[str, Any]] = []
    remaining_segments = len(segments)

    def _new_id() -> str:
        return f"seg-{uuid.uuid4().hex[:12]}"

    for seg in segments:
        seg_id = str(seg["id"])
        seg_start = _media_time_to_ticks(seg["sourceStart"])
        seg_dur = _media_time_to_ticks(seg["sourceDuration"])
        seg_end = seg_start + seg_dur

        seg_cuts: list[tuple[int, int]] = []
        for cs, ce in cuts:
            s = max(cs, seg_start)
            e = min(ce, seg_end)
            if e <= s:
                continue
            if s - seg_start < MIN_KEEP_TICKS:
                s = seg_start
            if seg_end - e < MIN_KEEP_TICKS:
                e = seg_end
            if e - s >= MIN_KEEP_TICKS:
                if seg_cuts and s <= seg_cuts[-1][1] + MIN_KEEP_TICKS:
                    seg_cuts[-1] = (seg_cuts[-1][0], max(seg_cuts[-1][1], e))
                else:
                    seg_cuts.append((s, e))

        active_id = seg_id
        active_start = seg_start
        for cs, ce in seg_cuts:
            cs = max(cs, active_start)
            if ce <= cs:
                continue
            if cs - active_start < MIN_KEEP_TICKS:
                cs = active_start

            if cs > active_start and ce < seg_end:
                if len(commands) + 3 > MAX_BATCH_COMMANDS:
                    break
                mid_id = _new_id()
                right_id = _new_id()
                commands.append({"op": "split", "segmentId": active_id, "sourceTicks": cs, "newId": mid_id})
                commands.append({"op": "split", "segmentId": mid_id, "sourceTicks": ce, "newId": right_id})
                commands.append({"op": "delete", "segmentId": mid_id})
                remaining_segments += 1
                active_id = right_id
                active_start = ce
            elif cs == active_start and ce < seg_end:
                if len(commands) + 2 > MAX_BATCH_COMMANDS:
                    break
                right_id = _new_id()
                commands.append({"op": "split", "segmentId": active_id, "sourceTicks": ce, "newId": right_id})
                commands.append({"op": "delete", "segmentId": active_id})
                active_id = right_id
                active_start = ce
            elif cs > active_start and ce == seg_end:
                if len(commands) + 2 > MAX_BATCH_COMMANDS:
                    break
                right_id = _new_id()
                commands.append({"op": "split", "segmentId": active_id, "sourceTicks": cs, "newId": right_id})
                commands.append({"op": "delete", "segmentId": right_id})
                active_start = seg_end
            else:
                # Cut covers the entire remaining portion of this segment.
                if remaining_segments > 1:
                    if len(commands) + 1 > MAX_BATCH_COMMANDS:
                        break
                    commands.append({"op": "delete", "segmentId": active_id})
                    remaining_segments -= 1
                elif seg_end - active_start > MIN_KEEP_TICKS:
                    if len(commands) + 2 > MAX_BATCH_COMMANDS:
                        break
                    right_id = _new_id()
                    keep_end = active_start + MIN_KEEP_TICKS
                    commands.append({"op": "split", "segmentId": active_id, "sourceTicks": keep_end, "newId": right_id})
                    commands.append({"op": "delete", "segmentId": right_id})
                active_start = seg_end

        if len(commands) >= MAX_BATCH_COMMANDS:
            break

    return {"op": "batch", "commands": commands}


def _normalize_word_list(raw_words: Any, seg_start_ticks: int) -> list[dict[str, Any]]:
    if not isinstance(raw_words, list):
        return []
    out: list[dict[str, Any]] = []
    for w in raw_words:
        if not isinstance(w, dict):
            continue
        w_text = str(w.get("text", w.get("word", w.get("Text", "")))).strip()
        if not w_text:
            continue
        w_start_ticks = w.get("StartTicks", w.get("startTicks"))
        w_end_ticks = w.get("EndTicks", w.get("endTicks"))
        w_start_sec = w.get("startSec", w.get("start", w.get("StartSec")))
        w_end_sec = w.get("endSec", w.get("end", w.get("EndSec")))
        if w_start_ticks is None and w_start_sec is not None:
            w_start_ticks = _sec_to_ticks(float(w_start_sec))
        if w_end_ticks is None and w_end_sec is not None:
            w_end_ticks = _sec_to_ticks(float(w_end_sec))
        if w_start_ticks is None and w.get("startOffset") is not None:
            w_start_ticks = seg_start_ticks + _sec_to_ticks(float(w["startOffset"]))
        if w_end_ticks is None and w.get("endOffset") is not None:
            w_end_ticks = seg_start_ticks + _sec_to_ticks(float(w["endOffset"]))
        if w_start_ticks is None or w_end_ticks is None:
            continue
        s_t = int(w_start_ticks)
        e_t = max(s_t, int(w_end_ticks))
        out.append(
            {
                "text": w_text,
                "startSec": round(s_t / TICKS_PER_SECOND, 4),
                "endSec": round(e_t / TICKS_PER_SECOND, 4),
                "StartTicks": s_t,
                "EndTicks": e_t,
            }
        )
    out.sort(key=lambda item: (item["StartTicks"], item["EndTicks"]))
    return out


def _normalize_transcript_list(raw: Any) -> list[dict[str, Any]]:
    items = raw.get("segments", []) if isinstance(raw, dict) else raw
    if not isinstance(items, list):
        return []
    normalized: list[dict[str, Any]] = []
    for item in items:
        if not isinstance(item, dict):
            continue
        start_ticks = item.get("StartTicks", item.get("startTicks"))
        end_ticks = item.get("EndTicks", item.get("endTicks"))
        start_sec = item.get("startSec", item.get("StartSec"))
        end_sec = item.get("endSec", item.get("EndSec"))
        if start_ticks is None and start_sec is not None:
            start_ticks = _sec_to_ticks(float(start_sec))
        if end_ticks is None and end_sec is not None:
            end_ticks = _sec_to_ticks(float(end_sec))
        if start_sec is None and start_ticks is not None:
            start_sec = round(int(start_ticks) / TICKS_PER_SECOND, 4)
        if end_sec is None and end_ticks is not None:
            end_sec = round(int(end_ticks) / TICKS_PER_SECOND, 4)
        if start_ticks is None or end_ticks is None:
            continue
        speaker = item.get("speaker") or _speaker_label(str(item.get("trackKind", item.get("TrackKind", "mic"))))
        text = str(item.get("text", item.get("Text", ""))).strip()
        entry: dict[str, Any] = {
            "startSec": float(start_sec),
            "endSec": float(end_sec),
            "StartTicks": int(start_ticks),
            "EndTicks": int(end_ticks),
            "speaker": str(speaker),
            "text": text,
        }
        words = _normalize_word_list(item.get("words", item.get("Words")), int(start_ticks))
        if words:
            entry["words"] = words
        if item.get("isFallback"):
            entry["isFallback"] = True
        normalized.append(entry)
    normalized.sort(key=lambda s: (s["StartTicks"], s["EndTicks"]))
    return normalized


def _is_vad_fallback_segments(segments: list[dict[str, Any]]) -> bool:
    if not segments:
        return False
    return all(
        bool(s.get("isFallback")) or "(offline VAD fallback)" in str(s.get("text", ""))
        for s in segments
    )


def _transcribe_with_faster_whisper(
    wav_path: Path,
    offset_ticks: int,
    speaker: str,
    language: str,
    model_size: str,
    vocabulary_prompt: str = "",
) -> list[dict[str, Any]]:
    from faster_whisper import WhisperModel  # type: ignore[import-not-found]

    model = WhisperModel(model_size, device="auto", compute_type="int8")
    lang_arg = None if not language or language.strip().lower() == "auto" else language.strip()
    transcribe_kwargs: dict[str, Any] = {
        "language": lang_arg,
        "vad_filter": True,
        "word_timestamps": True,
    }
    if vocabulary_prompt.strip():
        transcribe_kwargs["initial_prompt"] = vocabulary_prompt.strip()

    segments_iter, _ = model.transcribe(str(wav_path), **transcribe_kwargs)
    results: list[dict[str, Any]] = []
    for seg in segments_iter:
        start_ticks = offset_ticks + _sec_to_ticks(float(seg.start))
        end_ticks = offset_ticks + _sec_to_ticks(float(seg.end))
        text = (seg.text or "").strip()
        if not text or end_ticks <= start_ticks:
            continue
        words: list[dict[str, Any]] = []
        for w in getattr(seg, "words", None) or []:
            w_text = str(getattr(w, "word", "") or "").strip()
            if not w_text:
                continue
            ws = offset_ticks + _sec_to_ticks(float(getattr(w, "start", seg.start)))
            we = offset_ticks + _sec_to_ticks(float(getattr(w, "end", seg.end)))
            we = max(ws, we)
            words.append(
                {
                    "text": w_text,
                    "startSec": round(ws / TICKS_PER_SECOND, 4),
                    "endSec": round(we / TICKS_PER_SECOND, 4),
                    "StartTicks": ws,
                    "EndTicks": we,
                }
            )
        entry: dict[str, Any] = {
            "startSec": round(start_ticks / TICKS_PER_SECOND, 4),
            "endSec": round(end_ticks / TICKS_PER_SECOND, 4),
            "StartTicks": start_ticks,
            "EndTicks": end_ticks,
            "speaker": speaker,
            "text": text,
        }
        if words:
            entry["words"] = words
        results.append(entry)
    return results


def _resolve_cloud_whisper_config(api_key: str = "", base_url: str = "") -> tuple[str, str, str, str]:
    """Return (api_key, endpoint_url, model_name, engine_label)."""
    key = (
        (api_key or "").strip()
        or os.environ.get("OPENAI_API_KEY", "").strip()
        or os.environ.get("GROQ_API_KEY", "").strip()
    )
    raw_base = (base_url or "").strip() or os.environ.get("OPENAI_BASE_URL", "").strip()
    is_groq = "groq.com" in raw_base.lower() or (
        not (api_key or "").strip()
        and not os.environ.get("OPENAI_API_KEY", "").strip()
        and bool(os.environ.get("GROQ_API_KEY", "").strip())
    )
    if is_groq:
        if not raw_base:
            raw_base = "https://api.groq.com/openai/v1"
        model_name = os.environ.get("FRAMETRAIL_WHISPER_MODEL", "whisper-large-v3-turbo")
        engine_label = "groq-whisper"
    else:
        if not raw_base:
            raw_base = "https://api.openai.com/v1"
        model_name = os.environ.get("FRAMETRAIL_WHISPER_MODEL", "whisper-1")
        engine_label = "openai-whisper"

    trimmed = raw_base.rstrip("/")
    if trimmed.endswith("/audio/transcriptions"):
        endpoint_url = trimmed
    else:
        endpoint_url = f"{trimmed}/audio/transcriptions"
    return key, endpoint_url, model_name, engine_label


def _attach_top_level_words_to_segments(
    raw_segments: list[dict[str, Any]],
    top_words: list[dict[str, Any]],
) -> None:
    if not top_words or not raw_segments:
        return
    for seg in raw_segments:
        if seg.get("words"):
            continue
        s0 = float(seg["start"]) - 0.05
        s1 = float(seg["end"]) + 0.05
        matched = [w for w in top_words if w["start"] >= s0 and w["end"] <= s1]
        if matched:
            seg["words"] = matched


def _transcribe_with_openai(
    wav_path: Path,
    offset_ticks: int,
    speaker: str,
    language: str,
    api_key: str = "",
    base_url: str = "",
    vocabulary_prompt: str = "",
) -> tuple[list[dict[str, Any]], str]:
    effective_key, endpoint_url, model_name, engine_label = _resolve_cloud_whisper_config(api_key, base_url)
    if not effective_key:
        raise RuntimeError("No OpenAI or Groq API key configured.")

    lang_arg = None if not language or language.strip().lower() == "auto" else language.strip()
    prompt_arg = vocabulary_prompt.strip() or None

    raw_segments: list[dict[str, Any]] = []
    top_words: list[dict[str, Any]] = []

    used_sdk = False
    try:
        from openai import OpenAI  # type: ignore[import-not-found]

        client_base = endpoint_url[: -len("/audio/transcriptions")] if endpoint_url.endswith("/audio/transcriptions") else endpoint_url
        client = OpenAI(api_key=effective_key, base_url=client_base)
        req_kwargs: dict[str, Any] = {
            "model": model_name,
            "response_format": "verbose_json",
            "timestamp_granularities": ["word", "segment"],
        }
        if lang_arg:
            req_kwargs["language"] = lang_arg
        if prompt_arg:
            req_kwargs["prompt"] = prompt_arg
        with wav_path.open("rb") as audio_file:
            resp = client.audio.transcriptions.create(file=audio_file, **req_kwargs)
        used_sdk = True
        segs = getattr(resp, "segments", None) or []
        for seg in segs:
            s = float(getattr(seg, "start", 0.0) if not isinstance(seg, dict) else seg.get("start", 0.0))
            e = float(getattr(seg, "end", 0.0) if not isinstance(seg, dict) else seg.get("end", 0.0))
            t = str(getattr(seg, "text", "") if not isinstance(seg, dict) else seg.get("text", "")).strip()
            seg_words_raw = getattr(seg, "words", None) if not isinstance(seg, dict) else seg.get("words")
            seg_words: list[dict[str, Any]] = []
            for w in seg_words_raw or []:
                wt = str(getattr(w, "word", "") if not isinstance(w, dict) else w.get("word", w.get("text", ""))).strip()
                if not wt:
                    continue
                ws = float(getattr(w, "start", s) if not isinstance(w, dict) else w.get("start", s))
                we = float(getattr(w, "end", e) if not isinstance(w, dict) else w.get("end", e))
                seg_words.append({"word": wt, "start": ws, "end": max(ws, we)})
            raw_segments.append({"start": s, "end": e, "text": t, "words": seg_words})

        for w in getattr(resp, "words", None) or []:
            wt = str(getattr(w, "word", "") if not isinstance(w, dict) else w.get("word", w.get("text", ""))).strip()
            if not wt:
                continue
            ws = float(getattr(w, "start", 0.0) if not isinstance(w, dict) else w.get("start", 0.0))
            we = float(getattr(w, "end", ws) if not isinstance(w, dict) else w.get("end", ws))
            top_words.append({"word": wt, "start": ws, "end": max(ws, we)})
    except ImportError:
        used_sdk = False

    if not used_sdk:
        boundary = f"----FrameTrailBoundary{uuid.uuid4().hex}"
        file_bytes = wav_path.read_bytes()
        fields: list[tuple[str, str]] = [
            ("model", model_name),
            ("response_format", "verbose_json"),
            ("timestamp_granularities[]", "word"),
            ("timestamp_granularities[]", "segment"),
        ]
        if lang_arg:
            fields.append(("language", lang_arg))
        if prompt_arg:
            fields.append(("prompt", prompt_arg))
        parts: list[bytes] = []
        for name, val in fields:
            parts.append(
                f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{val}\r\n'.encode("utf-8")
            )
        parts.append(
            f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{wav_path.name}"\r\nContent-Type: audio/wav\r\n\r\n'.encode("utf-8")
            + file_bytes
            + b"\r\n"
        )
        parts.append(f"--{boundary}--\r\n".encode("utf-8"))
        body = b"".join(parts)
        req = urllib.request.Request(
            endpoint_url,
            data=body,
            headers={
                "Authorization": f"Bearer {effective_key}",
                "Content-Type": f"multipart/form-data; boundary={boundary}",
            },
            method="POST",
        )
        with urllib.request.urlopen(req, timeout=120) as resp:
            payload = json.loads(resp.read().decode("utf-8"))
        for seg in payload.get("segments", []) or []:
            seg_words = []
            for w in seg.get("words", []) or []:
                wt = str(w.get("word", w.get("text", ""))).strip()
                if not wt:
                    continue
                ws = float(w.get("start", seg.get("start", 0.0)))
                we = float(w.get("end", seg.get("end", ws)))
                seg_words.append({"word": wt, "start": ws, "end": max(ws, we)})
            raw_segments.append(
                {
                    "start": float(seg.get("start", 0.0)),
                    "end": float(seg.get("end", 0.0)),
                    "text": str(seg.get("text", "")).strip(),
                    "words": seg_words,
                }
            )
        for w in payload.get("words", []) or []:
            wt = str(w.get("word", w.get("text", ""))).strip()
            if not wt:
                continue
            ws = float(w.get("start", 0.0))
            we = float(w.get("end", ws))
            top_words.append({"word": wt, "start": ws, "end": max(ws, we)})

        # Some API responses return only top-level words or text without segments
        if not raw_segments and top_words:
            raw_segments.append(
                {
                    "start": top_words[0]["start"],
                    "end": top_words[-1]["end"],
                    "text": str(payload.get("text", "")).strip() or " ".join(w["word"] for w in top_words),
                    "words": top_words,
                }
            )

    _attach_top_level_words_to_segments(raw_segments, top_words)

    results: list[dict[str, Any]] = []
    for seg in raw_segments:
        start_ticks = offset_ticks + _sec_to_ticks(seg["start"])
        end_ticks = offset_ticks + _sec_to_ticks(seg["end"])
        if not seg["text"] or end_ticks <= start_ticks:
            continue
        words_out: list[dict[str, Any]] = []
        for w in seg.get("words") or []:
            ws = offset_ticks + _sec_to_ticks(float(w["start"]))
            we = max(ws, offset_ticks + _sec_to_ticks(float(w["end"])))
            words_out.append(
                {
                    "text": w["word"],
                    "startSec": round(ws / TICKS_PER_SECOND, 4),
                    "endSec": round(we / TICKS_PER_SECOND, 4),
                    "StartTicks": ws,
                    "EndTicks": we,
                }
            )
        entry: dict[str, Any] = {
            "startSec": round(start_ticks / TICKS_PER_SECOND, 4),
            "endSec": round(end_ticks / TICKS_PER_SECOND, 4),
            "StartTicks": start_ticks,
            "EndTicks": end_ticks,
            "speaker": speaker,
            "text": seg["text"],
        }
        if words_out:
            entry["words"] = words_out
        results.append(entry)
    return results, engine_label


def _transcribe_with_vad_fallback(wav_path: Path, offset_ticks: int, speaker: str, threshold_db: float = -38.0) -> list[dict[str, Any]]:
    """Offline WAV energy voice-activity segmenter when neither faster-whisper nor OpenAI is available."""
    sample_rate, _, windows = _read_wav_windows_db(wav_path, window_sec=0.05)
    active_spans: list[tuple[int, int]] = []
    cur_start: int | None = None
    cur_end: int = 0
    max_gap_frames = int(round(0.35 * sample_rate))
    min_active_frames = int(round(0.25 * sample_rate))

    for s, e, db in windows:
        if db > threshold_db:
            if cur_start is None:
                cur_start = s
                cur_end = e
            elif s - cur_end <= max_gap_frames:
                cur_end = e
            else:
                if cur_end - cur_start >= min_active_frames:
                    active_spans.append((cur_start, cur_end))
                cur_start = s
                cur_end = e
        elif cur_start is not None and s - cur_end > max_gap_frames:
            if cur_end - cur_start >= min_active_frames:
                active_spans.append((cur_start, cur_end))
            cur_start = None

    if cur_start is not None and cur_end - cur_start >= min_active_frames:
        active_spans.append((cur_start, cur_end))

    results: list[dict[str, Any]] = []
    for s_f, e_f in active_spans:
        start_ticks = offset_ticks + int(round(s_f * TICKS_PER_SECOND / sample_rate))
        end_ticks = offset_ticks + int(round(e_f * TICKS_PER_SECOND / sample_rate))
        s_sec = round(start_ticks / TICKS_PER_SECOND, 4)
        e_sec = round(end_ticks / TICKS_PER_SECOND, 4)
        results.append(
            {
                "startSec": s_sec,
                "endSec": e_sec,
                "StartTicks": start_ticks,
                "EndTicks": end_ticks,
                "speaker": speaker,
                "text": f"[speech activity {s_sec:.2f}s-{e_sec:.2f}s (offline VAD fallback)]",
                "isFallback": True,
            }
        )
    return results


# --- Smart subtitle cue segmentation ---

_STRONG_PUNCT = set("。！？.!?;；\n")
_SOFT_PUNCT = set("，,、：:—…")
_CJK_RE = re.compile(r"[\u3400-\u9fff\u3040-\u30ff\uac00-\ud7af]")


def _is_cjk_char(ch: str) -> bool:
    return bool(ch) and bool(_CJK_RE.match(ch))


def _join_tokens(tokens: list[str]) -> str:
    """Join word/character tokens cleanly without stray spaces between CJK characters."""
    out = ""
    for tok in tokens:
        t = tok.strip()
        if not t:
            continue
        if not out:
            out = t
            continue
        prev_ch = out[-1]
        next_ch = t[0]
        if next_ch in _STRONG_PUNCT or next_ch in _SOFT_PUNCT or next_ch in ")]}'\"”’）》】":
            out += t
        elif prev_ch in "([{'\"“‘（《【":
            out += t
        elif _is_cjk_char(prev_ch) or _is_cjk_char(next_ch):
            out += t
        else:
            out += " " + t
    return out.strip()


def _truncate_utf8(text: str, max_bytes: int = MAX_SUBTITLE_TEXT_BYTES) -> str:
    encoded = text.encode("utf-8")
    if len(encoded) <= max_bytes:
        return text
    return encoded[:max_bytes].decode("utf-8", errors="ignore").rstrip()


def _split_plain_text_chunks(text: str, max_chars: int) -> list[str]:
    """Split raw text cleanly by strong/soft punctuation and max_chars_per_line."""
    cleaned = " ".join(text.replace("\r", "\n").split())
    if not cleaned:
        return []
    limit = max(4, int(max_chars))
    if len(cleaned) <= limit:
        return [cleaned]

    # First split on strong punctuation keeping the punctuation attached to the preceding clause
    clauses: list[str] = []
    buf: list[str] = []
    for ch in cleaned:
        buf.append(ch)
        if ch in _STRONG_PUNCT:
            part = "".join(buf).strip()
            if part:
                clauses.append(part)
            buf = []
    tail = "".join(buf).strip()
    if tail:
        clauses.append(tail)

    chunks: list[str] = []
    for clause in clauses:
        if len(clause) <= limit:
            chunks.append(clause)
            continue

        # Split overlong clause on soft punctuation first
        subclauses: list[str] = []
        sbuf: list[str] = []
        for ch in clause:
            sbuf.append(ch)
            if ch in _SOFT_PUNCT:
                sp = "".join(sbuf).strip()
                if sp:
                    subclauses.append(sp)
                sbuf = []
        stail = "".join(sbuf).strip()
        if stail:
            subclauses.append(stail)

        # Greedily re-combine short subclauses up to limit, then hard-wrap any still-long subclause
        current = ""
        for sub in subclauses:
            candidate = _join_tokens([current, sub]) if current else sub
            if current and len(candidate) <= limit:
                current = candidate
            else:
                if current:
                    chunks.append(current)
                    current = ""
                if len(sub) <= limit:
                    current = sub
                else:
                    # Wrap respecting ASCII word boundaries where possible
                    pieces = re.findall(r"[A-Za-z0-9_'-]+|[\u3400-\u9fff\u3040-\u30ff\uac00-\ud7af]|[^\s]", sub)
                    line = ""
                    for p in pieces:
                        cand = _join_tokens([line, p]) if line else p
                        if line and len(cand) > limit:
                            chunks.append(line)
                            line = p
                        else:
                            line = cand
                    if line:
                        current = line
        if current:
            chunks.append(current)

    return [c.strip() for c in chunks if c.strip()]


def _make_subtitle_effect(
    start_ticks: int,
    end_ticks: int,
    text: str,
    words: list[dict[str, Any]] | None = None,
) -> dict[str, Any] | None:
    cleaned = _truncate_utf8(text.strip(), MAX_SUBTITLE_TEXT_BYTES)
    if not cleaned or end_ticks <= start_ticks:
        return None
    params: dict[str, Any] = {"text": cleaned}
    if words:
        cue_dur_sec = (end_ticks - start_ticks) / TICKS_PER_SECOND
        formatted_words: list[dict[str, Any]] = []
        for w in words[:MAX_SUBTITLE_WORDS]:
            wt = str(w.get("text", "")).strip()
            if not wt:
                continue
            s_off = max(0.0, min(cue_dur_sec, (int(w["StartTicks"]) - start_ticks) / TICKS_PER_SECOND))
            e_off = max(s_off, min(cue_dur_sec, (int(w["EndTicks"]) - start_ticks) / TICKS_PER_SECOND))
            formatted_words.append(
                {
                    "text": wt,
                    "startOffset": round(s_off, 4),
                    "endOffset": round(e_off, 4),
                }
            )
        if formatted_words:
            params["words"] = formatted_words

    return {
        "id": f"sub-{uuid.uuid4().hex[:12]}",
        "kind": "subtitle",
        "sourceStart": {"ticks": int(start_ticks), "timescale": TICKS_PER_SECOND},
        "sourceDuration": {"ticks": int(end_ticks - start_ticks), "timescale": TICKS_PER_SECOND},
        "parameters": params,
    }


def segment_transcript_to_subtitles(
    transcript_segments: list[dict[str, Any]],
    max_chars_per_line: int = 18,
    max_duration_sec: float = 6.0,
    pause_gap_sec: float = 0.6,
    total_source_ticks: int | None = None,
) -> list[dict[str, Any]]:
    """Split normalized ASR transcript segments into clean FrameTrail subtitle Effects.

    Preserves word-level startOffset/endOffset relative to each cue's sourceStart when word
    timestamps are present, and splits cleanly at punctuation, pauses, and max_chars_per_line.
    """
    limit = max(4, min(80, int(max_chars_per_line)))
    max_dur_ticks = max(TICKS_PER_SECOND, _sec_to_ticks(max_duration_sec))
    pause_gap_ticks = max(1, _sec_to_ticks(pause_gap_sec))
    normalized = _normalize_transcript_list(transcript_segments)
    cues: list[dict[str, Any]] = []

    def _clamp_bounds(s_ticks: int, e_ticks: int) -> tuple[int, int] | None:
        s = max(0, int(s_ticks))
        e = int(e_ticks)
        if total_source_ticks is not None and total_source_ticks > 0:
            if s >= total_source_ticks:
                return None
            e = min(total_source_ticks, e)
        if e <= s:
            e = s + 1 if (total_source_ticks is None or s + 1 <= total_source_ticks) else s
        if e <= s:
            return None
        return s, e

    for seg in normalized:
        seg_start = int(seg["StartTicks"])
        seg_end = int(seg["EndTicks"])
        seg_text = str(seg.get("text", "")).strip()
        if not seg_text or seg_end <= seg_start:
            continue

        words = seg.get("words") or []
        if words:
            buf: list[dict[str, Any]] = []

            def _flush_words(items: list[dict[str, Any]]) -> None:
                if not items:
                    return
                raw_s = min(int(items[0]["StartTicks"]), seg_end - 1)
                raw_e = max(int(items[-1]["EndTicks"]), raw_s + 1)
                bounds = _clamp_bounds(raw_s, raw_e)
                if bounds is None:
                    return
                cue_text = _join_tokens([str(w["text"]) for w in items])
                eff = _make_subtitle_effect(bounds[0], bounds[1], cue_text, items)
                if eff is not None:
                    cues.append(eff)

            for w in words:
                w_text = str(w.get("text", "")).strip()
                if not w_text:
                    continue
                if buf:
                    prev = buf[-1]
                    gap = int(w["StartTicks"]) - int(prev["EndTicks"])
                    prev_text = str(prev["text"]).strip()
                    prospective_text = _join_tokens([str(x["text"]) for x in buf] + [w_text])
                    prospective_dur = int(w["EndTicks"]) - int(buf[0]["StartTicks"])

                    ends_strong = bool(prev_text) and prev_text[-1] in _STRONG_PUNCT
                    if gap >= pause_gap_ticks or ends_strong:
                        _flush_words(buf)
                        buf = [w]
                        continue
                    if len(prospective_text) > limit or prospective_dur > max_dur_ticks:
                        # Prefer breaking right after the last soft punctuation inside buf
                        soft_idx = -1
                        for idx in range(len(buf) - 1, 0, -1):
                            t = str(buf[idx]["text"]).strip()
                            if t and t[-1] in _SOFT_PUNCT:
                                soft_idx = idx
                                break
                        if soft_idx >= 0 and soft_idx < len(buf) - 1:
                            _flush_words(buf[: soft_idx + 1])
                            buf = buf[soft_idx + 1 :] + [w]
                        else:
                            _flush_words(buf)
                            buf = [w]
                        continue
                buf.append(w)

            _flush_words(buf)
        else:
            chunks = _split_plain_text_chunks(seg_text, limit)
            if not chunks:
                continue
            weights = [max(1, len(c)) for c in chunks]
            total_weight = sum(weights)
            cursor = seg_start
            span = seg_end - seg_start
            for idx, chunk in enumerate(chunks):
                if idx == len(chunks) - 1:
                    next_cursor = seg_end
                else:
                    next_cursor = cursor + max(1, int(round(span * weights[idx] / total_weight)))
                    next_cursor = min(next_cursor, seg_end - (len(chunks) - 1 - idx))
                bounds = _clamp_bounds(cursor, next_cursor)
                if bounds is not None:
                    eff = _make_subtitle_effect(bounds[0], bounds[1], chunk)
                    if eff is not None:
                        cues.append(eff)
                cursor = next_cursor

    # Sort and ensure non-overlapping monotonic cues
    cues.sort(key=lambda c: (c["sourceStart"]["ticks"], c["sourceDuration"]["ticks"]))
    for idx in range(len(cues) - 1):
        cur = cues[idx]
        nxt = cues[idx + 1]
        cur_s = cur["sourceStart"]["ticks"]
        cur_e = cur_s + cur["sourceDuration"]["ticks"]
        nxt_s = nxt["sourceStart"]["ticks"]
        if cur_e > nxt_s and nxt_s > cur_s:
            cur["sourceDuration"]["ticks"] = nxt_s - cur_s
            new_dur_sec = (nxt_s - cur_s) / TICKS_PER_SECOND
            if "words" in cur["parameters"]:
                for w in cur["parameters"]["words"]:
                    w["startOffset"] = round(min(w["startOffset"], new_dur_sec), 4)
                    w["endOffset"] = round(min(w["endOffset"], new_dur_sec), 4)

    return cues[:MAX_SUBTITLE_CUES]


def _resolve_requested_source_ranges(
    project_data: dict[str, Any],
    clip_indices: list[int] | None = None,
    start_sec: float | None = None,
    end_sec: float | None = None,
) -> list[tuple[int, int]] | None:
    """Resolve optional 1-based clip_indices and/or [start_sec, end_sec] into source tick ranges."""
    total_ticks = _media_time_to_ticks(project_data.get("sourceDuration"), 0)
    segments = project_data.get("segments", []) or []

    ranges: list[tuple[int, int]] = []
    if clip_indices is not None and len(clip_indices) > 0:
        if not segments:
            raise ValueError("Project has no timeline clips to select by clip_indices.")
        seen: set[int] = set()
        for raw_idx in clip_indices:
            idx = int(raw_idx)
            if idx < 1 or idx > len(segments):
                raise ValueError(
                    f"clip_indices contains {idx}, but the timeline only has {len(segments)} clip(s) (1-based)."
                )
            if idx in seen:
                continue
            seen.add(idx)
            seg = segments[idx - 1]
            s0 = _media_time_to_ticks(seg.get("sourceStart"), 0)
            dur = _media_time_to_ticks(seg.get("sourceDuration"), 0)
            if dur > 0:
                ranges.append((s0, s0 + dur))
        ranges.sort()
        if not ranges:
            raise ValueError("Selected clips have zero duration.")

    if start_sec is not None or end_sec is not None:
        w_start = max(0, _sec_to_ticks(float(start_sec))) if start_sec is not None else 0
        if end_sec is not None:
            w_end = _sec_to_ticks(float(end_sec))
        elif total_ticks > 0:
            w_end = total_ticks
        else:
            w_end = w_start + TICKS_PER_SECOND * 3600
        if total_ticks > 0:
            w_end = min(total_ticks, w_end)
        if w_end <= w_start:
            raise ValueError(f"Invalid time range [{start_sec}, {end_sec}]: end_sec must exceed start_sec.")
        if ranges:
            clipped: list[tuple[int, int]] = []
            for r0, r1 in ranges:
                ov0 = max(r0, w_start)
                ov1 = min(r1, w_end)
                if ov1 > ov0:
                    clipped.append((ov0, ov1))
            if not clipped:
                raise ValueError("Requested clip_indices do not overlap [start_sec, end_sec].")
            ranges = clipped
        else:
            ranges = [(w_start, w_end)]

    if not ranges:
        return None

    # Merge contiguous/overlapping ranges
    merged: list[tuple[int, int]] = []
    for r0, r1 in sorted(ranges):
        if merged and r0 <= merged[-1][1]:
            merged[-1] = (merged[-1][0], max(merged[-1][1], r1))
        else:
            merged.append((r0, r1))
    return merged


def _filter_segments_to_ranges(
    segments: list[dict[str, Any]],
    ranges: list[tuple[int, int]],
) -> list[dict[str, Any]]:
    filtered: list[dict[str, Any]] = []
    for seg in segments:
        s0 = int(seg["StartTicks"])
        s1 = int(seg["EndTicks"])
        for r0, r1 in ranges:
            ov0 = max(s0, r0)
            ov1 = min(s1, r1)
            if ov1 <= ov0:
                continue
            copied = dict(seg)
            copied["StartTicks"] = ov0
            copied["EndTicks"] = ov1
            copied["startSec"] = round(ov0 / TICKS_PER_SECOND, 4)
            copied["endSec"] = round(ov1 / TICKS_PER_SECOND, 4)
            if seg.get("words"):
                kept_words = [
                    w for w in seg["words"] if int(w["EndTicks"]) > r0 and int(w["StartTicks"]) < r1
                ]
                if kept_words:
                    copied["words"] = kept_words
                    copied["text"] = _join_tokens([str(w["text"]) for w in kept_words])
            filtered.append(copied)
            break
    filtered.sort(key=lambda item: (item["StartTicks"], item["EndTicks"]))
    return filtered


def _transcribe_single_wav(
    wav_path: Path,
    offset_ticks: int,
    speaker: str,
    language: str,
    model_size: str,
    api_key: str,
    base_url: str,
    vocabulary_prompt: str,
    allow_vad_fallback: bool,
) -> tuple[list[dict[str, Any]], str, bool]:
    """Run faster-whisper -> OpenAI/Groq Whisper -> optional VAD fallback on one WAV file."""
    try:
        segs = _transcribe_with_faster_whisper(
            wav_path, offset_ticks, speaker, language, model_size, vocabulary_prompt=vocabulary_prompt
        )
        return segs, "faster-whisper", False
    except Exception:
        pass

    eff_key, _, _, _ = _resolve_cloud_whisper_config(api_key, base_url)
    if eff_key:
        try:
            segs, engine_label = _transcribe_with_openai(
                wav_path,
                offset_ticks,
                speaker,
                language,
                api_key=api_key,
                base_url=base_url,
                vocabulary_prompt=vocabulary_prompt,
            )
            return segs, engine_label, False
        except Exception:
            pass

    if allow_vad_fallback:
        segs = _transcribe_with_vad_fallback(wav_path, offset_ticks, speaker)
        return segs, "vad-fallback", True

    return [], "none", False


def transcribe_audio_for_subtitles(
    project_dir: str,
    language: str = "zh",
    model_size: str = "base",
    force_refresh: bool = False,
    api_key: str = "",
    base_url: str = "",
    clip_indices: list[int] | None = None,
    start_sec: float | None = None,
    end_sec: float | None = None,
    max_chars_per_line: int = 18,
    vocabulary_prompt: str = "",
    project_snapshot: dict[str, Any] | None = None,
    allow_vad_fallback: bool = True,
) -> dict[str, Any]:
    """Transcribe project audio (WAV + MP4/MOV/M4A) and segment into FrameTrail subtitle cues."""
    root = Path(project_dir)
    project_data = project_snapshot if isinstance(project_snapshot, dict) and project_snapshot else _load_project_json(root)
    total_source_ticks = _media_time_to_ticks(project_data.get("sourceDuration"), 0) or None

    requested_ranges = _resolve_requested_source_ranges(
        project_data,
        clip_indices=clip_indices,
        start_sec=start_sec,
        end_sec=end_sec,
    )
    range_start = requested_ranges[0][0] if requested_ranges else None
    range_end = requested_ranges[-1][1] if requested_ranges else None

    cache_path = root / "cache" / "ai-transcript.json"
    if not force_refresh and cache_path.is_file():
        try:
            cached = _normalize_transcript_list(json.loads(cache_path.read_text(encoding="utf-8")))
            if cached and not _is_vad_fallback_segments(cached):
                scoped = _filter_segments_to_ranges(cached, requested_ranges) if requested_ranges else cached
                subtitles = segment_transcript_to_subtitles(
                    scoped,
                    max_chars_per_line=max_chars_per_line,
                    total_source_ticks=total_source_ticks,
                )
                return {
                    "segments": scoped,
                    "subtitles": subtitles,
                    "rangeStart": range_start,
                    "rangeEnd": range_end,
                    "requestedRanges": requested_ranges,
                    "engineUsed": "cache",
                    "isFallback": False,
                    "warnings": [],
                }
        except (OSError, json.JSONDecodeError):
            pass

    audio_assets = _find_audio_assets(root, project_data)
    if not audio_assets:
        return {
            "segments": [],
            "subtitles": [],
            "rangeStart": range_start,
            "rangeEnd": range_end,
            "requestedRanges": requested_ranges,
            "engineUsed": "none",
            "isFallback": False,
            "warnings": ["No audio tracks or video clips with audio found in project."],
        }

    all_segments: list[dict[str, Any]] = []
    engines_used: list[str] = []
    used_fallback = False
    warnings: list[str] = []

    with tempfile.TemporaryDirectory(prefix="frametrail-asr-") as tmp_dir:
        tmp_root = Path(tmp_dir)
        chunk_counter = 0

        for asset in audio_assets:
            asset_path: Path = asset["path"]
            asset_start: int = int(asset["startTicks"])
            asset_dur: int = int(asset["durationTicks"])
            speaker: str = str(asset["speaker"])
            needs_extract: bool = bool(asset.get("needsExtract", asset_path.suffix.lower() != ".wav"))

            # Determine sub-slices of this asset to transcribe
            slices: list[tuple[int, float | None, float | None]] = []
            if requested_ranges:
                asset_end = asset_start + asset_dur if asset_dur > 0 else None
                for r0, r1 in requested_ranges:
                    ov0 = max(r0, asset_start)
                    ov1 = min(r1, asset_end) if asset_end is not None else r1
                    if ov1 <= ov0:
                        continue
                    local_start_sec = max(0.0, (ov0 - asset_start) / TICKS_PER_SECOND)
                    local_dur_sec = (ov1 - ov0) / TICKS_PER_SECOND
                    slices.append((ov0, local_start_sec, local_dur_sec))
                if not slices:
                    continue
            else:
                slices.append((asset_start, None, None))

            for slice_offset_ticks, local_start_sec, local_dur_sec in slices:
                chunk_counter += 1
                work_wav = asset_path
                if needs_extract or local_start_sec is not None or local_dur_sec is not None:
                    extracted_wav = tmp_root / f"chunk-{chunk_counter:03d}.wav"
                    ok = _extract_audio_to_wav16k(
                        asset_path,
                        extracted_wav,
                        start_sec=local_start_sec,
                        duration_sec=local_dur_sec,
                    )
                    if not ok and not needs_extract and local_start_sec is not None:
                        ok = _slice_pcm_wav_pure_python(
                            asset_path,
                            extracted_wav,
                            start_sec=local_start_sec,
                            duration_sec=local_dur_sec,
                        )
                    if not ok:
                        if needs_extract:
                            warnings.append(
                                f"Skipped '{asset_path.name}' (no extractable audio stream or ffmpeg unavailable)."
                            )
                            continue
                        # If full-track WAV couldn't be resampled, use the original WAV directly
                        work_wav = asset_path
                    else:
                        work_wav = extracted_wav

                track_segs, eng, is_fb = _transcribe_single_wav(
                    work_wav,
                    offset_ticks=slice_offset_ticks,
                    speaker=speaker,
                    language=language,
                    model_size=model_size,
                    api_key=api_key,
                    base_url=base_url,
                    vocabulary_prompt=vocabulary_prompt,
                    allow_vad_fallback=allow_vad_fallback,
                )
                if eng != "none" and eng not in engines_used:
                    engines_used.append(eng)
                if is_fb:
                    used_fallback = True
                all_segments.extend(track_segs)

    all_segments.sort(key=lambda s: (s["StartTicks"], s["EndTicks"]))
    if requested_ranges:
        all_segments = _filter_segments_to_ranges(all_segments, requested_ranges)

    is_only_fallback = bool(all_segments) and (used_fallback or _is_vad_fallback_segments(all_segments))
    # Never cache placeholder VAD text as real transcripts, and only write full-project transcripts to the shared cache.
    if all_segments and not is_only_fallback and requested_ranges is None:
        try:
            cache_path.parent.mkdir(parents=True, exist_ok=True)
            tmp_path = cache_path.with_suffix(".json.tmp")
            tmp_path.write_text(json.dumps(all_segments, ensure_ascii=False, indent=2), encoding="utf-8")
            tmp_path.replace(cache_path)
        except OSError:
            pass

    subtitles = segment_transcript_to_subtitles(
        all_segments,
        max_chars_per_line=max_chars_per_line,
        total_source_ticks=total_source_ticks,
    )
    engine_summary = "+".join(engines_used) if engines_used else "none"
    return {
        "segments": all_segments,
        "subtitles": subtitles,
        "rangeStart": range_start,
        "rangeEnd": range_end,
        "requestedRanges": requested_ranges,
        "engineUsed": engine_summary,
        "isFallback": is_only_fallback,
        "warnings": warnings,
    }


def build_subtitles_replace_command(
    snapshot: dict[str, Any],
    generated_subtitles: list[dict[str, Any]],
    requested_ranges: list[tuple[int, int]] | None = None,
    subtitle_style: dict[str, Any] | None = None,
) -> dict[str, Any]:
    """Construct an atomic op='subtitlesReplace' command, preserving gap subtitles for non-contiguous clip ranges."""
    subtitles = list(generated_subtitles)
    cmd: dict[str, Any] = {"op": "subtitlesReplace"}

    if requested_ranges:
        range_start = int(requested_ranges[0][0])
        range_end = int(requested_ranges[-1][1])
        cmd["rangeStart"] = range_start
        cmd["rangeEnd"] = range_end

        if len(requested_ranges) > 1:
            # Preserve existing subtitles that fall inside [rangeStart, rangeEnd) but outside every targeted range
            for eff in snapshot.get("effects", []) or []:
                if eff.get("kind") != "subtitle":
                    continue
                s0 = _media_time_to_ticks(eff.get("sourceStart"), 0)
                dur = _media_time_to_ticks(eff.get("sourceDuration"), 0)
                s1 = s0 + dur
                if not (s0 < range_end and s1 > range_start):
                    continue
                in_targeted = any(s0 < r1 and s1 > r0 for r0, r1 in requested_ranges)
                if not in_targeted:
                    subtitles.append(eff)
            subtitles.sort(key=lambda c: (_media_time_to_ticks(c.get("sourceStart"), 0), _media_time_to_ticks(c.get("sourceDuration"), 0)))

    cmd["subtitles"] = subtitles[:MAX_SUBTITLE_CUES]
    if subtitle_style is not None:
        cmd["subtitleStyle"] = subtitle_style
    return cmd


@tool
def transcribe_project_audio(
    project_dir: str,
    language: str = "zh",
    model_size: str = "base",
    force_refresh: bool = False,
    api_key: str = "",
    base_url: str = "",
    clip_indices: list[int] | None = None,
    start_sec: float | None = None,
    end_sec: float | None = None,
    max_chars_per_line: int = 18,
    vocabulary_prompt: str = "",
) -> str:
    """Transcribe audio tracks (recording WAVs and imported MP4/MOV/M4A clips) in a FrameTrail project with word-level timestamps."""
    result = transcribe_audio_for_subtitles(
        project_dir=project_dir,
        language=language,
        model_size=model_size,
        force_refresh=force_refresh,
        api_key=api_key,
        base_url=base_url,
        clip_indices=clip_indices,
        start_sec=start_sec,
        end_sec=end_sec,
        max_chars_per_line=max_chars_per_line,
        vocabulary_prompt=vocabulary_prompt,
        allow_vad_fallback=True,
    )
    return json.dumps(result["segments"], ensure_ascii=False, indent=2)


@tool
def detect_silence_and_pauses(project_dir: str, min_silence_sec: float = 1.2, threshold_db: float = -38.0) -> str:
    """Detect silent pauses in project audio tracks using 50ms RMS dBFS windows and build a FrameTrail batch split+delete command."""
    root = Path(project_dir)
    project_data = _load_project_json(root)
    audio_assets = _find_audio_assets(root, project_data)
    if not audio_assets:
        return json.dumps(
            {
                "silentIntervals": [],
                "batchCommand": {"op": "batch", "commands": []},
                "note": "No audio tracks found in project.",
            },
            ensure_ascii=False,
            indent=2,
        )

    per_track: list[list[dict[str, Any]]] = []
    with tempfile.TemporaryDirectory(prefix="frametrail-silence-") as tmp_dir:
        tmp_root = Path(tmp_dir)
        for idx, asset in enumerate(audio_assets):
            wav_path: Path = asset["path"]
            if asset.get("needsExtract", wav_path.suffix.lower() != ".wav"):
                extracted = tmp_root / f"track-{idx:02d}.wav"
                if not _extract_audio_to_wav16k(wav_path, extracted):
                    continue
                wav_path = extracted
            per_track.append(
                _detect_track_silence(
                    wav_path,
                    offset_ticks=asset["startTicks"],
                    min_silence_sec=min_silence_sec,
                    threshold_db=threshold_db,
                )
            )

    silent_intervals = _intersect_silence_tracks(per_track, min_silence_sec)
    batch_command = _build_remove_silence_batch(project_data, silent_intervals)
    return json.dumps(
        {
            "minSilenceSec": min_silence_sec,
            "thresholdDb": threshold_db,
            "silentIntervals": silent_intervals,
            "batchCommand": batch_command,
        },
        ensure_ascii=False,
        indent=2,
    )


def _load_pointer_events(project_dir: Path, project_data: dict[str, Any]) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    """Return (clicks, dwell_hotspots) from events/*.jsonl."""
    sources: list[tuple[Path, int, int | None]] = []
    seen: set[Path] = set()
    for item in project_data.get("media", []):
        if item.get("kind") != "pointerEvents":
            continue
        rel = item.get("path", "")
        if not rel:
            continue
        try:
            full = _resolve_project_path(project_dir, rel)
        except ValueError:
            continue
        if full.is_file():
            seen.add(full)
            dur = _media_time_to_ticks(item.get("duration"), 0)
            sources.append((full, _media_time_to_ticks(item.get("startOffset"), 0), dur if dur > 0 else None))

    events_dir = project_dir / "events"
    if events_dir.is_dir():
        for jsonl in sorted(events_dir.glob("*.jsonl")):
            resolved = jsonl.resolve()
            if resolved not in seen:
                sources.append((resolved, 0, None))

    clicks: list[dict[str, Any]] = []
    samples: list[dict[str, Any]] = []
    for file_path, offset_ticks, max_dur_ticks in sources:
        for line in file_path.read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if not line:
                continue
            try:
                ev = json.loads(line)
            except json.JSONDecodeError:
                continue
            if not isinstance(ev, dict) or ev.get("type") != "pointer":
                continue
            if ev.get("inside") is False:
                continue
            timescale = int(ev.get("timescale", 0) or 0)
            if timescale <= 0 or "ticks" not in ev or "x" not in ev or "y" not in ev:
                continue
            local_ticks = int(round(int(ev["ticks"]) * TICKS_PER_SECOND / timescale))
            if local_ticks < 0 or (max_dur_ticks is not None and local_ticks >= max_dur_ticks):
                continue
            total_ticks = offset_ticks + local_ticks
            x = float(ev["x"])
            y = float(ev["y"])
            if not (math.isfinite(x) and math.isfinite(y) and 0.0 <= x <= 1.0 and 0.0 <= y <= 1.0):
                continue
            kind = int(ev.get("kind", 0) or 0)
            entry = {
                "ticks": total_ticks,
                "sec": round(total_ticks / TICKS_PER_SECOND, 4),
                "x": round(x, 4),
                "y": round(y, 4),
                "kind": kind,
            }
            samples.append(entry)
            if kind in (1, 3):
                clicks.append(entry)

    clicks.sort(key=lambda c: c["ticks"])
    samples.sort(key=lambda s: s["ticks"])

    # Compute dwell hotspots where pointer remains within radius 0.03 for >= 0.8s
    dwell_hotspots: list[dict[str, Any]] = []
    if samples:
        cluster: list[dict[str, Any]] = [samples[0]]
        for s in samples[1:]:
            anchor = cluster[0]
            if math.hypot(s["x"] - anchor["x"], s["y"] - anchor["y"]) <= 0.03:
                cluster.append(s)
            else:
                _maybe_add_dwell(cluster, dwell_hotspots)
                cluster = [s]
        _maybe_add_dwell(cluster, dwell_hotspots)

    return clicks, dwell_hotspots


def _maybe_add_dwell(cluster: list[dict[str, Any]], out: list[dict[str, Any]]) -> None:
    if len(cluster) < 2:
        return
    span_ticks = cluster[-1]["ticks"] - cluster[0]["ticks"]
    if span_ticks < int(0.8 * TICKS_PER_SECOND):
        return
    mid_ticks = (cluster[0]["ticks"] + cluster[-1]["ticks"]) // 2
    avg_x = sum(p["x"] for p in cluster) / len(cluster)
    avg_y = sum(p["y"] for p in cluster) / len(cluster)
    out.append(
        {
            "ticks": mid_ticks,
            "sec": round(mid_ticks / TICKS_PER_SECOND, 4),
            "durationSec": round(span_ticks / TICKS_PER_SECOND, 4),
            "x": round(avg_x, 4),
            "y": round(avg_y, 4),
            "kind": 0,
        }
    )


@tool
def search_transcript_and_clicks(project_dir: str, query: str = "", start_sec: float = 0.0, end_sec: float = -1.0) -> str:
    """Search ASR transcript segments and correlate them with pointer click coordinates and dwell hotspots."""
    root = Path(project_dir)
    project_data = _load_project_json(root)
    cache_path = root / "cache" / "ai-transcript.json"

    transcript: list[dict[str, Any]] = []
    if cache_path.is_file():
        try:
            transcript = _normalize_transcript_list(json.loads(cache_path.read_text(encoding="utf-8")))
        except (OSError, json.JSONDecodeError):
            transcript = []
    elif _find_audio_assets(root, project_data):
        transcript = transcribe_audio_for_subtitles(str(root), allow_vad_fallback=True)["segments"]

    clicks, dwell_hotspots = _load_pointer_events(root, project_data)

    start_ticks = _sec_to_ticks(max(0.0, start_sec))
    end_ticks = _sec_to_ticks(end_sec) if end_sec >= 0 else None

    def _in_range(ticks: int) -> bool:
        if ticks < start_ticks:
            return False
        return end_ticks is None or ticks <= end_ticks

    filtered_clicks = [c for c in clicks if _in_range(c["ticks"])]
    filtered_dwells = [d for d in dwell_hotspots if _in_range(d["ticks"])]

    q = query.strip().lower()
    keywords = [w for w in q.split() if w]
    matched_segments: list[dict[str, Any]] = []
    for seg in transcript:
        if seg["EndTicks"] < start_ticks:
            continue
        if end_ticks is not None and seg["StartTicks"] > end_ticks:
            continue
        text_lower = seg["text"].lower()
        if q and q not in text_lower and not all(k in text_lower for k in keywords):
            continue

        pad = TICKS_PER_SECOND  # +/- 1.0s correlation window
        nearby_clicks = [
            c for c in clicks if seg["StartTicks"] - pad <= c["ticks"] <= seg["EndTicks"] + pad
        ]
        inside_clicks = [
            c for c in nearby_clicks if seg["StartTicks"] <= c["ticks"] <= seg["EndTicks"]
        ]
        nearby_dwells = [
            d for d in dwell_hotspots if seg["StartTicks"] - pad <= d["ticks"] <= seg["EndTicks"] + pad
        ]

        primary = (inside_clicks or nearby_clicks or nearby_dwells)
        focus_x = primary[0]["x"] if primary else 0.5
        focus_y = primary[0]["y"] if primary else 0.5

        matched_segments.append(
            {
                "segment": seg,
                "clicks": nearby_clicks,
                "dwellHotspots": nearby_dwells,
                "focusX": focus_x,
                "focusY": focus_y,
                "suggestedZoom": {
                    "startSec": seg["startSec"],
                    "endSec": seg["endSec"],
                    "focusX": focus_x,
                    "focusY": focus_y,
                    "scale": 2.0,
                },
            }
        )

    return json.dumps(
        {
            "query": query,
            "matches": matched_segments,
            "clicks": filtered_clicks,
            "dwellHotspots": filtered_dwells,
        },
        ensure_ascii=False,
        indent=2,
    )


def _locate_ffmpeg() -> str | None:
    for env_var in ("IMAGEIO_FFMPEG_EXE", "FFMPEG_PATH"):
        candidate = os.environ.get(env_var, "").strip()
        if candidate and Path(candidate).is_file():
            return candidate

    repo_root = Path(__file__).resolve().parent.parent
    bundled = repo_root / ".tools" / "promotion-runtime" / "imageio_ffmpeg" / "binaries"
    if bundled.is_dir():
        for exe in sorted(bundled.glob("ffmpeg*.exe")):
            if exe.is_file():
                return str(exe)

    try:
        import imageio_ffmpeg  # type: ignore[import-not-found]

        exe = imageio_ffmpeg.get_ffmpeg_exe()
        if exe and Path(exe).is_file():
            return exe
    except Exception:
        pass

    return shutil.which("ffmpeg")


def _resolve_chat_completions_url(base_url: str, default_base: str) -> str:
    base = (base_url or default_base).strip().rstrip("/")
    if not base:
        base = default_base.rstrip("/")
    if base.endswith("/chat/completions"):
        return base
    return f"{base}/chat/completions"


def _resolve_anthropic_messages_url(base_url: str, default_base: str = "https://api.anthropic.com") -> str:
    base = (base_url or default_base).strip().rstrip("/")
    if not base:
        base = default_base.rstrip("/")
    if base.endswith("/v1/messages") or base.endswith("/messages"):
        return base
    if base.endswith("/v1"):
        return f"{base}/messages"
    return f"{base}/v1/messages"


def _is_deepseek_vision_model(model_name: str) -> bool:
    normalized = (model_name or "").strip().lower()
    return bool(normalized) and any(token in normalized for token in ("flash", "vision", "vl"))


def _get_active_project_context() -> dict[str, Any]:
    try:
        from agent.editor_tools import get_project_context

        return get_project_context()
    except Exception:
        return {}


def _sanitize_vision_regions(raw_regions: Any) -> list[dict[str, Any]]:
    if not isinstance(raw_regions, list):
        return []
    sanitized: list[dict[str, Any]] = []
    for item in raw_regions:
        if not isinstance(item, dict):
            continue
        try:
            x = max(0.0, min(0.999, float(item.get("x", 0.0))))
            y = max(0.0, min(0.999, float(item.get("y", 0.0))))
            width = max(0.001, min(1.0 - x, float(item.get("width", 0.1))))
            height = max(0.001, min(1.0 - y, float(item.get("height", 0.1))))
            ts = max(0.0, float(item.get("timestampSec", 0.0)))
        except (TypeError, ValueError):
            continue
        label = str(item.get("label") or item.get("name") or "region").strip() or "region"
        sanitized.append(
            {
                "label": label,
                "timestampSec": round(ts, 4),
                "x": round(x, 4),
                "y": round(y, 4),
                "width": round(width, 4),
                "height": round(height, 4),
            }
        )
    return sanitized


def _parse_vision_json(text: str) -> dict[str, Any]:
    cleaned = (text or "").strip()
    if not cleaned:
        return {"answer": "", "regions": []}

    parsed: dict[str, Any] | None = None
    candidates = [cleaned]
    fenced = re.search(r"```(?:json)?\s*(\{[\s\S]*?\})\s*```", cleaned, flags=re.IGNORECASE)
    if fenced:
        candidates.append(fenced.group(1).strip())
    first_brace = cleaned.find("{")
    last_brace = cleaned.rfind("}")
    if first_brace != -1 and last_brace > first_brace:
        candidates.append(cleaned[first_brace : last_brace + 1])

    for candidate in candidates:
        try:
            obj = json.loads(candidate)
            if isinstance(obj, dict):
                parsed = obj
                break
        except json.JSONDecodeError:
            continue

    if parsed is None:
        return {"answer": cleaned, "regions": []}

    answer = str(parsed.get("answer", "") or "").strip()
    regions = _sanitize_vision_regions(parsed.get("regions"))
    if not answer and not regions:
        answer = cleaned
    return {"answer": answer, "regions": regions}


def _extract_chat_completion_text(data: dict[str, Any]) -> str:
    choices = data.get("choices")
    if not isinstance(choices, list) or not choices:
        return ""
    msg = choices[0].get("message") if isinstance(choices[0], dict) else None
    if not isinstance(msg, dict):
        return ""
    content = msg.get("content", "")
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        parts: list[str] = []
        for block in content:
            if isinstance(block, str):
                parts.append(block)
            elif isinstance(block, dict) and block.get("type") == "text":
                parts.append(str(block.get("text", "")))
        return "".join(parts)
    return ""


def _resolve_vision_candidates(ctx: dict[str, Any] | None = None) -> list[dict[str, str]]:
    """Build prioritized Vision LLM candidates from active session BYOK credentials and env vars."""
    active = ctx if isinstance(ctx, dict) else _get_active_project_context()
    ctx_provider = str(active.get("provider", "") or "").strip().lower()
    ctx_api_key = str(active.get("api_key", "") or "").strip()
    ctx_base_url = str(active.get("base_url", "") or "").strip()
    ctx_model = str(active.get("model_name", "") or "").strip()

    candidates: list[dict[str, str]] = []
    seen: set[tuple[str, str, str, str]] = set()

    def add_candidate(provider: str, api_key: str, url: str, model: str) -> None:
        key = (api_key or "").strip()
        if not key or key.lower() == "missing-key":
            return
        dedupe_key = (provider, url, model, key)
        if dedupe_key in seen:
            return
        seen.add(dedupe_key)
        candidates.append(
            {
                "provider": provider,
                "api_key": key,
                "url": url,
                "model": model,
            }
        )

    # 1. Active session provider gets first priority so BYOK DeepSeek / OpenAI / Anthropic works without env vars
    if ctx_provider == "deepseek" and ctx_api_key:
        ds_model = (
            os.environ.get("FRAMETRAIL_DEEPSEEK_VISION_MODEL", "").strip()
            or (ctx_model if _is_deepseek_vision_model(ctx_model) else "deepseek-flash")
        )
        add_candidate(
            "deepseek",
            ctx_api_key,
            _resolve_chat_completions_url(ctx_base_url, "https://api.deepseek.com"),
            ds_model,
        )
    elif ctx_provider == "openai" and ctx_api_key:
        oa_model = os.environ.get("FRAMETRAIL_VISION_MODEL", "").strip() or ctx_model or "gpt-4o-mini"
        add_candidate(
            "openai",
            ctx_api_key,
            _resolve_chat_completions_url(ctx_base_url, "https://api.openai.com/v1"),
            oa_model,
        )
    elif ctx_provider == "anthropic" and ctx_api_key:
        an_model = (
            os.environ.get("FRAMETRAIL_ANTHROPIC_VISION_MODEL", "").strip()
            or ctx_model
            or "claude-3-5-sonnet-latest"
        )
        add_candidate(
            "anthropic",
            ctx_api_key,
            _resolve_anthropic_messages_url(ctx_base_url, "https://api.anthropic.com"),
            an_model,
        )

    # 2. Environment-variable fallbacks
    deepseek_env_key = os.environ.get("DEEPSEEK_API_KEY", "").strip()
    if deepseek_env_key:
        add_candidate(
            "deepseek",
            deepseek_env_key,
            _resolve_chat_completions_url(
                os.environ.get("DEEPSEEK_BASE_URL", ""),
                "https://api.deepseek.com",
            ),
            os.environ.get("FRAMETRAIL_DEEPSEEK_VISION_MODEL", "").strip() or "deepseek-flash",
        )

    openai_env_key = os.environ.get("OPENAI_API_KEY", "").strip()
    if openai_env_key:
        add_candidate(
            "openai",
            openai_env_key,
            _resolve_chat_completions_url(
                os.environ.get("OPENAI_BASE_URL", ""),
                "https://api.openai.com/v1",
            ),
            os.environ.get("FRAMETRAIL_VISION_MODEL", "").strip() or "gpt-4o-mini",
        )

    anthropic_env_key = os.environ.get("ANTHROPIC_API_KEY", "").strip()
    if anthropic_env_key:
        add_candidate(
            "anthropic",
            anthropic_env_key,
            _resolve_anthropic_messages_url(
                os.environ.get("ANTHROPIC_BASE_URL", ""),
                "https://api.anthropic.com",
            ),
            os.environ.get("FRAMETRAIL_ANTHROPIC_VISION_MODEL", "").strip() or "claude-3-5-sonnet-latest",
        )

    return candidates


def _post_openai_compatible_vision(
    *,
    provider: str,
    api_key: str,
    url: str,
    model: str,
    prompt: str,
    frames: list[dict[str, Any]],
) -> dict[str, Any]:
    content: list[dict[str, Any]] = [{"type": "text", "text": prompt}]
    for idx, fr in enumerate(frames, start=1):
        ts = float(fr.get("timestampSec", 0.0))
        w = int(fr.get("width", 0) or 0)
        h = int(fr.get("height", 0) or 0)
        mime = str(fr.get("mimeType") or "image/jpeg")
        content.append(
            {
                "type": "text",
                "text": f"[Frame #{idx} at timestampSec={ts:.3f}s, resolution={w}x{h}]",
            }
        )
        image_url_obj: dict[str, Any] = {"url": f"data:{mime};base64,{fr['imageBase64']}"}
        # DeepSeek Vision (deepseek-flash) expects image_url.url without OpenAI's detail field
        if provider == "openai":
            image_url_obj["detail"] = "low"
        content.append({"type": "image_url", "image_url": image_url_obj})

    base_body: dict[str, Any] = {
        "model": model,
        "messages": [{"role": "user", "content": content}],
        "max_tokens": 1024,
    }

    def _send(use_json_format: bool) -> dict[str, Any]:
        body = dict(base_body)
        if use_json_format:
            body["response_format"] = {"type": "json_object"}
        payload = json.dumps(body).encode("utf-8")
        req = urllib.request.Request(
            url,
            data=payload,
            headers={
                "Authorization": f"Bearer {api_key}",
                "Content-Type": "application/json",
            },
            method="POST",
        )
        with urllib.request.urlopen(req, timeout=60) as resp:
            return json.loads(resp.read().decode("utf-8"))

    try:
        data = _send(use_json_format=True)
    except urllib.error.HTTPError as exc:
        if exc.code == 400:
            data = _send(use_json_format=False)
        else:
            raise

    text = _extract_chat_completion_text(data)
    parsed = _parse_vision_json(text)
    parsed["visionProvider"] = provider
    parsed["visionModel"] = model
    return parsed


def _post_anthropic_vision(
    *,
    api_key: str,
    url: str,
    model: str,
    prompt: str,
    frames: list[dict[str, Any]],
) -> dict[str, Any]:
    content_blocks: list[dict[str, Any]] = []
    for idx, fr in enumerate(frames, start=1):
        ts = float(fr.get("timestampSec", 0.0))
        w = int(fr.get("width", 0) or 0)
        h = int(fr.get("height", 0) or 0)
        mime = str(fr.get("mimeType") or "image/jpeg")
        content_blocks.append(
            {
                "type": "text",
                "text": f"[Frame #{idx} at timestampSec={ts:.3f}s, resolution={w}x{h}]",
            }
        )
        content_blocks.append(
            {
                "type": "image",
                "source": {"type": "base64", "media_type": mime, "data": fr["imageBase64"]},
            }
        )
    content_blocks.append({"type": "text", "text": prompt})
    payload = json.dumps(
        {
            "model": model,
            "max_tokens": 1024,
            "messages": [{"role": "user", "content": content_blocks}],
        }
    ).encode("utf-8")
    req = urllib.request.Request(
        url,
        data=payload,
        headers={
            "x-api-key": api_key,
            "anthropic-version": "2023-06-01",
            "Content-Type": "application/json",
        },
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=60) as resp:
        data = json.loads(resp.read().decode("utf-8"))
    text = data["content"][0]["text"]
    parsed = _parse_vision_json(text)
    parsed["visionProvider"] = "anthropic"
    parsed["visionModel"] = model
    return parsed


def _query_vision_llm(
    question: str,
    frames: list[dict[str, Any]],
    ctx: dict[str, Any] | None = None,
) -> dict[str, Any] | None:
    """Query DeepSeek (deepseek-flash), OpenAI, or Anthropic Vision model for visual understanding and NormalizedRect UI bounding boxes."""
    prompt = (
        f"You are inspecting video frames from a screen recording.\n"
        f"User question: {question}\n"
        "Respond strictly with a single JSON object containing:\n"
        '{"answer": "<concise answer describing the relevant visual elements>", '
        '"regions": [{"label": "<element name>", "timestampSec": <float>, "x": <0..1>, "y": <0..1>, "width": <0..1>, "height": <0..1>}]}\n'
        "Use the timestampSec from the corresponding frame header. "
        "Ensure every NormalizedRect satisfies 0 <= x,y <= 1, width > 0, height > 0, x+width <= 1, y+height <= 1."
    )

    candidates = _resolve_vision_candidates(ctx)
    if not candidates:
        return {
            "answer": "",
            "regions": [],
            "visionProvider": "",
            "visionModel": "",
            "visionError": "No Vision LLM API key configured (DeepSeek, OpenAI, or Anthropic).",
        }

    last_error = ""
    last_provider = ""
    last_model = ""
    for cand in candidates:
        provider = cand["provider"]
        model = cand["model"]
        last_provider = provider
        last_model = model
        try:
            if provider in ("deepseek", "openai"):
                return _post_openai_compatible_vision(
                    provider=provider,
                    api_key=cand["api_key"],
                    url=cand["url"],
                    model=model,
                    prompt=prompt,
                    frames=frames,
                )
            if provider == "anthropic":
                return _post_anthropic_vision(
                    api_key=cand["api_key"],
                    url=cand["url"],
                    model=model,
                    prompt=prompt,
                    frames=frames,
                )
        except Exception as exc:
            last_error = f"{provider}/{model}: {exc}"
            continue

    return {
        "answer": "",
        "regions": [],
        "visionProvider": last_provider,
        "visionModel": last_model,
        "visionError": last_error or "Vision LLM request failed.",
    }


@tool
def inspect_video_frames(
    project_dir: str = "",
    start_sec: float = -1.0,
    end_sec: float = -1.0,
    max_frames: int = 4,
    question: str = "",
    include_base64: bool = False,
) -> str:
    """Extract keyframes from screen video in [start_sec, end_sec] and analyze visual elements / NormalizedRect UI regions via a Vision LLM (supports DeepSeek deepseek-flash, OpenAI, and Anthropic).

    project_dir is optional and automatically defaults to the active FrameTrail project directory.
    If start_sec < 0, defaults to the current playhead_sec; if end_sec < 0, inspects a single frame at start_sec.
    """
    ctx = _get_active_project_context()
    resolved_dir = (project_dir or "").strip()
    ctx_dir = str(ctx.get("project_dir", "") or "").strip()
    if (not resolved_dir or not Path(resolved_dir).is_dir()) and ctx_dir:
        resolved_dir = ctx_dir
    if not resolved_dir:
        return json.dumps(
            {"frames": [], "regions": [], "error": "project_dir is required and no active project directory is bound."},
            ensure_ascii=False,
        )

    root = Path(resolved_dir)
    project_data = _load_project_json(root)
    screen_assets = _find_screen_assets(root, project_data)
    if not screen_assets:
        return json.dumps({"frames": [], "regions": [], "error": "No screen video found in project."}, ensure_ascii=False)

    ffmpeg_exe = _locate_ffmpeg()
    if not ffmpeg_exe:
        return json.dumps({"frames": [], "regions": [], "error": "ffmpeg executable not found."}, ensure_ascii=False)

    default_ts = float(ctx.get("playhead_sec", 0.0) or 0.0)
    s_sec = max(0.0, float(default_ts if start_sec < 0 else start_sec))
    e_sec = max(s_sec, float(s_sec if end_sec < 0 else end_sec))
    count = max(1, min(12, int(max_frames)))
    if count == 1 or math.isclose(s_sec, e_sec):
        timestamps = [s_sec]
    else:
        step = (e_sec - s_sec) / (count - 1)
        timestamps = [s_sec + i * step for i in range(count)]

    frames: list[dict[str, Any]] = []
    errors: list[str] = []
    with tempfile.TemporaryDirectory(prefix="frametrail-frames-") as tmp_dir:
        tmp = Path(tmp_dir)
        for idx, ts in enumerate(timestamps):
            ts_ticks = _sec_to_ticks(ts)
            chosen = screen_assets[0]
            local_sec = ts
            for asset in screen_assets:
                a_start = asset["startTicks"]
                a_dur = asset["durationTicks"]
                if a_dur > 0 and a_start <= ts_ticks <= a_start + a_dur:
                    chosen = asset
                    raw_local = max(0.0, (ts_ticks - a_start) / TICKS_PER_SECOND)
                    clip_dur_sec = a_dur / TICKS_PER_SECOND
                    if ts_ticks >= a_start + a_dur and clip_dur_sec > 0.05:
                        local_sec = max(0.0, clip_dur_sec - 0.05)
                    else:
                        local_sec = raw_local
                    break

            out_jpg = tmp / f"frame-{idx:03d}.jpg"
            cmd = [
                ffmpeg_exe,
                "-hide_banner",
                "-loglevel",
                "error",
                "-ss",
                f"{local_sec:.3f}",
                "-i",
                str(chosen["path"]),
                "-frames:v",
                "1",
                "-vf",
                "scale='min(960,iw)':-2",
                "-q:v",
                "4",
                "-y",
                str(out_jpg),
            ]
            proc = subprocess.run(cmd, capture_output=True, text=True, timeout=30)
            if proc.returncode != 0 or not out_jpg.is_file():
                errors.append(proc.stderr.strip() or f"ffmpeg exited with code {proc.returncode}")
                continue
            jpg_bytes = out_jpg.read_bytes()
            frames.append(
                {
                    "timestampSec": round(ts, 4),
                    "ticks": ts_ticks,
                    "mimeType": mimetypes.guess_type(str(out_jpg))[0] or "image/jpeg",
                    "width": chosen["width"],
                    "height": chosen["height"],
                    "byteSize": len(jpg_bytes),
                    "imageBase64": base64.b64encode(jpg_bytes).decode("ascii"),
                }
            )

    effective_question = (
        question.strip()
        or "Describe the visible visual elements, text, and UI layout in these video frames, and locate notable UI regions as normalized bounding boxes."
    )
    vision_result = _query_vision_llm(effective_question, frames, ctx=ctx) if frames else None
    regions = vision_result.get("regions", []) if isinstance(vision_result, dict) else []
    answer = vision_result.get("answer", "") if isinstance(vision_result, dict) else ""
    vision_provider = vision_result.get("visionProvider", "") if isinstance(vision_result, dict) else ""
    vision_model = vision_result.get("visionModel", "") if isinstance(vision_result, dict) else ""
    vision_error = vision_result.get("visionError", "") if isinstance(vision_result, dict) else ""

    output_frames = (
        frames
        if include_base64
        else [{k: v for k, v in fr.items() if k != "imageBase64"} for fr in frames]
    )

    payload: dict[str, Any] = {
        "startSec": round(s_sec, 4),
        "endSec": round(e_sec, 4),
        "frameCount": len(frames),
        "frames": output_frames,
        "answer": answer,
        "regions": regions,
        "visionProvider": vision_provider,
        "visionModel": vision_model,
        "errors": errors,
    }
    if vision_error:
        payload["visionError"] = vision_error

    return json.dumps(payload, ensure_ascii=False, indent=2)


# --- Speech expression polish (pauses + Chinese/English fillers + stutters/repeats) ---

_VERBATIM_FILLER_PROMPT = (
    "请逐字如实转录，保留所有嗯、呃、额、啊、哦、噢、唔、诶、那个、就是说、然后呢等口语语气词与卡顿重复，不要自动删改。"
    "Verbatim speech transcript keeping um, uh, er, erm, ah, hmm, like, you know, and stutters."
)

_STRONG_FILLERS = {
    "嗯", "嗯嗯", "呃", "呃呃", "额", "额额", "唔", "噢", "诶", "欸",
    "um", "umm", "ummm", "uh", "uhh", "uhhh", "erm", "er", "err", "hmm", "hmmm", "mm", "mmm", "ah", "ahh",
}
_CONTEXTUAL_PARTICLES = {"啊", "哦", "哎", "呐", "嘛", "呢", "呀"}
_ZH_VERBAL_TICS = {"那个", "就是说", "怎么说呢", "然后呢", "就是那个", "这个这个", "那个那个"}
_EN_PHRASE_TICS = {"you know", "i mean"}
_ALLOWED_ZH_REDUPLICATIONS = {
    "看", "试", "慢", "好", "刚", "常", "往", "仅", "渐", "轻", "小", "大", "多", "少",
    "天", "年", "人", "处", "家", "件", "个", "条", "点", "滴", "谢", "爸", "妈", "哥",
    "姐", "弟", "妹", "爷", "奶", "宝", "哈", "嘻", "呵", "呜", "听", "说", "想", "走",
    "查", "找", "写", "读", "学", "练", "玩", "等", "歇", "聊", "转", "扫", "拉", "推",
    "按", "切", "换", "改", "调", "测", "算", "比", "对", "清", "简", "高", "低", "早",
    "晚", "长", "短", "新", "老", "真", "实", "稳", "准", "快", "近", "远", "满", "全",
}
_EDGE_PUNCT = "，,、。.！!？?；;：:…—-~～\"'”’）)》】“‘（(《【"


def _normalize_intensity(raw: str | None) -> str:
    val = (raw or "standard").strip().lower()
    if val in ("gentle", "light", "conservative", "轻度", "保守", "轻微"):
        return "gentle"
    if val in ("tight", "aggressive", "compact", "紧凑", "激进", "强力"):
        return "tight"
    return "standard"


def _clean_speech_token(raw: str) -> str:
    return (raw or "").strip().strip(_EDGE_PUNCT).strip()


def _detect_speech_fillers_and_repeats(
    segments: list[dict[str, Any]],
    intensity: str = "standard",
    remove_fillers: bool = True,
    remove_repeats: bool = True,
) -> list[dict[str, Any]]:
    """Detect filler words, contextual verbal tics, immediate stutters, and short phrase restarts."""
    words: list[dict[str, Any]] = []
    for seg_idx, seg in enumerate(segments):
        for w in seg.get("words") or []:
            raw = str(w.get("text") or "").strip()
            if not raw:
                continue
            clean = _clean_speech_token(raw)
            s_sec = float(w.get("startSec", int(w.get("StartTicks", 0)) / TICKS_PER_SECOND))
            e_sec = max(s_sec + 0.02, float(w.get("endSec", int(w.get("EndTicks", 0)) / TICKS_PER_SECOND)))
            words.append(
                {
                    "raw": raw,
                    "clean": clean,
                    "lower": clean.lower(),
                    "startSec": s_sec,
                    "endSec": e_sec,
                    "segIdx": seg_idx,
                    "trailingPunct": bool(raw and raw[-1] in _EDGE_PUNCT),
                }
            )
    words.sort(key=lambda item: item["startSec"])
    if not words:
        return []

    consumed = [False] * len(words)
    cuts: list[dict[str, Any]] = []
    allow_repeats = remove_repeats and intensity != "gentle"

    def _join_clean(start_i: int, length: int) -> str:
        out = ""
        for k in range(length):
            tok = words[start_i + k]["clean"]
            if not tok:
                continue
            if out and out[-1].isascii() and out[-1].isalnum() and tok[0].isascii() and tok[0].isalnum():
                out += " " + tok
            else:
                out += tok
        return out

    # 1. Short phrase restarts (2..5 token prefix repeated within 3.2s)
    if allow_repeats:
        i = 0
        while i < len(words):
            if consumed[i]:
                i += 1
                continue
            match_j = -1
            match_phrase = ""
            max_len = min(5, (len(words) - i) // 2)
            for length in range(max_len, 1, -1):
                if any(not words[i + k]["clean"] for k in range(length)):
                    continue
                for j in range(i + length, min(len(words) - length, i + length + 3) + 1):
                    if words[j]["startSec"] - words[i]["startSec"] > 3.2:
                        break
                    if all(words[i + k]["lower"] == words[j + k]["lower"] for k in range(length)):
                        match_j = j
                        match_phrase = _join_clean(i, length)
                        break
                if match_j > i:
                    break
            if match_j > i:
                s_ticks = _sec_to_ticks(words[i]["startSec"])
                e_ticks = _sec_to_ticks(words[match_j]["startSec"])
                if e_ticks - s_ticks >= 1_200_000:
                    cuts.append({"start": s_ticks, "end": e_ticks, "reason": "repeat", "label": f"「{match_phrase}」"})
                    for k in range(i, match_j):
                        consumed[k] = True
                    i = match_j
                    continue
            i += 1

    # 2. Immediate token stutters
    if allow_repeats:
        for i in range(len(words) - 1):
            if consumed[i] or consumed[i + 1]:
                continue
            a = words[i]["clean"]
            b = words[i + 1]["clean"]
            if not a or a.lower() != b.lower():
                continue
            gap = words[i + 1]["startSec"] - words[i]["endSec"]
            if gap > 0.70 or a.lower() in _STRONG_FILLERS:
                continue
            triple = i + 2 < len(words) and words[i + 2]["lower"] == a.lower()
            if len(a) == 1 and a in _ALLOWED_ZH_REDUPLICATIONS and gap < 0.14 and not triple:
                continue
            s_ticks = _sec_to_ticks(words[i]["startSec"])
            e_ticks = _sec_to_ticks(words[i + 1]["startSec"])
            if e_ticks - s_ticks >= 800_000:
                cuts.append({"start": s_ticks, "end": e_ticks, "reason": "repeat", "label": f"「{a}」"})
                consumed[i] = True

    # 3. Filler words & contextual verbal tics
    if remove_fillers:
        i = 0
        while i < len(words):
            if consumed[i] or not words[i]["clean"]:
                i += 1
                continue
            w = words[i]
            gap_before = 1.0 if i == 0 else max(0.0, w["startSec"] - words[i - 1]["endSec"])
            gap_after = 1.0 if i == len(words) - 1 else max(0.0, words[i + 1]["startSec"] - w["endSec"])
            prev_clause = i == 0 or words[i - 1]["trailingPunct"] or words[i - 1]["segIdx"] != w["segIdx"]
            ends_clause = w["trailingPunct"] or (i + 1 < len(words) and words[i + 1]["segIdx"] != w["segIdx"])

            # Multi-token tics
            if intensity != "gentle" and i + 1 < len(words) and not consumed[i + 1]:
                matched_tic = None
                for length in (3, 2):
                    if i + length > len(words) or any(consumed[i + k] for k in range(length)):
                        continue
                    if any(words[i + k]["startSec"] - words[i + k - 1]["endSec"] > 0.25 for k in range(1, length)):
                        continue
                    zh_join = "".join(words[i + k]["clean"] for k in range(length))
                    if zh_join in _ZH_VERBAL_TICS:
                        last_i = i + length - 1
                        ga = 1.0 if last_i == len(words) - 1 else max(0.0, words[last_i + 1]["startSec"] - words[last_i]["endSec"])
                        ec = words[last_i]["trailingPunct"] or (last_i + 1 < len(words) and words[last_i + 1]["segIdx"] != words[last_i]["segIdx"])
                        if ((prev_clause or gap_before >= 0.08) and (ec or ga >= 0.12)) or (intensity == "tight" and (prev_clause or ga >= 0.10)):
                            matched_tic = (length, zh_join)
                            break
                if matched_tic is None and words[i + 1]["startSec"] - w["endSec"] <= 0.25:
                    en_join = f"{w['lower']} {words[i + 1]['lower']}"
                    if en_join in _EN_PHRASE_TICS:
                        ga = 1.0 if i + 2 >= len(words) else max(0.0, words[i + 2]["startSec"] - words[i + 1]["endSec"])
                        if ((prev_clause or gap_before >= 0.10) and (words[i + 1]["trailingPunct"] or ga >= 0.10)) or intensity == "tight":
                            matched_tic = (2, en_join)
                if matched_tic is not None:
                    span_len, tic_text = matched_tic
                    s_ticks = _sec_to_ticks(words[i]["startSec"])
                    e_ticks = _sec_to_ticks(words[i + span_len - 1]["endSec"])
                    if e_ticks - s_ticks >= 1_000_000:
                        cuts.append({"start": s_ticks, "end": e_ticks, "reason": "filler", "label": f"「{tic_text}」"})
                        for k in range(i, i + span_len):
                            consumed[k] = True
                        i += span_len
                        continue

            low = w["lower"]
            is_strong = low in _STRONG_FILLERS
            is_contextual = not is_strong and w["clean"] in _CONTEXTUAL_PARTICLES
            is_zh_tic = not is_strong and not is_contextual and intensity != "gentle" and w["clean"] in _ZH_VERBAL_TICS

            should_cut = False
            if is_strong:
                should_cut = intensity != "gentle" or prev_clause or ends_clause or gap_before >= 0.08 or gap_after >= 0.10
            elif is_contextual:
                dur = w["endSec"] - w["startSec"]
                isolated = (prev_clause or gap_before >= 0.08) and (ends_clause or gap_after >= 0.08 or dur >= 0.18)
                opening = prev_clause and (ends_clause or gap_after >= 0.10)
                should_cut = isolated or opening or (intensity == "tight" and gap_before >= 0.06)
            elif is_zh_tic:
                repeated = i + 1 < len(words) and words[i + 1]["clean"] == w["clean"]
                should_cut = repeated or ((prev_clause or gap_before >= 0.10) and (ends_clause or gap_after >= 0.12)) or (intensity == "tight" and (prev_clause or gap_after >= 0.10))

            if should_cut:
                end_i = i
                while (
                    end_i + 1 < len(words)
                    and not consumed[end_i + 1]
                    and words[end_i + 1]["lower"] in _STRONG_FILLERS
                    and words[end_i + 1]["startSec"] - words[end_i]["endSec"] <= 0.35
                ):
                    end_i += 1
                s_ticks = _sec_to_ticks(words[i]["startSec"])
                e_ticks = _sec_to_ticks(words[end_i]["endSec"])
                if e_ticks - s_ticks >= 800_000:
                    lbl = _join_clean(i, end_i - i + 1)
                    cuts.append({"start": s_ticks, "end": e_ticks, "reason": "filler", "label": f"「{lbl}」"})
                    for k in range(i, end_i + 1):
                        consumed[k] = True
                    i = end_i + 1
                    continue
            i += 1

    return cuts


def _merge_and_clamp_cut_ranges(
    project_data: dict[str, Any],
    raw_cuts: list[dict[str, Any]],
    min_keep_ticks: int = MIN_KEEP_TICKS,
) -> list[dict[str, Any]]:
    segments = project_data.get("segments", []) or []
    total_ticks = _media_time_to_ticks(project_data.get("sourceDuration"), 0)
    if not segments or total_ticks <= 0 or not raw_cuts:
        return []

    clipped: list[dict[str, Any]] = []
    for item in raw_cuts:
        cs = max(0, min(total_ticks, int(item["start"])))
        ce = max(0, min(total_ticks, int(item["end"])))
        if ce <= cs:
            continue
        for seg in segments:
            s0 = _media_time_to_ticks(seg.get("sourceStart"), 0)
            s1 = s0 + _media_time_to_ticks(seg.get("sourceDuration"), 0)
            ov0 = max(cs, s0)
            ov1 = min(ce, s1)
            if ov1 - ov0 >= 800_000:
                clipped.append(
                    {
                        "start": ov0,
                        "end": ov1,
                        "reason": str(item.get("reason") or "pause"),
                        "label": str(item.get("label") or ""),
                    }
                )
    if not clipped:
        return []

    clipped.sort(key=lambda c: c["start"])
    merged: list[dict[str, Any]] = []
    for item in clipped:
        if merged and item["start"] <= merged[-1]["end"] + min_keep_ticks:
            prev = merged[-1]
            new_end = max(prev["end"], item["end"])
            reason = "filler" if ("filler" in (prev["reason"], item["reason"])) else ("repeat" if "repeat" in (prev["reason"], item["reason"]) else "pause")
            if prev["reason"] == "pause" and item["reason"] == "pause":
                label = f"{(new_end - prev['start']) / TICKS_PER_SECOND:.1f}s"
            elif prev["reason"] != "pause" and item["reason"] == "pause":
                label = prev["label"]
            elif prev["reason"] == "pause" and item["reason"] != "pause":
                label = item["label"]
            else:
                label = prev["label"] if prev["label"] == item["label"] else f"{prev['label']} + {item['label']}"
            merged[-1] = {"start": prev["start"], "end": new_end, "reason": reason, "label": label}
        else:
            merged.append(item)

    def _erases_all(cuts_list: list[dict[str, Any]]) -> bool:
        for seg in segments:
            s0 = _media_time_to_ticks(seg.get("sourceStart"), 0)
            s1 = s0 + _media_time_to_ticks(seg.get("sourceDuration"), 0)
            cur = s0
            for c in cuts_list:
                if c["end"] <= s0:
                    continue
                if c["start"] >= s1:
                    break
                cs = s0 if c["start"] - s0 < min_keep_ticks else max(c["start"], s0)
                ce = s1 if s1 - c["end"] < min_keep_ticks else min(c["end"], s1)
                if cs - cur >= min_keep_ticks:
                    return False
                cur = max(cur, ce)
            if s1 - cur >= min_keep_ticks:
                return False
        return True

    while merged and _erases_all(merged):
        merged.pop()

    return merged[:2000]


def analyze_speech_polish(
    project_dir: str,
    intensity: str = "standard",
    remove_pauses: bool = True,
    remove_fillers: bool = True,
    remove_repeats: bool = True,
    min_pause_sec: float | None = None,
    pause_padding_sec: float | None = None,
    threshold_db: float = -38.0,
    clip_indices: list[int] | None = None,
    start_sec: float | None = None,
    end_sec: float | None = None,
    language: str = "auto",
    force_refresh: bool = False,
    api_key: str = "",
    base_url: str = "",
    project_snapshot: dict[str, Any] | None = None,
) -> dict[str, Any]:
    """Analyze project audio + verbatim word-level transcript and build an atomic op='cutRanges' speech polish plan."""
    root = Path(project_dir)
    project_data = project_snapshot if isinstance(project_snapshot, dict) and project_snapshot else _load_project_json(root)
    norm_intensity = _normalize_intensity(intensity)
    eff_min_pause = float(min_pause_sec) if min_pause_sec is not None else (1.0 if norm_intensity == "gentle" else (0.45 if norm_intensity == "tight" else 0.70))
    eff_pad = float(pause_padding_sec) if pause_padding_sec is not None else (0.24 if norm_intensity == "gentle" else (0.10 if norm_intensity == "tight" else 0.16))
    eff_repeats = bool(remove_repeats) and norm_intensity != "gentle"

    requested_ranges = _resolve_requested_source_ranges(
        project_data, clip_indices=clip_indices, start_sec=start_sec, end_sec=end_sec
    )
    raw_cuts: list[dict[str, Any]] = []
    warnings: list[str] = []
    engine_used = "wav-rms"
    used_asr = False

    # 1. Pause detection across audio tracks
    if remove_pauses:
        audio_assets = _find_audio_assets(root, project_data)
        per_track: list[list[dict[str, Any]]] = []
        with tempfile.TemporaryDirectory(prefix="frametrail-polish-wav-") as tmp_dir:
            tmp_root = Path(tmp_dir)
            for idx, asset in enumerate(audio_assets):
                wav_path: Path = asset["path"]
                if asset.get("needsExtract", wav_path.suffix.lower() != ".wav"):
                    extracted = tmp_root / f"track-{idx:02d}.wav"
                    if not _extract_audio_to_wav16k(wav_path, extracted):
                        continue
                    wav_path = extracted
                per_track.append(
                    _detect_track_silence(
                        wav_path,
                        offset_ticks=asset["startTicks"],
                        min_silence_sec=eff_min_pause,
                        threshold_db=threshold_db,
                    )
                )
        silent_intervals = _intersect_silence_tracks(per_track, eff_min_pause)
        pad_ticks = max(0, _sec_to_ticks(eff_pad))
        for iv in silent_intervals:
            cs = int(iv["startTicks"]) + pad_ticks
            ce = int(iv["endTicks"]) - pad_ticks
            if ce - cs >= MIN_KEEP_TICKS:
                raw_cuts.append(
                    {
                        "start": cs,
                        "end": ce,
                        "reason": "pause",
                        "label": f"{(ce - cs) / TICKS_PER_SECOND:.1f}s",
                    }
                )

    # 2. Filler & stutter detection via verbatim word-level Whisper ASR
    if remove_fillers or eff_repeats:
        asr = transcribe_audio_for_subtitles(
            project_dir=str(root),
            language=language,
            force_refresh=force_refresh,
            api_key=api_key,
            base_url=base_url,
            clip_indices=clip_indices,
            start_sec=start_sec,
            end_sec=end_sec,
            vocabulary_prompt=_VERBATIM_FILLER_PROMPT,
            project_snapshot=project_data,
            allow_vad_fallback=False,
        )
        segs = asr.get("segments") or []
        if segs:
            used_asr = True
            engine_used = str(asr.get("engineUsed") or "asr")
            raw_cuts.extend(
                _detect_speech_fillers_and_repeats(
                    segs,
                    intensity=norm_intensity,
                    remove_fillers=bool(remove_fillers),
                    remove_repeats=eff_repeats,
                )
            )
        else:
            warnings.extend(asr.get("warnings") or ["No speech-to-text engine available for filler word detection."])

    if requested_ranges:
        scoped_raw: list[dict[str, Any]] = []
        for c in raw_cuts:
            for r0, r1 in requested_ranges:
                ov0 = max(int(c["start"]), r0)
                ov1 = min(int(c["end"]), r1)
                if ov1 > ov0:
                    scoped_raw.append({**c, "start": ov0, "end": ov1})
        raw_cuts = scoped_raw

    final_cuts = _merge_and_clamp_cut_ranges(project_data, raw_cuts)
    pause_count = sum(1 for c in final_cuts if c["reason"] == "pause")
    filler_count = sum(1 for c in final_cuts if c["reason"] == "filler")
    repeat_count = sum(1 for c in final_cuts if c["reason"] == "repeat")
    total_removed_sec = round(sum((c["end"] - c["start"]) / TICKS_PER_SECOND for c in final_cuts), 2)
    command = (
        {"op": "cutRanges", "ranges": final_cuts, "minKeepTicks": MIN_KEEP_TICKS}
        if final_cuts
        else None
    )

    return {
        "intensity": norm_intensity,
        "cutCount": len(final_cuts),
        "pauseCount": pause_count,
        "fillerCount": filler_count,
        "repeatCount": repeat_count,
        "totalRemovedSeconds": total_removed_sec,
        "engineUsed": engine_used,
        "usedAsr": used_asr,
        "warnings": warnings,
        "cuts": [
            {
                "startSec": round(c["start"] / TICKS_PER_SECOND, 4),
                "endSec": round(c["end"] / TICKS_PER_SECOND, 4),
                "reason": c["reason"],
                "label": c["label"],
            }
            for c in final_cuts
        ],
        "command": command,
    }


@tool
def optimize_speech_flow(
    intensity: str = "standard",
    remove_pauses: bool = True,
    remove_fillers: bool = True,
    remove_repeats: bool = True,
    min_pause_sec: float | None = None,
    pause_padding_sec: float | None = None,
    threshold_db: float = -38.0,
    clip_indices: list[int] | None = None,
    start_sec: float | None = None,
    end_sec: float | None = None,
    language: str = "auto",
    force_refresh: bool = False,
) -> str:
    """One-click voiceover fluency optimizer: detects and atomically cuts long pauses, filler words (嗯/啊/哦/呃/那个/um/uh), and stutters/repeats, directly staging an op='cutRanges' edit command. Do NOT call cut_time_ranges or cut_time_range afterward."""
    ctx = _get_active_project_context()
    project_dir = str(ctx.get("project_dir", "") or "").strip()
    snapshot = ctx.get("snapshot", {}) or {}
    callback_url = str(ctx.get("callback_url", "") or "").strip()

    if callback_url:
        action_params: dict[str, Any] = {
            "intensity": _normalize_intensity(intensity),
            "remove_pauses": bool(remove_pauses),
            "remove_fillers": bool(remove_fillers),
            "remove_repeats": bool(remove_repeats),
            "threshold_db": float(threshold_db),
            "language": language,
            "force_refresh": bool(force_refresh),
            "apply": False,
        }
        if min_pause_sec is not None:
            action_params["min_pause_sec"] = float(min_pause_sec)
        if pause_padding_sec is not None:
            action_params["pause_padding_sec"] = float(pause_padding_sec)
        if clip_indices:
            action_params["clip_indices"] = [int(i) for i in clip_indices]
        if start_sec is not None:
            action_params["start_sec"] = float(start_sec)
        if end_sec is not None:
            action_params["end_sec"] = float(end_sec)

        body = json.dumps(
            {"projectDir": project_dir, "action": "optimize_speech_flow", "params": action_params},
            ensure_ascii=False,
        ).encode("utf-8")
        req = urllib.request.Request(
            callback_url,
            data=body,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            with urllib.request.urlopen(req, timeout=600.0) as resp:
                parsed = json.loads(resp.read().decode("utf-8", errors="replace"))
            if parsed.get("ok"):
                data = parsed.get("data") or {}
                cmd = data.get("command")
                if isinstance(cmd, dict) and cmd.get("op") == "cutRanges":
                    return json.dumps(cmd, ensure_ascii=False)
                return json.dumps(
                    {
                        "cutCount": int(data.get("cutCount") or 0),
                        "message": parsed.get("message") or "No removable pauses or filler words detected.",
                        "usedAsr": bool(data.get("usedAsr", False)),
                        "warning": data.get("warning"),
                    },
                    ensure_ascii=False,
                )
        except Exception:
            pass

    if not project_dir:
        raise ValueError("No project directory is bound for speech flow optimization.")

    plan = analyze_speech_polish(
        project_dir=project_dir,
        intensity=intensity,
        remove_pauses=remove_pauses,
        remove_fillers=remove_fillers,
        remove_repeats=remove_repeats,
        min_pause_sec=min_pause_sec,
        pause_padding_sec=pause_padding_sec,
        threshold_db=threshold_db,
        clip_indices=clip_indices,
        start_sec=start_sec,
        end_sec=end_sec,
        language=language,
        force_refresh=force_refresh,
        api_key=str(ctx.get("api_key", "") or ""),
        base_url=str(ctx.get("base_url", "") or ""),
        project_snapshot=snapshot,
    )
    if plan.get("command") is not None:
        return json.dumps(plan["command"], ensure_ascii=False)
    return json.dumps(plan, ensure_ascii=False)


def get_phase2_tools() -> list:
    """Return the Phase 2 multimodal perception tools for LangGraph."""
    return [
        transcribe_project_audio,
        detect_silence_and_pauses,
        optimize_speech_flow,
        search_transcript_and_clicks,
        inspect_video_frames,
    ]
