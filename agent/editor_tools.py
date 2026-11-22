"""LangChain @tool definitions mapping natural-language edits to FrameTrail native JSON commands."""

from __future__ import annotations

import contextvars
import json
import uuid
from fractions import Fraction
from pathlib import Path
from typing import Any, Optional

try:
    from langchain_core.tools import tool
except ImportError:
    def tool(fn=None, **_kwargs):  # type: ignore[misc]
        def _wrap(f):
            if not hasattr(f, "name"):
                f.name = f.__name__
            if not hasattr(f, "description"):
                f.description = (f.__doc__ or "").strip()
            if not hasattr(f, "invoke"):
                f.invoke = lambda args=None, **kw: f(**(args or {}), **kw) if isinstance(args, dict) else f(*( () if args is None else (args,) ), **kw)
            return f
        return _wrap(fn) if callable(fn) else _wrap

TICKS_PER_SECOND = 10_000_000

_VALID_FRAME_STYLES = {
    "none",
    "window",
    "browser",
    "device",
    "windowLight",
    "windowDark",
    "browserLight",
    "browserDark",
    "devicePhone",
}

_current_project_context: contextvars.ContextVar[dict[str, Any]] = contextvars.ContextVar(
    "frametrail_project_context",
    default={},
)


def set_project_context(
    snapshot: dict[str, Any],
    project_dir: str = "",
    playhead_sec: float = 0.0,
    api_key: str = "",
    base_url: str = "",
    provider: str = "",
    model_name: str = "",
) -> contextvars.Token:
    """Bind the active project snapshot, workspace path, and BYOK credentials for tool calls in this turn."""
    return _current_project_context.set(
        {
            "snapshot": snapshot or {},
            "project_dir": project_dir or "",
            "playhead_sec": float(playhead_sec or 0.0),
            "api_key": (api_key or "").strip(),
            "base_url": (base_url or "").strip(),
            "provider": (provider or "").strip().lower(),
            "model_name": (model_name or "").strip(),
        }
    )


def get_project_context() -> dict[str, Any]:
    """Return the active turn's project and provider context dictionary."""
    return dict(_current_project_context.get() or {})


def reset_project_context(token: contextvars.Token) -> None:
    _current_project_context.reset(token)


def _ticks(time_obj: Any) -> int:
    if isinstance(time_obj, (int, float)):
        return int(time_obj)
    if not isinstance(time_obj, dict):
        return 0
    raw = int(time_obj.get("ticks", 0))
    scale = max(1, int(time_obj.get("timescale", TICKS_PER_SECOND)))
    return round(raw * TICKS_PER_SECOND / scale)


def _media_time(ticks: int) -> dict[str, int]:
    return {"ticks": int(ticks), "timescale": TICKS_PER_SECOND}


def _resolve_source_window(snapshot: dict[str, Any], start_sec: float, end_sec: float) -> tuple[int, int]:
    total_ticks = _ticks(snapshot.get("sourceDuration", {}))
    if total_ticks <= 0:
        raise ValueError("Project sourceDuration must be positive.")
    if end_sec <= start_sec:
        raise ValueError("end_sec must be greater than start_sec.")
    start_ticks = max(0, round(start_sec * TICKS_PER_SECOND))
    if start_ticks >= total_ticks:
        raise ValueError(f"start_sec ({start_sec}s) exceeds project duration ({total_ticks / TICKS_PER_SECOND:.2f}s).")
    end_ticks = min(total_ticks, round(end_sec * TICKS_PER_SECOND))
    if end_ticks <= start_ticks:
        end_ticks = min(total_ticks, start_ticks + 1)
    return start_ticks, end_ticks - start_ticks


def _resolve_clips(snapshot: dict[str, Any]) -> list[dict[str, Any]]:
    segments = snapshot.get("segments", [])
    clips: list[dict[str, Any]] = []
    out_cursor = 0
    for seg in segments:
        src_start = _ticks(seg.get("sourceStart", {}))
        src_dur = _ticks(seg.get("sourceDuration", {}))
        rate = seg.get("rate", {}) or {}
        num = max(1, int(rate.get("numerator", 1)))
        den = max(1, int(rate.get("denominator", 1)))
        out_dur = max(1, round(src_dur * den / num))
        clips.append(
            {
                "id": seg.get("id", ""),
                "sourceStart": src_start,
                "sourceDuration": src_dur,
                "outputStart": out_cursor,
                "outputDuration": out_dur,
                "numerator": num,
                "denominator": den,
                "rate": num / den,
            }
        )
        out_cursor += out_dur
    return clips


@tool
def get_timeline_overview() -> str:
    """Get an overview of the current project timeline: total duration in seconds, clips, effects, and canvas settings."""
    ctx = _current_project_context.get()
    snapshot = ctx.get("snapshot", {})
    clips = _resolve_clips(snapshot)
    total_out = sum(c["outputDuration"] for c in clips)
    source_dur = _ticks(snapshot.get("sourceDuration", {}))

    effects = [
        {
            "id": eff.get("id", ""),
            "kind": eff.get("kind", ""),
            "startSec": round(_ticks(eff.get("sourceStart", {})) / TICKS_PER_SECOND, 4),
            "durationSec": round(_ticks(eff.get("sourceDuration", {})) / TICKS_PER_SECOND, 4),
        }
        for eff in snapshot.get("effects", [])
    ]

    canvas = snapshot.get("canvas", {}) or {}
    editor = snapshot.get("editor", {}) or {}
    overview = {
        "totalDurationSec": round(total_out / TICKS_PER_SECOND, 4),
        "sourceDurationSec": round(source_dur / TICKS_PER_SECOND, 4),
        "playheadSec": round(float(ctx.get("playhead_sec", 0.0)), 4),
        "clips": [
            {
                "id": c["id"],
                "startSec": round(c["outputStart"] / TICKS_PER_SECOND, 4),
                "durationSec": round(c["outputDuration"] / TICKS_PER_SECOND, 4),
                "rate": round(c["rate"], 4),
            }
            for c in clips
        ],
        "effects": effects,
        "canvas": {
            "width": int(canvas.get("width", 1920)),
            "height": int(canvas.get("height", 1080)),
            "padding": float(canvas.get("padding", 0.05)),
            "cornerRadius": float(editor.get("cornerRadius", 16.0)),
            "frameStyle": str(editor.get("frameStyle", "none")),
        },
    }
    return json.dumps(overview, ensure_ascii=False)


@tool
def search_pointer_clicks(start_sec: float = 0.0, end_sec: Optional[float] = None, limit: int = 20) -> str:
    """Search recorded mouse click events (timestamps in seconds and normalized x/y coordinates) within a time range."""
    ctx = _current_project_context.get()
    snapshot = ctx.get("snapshot", {})
    project_dir = ctx.get("project_dir", "")
    if not project_dir:
        return json.dumps({"clicks": [], "note": "No project directory bound."})

    root = Path(project_dir)
    pointer_media = [m for m in snapshot.get("media", []) if m.get("kind") == "pointerEvents"]
    clicks: list[dict[str, Any]] = []
    start_ticks = max(0, round(start_sec * TICKS_PER_SECOND))
    end_ticks = round(end_sec * TICKS_PER_SECOND) if end_sec is not None else None

    for media in pointer_media:
        rel = media.get("path", "")
        file_path = root / rel
        if not rel or not file_path.is_file():
            continue
        offset = _ticks(media.get("startOffset", {}))
        with file_path.open("r", encoding="utf-8", errors="ignore") as handle:
            for line in handle:
                line = line.strip()
                if not line:
                    continue
                try:
                    ev = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if ev.get("type") != "pointer":
                    continue
                kind = int(ev.get("kind", 0))
                if kind not in (1, 3):
                    continue
                timescale = max(1, int(ev.get("timescale", TICKS_PER_SECOND)))
                abs_ticks = offset + round(int(ev.get("ticks", 0)) * TICKS_PER_SECOND / timescale)
                if abs_ticks < start_ticks:
                    continue
                if end_ticks is not None and abs_ticks > end_ticks:
                    continue
                clicks.append(
                    {
                        "timeSec": round(abs_ticks / TICKS_PER_SECOND, 3),
                        "x": round(float(ev.get("x", 0.5)), 4),
                        "y": round(float(ev.get("y", 0.5)), 4),
                        "button": "right" if kind == 3 else "left",
                    }
                )
                if len(clicks) >= max(1, min(limit, 100)):
                    return json.dumps({"clicks": clicks}, ensure_ascii=False)

    return json.dumps({"clicks": clicks}, ensure_ascii=False)


@tool
def cut_time_range(start_sec: float, end_sec: float) -> str:
    """Cut out (remove) the specified output time range [start_sec, end_sec] from the timeline using atomic split/trim/delete."""
    if end_sec <= start_sec:
        raise ValueError("end_sec must be greater than start_sec.")
    ctx = _current_project_context.get()
    snapshot = ctx.get("snapshot", {})
    clips = _resolve_clips(snapshot)
    if not clips:
        raise ValueError("No clips available on timeline.")

    total_out = sum(c["outputDuration"] for c in clips)
    cut_start = max(0, round(start_sec * TICKS_PER_SECOND))
    cut_end = min(total_out, round(end_sec * TICKS_PER_SECOND))
    if cut_end <= cut_start:
        raise ValueError("Cut range does not overlap the timeline.")

    commands: list[dict[str, Any]] = []
    deleted = 0
    for clip in clips:
        c_out_start = clip["outputStart"]
        c_out_end = c_out_start + clip["outputDuration"]
        ov_start = max(cut_start, c_out_start)
        ov_end = min(cut_end, c_out_end)
        if ov_end <= ov_start:
            continue

        def to_source(out_t: int) -> int:
            delta_out = out_t - c_out_start
            delta_src = round(delta_out * clip["numerator"] / clip["denominator"])
            return max(clip["sourceStart"], min(clip["sourceStart"] + clip["sourceDuration"], clip["sourceStart"] + delta_src))

        s_start = clip["sourceStart"]
        s_end = clip["sourceStart"] + clip["sourceDuration"]
        cut_s = to_source(ov_start)
        cut_e = to_source(ov_end)
        if cut_e <= cut_s:
            continue

        if cut_s <= s_start and cut_e >= s_end:
            commands.append({"op": "delete", "segmentId": clip["id"]})
            deleted += 1
        elif cut_s <= s_start:
            commands.append({"op": "trim", "segmentId": clip["id"], "sourceStart": cut_e, "sourceEnd": s_end})
        elif cut_e >= s_end:
            commands.append({"op": "trim", "segmentId": clip["id"], "sourceStart": s_start, "sourceEnd": cut_s})
        else:
            right_id = f"clip-{uuid.uuid4().hex[:12]}"
            commands.append({"op": "split", "segmentId": clip["id"], "sourceTicks": cut_s, "newId": right_id})
            commands.append({"op": "trim", "segmentId": right_id, "sourceStart": cut_e, "sourceEnd": s_end})

    if not commands:
        raise ValueError("Cut range does not overlap any clip.")
    if deleted >= len(clips):
        raise ValueError("Cannot cut the entire timeline; at least one clip must remain.")

    return json.dumps({"op": "batch", "commands": commands}, ensure_ascii=False)


@tool
def change_segment_speed(segment_id: str, rate: float) -> str:
    """Change playback speed of a segment (rate in 0.25..4.0, e.g. 0.5, 1.5, 2.0)."""
    if not segment_id:
        raise ValueError("segment_id is required.")
    clamped = max(0.25, min(4.0, float(rate)))
    frac = Fraction(clamped).limit_denominator(1000)
    cmd = {
        "op": "rate",
        "segmentId": segment_id,
        "numerator": frac.numerator,
        "denominator": frac.denominator,
    }
    return json.dumps(cmd, ensure_ascii=False)


@tool
def add_zoom_effect(
    start_sec: float,
    end_sec: float,
    scale: float = 1.6,
    focus_x: float = 0.5,
    focus_y: float = 0.5,
    effect_id: Optional[str] = None,
) -> str:
    """Add or update a zoom camera effect over [start_sec, end_sec] with scale (1.05..4.0) and normalized focus (0..1)."""
    ctx = _current_project_context.get()
    snapshot = ctx.get("snapshot", {})
    start_ticks, dur_ticks = _resolve_source_window(snapshot, start_sec, end_sec)
    eid = (effect_id or "").strip() or f"ai-zoom-{uuid.uuid4().hex[:12]}"
    cmd = {
        "op": "effectUpsert",
        "effectId": eid,
        "effect": {
            "id": eid,
            "kind": "zoom",
            "sourceStart": _media_time(start_ticks),
            "sourceDuration": _media_time(dur_ticks),
            "parameters": {
                "origin": "manual",
                "scale": max(1.05, min(4.0, float(scale))),
                "focusX": max(0.0, min(1.0, float(focus_x))),
                "focusY": max(0.0, min(1.0, float(focus_y))),
                "easing": "cubic",
                "enter": 0.7,
                "exit": 0.7,
            },
        },
    }
    return json.dumps(cmd, ensure_ascii=False)


@tool
def add_spotlight_or_blur(
    start_sec: float,
    end_sec: float,
    mode: str = "focus",
    x: float = 0.25,
    y: float = 0.25,
    width: float = 0.5,
    height: float = 0.5,
    strength: float = 16.0,
    mask_mode: str = "blur",
    effect_id: Optional[str] = None,
) -> str:
    """Add a background spotlight ('focus') or redaction mask ('mask') effect over [start_sec, end_sec]."""
    kind = mode.strip().lower()
    if kind not in ("focus", "mask"):
        raise ValueError("mode must be 'focus' or 'mask'.")
    ctx = _current_project_context.get()
    snapshot = ctx.get("snapshot", {})
    start_ticks, dur_ticks = _resolve_source_window(snapshot, start_sec, end_sec)
    eid = (effect_id or "").strip() or f"ai-{kind}-{uuid.uuid4().hex[:12]}"

    cx = max(0.0, min(0.99, float(x)))
    cy = max(0.0, min(0.99, float(y)))
    cw = max(0.01, min(1.0 - cx, float(width)))
    ch = max(0.01, min(1.0 - cy, float(height)))
    c_strength = max(1.0, min(64.0, float(strength)))

    params: dict[str, Any] = {
        "origin": "manual",
        "x": cx,
        "y": cy,
        "width": cw,
        "height": ch,
        "strength": c_strength,
        "opacity": 1.0,
        "attachment": "screen",
    }
    if kind == "mask":
        mm = mask_mode.strip().lower()
        params["mode"] = mm if mm in ("solid", "pixelate", "blur") else "blur"

    cmd = {
        "op": "effectUpsert",
        "effectId": eid,
        "effect": {
            "id": eid,
            "kind": kind,
            "sourceStart": _media_time(start_ticks),
            "sourceDuration": _media_time(dur_ticks),
            "parameters": params,
        },
    }
    return json.dumps(cmd, ensure_ascii=False)


@tool
def add_3d_tilt_effect(
    start_sec: float,
    end_sec: float,
    angle_x: float = 8.0,
    angle_y: float = -10.0,
    effect_id: Optional[str] = None,
) -> str:
    """Add a 3D perspective tilt effect over [start_sec, end_sec] with angles in [-35, 35] degrees."""
    ctx = _current_project_context.get()
    snapshot = ctx.get("snapshot", {})
    start_ticks, dur_ticks = _resolve_source_window(snapshot, start_sec, end_sec)
    eid = (effect_id or "").strip() or f"ai-tilt-{uuid.uuid4().hex[:12]}"
    cmd = {
        "op": "effectUpsert",
        "effectId": eid,
        "effect": {
            "id": eid,
            "kind": "tilt",
            "sourceStart": _media_time(start_ticks),
            "sourceDuration": _media_time(dur_ticks),
            "parameters": {
                "origin": "manual",
                "angleX": max(-35.0, min(35.0, float(angle_x))),
                "angleY": max(-35.0, min(35.0, float(angle_y))),
                "easing": "cubic",
                "enter": 0.7,
                "exit": 0.7,
                "target": "screen",
            },
        },
    }
    return json.dumps(cmd, ensure_ascii=False)


@tool
def add_transition_effect(
    from_clip_index: int = 1,
    style: str = "crossfade",
    duration_sec: float = 0.6,
    easing: str = "cubic",
) -> str:
    """Add or update a clip-to-clip transition between adjacent clips (1-based from_clip_index -> from_clip_index + 1). Styles: crossfade, fadeBlack, fadeWhite, slideLeft, slideRight, wipeLeft, wipeRight, zoomIn."""
    ctx = _current_project_context.get()
    snapshot = ctx.get("snapshot", {})
    segments = snapshot.get("segments", []) or []
    if len(segments) < 2:
        raise ValueError("At least two clips are required to add a transition.")
    idx = int(from_clip_index)
    if idx < 1 or idx >= len(segments):
        raise ValueError(f"from_clip_index must be between 1 and {len(segments) - 1}.")

    valid_styles = ("crossfade", "fadeBlack", "fadeWhite", "slideLeft", "slideRight", "wipeLeft", "wipeRight", "zoomIn")
    cleaned_style = style.strip() if style and style.strip() in valid_styles else "crossfade"
    cleaned_easing = easing.strip() if easing and easing.strip() in ("linear", "cubic", "spring") else "cubic"
    dur = round(max(0.1, min(5.0, float(duration_sec))), 2)

    left_seg = segments[idx - 1]
    right_seg = segments[idx]
    left_id = str(left_seg["id"])
    right_id = str(right_seg["id"])
    eid = ""
    for existing in snapshot.get("effects", []) or []:
        if existing.get("kind") == "transition":
            ep = existing.get("parameters") or {}
            if ep.get("fromSegmentId") == left_id and ep.get("toSegmentId") == right_id:
                eid = str(existing["id"])
                break
    if not eid:
        eid = f"transition-{uuid.uuid4().hex[:12]}"

    total_ticks = _ticks(snapshot.get("sourceDuration", {}))
    anchor_start = max(0, min(_ticks(right_seg.get("sourceStart", {})), max(0, total_ticks - 1)))
    anchor_dur = max(1, min(round(dur * TICKS_PER_SECOND), max(1, total_ticks - anchor_start)))

    cmd = {
        "op": "effectUpsert",
        "effectId": eid,
        "effect": {
            "id": eid,
            "kind": "transition",
            "sourceStart": {"ticks": anchor_start, "timescale": TICKS_PER_SECOND},
            "sourceDuration": {"ticks": anchor_dur, "timescale": TICKS_PER_SECOND},
            "parameters": {
                "origin": "manual",
                "style": cleaned_style,
                "duration": dur,
                "easing": cleaned_easing,
                "fromSegmentId": left_id,
                "toSegmentId": right_id,
            },
        },
    }
    return json.dumps(cmd, ensure_ascii=False)


@tool
def update_canvas_style(
    aspect_ratio: Optional[str] = None,
    width: Optional[int] = None,
    height: Optional[int] = None,
    padding: Optional[float] = None,
    corner_radius: Optional[float] = None,
    frame_style: Optional[str] = None,
) -> str:
    """Update canvas aspect ratio/dimensions, padding (0..0.45), corner radius (0..200), or mockup frame style."""
    ctx = _current_project_context.get()
    snapshot = ctx.get("snapshot", {})
    canvas = dict(snapshot.get("canvas", {}) or {})
    editor = dict(snapshot.get("editor", {}) or {})

    ratio_map = {
        "9:16": (1080, 1920),
        "16:9": (1920, 1080),
        "1:1": (1080, 1080),
        "4:3": (1440, 1080),
    }
    if aspect_ratio and aspect_ratio.strip() in ratio_map:
        w, h = ratio_map[aspect_ratio.strip()]
        canvas["width"] = w
        canvas["height"] = h
    if width is not None:
        canvas["width"] = max(2, (min(7680, int(width)) // 2) * 2)
    if height is not None:
        canvas["height"] = max(2, (min(7680, int(height)) // 2) * 2)
    if padding is not None:
        canvas["padding"] = max(0.0, min(0.45, float(padding)))
    if corner_radius is not None:
        editor["cornerRadius"] = max(0.0, min(200.0, float(corner_radius)))
    if frame_style is not None:
        fs = frame_style.strip()
        if fs not in _VALID_FRAME_STYLES:
            raise ValueError(f"Unsupported frame_style '{fs}'.")
        editor["frameStyle"] = fs

    cmd = {"op": "settings", "canvas": canvas, "editor": editor}
    return json.dumps(cmd, ensure_ascii=False)


@tool
def apply_edit_batch(commands: list[dict[str, Any]]) -> str:
    """Combine multiple FrameTrail edit commands into a single atomic batch command."""
    flattened: list[dict[str, Any]] = []
    for item in commands:
        if not isinstance(item, dict) or "op" not in item:
            continue
        if item["op"] == "batch" and isinstance(item.get("commands"), list):
            flattened.extend(c for c in item["commands"] if isinstance(c, dict))
        else:
            flattened.append(item)
    if not flattened:
        raise ValueError("apply_edit_batch requires at least one valid command.")
    if len(flattened) > 100:
        raise ValueError("Batch cannot contain more than 100 commands.")
    return json.dumps({"op": "batch", "commands": flattened}, ensure_ascii=False)


@tool
def generate_subtitles(
    clip_indices: Optional[list[int]] = None,
    start_sec: Optional[float] = None,
    end_sec: Optional[float] = None,
    language: str = "zh",
    style_preset: Optional[str] = None,
    position_y: Optional[float] = None,
    vocabulary_prompt: str = "",
    max_chars_per_line: int = 18,
    force_refresh: bool = False,
) -> str:
    """Transcribe speech from project audio/video tracks (whole timeline, specific 1-based clip_indices such as [2, 3], or [start_sec, end_sec]) and emit an atomic op='subtitlesReplace' command."""
    from agent.multimodal_tools import (
        build_subtitle_style,
        build_subtitles_replace_command,
        transcribe_audio_for_subtitles,
    )

    ctx = _current_project_context.get()
    snapshot = ctx.get("snapshot", {}) or {}
    project_dir = str(ctx.get("project_dir", "") or "").strip()
    if not project_dir:
        raise ValueError("No project directory is bound for speech-to-subtitle transcription.")

    asr = transcribe_audio_for_subtitles(
        project_dir=project_dir,
        language=language,
        force_refresh=force_refresh,
        api_key=str(ctx.get("api_key", "") or ""),
        base_url=str(ctx.get("base_url", "") or ""),
        clip_indices=clip_indices,
        start_sec=start_sec,
        end_sec=end_sec,
        max_chars_per_line=max_chars_per_line,
        vocabulary_prompt=vocabulary_prompt,
        project_snapshot=snapshot,
        allow_vad_fallback=False,
    )

    cues = asr.get("subtitles") or []
    if not cues:
        warn_text = "; ".join(asr.get("warnings") or [])
        if asr.get("engineUsed") == "none":
            raise ValueError(
                "No speech-to-text engine produced captions. Configure an OpenAI/Groq API key, "
                "install faster-whisper, or run Auto Captions in FrameTrail's Subtitles panel first."
                + (f" ({warn_text})" if warn_text else "")
            )
        raise ValueError(
            "No spoken speech was detected in the requested range."
            + (f" ({warn_text})" if warn_text else "")
        )

    subtitle_style = None
    if style_preset is not None or position_y is not None:
        existing_style = (snapshot.get("editor") or {}).get("subtitleStyle")
        subtitle_style = build_subtitle_style(
            existing=existing_style,
            preset=style_preset,
            position_y=position_y,
            max_chars_per_line=max_chars_per_line,
            canvas=snapshot.get("canvas"),
        )

    cmd = build_subtitles_replace_command(
        snapshot=snapshot,
        generated_subtitles=cues,
        requested_ranges=asr.get("requestedRanges"),
        subtitle_style=subtitle_style,
    )
    return json.dumps(cmd, ensure_ascii=False)


@tool
def upsert_subtitle(
    start_sec: float,
    end_sec: float,
    text: str,
    secondary_text: str = "",
    subtitle_id: str = "",
) -> str:
    """Add or update a single subtitle cue over [start_sec, end_sec] with primary text and optional secondary bilingual text."""
    cleaned_text = (text or "").strip()
    if not cleaned_text:
        raise ValueError("Subtitle text must not be empty.")
    if len(cleaned_text.encode("utf-8")) > 1024:
        raise ValueError("Subtitle text exceeds 1024 UTF-8 bytes.")

    cleaned_secondary = (secondary_text or "").strip()
    if len(cleaned_secondary.encode("utf-8")) > 1024:
        raise ValueError("Subtitle secondary_text exceeds 1024 UTF-8 bytes.")

    ctx = _current_project_context.get()
    snapshot = ctx.get("snapshot", {}) or {}
    start_ticks, dur_ticks = _resolve_source_window(snapshot, start_sec, end_sec)
    eid = (subtitle_id or "").strip() or f"sub-{uuid.uuid4().hex[:12]}"

    params: dict[str, Any] = {"text": cleaned_text}
    if cleaned_secondary:
        params["secondaryText"] = cleaned_secondary

    cmd = {
        "op": "effectUpsert",
        "effectId": eid,
        "effect": {
            "id": eid,
            "kind": "subtitle",
            "sourceStart": _media_time(start_ticks),
            "sourceDuration": _media_time(dur_ticks),
            "parameters": params,
        },
    }
    return json.dumps(cmd, ensure_ascii=False)


@tool
def update_subtitle_style(
    preset: Optional[str] = None,
    font_size: Optional[float] = None,
    position_y: Optional[float] = None,
    text_color: Optional[str] = None,
    outline_color: Optional[str] = None,
    background_opacity: Optional[float] = None,
    highlight_mode: Optional[str] = None,
    bilingual_enabled: Optional[bool] = None,
    font_family: Optional[str] = None,
    position_x: Optional[float] = None,
    alignment: Optional[str] = None,
    max_chars_per_line: Optional[int] = None,
) -> str:
    """Update global subtitle styling (preset: classicOutline/darkPill/karaokePop/bilingualClean/custom, font size, position_y, colors, background pill, karaoke highlight, or bilingual mode)."""
    from agent.multimodal_tools import build_subtitle_style

    ctx = _current_project_context.get()
    snapshot = ctx.get("snapshot", {}) or {}
    editor = dict(snapshot.get("editor", {}) or {})
    existing_style = editor.get("subtitleStyle")
    editor["subtitleStyle"] = build_subtitle_style(
        existing=existing_style if isinstance(existing_style, dict) else None,
        preset=preset,
        font_family=font_family,
        font_size=font_size,
        text_color=text_color,
        outline_color=outline_color,
        background_opacity=background_opacity,
        highlight_mode=highlight_mode,
        position_x=position_x,
        position_y=position_y,
        alignment=alignment,
        max_chars_per_line=max_chars_per_line,
        bilingual_enabled=bilingual_enabled,
        canvas=snapshot.get("canvas"),
    )
    cmd = {"op": "settings", "editor": editor}
    return json.dumps(cmd, ensure_ascii=False)


@tool
def delete_subtitles(
    subtitle_ids: Optional[list[str]] = None,
    start_sec: Optional[float] = None,
    end_sec: Optional[float] = None,
) -> str:
    """Delete subtitles by explicit subtitle_ids, by source time range [start_sec, end_sec], or clear all subtitles when omitted."""
    ctx = _current_project_context.get()
    snapshot = ctx.get("snapshot", {}) or {}

    if subtitle_ids is not None and len(subtitle_ids) > 0:
        cleaned_ids = [str(sid).strip() for sid in subtitle_ids if str(sid).strip()]
        if not cleaned_ids:
            raise ValueError("subtitle_ids must contain at least one non-empty ID.")
        if len(cleaned_ids) == 1:
            return json.dumps({"op": "effectDelete", "effectId": cleaned_ids[0]}, ensure_ascii=False)
        if len(cleaned_ids) <= 100:
            return json.dumps(
                {"op": "batch", "commands": [{"op": "effectDelete", "effectId": sid} for sid in cleaned_ids]},
                ensure_ascii=False,
            )
        remove_set = set(cleaned_ids)
        remaining = [
            eff
            for eff in snapshot.get("effects", []) or []
            if eff.get("kind") == "subtitle" and eff.get("id") not in remove_set
        ]
        return json.dumps({"op": "subtitlesReplace", "subtitles": remaining}, ensure_ascii=False)

    if start_sec is not None or end_sec is not None:
        total_ticks = _ticks(snapshot.get("sourceDuration", {}))
        r_start = max(0, round(float(start_sec) * TICKS_PER_SECOND)) if start_sec is not None else 0
        if end_sec is not None:
            r_end = round(float(end_sec) * TICKS_PER_SECOND)
        elif total_ticks > 0:
            r_end = total_ticks
        else:
            raise ValueError("end_sec is required when project sourceDuration is unavailable.")
        if total_ticks > 0:
            r_end = min(total_ticks, r_end)
        if r_end <= r_start:
            raise ValueError("end_sec must be greater than start_sec.")
        return json.dumps(
            {"op": "subtitlesReplace", "rangeStart": r_start, "rangeEnd": r_end, "subtitles": []},
            ensure_ascii=False,
        )

    return json.dumps({"op": "subtitlesReplace", "subtitles": []}, ensure_ascii=False)


def get_phase1_tools() -> list[Any]:
    """Return all Phase 1 MVP + Subtitle LangChain editing and inspection tools."""
    return [
        get_timeline_overview,
        search_pointer_clicks,
        cut_time_range,
        change_segment_speed,
        add_zoom_effect,
        add_spotlight_or_blur,
        add_3d_tilt_effect,
        add_transition_effect,
        update_canvas_style,
        apply_edit_batch,
        generate_subtitles,
        upsert_subtitle,
        update_subtitle_style,
        delete_subtitles,
    ]
