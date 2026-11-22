"""FrameTrail Model Context Protocol (MCP) server.

Exposes FrameTrail video editing operations over MCP stdio so external
subscription CLIs (Claude Code CLI, OpenAI Codex CLI, Cursor, etc.) and local
agents can inspect timelines and apply non-destructive edits.

Supports two execution backends seamlessly:
1. Official ``mcp.server.fastmcp.FastMCP`` when the ``mcp`` package is installed.
2. A built-in zero-dependency JSON-RPC 2.0 over stdio implementation supporting
   ``initialize``, ``notifications/initialized``, ``ping``, ``tools/list``, and
   ``tools/call`` so external CLIs work even in minimal Python environments.

Usage:
    python agent/mcp_server.py --project-dir path/to/recording.frametrailproject
    python agent/mcp_server.py --project-dir <dir> --callback-url http://127.0.0.1:18765/api/commands
"""

from __future__ import annotations

import argparse
from fractions import Fraction
import json
import os
from pathlib import Path
import sys
import tempfile
import urllib.error
import urllib.request
import uuid
from typing import Any

# Ensure repo root is importable when executed directly as `python agent/mcp_server.py`
_REPO_ROOT = str(Path(__file__).resolve().parent.parent)
if _REPO_ROOT not in sys.path:
    sys.path.insert(0, _REPO_ROOT)

TICKS_PER_SECOND = 10_000_000
VALID_FRAME_STYLES = (
    "none",
    "windowLight",
    "windowDark",
    "browserLight",
    "browserDark",
    "devicePhone",
)
VALID_EASINGS = ("linear", "cubic", "spring")
VALID_TRANSITION_STYLES = (
    "crossfade",
    "fadeBlack",
    "fadeWhite",
    "slideLeft",
    "slideRight",
    "wipeLeft",
    "wipeRight",
    "zoomIn",
)
ASPECT_RATIO_PRESETS: dict[str, tuple[int, int]] = {
    "16:9": (1920, 1080),
    "9:16": (1080, 1920),
    "1:1": (1080, 1080),
    "4:5": (1080, 1350),
    "4:3": (1440, 1080),
    "21:9": (2560, 1080),
}


def _sec_to_ticks(seconds: float) -> int:
    return int(round(float(seconds) * TICKS_PER_SECOND))


def _ticks_to_sec(ticks: int, timescale: int = TICKS_PER_SECOND) -> float:
    return round(float(ticks) / float(timescale or TICKS_PER_SECOND), 6)


def _normalized_ticks(time_obj: dict[str, Any] | None) -> int:
    if not time_obj:
        return 0
    ticks = int(time_obj.get("ticks", 0))
    timescale = int(time_obj.get("timescale", TICKS_PER_SECOND))
    if timescale == TICKS_PER_SECOND:
        return ticks
    return int(round(ticks * (TICKS_PER_SECOND / timescale)))


def _canonical_time(ticks: int) -> dict[str, int]:
    return {"ticks": int(ticks), "timescale": TICKS_PER_SECOND}


def _new_id(prefix: str) -> str:
    return f"{prefix}-{uuid.uuid4().hex[:8]}"


def _clamp(value: float, low: float, high: float) -> float:
    return max(low, min(high, float(value)))


def _rate_to_rational(rate: float) -> tuple[int, int]:
    clamped = _clamp(rate, 0.25, 4.0)
    frac = Fraction(clamped).limit_denominator(1000)
    return int(frac.numerator), int(frac.denominator)


def _parse_aspect_ratio(aspect_ratio: str) -> tuple[int, int]:
    normalized = aspect_ratio.strip().lower()
    if normalized in ASPECT_RATIO_PRESETS:
        return ASPECT_RATIO_PRESETS[normalized]
    for sep in (":", "x", "/"):
        if sep in normalized:
            parts = normalized.split(sep, 1)
            w = int(parts[0].strip())
            h = int(parts[1].strip())
            if w <= 0 or h <= 0:
                raise ValueError("Aspect ratio dimensions must be positive.")
            if w <= 64 and h <= 64:
                # Ratio like 16:10 -> scale to ~1080p short/long side, ensuring even pixels
                long_edge = 1920
                if w >= h:
                    width = long_edge
                    height = max(2, int(round(long_edge * h / w / 2.0)) * 2)
                else:
                    height = long_edge
                    width = max(2, int(round(long_edge * w / h / 2.0)) * 2)
                return width, height
            width = max(2, min(16384, (w // 2) * 2))
            height = max(2, min(16384, (h // 2) * 2))
            return width, height
    raise ValueError(
        f"Unsupported aspect_ratio '{aspect_ratio}'. Use presets like 16:9, 9:16, 1:1, 4:5, 4:3, or WxH."
    )


class FrameTrailProjectBridge:
    """Reads project.json and dispatches or applies FrameTrail editing commands."""

    def __init__(self, project_dir: str, callback_url: str = "") -> None:
        self.project_dir = Path(project_dir).resolve()
        self.callback_url = callback_url.strip()

    @property
    def project_json_path(self) -> Path:
        return self.project_dir / "project.json"

    def _load_project(self) -> dict[str, Any]:
        if not self.project_json_path.is_file():
            raise FileNotFoundError(
                f"project.json not found in '{self.project_dir}'."
            )
        with self.project_json_path.open("r", encoding="utf-8") as fh:
            data = json.load(fh)
        if not isinstance(data, dict):
            raise ValueError("project.json root must be a JSON object.")
        return data

    def _save_project(self, snapshot: dict[str, Any]) -> None:
        self.project_dir.mkdir(parents=True, exist_ok=True)
        payload = json.dumps(snapshot, indent=2, ensure_ascii=False)
        fd, tmp_name = tempfile.mkstemp(
            prefix=".project.", suffix=".json.tmp", dir=str(self.project_dir)
        )
        try:
            with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as fh:
                fh.write(payload)
            os.replace(tmp_name, self.project_json_path)
        except Exception:
            if os.path.exists(tmp_name):
                os.unlink(tmp_name)
            raise

    def _post_callback(self, command: dict[str, Any]) -> dict[str, Any] | None:
        if not self.callback_url:
            return None
        body = json.dumps(
            {"projectDir": str(self.project_dir), "command": command}
        ).encode("utf-8")
        req = urllib.request.Request(
            self.callback_url,
            data=body,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            with urllib.request.urlopen(req, timeout=10.0) as resp:
                raw = resp.read().decode("utf-8", errors="replace")
                return {
                    "dispatched": True,
                    "status": resp.status,
                    "response": json.loads(raw) if raw.strip() else {},
                }
        except (urllib.error.URLError, TimeoutError, ValueError) as exc:
            return {"dispatched": False, "error": str(exc)}

    def _post_action_callback(
        self,
        action: str,
        params: dict[str, Any],
        timeout: float = 600.0,
    ) -> dict[str, Any] | None:
        if not self.callback_url:
            return None
        body = json.dumps(
            {
                "projectDir": str(self.project_dir),
                "action": action,
                "params": params,
            },
            ensure_ascii=False,
        ).encode("utf-8")
        req = urllib.request.Request(
            self.callback_url,
            data=body,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                raw = resp.read().decode("utf-8", errors="replace")
                parsed = json.loads(raw) if raw.strip() else {}
                return {
                    "supported": True,
                    "ok": bool(parsed.get("ok", True)),
                    "status": resp.status,
                    "response": parsed,
                    "error": parsed.get("error"),
                }
        except urllib.error.HTTPError as exc:
            raw = ""
            try:
                raw = exc.read().decode("utf-8", errors="replace")
            except Exception:
                pass
            parsed_err: dict[str, Any] = {}
            try:
                if raw.strip():
                    parsed_err = json.loads(raw)
            except ValueError:
                pass
            err_msg = str(parsed_err.get("error") or raw or exc)
            if exc.code == 501 or (exc.code == 400 and "Missing 'command'" in err_msg):
                return {"supported": False, "ok": False, "status": exc.code, "error": err_msg}
            return {
                "supported": True,
                "ok": False,
                "status": exc.code,
                "response": parsed_err,
                "error": err_msg,
            }
        except TimeoutError as exc:
            return {
                "supported": True,
                "ok": False,
                "error": f"Host subtitle generation timed out after {int(timeout)}s: {exc}",
            }
        except (urllib.error.URLError, ValueError) as exc:
            return {"supported": False, "ok": False, "error": str(exc)}

    def _resolve_segments(self, snapshot: dict[str, Any]) -> list[dict[str, Any]]:
        resolved: list[dict[str, Any]] = []
        output_cursor = 0
        for seg in snapshot.get("segments", []):
            src_start = _normalized_ticks(seg.get("sourceStart"))
            src_dur = _normalized_ticks(seg.get("sourceDuration"))
            rate = seg.get("rate") or {"numerator": 1, "denominator": 1}
            num = max(1, int(rate.get("numerator", 1)))
            den = max(1, int(rate.get("denominator", 1)))
            out_dur = max(1, int(round(src_dur * den / num)))
            resolved.append(
                {
                    "id": seg["id"],
                    "sourceStartTicks": src_start,
                    "sourceDurationTicks": src_dur,
                    "sourceStartSec": _ticks_to_sec(src_start),
                    "sourceEndSec": _ticks_to_sec(src_start + src_dur),
                    "outputStartTicks": output_cursor,
                    "outputDurationTicks": out_dur,
                    "outputStartSec": _ticks_to_sec(output_cursor),
                    "outputEndSec": _ticks_to_sec(output_cursor + out_dur),
                    "rate": round(num / den, 4),
                    "numerator": num,
                    "denominator": den,
                }
            )
            output_cursor += out_dur
        return resolved

    def _apply_local_command(
        self, snapshot: dict[str, Any], command: dict[str, Any]
    ) -> dict[str, Any]:
        op = command.get("op")
        if op == "batch":
            for sub in command.get("commands", []):
                snapshot = self._apply_local_command(snapshot, sub)
            return snapshot

        if op == "effectUpsert":
            effect_id = command["effectId"]
            effect = dict(command["effect"])
            effect["id"] = effect_id
            effects = list(snapshot.get("effects", []))
            for idx, existing in enumerate(effects):
                if existing.get("id") == effect_id:
                    params = dict(effect.get("parameters") or {})
                    if effect.get("kind") != "subtitle":
                        params["origin"] = "edited"
                    params.pop("plannerState", None)
                    effect["parameters"] = params
                    effects[idx] = effect
                    break
            else:
                effects.append(effect)
            snapshot["effects"] = effects

        elif op == "effectDelete":
            effect_id = command["effectId"]
            effects = [
                e for e in snapshot.get("effects", []) if e.get("id") != effect_id
            ]
            if len(effects) == len(snapshot.get("effects", [])):
                raise ValueError(f"Effect '{effect_id}' not found.")
            snapshot["effects"] = effects

        elif op == "subtitlesReplace":
            subtitles = command.get("subtitles")
            if not isinstance(subtitles, list):
                raise ValueError("subtitlesReplace requires a 'subtitles' list.")
            if len(subtitles) > 10_000:
                raise ValueError("subtitlesReplace supports at most 10000 subtitle cues.")

            normalized_subs: list[dict[str, Any]] = []
            for sub in subtitles:
                if not isinstance(sub, dict) or sub.get("kind") != "subtitle":
                    raise ValueError("All entries in subtitlesReplace.subtitles must have kind='subtitle'.")
                sub_copy = dict(sub)
                if not sub_copy.get("id"):
                    sub_copy["id"] = f"sub-{uuid.uuid4().hex[:12]}"
                normalized_subs.append(sub_copy)

            has_range_start = "rangeStart" in command and command["rangeStart"] is not None
            has_range_end = "rangeEnd" in command and command["rangeEnd"] is not None
            if has_range_start != has_range_end:
                raise ValueError("subtitlesReplace requires both rangeStart and rangeEnd when scoping a range.")

            existing_effects = list(snapshot.get("effects", []))
            non_subtitles = [e for e in existing_effects if e.get("kind") != "subtitle"]
            kept_subtitles: list[dict[str, Any]] = []
            if has_range_start and has_range_end:
                r_start = int(command["rangeStart"])
                r_end = int(command["rangeEnd"])
                if r_start < 0 or r_end <= r_start:
                    raise ValueError("subtitlesReplace requires 0 <= rangeStart < rangeEnd.")
                for e in existing_effects:
                    if e.get("kind") != "subtitle":
                        continue
                    s_start = _normalized_ticks(e.get("sourceStart"))
                    s_dur = _normalized_ticks(e.get("sourceDuration"))
                    s_end = s_start + s_dur
                    overlaps = s_start < r_end and s_end > r_start
                    if not overlaps:
                        kept_subtitles.append(e)

            merged_subtitles = kept_subtitles + normalized_subs
            merged_subtitles.sort(
                key=lambda e: (
                    _normalized_ticks(e.get("sourceStart")),
                    _normalized_ticks(e.get("sourceDuration")),
                )
            )
            snapshot["effects"] = non_subtitles + merged_subtitles

            if "subtitleStyle" in command and command["subtitleStyle"] is not None:
                editor = dict(snapshot.get("editor") or {})
                editor["subtitleStyle"] = dict(command["subtitleStyle"])
                snapshot["editor"] = editor

        elif op == "rate":
            seg_id = command["segmentId"]
            num = int(command["numerator"])
            den = int(command["denominator"])
            if num <= 0 or den <= 0 or not (0.25 <= num / den <= 4.0):
                raise ValueError("Playback rate must be between 0.25x and 4.0x.")
            segments = list(snapshot.get("segments", []))
            found = False
            for seg in segments:
                if seg.get("id") == seg_id:
                    seg["rate"] = {"numerator": num, "denominator": den}
                    found = True
                    break
            if not found:
                raise ValueError(f"Segment '{seg_id}' not found.")
            snapshot["segments"] = segments

        elif op == "split":
            seg_id = command["segmentId"]
            cut_ticks = int(command["sourceTicks"])
            new_id = command["newId"]
            segments = list(snapshot.get("segments", []))
            for idx, seg in enumerate(segments):
                if seg.get("id") != seg_id:
                    continue
                start = _normalized_ticks(seg["sourceStart"])
                dur = _normalized_ticks(seg["sourceDuration"])
                if cut_ticks <= start or cut_ticks >= start + dur:
                    raise ValueError("Split point must lie strictly inside segment.")
                right = {
                    "id": new_id,
                    "sourceStart": _canonical_time(cut_ticks),
                    "sourceDuration": _canonical_time(start + dur - cut_ticks),
                    "rate": dict(seg.get("rate") or {"numerator": 1, "denominator": 1}),
                }
                seg["sourceDuration"] = _canonical_time(cut_ticks - start)
                segments.insert(idx + 1, right)
                snapshot["segments"] = segments
                editor = snapshot.get("editor")
                if (
                    isinstance(editor, dict)
                    and isinstance(editor.get("clipAudio"), dict)
                    and seg_id in editor["clipAudio"]
                ):
                    editor["clipAudio"][new_id] = dict(editor["clipAudio"][seg_id])
                break
            else:
                raise ValueError(f"Segment '{seg_id}' not found.")

        elif op == "trim":
            seg_id = command["segmentId"]
            new_start = int(command["sourceStart"])
            new_end = int(command["sourceEnd"])
            for seg in snapshot.get("segments", []):
                if seg.get("id") != seg_id:
                    continue
                cur_start = _normalized_ticks(seg["sourceStart"])
                cur_dur = _normalized_ticks(seg["sourceDuration"])
                if new_start < cur_start or new_end > cur_start + cur_dur or new_end <= new_start:
                    raise ValueError("Trim must retain a non-empty subset of the clip.")
                seg["sourceStart"] = _canonical_time(new_start)
                seg["sourceDuration"] = _canonical_time(new_end - new_start)
                break
            else:
                raise ValueError(f"Segment '{seg_id}' not found.")

        elif op == "delete":
            segments = list(snapshot.get("segments", []))
            if len(segments) <= 1:
                raise ValueError("Cannot delete the last remaining segment.")
            seg_id = command["segmentId"]
            remaining = [s for s in segments if s.get("id") != seg_id]
            if len(remaining) == len(segments):
                raise ValueError(f"Segment '{seg_id}' not found.")
            snapshot["segments"] = remaining

        elif op == "settings":
            for key in ("canvas", "export", "editor", "effects"):
                if key in command and command[key] is not None:
                    snapshot[key] = command[key]

        else:
            raise ValueError(f"Unsupported edit op '{op}'.")

        return snapshot

    def _commit_command(
        self, command: dict[str, Any], summary: str
    ) -> dict[str, Any]:
        callback_result = self._post_callback(command)
        if callback_result and callback_result.get("dispatched"):
            return {
                "ok": True,
                "mode": "callback",
                "summary": summary,
                "command": command,
                "callback": callback_result,
            }

        snapshot = self._load_project()
        updated = self._apply_local_command(snapshot, command)
        updated["revision"] = int(updated.get("revision", 0)) + 1
        self._save_project(updated)
        return {
            "ok": True,
            "mode": "direct",
            "revision": updated["revision"],
            "summary": summary,
            "command": command,
        }

    # --- Tool implementations ---

    def get_timeline(self) -> dict[str, Any]:
        snapshot = self._load_project()
        segments = self._resolve_segments(snapshot)
        effects = []
        for eff in snapshot.get("effects", []):
            start_ticks = _normalized_ticks(eff.get("sourceStart"))
            dur_ticks = _normalized_ticks(eff.get("sourceDuration"))
            effects.append(
                {
                    "id": eff.get("id"),
                    "kind": eff.get("kind"),
                    "startSec": _ticks_to_sec(start_ticks),
                    "endSec": _ticks_to_sec(start_ticks + dur_ticks),
                    "parameters": eff.get("parameters", {}),
                }
            )
        markers = []
        for mk in snapshot.get("markers", []):
            t = _normalized_ticks(mk.get("time"))
            markers.append(
                {
                    "id": mk.get("id"),
                    "timeSec": _ticks_to_sec(t),
                    "label": mk.get("label", ""),
                }
            )
        output_duration_sec = segments[-1]["outputEndSec"] if segments else 0.0
        return {
            "projectId": snapshot.get("projectId", ""),
            "revision": snapshot.get("revision", 0),
            "sourceDurationSec": _ticks_to_sec(
                _normalized_ticks(snapshot.get("sourceDuration"))
            ),
            "outputDurationSec": output_duration_sec,
            "segments": segments,
            "effects": effects,
            "canvas": snapshot.get("canvas", {}),
            "editor": snapshot.get("editor", {}),
            "markers": markers,
        }

    def add_zoom(
        self,
        start_sec: float,
        end_sec: float,
        scale: float = 1.8,
        focus_x: float = 0.5,
        focus_y: float = 0.5,
        enter_sec: float = 0.7,
        exit_sec: float = 0.7,
        easing: str = "cubic",
        effect_id: str | None = None,
    ) -> dict[str, Any]:
        if end_sec <= start_sec or start_sec < 0:
            raise ValueError("Zoom range requires 0 <= start_sec < end_sec.")
        if easing not in ("linear", "cubic", "spring"):
            raise ValueError("easing must be one of: linear, cubic, spring.")
        eid = effect_id or _new_id("zoom")
        start_ticks = _sec_to_ticks(start_sec)
        dur_ticks = max(1, _sec_to_ticks(end_sec - start_sec))
        command = {
            "op": "effectUpsert",
            "effectId": eid,
            "effect": {
                "id": eid,
                "kind": "zoom",
                "sourceStart": _canonical_time(start_ticks),
                "sourceDuration": _canonical_time(dur_ticks),
                "parameters": {
                    "scale": round(_clamp(scale, 1.0, 4.0), 4),
                    "focusX": round(_clamp(focus_x, 0.0, 1.0), 4),
                    "focusY": round(_clamp(focus_y, 0.0, 1.0), 4),
                    "enter": round(_clamp(enter_sec, 0.0, 5.0), 3),
                    "exit": round(_clamp(exit_sec, 0.0, 5.0), 3),
                    "easing": easing,
                    "origin": "manual",
                },
            },
        }
        return self._commit_command(
            command,
            f"Added {scale:.2f}x zoom ({start_sec:.2f}s - {end_sec:.2f}s) focused at ({focus_x:.2f}, {focus_y:.2f}).",
        )

    def add_focus_or_mask(
        self,
        start_sec: float,
        end_sec: float,
        kind: str = "focus",
        x: float = 0.25,
        y: float = 0.25,
        width: float = 0.5,
        height: float = 0.5,
        mode: str = "blur",
        strength: float = 16.0,
        attachment: str = "screen",
        effect_id: str | None = None,
    ) -> dict[str, Any]:
        if kind not in ("focus", "mask"):
            raise ValueError("kind must be 'focus' or 'mask'.")
        if attachment not in ("screen", "canvas"):
            raise ValueError("attachment must be 'screen' or 'canvas'.")
        if end_sec <= start_sec or start_sec < 0:
            raise ValueError("Effect range requires 0 <= start_sec < end_sec.")
        nx = _clamp(x, 0.0, 1.0)
        ny = _clamp(y, 0.0, 1.0)
        nw = _clamp(width, 0.001, 1.0 - nx)
        nh = _clamp(height, 0.001, 1.0 - ny)
        eid = effect_id or _new_id(kind)
        params: dict[str, Any] = {
            "x": round(nx, 4),
            "y": round(ny, 4),
            "width": round(nw, 4),
            "height": round(nh, 4),
            "strength": round(_clamp(strength, 1.0, 64.0), 2),
            "attachment": attachment,
            "origin": "manual",
        }
        if kind == "mask":
            if mode not in ("solid", "pixelate", "blur"):
                raise ValueError("mask mode must be 'solid', 'pixelate', or 'blur'.")
            params["mode"] = mode
        command = {
            "op": "effectUpsert",
            "effectId": eid,
            "effect": {
                "id": eid,
                "kind": kind,
                "sourceStart": _canonical_time(_sec_to_ticks(start_sec)),
                "sourceDuration": _canonical_time(
                    max(1, _sec_to_ticks(end_sec - start_sec))
                ),
                "parameters": params,
            },
        }
        return self._commit_command(
            command,
            f"Added {kind} effect ({start_sec:.2f}s - {end_sec:.2f}s) on {attachment}.",
        )

    def add_3d_tilt(
        self,
        start_sec: float,
        end_sec: float,
        angle_x: float = 8.0,
        angle_y: float = -12.0,
        target: str = "screen",
        enter_sec: float = 0.7,
        exit_sec: float = 0.7,
        easing: str = "cubic",
        effect_id: str | None = None,
    ) -> dict[str, Any]:
        if end_sec <= start_sec or start_sec < 0:
            raise ValueError("Tilt range requires 0 <= start_sec < end_sec.")
        if target not in ("screen", "camera"):
            raise ValueError("target must be 'screen' or 'camera'.")
        if easing not in ("linear", "cubic", "spring"):
            raise ValueError("easing must be one of: linear, cubic, spring.")
        eid = effect_id or _new_id("tilt")
        command = {
            "op": "effectUpsert",
            "effectId": eid,
            "effect": {
                "id": eid,
                "kind": "tilt",
                "sourceStart": _canonical_time(_sec_to_ticks(start_sec)),
                "sourceDuration": _canonical_time(
                    max(1, _sec_to_ticks(end_sec - start_sec))
                ),
                "parameters": {
                    "angleX": round(_clamp(angle_x, -35.0, 35.0), 3),
                    "angleY": round(_clamp(angle_y, -35.0, 35.0), 3),
                    "target": target,
                    "enter": round(_clamp(enter_sec, 0.0, 5.0), 3),
                    "exit": round(_clamp(exit_sec, 0.0, 5.0), 3),
                    "easing": easing,
                    "origin": "manual",
                },
            },
        }
        return self._commit_command(
            command,
            f"Added 3D tilt ({angle_x:.1f}deg, {angle_y:.1f}deg) for {start_sec:.2f}s - {end_sec:.2f}s.",
        )

    def set_clip_speed(self, segment_id: str, rate: float) -> dict[str, Any]:
        if rate < 0.25 or rate > 4.0:
            raise ValueError("Playback rate must be between 0.25 and 4.0.")
        num, den = _rate_to_rational(rate)
        command = {
            "op": "rate",
            "segmentId": segment_id,
            "numerator": num,
            "denominator": den,
        }
        return self._commit_command(
            command, f"Set clip '{segment_id}' speed to {num}/{den} ({num/den:.2f}x)."
        )

    def cut_range(
        self,
        start_sec: float,
        end_sec: float,
        time_space: str = "source",
    ) -> dict[str, Any]:
        if end_sec <= start_sec or start_sec < 0:
            raise ValueError("cut_range requires 0 <= start_sec < end_sec.")
        snapshot = self._load_project()
        resolved = self._resolve_segments(snapshot)
        if not resolved:
            raise ValueError("Project has no segments.")

        if time_space == "output":
            # Map output seconds into source ranges per intersecting segment
            cut_out_start = _sec_to_ticks(start_sec)
            cut_out_end = _sec_to_ticks(end_sec)
            source_cuts: list[tuple[dict[str, Any], int, int]] = []
            for seg in resolved:
                o0 = seg["outputStartTicks"]
                o1 = o0 + seg["outputDurationTicks"]
                ov0 = max(o0, cut_out_start)
                ov1 = min(o1, cut_out_end)
                if ov1 <= ov0:
                    continue
                num, den = seg["numerator"], seg["denominator"]
                s0 = seg["sourceStartTicks"] + int(round((ov0 - o0) * num / den))
                s1 = seg["sourceStartTicks"] + int(round((ov1 - o0) * num / den))
                s0 = max(seg["sourceStartTicks"], s0)
                s1 = min(seg["sourceStartTicks"] + seg["sourceDurationTicks"], s1)
                if s1 > s0:
                    source_cuts.append((seg, s0, s1))
        elif time_space == "source":
            cut_src_start = _sec_to_ticks(start_sec)
            cut_src_end = _sec_to_ticks(end_sec)
            source_cuts = []
            for seg in resolved:
                s0 = seg["sourceStartTicks"]
                s1 = s0 + seg["sourceDurationTicks"]
                ov0 = max(s0, cut_src_start)
                ov1 = min(s1, cut_src_end)
                if ov1 > ov0:
                    source_cuts.append((seg, ov0, ov1))
        else:
            raise ValueError("time_space must be 'source' or 'output'.")

        if not source_cuts:
            raise ValueError(
                f"No timeline segments intersect [{start_sec:.2f}s, {end_sec:.2f}s] in {time_space} time."
            )

        commands: list[dict[str, Any]] = []
        remaining_count = len(resolved)
        for seg, cut_s0, cut_s1 in source_cuts:
            seg_id = seg["id"]
            seg_s0 = seg["sourceStartTicks"]
            seg_s1 = seg_s0 + seg["sourceDurationTicks"]
            if cut_s0 <= seg_s0 and cut_s1 >= seg_s1:
                if remaining_count <= 1:
                    raise ValueError(
                        "Cannot cut the entire remaining clip; at least one clip must remain."
                    )
                commands.append({"op": "delete", "segmentId": seg_id})
                remaining_count -= 1
            elif cut_s0 <= seg_s0:
                commands.append(
                    {
                        "op": "trim",
                        "segmentId": seg_id,
                        "sourceStart": cut_s1,
                        "sourceEnd": seg_s1,
                    }
                )
            elif cut_s1 >= seg_s1:
                commands.append(
                    {
                        "op": "trim",
                        "segmentId": seg_id,
                        "sourceStart": seg_s0,
                        "sourceEnd": cut_s0,
                    }
                )
            else:
                # Middle cut: split at cut_s1 into [seg_s0, cut_s1] and right [cut_s1, seg_s1],
                # then trim left segment to [seg_s0, cut_s0] (equivalently split+split+delete).
                right_id = _new_id("clip")
                commands.append(
                    {
                        "op": "split",
                        "segmentId": seg_id,
                        "sourceTicks": cut_s1,
                        "newId": right_id,
                    }
                )
                commands.append(
                    {
                        "op": "trim",
                        "segmentId": seg_id,
                        "sourceStart": seg_s0,
                        "sourceEnd": cut_s0,
                    }
                )
                remaining_count += 1

        batch_cmd: dict[str, Any] = (
            commands[0]
            if len(commands) == 1
            else {"op": "batch", "commands": commands}
        )
        return self._commit_command(
            batch_cmd,
            f"Cut range [{start_sec:.2f}s, {end_sec:.2f}s] ({time_space} time).",
        )

    def update_canvas(
        self,
        aspect_ratio: str | None = None,
        padding: float | None = None,
        corner_radius: float | None = None,
        frame_style: str | None = None,
    ) -> dict[str, Any]:
        snapshot = self._load_project()
        canvas = dict(snapshot.get("canvas") or {})
        editor = dict(snapshot.get("editor") or {})

        if aspect_ratio is not None:
            w, h = _parse_aspect_ratio(aspect_ratio)
            canvas["width"] = w
            canvas["height"] = h
        if padding is not None:
            if not (0.0 <= padding < 0.5):
                raise ValueError("padding must be >= 0.0 and < 0.5.")
            canvas["padding"] = round(float(padding), 4)
        if corner_radius is not None:
            if not (0.0 <= corner_radius <= 200.0):
                raise ValueError("corner_radius must be between 0 and 200.")
            editor["cornerRadius"] = round(float(corner_radius), 2)
        if frame_style is not None:
            if frame_style not in VALID_FRAME_STYLES:
                raise ValueError(
                    f"frame_style must be one of {', '.join(VALID_FRAME_STYLES)}."
                )
            editor["frameStyle"] = frame_style

        command = {"op": "settings", "canvas": canvas, "editor": editor}
        return self._commit_command(command, "Updated canvas/editor styling.")

    def add_transition(
        self,
        from_clip_index: int = 1,
        to_clip_index: int | None = None,
        style: str = "crossfade",
        duration_sec: float = 0.6,
        easing: str = "cubic",
        effect_id: str | None = None,
    ) -> dict[str, Any]:
        snapshot = self._load_project()
        segments = snapshot.get("segments", []) or []
        if len(segments) < 2:
            raise ValueError("At least two timeline clips are required to add a transition.")

        left_idx = int(from_clip_index)
        right_idx = int(to_clip_index) if to_clip_index is not None else left_idx + 1
        if left_idx < 1 or left_idx >= len(segments):
            raise ValueError(
                f"from_clip_index must be between 1 and {len(segments) - 1} (timeline has {len(segments)} clips)."
            )
        if right_idx != left_idx + 1:
            raise ValueError(
                f"Transition clips must be adjacent (expected to_clip_index={left_idx + 1}, got {right_idx})."
            )

        cleaned_style = (style or "crossfade").strip()
        if cleaned_style not in VALID_TRANSITION_STYLES:
            raise ValueError(
                f"style must be one of {', '.join(VALID_TRANSITION_STYLES)}."
            )
        cleaned_easing = (easing or "cubic").strip()
        if cleaned_easing not in VALID_EASINGS:
            raise ValueError(f"easing must be one of {', '.join(VALID_EASINGS)}.")
        dur = round(_clamp(duration_sec, 0.1, 5.0), 2)

        left_seg = segments[left_idx - 1]
        right_seg = segments[right_idx - 1]
        left_id = str(left_seg["id"])
        right_id = str(right_seg["id"])

        eid = (effect_id or "").strip()
        if not eid:
            for existing in snapshot.get("effects", []) or []:
                if existing.get("kind") != "transition":
                    continue
                ep = existing.get("parameters") or {}
                if ep.get("fromSegmentId") == left_id and ep.get("toSegmentId") == right_id:
                    eid = str(existing["id"])
                    break
        if not eid:
            eid = _new_id("transition")

        total_ticks = _normalized_ticks(snapshot.get("sourceDuration"))
        anchor_start = max(0, min(_normalized_ticks(right_seg.get("sourceStart")), max(0, total_ticks - 1)))
        anchor_dur = max(1, min(_sec_to_ticks(dur), max(1, total_ticks - anchor_start)))

        command = {
            "op": "effectUpsert",
            "effectId": eid,
            "effect": {
                "id": eid,
                "kind": "transition",
                "sourceStart": _canonical_time(anchor_start),
                "sourceDuration": _canonical_time(anchor_dur),
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
        return self._commit_command(
            command,
            f"Added '{cleaned_style}' transition ({dur:.2f}s) between clip #{left_idx} and clip #{right_idx}.",
        )

    def remove_transition(
        self,
        from_clip_index: int | None = None,
        effect_id: str | None = None,
    ) -> dict[str, Any]:
        snapshot = self._load_project()
        target_id = (effect_id or "").strip()
        if not target_id:
            segments = snapshot.get("segments", []) or []
            if from_clip_index is not None:
                left_idx = int(from_clip_index)
                if left_idx < 1 or left_idx >= len(segments):
                    raise ValueError(
                        f"from_clip_index must be between 1 and {max(1, len(segments) - 1)}."
                    )
                left_id = str(segments[left_idx - 1]["id"])
                right_id = str(segments[left_idx]["id"])
                for existing in snapshot.get("effects", []) or []:
                    if existing.get("kind") != "transition":
                        continue
                    ep = existing.get("parameters") or {}
                    if ep.get("fromSegmentId") == left_id and ep.get("toSegmentId") == right_id:
                        target_id = str(existing["id"])
                        break
            else:
                for existing in snapshot.get("effects", []) or []:
                    if existing.get("kind") == "transition":
                        target_id = str(existing["id"])
                        break
        if not target_id:
            raise ValueError("No matching transition effect was found to remove.")
        return self._commit_command(
            {"op": "effectDelete", "effectId": target_id},
            f"Removed transition '{target_id}'.",
        )

    def list_whisper_models(self) -> dict[str, Any]:
        """List available local Whisper speech-to-text models and their install status."""
        if self.callback_url:
            cb = self._post_action_callback("list_whisper_models", {}, timeout=15.0)
            if cb and cb.get("supported"):
                if cb.get("ok"):
                    payload = cb.get("response") or {}
                    data = payload.get("data") if isinstance(payload.get("data"), dict) else {}
                    return {
                        "ok": True,
                        "appliedVia": "host_callback",
                        "message": payload.get("message") or "",
                        **data,
                    }
                raise ValueError(str(cb.get("error") or "Failed to list Whisper models from host."))

        local_app_data = os.environ.get("LOCALAPPDATA") or str(Path.home() / "AppData" / "Local")
        models_dir = Path(local_app_data) / "FrameTrail" / "models" / "whisper"
        catalog = [
            {
                "id": "small-q5",
                "displayName": "Whisper Small (Q5_1, ~190 MB)",
                "fileName": "ggml-small-q5_1.bin",
                "expectedBytes": 190_085_487,
                "sizeMb": 181.3,
            },
            {
                "id": "large-v3-turbo-q5_0",
                "displayName": "Whisper Large v3 Turbo (Q5_0, ~574 MB)",
                "fileName": "ggml-large-v3-turbo-q5_0.bin",
                "expectedBytes": 574_041_195,
                "sizeMb": 547.4,
            },
        ]
        models: list[dict[str, Any]] = []
        for item in catalog:
            fp = models_dir / item["fileName"]
            installed = fp.is_file() and fp.stat().st_size == item["expectedBytes"]
            models.append({**item, "installed": installed, "isSelected": item["id"] == "large-v3-turbo-q5_0"})
        any_installed = any(m["installed"] for m in models)
        return {
            "ok": True,
            "appliedVia": "local_fs",
            "selectedEngine": "whisper-local",
            "selectedModelId": "large-v3-turbo-q5_0",
            "anyLocalModelInstalled": any_installed,
            "modelsDirectory": str(models_dir),
            "models": models,
        }

    def download_whisper_model(self, model_id: str | None = None) -> dict[str, Any]:
        """Download a local Whisper GGML model via FrameTrail host (respecting Review approval mode)."""
        params: dict[str, Any] = {}
        if model_id is not None and str(model_id).strip():
            params["model_id"] = str(model_id).strip()

        if self.callback_url:
            cb = self._post_action_callback("download_whisper_model", params, timeout=900.0)
            if cb and cb.get("supported"):
                if cb.get("ok"):
                    payload = cb.get("response") or {}
                    data = payload.get("data") if isinstance(payload.get("data"), dict) else {}
                    return {
                        "ok": True,
                        "appliedVia": "host_callback",
                        "engineUsed": payload.get("engineUsed"),
                        "message": payload.get("message") or "Whisper model downloaded.",
                        **data,
                    }
                raise ValueError(str(cb.get("error") or "Failed to download Whisper model."))

        raise ValueError(
            "FrameTrail desktop host callback is unavailable; open the project in FrameTrail to download local Whisper models."
        )

    def generate_subtitles(
        self,
        clip_indices: list[int] | None = None,
        start_sec: float | None = None,
        end_sec: float | None = None,
        language: str = "zh",
        style_preset: str | None = None,
        position_y: float | None = None,
        vocabulary_prompt: str = "",
        max_chars_per_line: int = 18,
        force_refresh: bool = False,
    ) -> dict[str, Any]:
        # Prefer the running FrameTrail desktop host's native Whisper.net / BYOK subtitle engine
        # whenever an authenticated loopback callback URL is available.
        if self.callback_url:
            action_params: dict[str, Any] = {
                "language": language,
                "vocabulary_prompt": vocabulary_prompt,
                "max_chars_per_line": max_chars_per_line,
                "force_refresh": bool(force_refresh),
            }
            if clip_indices is not None:
                action_params["clip_indices"] = [int(i) for i in clip_indices]
            if start_sec is not None:
                action_params["start_sec"] = float(start_sec)
            if end_sec is not None:
                action_params["end_sec"] = float(end_sec)
            if style_preset is not None:
                action_params["style_preset"] = style_preset
            if position_y is not None:
                action_params["position_y"] = float(position_y)

            cb = self._post_action_callback("generate_subtitles", action_params, timeout=600.0)
            if cb and cb.get("supported"):
                if cb.get("ok"):
                    payload = cb.get("response") or {}
                    cue_count = int(payload.get("cueCount") or 0)
                    engine_used = str(payload.get("engineUsed") or "whisper-local")
                    message = str(
                        payload.get("message")
                        or f"Generated {cue_count} subtitle cue(s) via {engine_used}."
                    )
                    return {
                        "ok": True,
                        "appliedVia": "host_callback",
                        "revision": payload.get("revision"),
                        "cueCount": cue_count,
                        "engineUsed": engine_used,
                        "message": message,
                    }
                raise ValueError(str(cb.get("error") or "FrameTrail host subtitle generation failed."))

        from agent.multimodal_tools import (
            build_subtitle_style,
            build_subtitles_replace_command,
            transcribe_audio_for_subtitles,
        )

        snapshot = self._load_project()
        asr = transcribe_audio_for_subtitles(
            project_dir=str(self.project_dir),
            language=language,
            force_refresh=force_refresh,
            api_key=os.environ.get("OPENAI_API_KEY", ""),
            base_url=os.environ.get("OPENAI_BASE_URL", ""),
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
                    "No speech-to-text engine produced captions. Configure OPENAI_API_KEY/GROQ_API_KEY, "
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
                existing=existing_style if isinstance(existing_style, dict) else None,
                preset=style_preset,
                position_y=position_y,
                max_chars_per_line=max_chars_per_line,
                canvas=snapshot.get("canvas"),
            )

        command = build_subtitles_replace_command(
            snapshot=snapshot,
            generated_subtitles=cues,
            requested_ranges=asr.get("requestedRanges"),
            subtitle_style=subtitle_style,
        )
        scope_desc = (
            f"clips {clip_indices}"
            if clip_indices
            else (
                f"[{start_sec or 0.0:.2f}s, {end_sec:.2f}s]"
                if end_sec is not None
                else "full timeline"
            )
        )
        result = self._commit_command(
            command,
            f"Generated {len(cues)} subtitle cue(s) for {scope_desc} via {asr.get('engineUsed', 'asr')}.",
        )
        result["cueCount"] = len(cues)
        result["engineUsed"] = asr.get("engineUsed", "asr")
        return result

    def upsert_subtitle(
        self,
        start_sec: float,
        end_sec: float,
        text: str,
        secondary_text: str = "",
        subtitle_id: str | None = None,
    ) -> dict[str, Any]:
        if end_sec <= start_sec or start_sec < 0:
            raise ValueError("Subtitle range requires 0 <= start_sec < end_sec.")
        cleaned_text = (text or "").strip()
        if not cleaned_text:
            raise ValueError("Subtitle text must not be empty.")
        if len(cleaned_text.encode("utf-8")) > 1024:
            raise ValueError("Subtitle text exceeds 1024 UTF-8 bytes.")
        cleaned_secondary = (secondary_text or "").strip()
        if len(cleaned_secondary.encode("utf-8")) > 1024:
            raise ValueError("Subtitle secondary_text exceeds 1024 UTF-8 bytes.")

        eid = (subtitle_id or "").strip() or f"sub-{uuid.uuid4().hex[:12]}"
        params: dict[str, Any] = {"text": cleaned_text}
        if cleaned_secondary:
            params["secondaryText"] = cleaned_secondary

        command = {
            "op": "effectUpsert",
            "effectId": eid,
            "effect": {
                "id": eid,
                "kind": "subtitle",
                "sourceStart": _canonical_time(_sec_to_ticks(start_sec)),
                "sourceDuration": _canonical_time(max(1, _sec_to_ticks(end_sec - start_sec))),
                "parameters": params,
            },
        }
        return self._commit_command(
            command,
            f"Upserted subtitle '{eid}' ({start_sec:.2f}s - {end_sec:.2f}s): {cleaned_text[:48]}",
        )

    def update_subtitle_style(
        self,
        preset: str | None = None,
        font_size: float | None = None,
        position_y: float | None = None,
        text_color: str | None = None,
        outline_color: str | None = None,
        background_opacity: float | None = None,
        highlight_mode: str | None = None,
        bilingual_enabled: bool | None = None,
        font_family: str | None = None,
        position_x: float | None = None,
        alignment: str | None = None,
        max_chars_per_line: int | None = None,
    ) -> dict[str, Any]:
        from agent.multimodal_tools import build_subtitle_style

        snapshot = self._load_project()
        editor = dict(snapshot.get("editor") or {})
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
        command = {"op": "settings", "editor": editor}
        return self._commit_command(
            command,
            f"Updated subtitle style (preset={editor['subtitleStyle']['preset']}).",
        )

    def delete_subtitles(
        self,
        subtitle_ids: list[str] | None = None,
        start_sec: float | None = None,
        end_sec: float | None = None,
    ) -> dict[str, Any]:
        snapshot = self._load_project()
        if subtitle_ids is not None and len(subtitle_ids) > 0:
            cleaned_ids = [str(sid).strip() for sid in subtitle_ids if str(sid).strip()]
            if not cleaned_ids:
                raise ValueError("subtitle_ids must contain at least one non-empty ID.")
            if len(cleaned_ids) == 1:
                command: dict[str, Any] = {"op": "effectDelete", "effectId": cleaned_ids[0]}
            elif len(cleaned_ids) <= 100:
                command = {
                    "op": "batch",
                    "commands": [{"op": "effectDelete", "effectId": sid} for sid in cleaned_ids],
                }
            else:
                remove_set = set(cleaned_ids)
                remaining = [
                    e
                    for e in snapshot.get("effects", [])
                    if e.get("kind") == "subtitle" and e.get("id") not in remove_set
                ]
                command = {"op": "subtitlesReplace", "subtitles": remaining}
            return self._commit_command(
                command, f"Deleted {len(cleaned_ids)} subtitle cue(s) by ID."
            )

        if start_sec is not None or end_sec is not None:
            total_ticks = _normalized_ticks(snapshot.get("sourceDuration"))
            r_start = max(0, _sec_to_ticks(start_sec)) if start_sec is not None else 0
            if end_sec is not None:
                r_end = _sec_to_ticks(end_sec)
            elif total_ticks > 0:
                r_end = total_ticks
            else:
                raise ValueError("end_sec is required when project sourceDuration is unavailable.")
            if total_ticks > 0:
                r_end = min(total_ticks, r_end)
            if r_end <= r_start:
                raise ValueError("delete_subtitles requires end_sec > start_sec.")
            command = {
                "op": "subtitlesReplace",
                "rangeStart": r_start,
                "rangeEnd": r_end,
                "subtitles": [],
            }
            return self._commit_command(
                command,
                f"Deleted subtitles overlapping [{_ticks_to_sec(r_start):.2f}s, {_ticks_to_sec(r_end):.2f}s].",
            )

        command = {"op": "subtitlesReplace", "subtitles": []}
        return self._commit_command(command, "Cleared all subtitles from timeline.")


# --- Tool metadata schema for JSON-RPC stdio MCP server ---

TOOL_DEFINITIONS: list[dict[str, Any]] = [
    {
        "name": "frametrail_get_timeline",
        "description": "Read FrameTrail project.json and return segments, effects, canvas, editor settings, and markers.",
        "inputSchema": {
            "type": "object",
            "properties": {},
            "additionalProperties": False,
        },
    },
    {
        "name": "frametrail_add_zoom",
        "description": "Add or update a zoom effect in FrameTrail project.json (or queue to the C# host).",
        "inputSchema": {
            "type": "object",
            "properties": {
                "start_sec": {"type": "number", "minimum": 0},
                "end_sec": {"type": "number", "exclusiveMinimum": 0},
                "scale": {"type": "number", "minimum": 1.0, "maximum": 4.0, "default": 1.8},
                "focus_x": {"type": "number", "minimum": 0.0, "maximum": 1.0, "default": 0.5},
                "focus_y": {"type": "number", "minimum": 0.0, "maximum": 1.0, "default": 0.5},
                "enter_sec": {"type": "number", "minimum": 0.0, "maximum": 5.0, "default": 0.7},
                "exit_sec": {"type": "number", "minimum": 0.0, "maximum": 5.0, "default": 0.7},
                "easing": {"type": "string", "enum": ["linear", "cubic", "spring"], "default": "cubic"},
                "effect_id": {"type": "string"},
            },
            "required": ["start_sec", "end_sec"],
            "additionalProperties": False,
        },
    },
    {
        "name": "frametrail_add_focus_or_mask",
        "description": "Add a focus spotlight blur or privacy redact mask effect over a normalized rectangle.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "start_sec": {"type": "number", "minimum": 0},
                "end_sec": {"type": "number", "exclusiveMinimum": 0},
                "kind": {"type": "string", "enum": ["focus", "mask"], "default": "focus"},
                "x": {"type": "number", "minimum": 0.0, "maximum": 1.0, "default": 0.25},
                "y": {"type": "number", "minimum": 0.0, "maximum": 1.0, "default": 0.25},
                "width": {"type": "number", "exclusiveMinimum": 0.0, "maximum": 1.0, "default": 0.5},
                "height": {"type": "number", "exclusiveMinimum": 0.0, "maximum": 1.0, "default": 0.5},
                "mode": {"type": "string", "enum": ["solid", "pixelate", "blur"], "default": "blur"},
                "strength": {"type": "number", "minimum": 1.0, "maximum": 64.0, "default": 16.0},
                "attachment": {"type": "string", "enum": ["screen", "canvas"], "default": "screen"},
                "effect_id": {"type": "string"},
            },
            "required": ["start_sec", "end_sec"],
            "additionalProperties": False,
        },
    },
    {
        "name": "frametrail_add_3d_tilt",
        "description": "Add a 3D perspective tilt effect to screen or camera.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "start_sec": {"type": "number", "minimum": 0},
                "end_sec": {"type": "number", "exclusiveMinimum": 0},
                "angle_x": {"type": "number", "minimum": -35.0, "maximum": 35.0, "default": 8.0},
                "angle_y": {"type": "number", "minimum": -35.0, "maximum": 35.0, "default": -12.0},
                "target": {"type": "string", "enum": ["screen", "camera"], "default": "screen"},
                "enter_sec": {"type": "number", "minimum": 0.0, "maximum": 5.0, "default": 0.7},
                "exit_sec": {"type": "number", "minimum": 0.0, "maximum": 5.0, "default": 0.7},
                "easing": {"type": "string", "enum": ["linear", "cubic", "spring"], "default": "cubic"},
                "effect_id": {"type": "string"},
            },
            "required": ["start_sec", "end_sec"],
            "additionalProperties": False,
        },
    },
    {
        "name": "frametrail_set_clip_speed",
        "description": "Update playback rate (0.25x to 4.0x) for a timeline segment.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "segment_id": {"type": "string"},
                "rate": {"type": "number", "minimum": 0.25, "maximum": 4.0},
            },
            "required": ["segment_id", "rate"],
            "additionalProperties": False,
        },
    },
    {
        "name": "frametrail_cut_range",
        "description": "Split and remove a time range [start_sec, end_sec] from the timeline.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "start_sec": {"type": "number", "minimum": 0},
                "end_sec": {"type": "number", "exclusiveMinimum": 0},
                "time_space": {"type": "string", "enum": ["source", "output"], "default": "source"},
            },
            "required": ["start_sec", "end_sec"],
            "additionalProperties": False,
        },
    },
    {
        "name": "frametrail_update_canvas",
        "description": "Update canvas aspect ratio, padding, corner radius, and device/window frameStyle.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "aspect_ratio": {"type": "string"},
                "padding": {"type": "number", "minimum": 0.0, "maximum": 0.499},
                "corner_radius": {"type": "number", "minimum": 0.0, "maximum": 200.0},
                "frame_style": {"type": "string", "enum": list(VALID_FRAME_STYLES)},
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "frametrail_add_transition",
        "description": "Add or update a clip-to-clip transition effect between two adjacent timeline clips (1-based from_clip_index -> from_clip_index + 1). Supports crossfade, fadeBlack, fadeWhite, slideLeft, slideRight, wipeLeft, wipeRight, and zoomIn.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "from_clip_index": {
                    "type": "integer",
                    "minimum": 1,
                    "default": 1,
                    "description": "1-based index of the outgoing clip (e.g. 1 adds a transition between clip #1 and clip #2).",
                },
                "to_clip_index": {
                    "type": "integer",
                    "minimum": 2,
                    "description": "Optional 1-based index of the incoming clip (must equal from_clip_index + 1).",
                },
                "style": {
                    "type": "string",
                    "enum": ["crossfade", "fadeBlack", "fadeWhite", "slideLeft", "slideRight", "wipeLeft", "wipeRight", "zoomIn"],
                    "default": "crossfade",
                },
                "duration_sec": {"type": "number", "minimum": 0.1, "maximum": 5.0, "default": 0.6},
                "easing": {"type": "string", "enum": ["cubic", "spring", "linear"], "default": "cubic"},
                "effect_id": {"type": "string"},
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "frametrail_remove_transition",
        "description": "Remove a clip-to-clip transition by 1-based from_clip_index (seam between clip N and N+1) or explicit effect_id.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "from_clip_index": {"type": "integer", "minimum": 1},
                "effect_id": {"type": "string"},
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "frametrail_list_whisper_models",
        "description": "List FrameTrail's local offline Whisper speech-to-text models ('small-q5' ~181 MB, 'large-v3-turbo-q5_0' ~547 MB), whether each is installed, and whether user approval (Review mode) is required before downloading.",
        "inputSchema": {
            "type": "object",
            "properties": {},
            "additionalProperties": False,
        },
    },
    {
        "name": "frametrail_download_whisper_model",
        "description": "Download and verify a local Whisper GGML speech-to-text model into FrameTrail's model store. When the user's permission mode is set to Review (preview confirm), FrameTrail automatically displays an inline approval card in the AI chat panel and waits for the user to approve or decline before downloading.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "model_id": {
                    "type": "string",
                    "enum": ["small-q5", "large-v3-turbo-q5_0"],
                    "description": "Local Whisper model ID to download ('large-v3-turbo-q5_0' for best quality ~547 MB, or 'small-q5' for faster/smaller download ~181 MB). Defaults to the user's currently selected model in FrameTrail.",
                }
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "frametrail_generate_subtitles",
        "description": "Transcribe speech from project audio/video tracks (whole timeline, 1-based clip_indices like [2, 3], or [start_sec, end_sec]) and apply an atomic op='subtitlesReplace' command.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "clip_indices": {
                    "type": "array",
                    "items": {"type": "integer", "minimum": 1},
                    "description": "Optional 1-based timeline clip indices (e.g. [2, 3] for the 2nd and 3rd clips).",
                },
                "start_sec": {"type": "number", "minimum": 0.0},
                "end_sec": {"type": "number", "exclusiveMinimum": 0.0},
                "language": {"type": "string", "default": "zh"},
                "style_preset": {
                    "type": "string",
                    "enum": ["classicOutline", "darkPill", "karaokePop", "bilingualClean", "custom"],
                },
                "position_y": {"type": "number", "minimum": 0.05, "maximum": 0.95},
                "vocabulary_prompt": {"type": "string", "default": ""},
                "max_chars_per_line": {"type": "integer", "minimum": 4, "maximum": 80, "default": 18},
                "force_refresh": {"type": "boolean", "default": False},
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "frametrail_upsert_subtitle",
        "description": "Add or update a single subtitle cue (kind='subtitle') over [start_sec, end_sec].",
        "inputSchema": {
            "type": "object",
            "properties": {
                "start_sec": {"type": "number", "minimum": 0.0},
                "end_sec": {"type": "number", "exclusiveMinimum": 0.0},
                "text": {"type": "string", "minLength": 1},
                "secondary_text": {"type": "string", "default": ""},
                "subtitle_id": {"type": "string"},
            },
            "required": ["start_sec", "end_sec", "text"],
            "additionalProperties": False,
        },
    },
    {
        "name": "frametrail_update_subtitle_style",
        "description": "Update global editor.subtitleStyle preset, font size, vertical position, colors, background pill, karaoke highlight mode, or bilingual display.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "preset": {
                    "type": "string",
                    "enum": ["classicOutline", "darkPill", "karaokePop", "bilingualClean", "custom"],
                },
                "font_family": {"type": "string"},
                "font_size": {"type": "number", "minimum": 12.0, "maximum": 160.0},
                "position_x": {"type": "number", "minimum": 0.05, "maximum": 0.95},
                "position_y": {"type": "number", "minimum": 0.05, "maximum": 0.95},
                "text_color": {"type": "string"},
                "outline_color": {"type": "string"},
                "background_opacity": {"type": "number", "minimum": 0.0, "maximum": 1.0},
                "highlight_mode": {"type": "string", "enum": ["none", "wordColor", "wordPill"]},
                "alignment": {"type": "string", "enum": ["left", "center", "right"]},
                "max_chars_per_line": {"type": "integer", "minimum": 4, "maximum": 80},
                "bilingual_enabled": {"type": "boolean"},
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "frametrail_delete_subtitles",
        "description": "Delete subtitle cues by explicit subtitle_ids, by source time range [start_sec, end_sec], or clear the entire subtitle track.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "subtitle_ids": {"type": "array", "items": {"type": "string"}},
                "start_sec": {"type": "number", "minimum": 0.0},
                "end_sec": {"type": "number", "exclusiveMinimum": 0.0},
            },
            "additionalProperties": False,
        },
    },
]


def _dispatch_tool(
    bridge: FrameTrailProjectBridge, name: str, arguments: dict[str, Any]
) -> dict[str, Any]:
    if name == "frametrail_get_timeline":
        return bridge.get_timeline()
    if name == "frametrail_add_zoom":
        return bridge.add_zoom(**arguments)
    if name == "frametrail_add_focus_or_mask":
        return bridge.add_focus_or_mask(**arguments)
    if name == "frametrail_add_3d_tilt":
        return bridge.add_3d_tilt(**arguments)
    if name == "frametrail_set_clip_speed":
        return bridge.set_clip_speed(**arguments)
    if name == "frametrail_cut_range":
        return bridge.cut_range(**arguments)
    if name == "frametrail_update_canvas":
        return bridge.update_canvas(**arguments)
    if name == "frametrail_add_transition":
        return bridge.add_transition(**arguments)
    if name == "frametrail_remove_transition":
        return bridge.remove_transition(**arguments)
    if name == "frametrail_list_whisper_models":
        return bridge.list_whisper_models()
    if name == "frametrail_download_whisper_model":
        return bridge.download_whisper_model(**arguments)
    if name == "frametrail_generate_subtitles":
        return bridge.generate_subtitles(**arguments)
    if name == "frametrail_upsert_subtitle":
        return bridge.upsert_subtitle(**arguments)
    if name == "frametrail_update_subtitle_style":
        return bridge.update_subtitle_style(**arguments)
    if name == "frametrail_delete_subtitles":
        return bridge.delete_subtitles(**arguments)
    raise KeyError(f"Unknown tool: {name}")


def _write_jsonrpc_message(payload: dict[str, Any], framed: bool = False) -> None:
    encoded = json.dumps(payload, ensure_ascii=False)
    if framed:
        raw = encoded.encode("utf-8")
        sys.stdout.buffer.write(
            f"Content-Length: {len(raw)}\r\n\r\n".encode("ascii") + raw
        )
        sys.stdout.buffer.flush()
    else:
        sys.stdout.write(encoded + "\n")
        sys.stdout.flush()


def _read_jsonrpc_requests() -> tuple[dict[str, Any] | None, bool]:
    """Read one JSON-RPC request from stdin, supporting both NDJSON and Content-Length framing."""
    while True:
        line = sys.stdin.buffer.readline()
        if not line:
            return None, False
        stripped = line.strip()
        if not stripped:
            continue
        if stripped.lower().startswith(b"content-length:"):
            length = int(stripped.split(b":", 1)[1].strip())
            while True:
                header = sys.stdin.buffer.readline()
                if not header or header in (b"\r\n", b"\n"):
                    break
            body = sys.stdin.buffer.read(length)
            if not body:
                return None, True
            return json.loads(body.decode("utf-8")), True
        return json.loads(stripped.decode("utf-8")), False


def run_builtin_stdio_server(bridge: FrameTrailProjectBridge) -> None:
    """Zero-dependency JSON-RPC 2.0 over stdio MCP server."""
    while True:
        try:
            msg, framed = _read_jsonrpc_requests()
        except Exception as exc:
            _write_jsonrpc_message(
                {
                    "jsonrpc": "2.0",
                    "id": None,
                    "error": {"code": -32700, "message": f"Parse error: {exc}"},
                }
            )
            continue

        if msg is None:
            break

        req_id = msg.get("id")
        method = msg.get("method", "")
        params = msg.get("params") or {}

        # Notifications have no id and require no response
        if req_id is None:
            continue

        try:
            if method == "initialize":
                proto = params.get("protocolVersion") or "2024-11-05"
                result: dict[str, Any] = {
                    "protocolVersion": proto,
                    "capabilities": {"tools": {"listChanged": False}},
                    "serverInfo": {"name": "frametrail-mcp", "version": "1.0.0"},
                }
            elif method == "ping":
                result = {}
            elif method == "tools/list":
                result = {"tools": TOOL_DEFINITIONS}
            elif method == "tools/call":
                tool_name = params.get("name", "")
                tool_args = params.get("arguments") or {}
                try:
                    output = _dispatch_tool(bridge, tool_name, tool_args)
                    result = {
                        "content": [
                            {
                                "type": "text",
                                "text": json.dumps(output, ensure_ascii=False, indent=2),
                            }
                        ],
                        "isError": False,
                    }
                except KeyError as exc:
                    _write_jsonrpc_message(
                        {
                            "jsonrpc": "2.0",
                            "id": req_id,
                            "error": {"code": -32602, "message": str(exc)},
                        },
                        framed=framed,
                    )
                    continue
                except Exception as exc:
                    result = {
                        "content": [{"type": "text", "text": f"Error: {exc}"}],
                        "isError": True,
                    }
            else:
                _write_jsonrpc_message(
                    {
                        "jsonrpc": "2.0",
                        "id": req_id,
                        "error": {"code": -32601, "message": f"Method not found: {method}"},
                    },
                    framed=framed,
                )
                continue

            _write_jsonrpc_message(
                {"jsonrpc": "2.0", "id": req_id, "result": result},
                framed=framed,
            )
        except Exception as exc:
            _write_jsonrpc_message(
                {
                    "jsonrpc": "2.0",
                    "id": req_id,
                    "error": {"code": -32603, "message": str(exc)},
                },
                framed=framed,
            )


def run_fastmcp_server(bridge: FrameTrailProjectBridge) -> bool:
    """Attempt to launch via official ``mcp.server.fastmcp.FastMCP``; returns False if unavailable."""
    try:
        from mcp.server.fastmcp import FastMCP  # type: ignore
    except ImportError:
        return False

    mcp = FastMCP("frametrail-mcp")

    @mcp.tool()
    def frametrail_get_timeline() -> dict[str, Any]:
        """Read project.json and return segments, effects, canvas, and markers."""
        return bridge.get_timeline()

    @mcp.tool()
    def frametrail_add_zoom(
        start_sec: float,
        end_sec: float,
        scale: float = 1.8,
        focus_x: float = 0.5,
        focus_y: float = 0.5,
        enter_sec: float = 0.7,
        exit_sec: float = 0.7,
        easing: str = "cubic",
        effect_id: str | None = None,
    ) -> dict[str, Any]:
        """Add or update a zoom effect in project.json or queue a command to C# host."""
        return bridge.add_zoom(
            start_sec, end_sec, scale, focus_x, focus_y, enter_sec, exit_sec, easing, effect_id
        )

    @mcp.tool()
    def frametrail_add_focus_or_mask(
        start_sec: float,
        end_sec: float,
        kind: str = "focus",
        x: float = 0.25,
        y: float = 0.25,
        width: float = 0.5,
        height: float = 0.5,
        mode: str = "blur",
        strength: float = 16.0,
        attachment: str = "screen",
        effect_id: str | None = None,
    ) -> dict[str, Any]:
        """Add a focus spotlight blur or mask privacy redact effect."""
        return bridge.add_focus_or_mask(
            start_sec, end_sec, kind, x, y, width, height, mode, strength, attachment, effect_id
        )

    @mcp.tool()
    def frametrail_add_3d_tilt(
        start_sec: float,
        end_sec: float,
        angle_x: float = 8.0,
        angle_y: float = -12.0,
        target: str = "screen",
        enter_sec: float = 0.7,
        exit_sec: float = 0.7,
        easing: str = "cubic",
        effect_id: str | None = None,
    ) -> dict[str, Any]:
        """Add a 3D perspective tilt effect."""
        return bridge.add_3d_tilt(
            start_sec, end_sec, angle_x, angle_y, target, enter_sec, exit_sec, easing, effect_id
        )

    @mcp.tool()
    def frametrail_set_clip_speed(segment_id: str, rate: float) -> dict[str, Any]:
        """Update segment playback rate (0.25x to 4.0x)."""
        return bridge.set_clip_speed(segment_id, rate)

    @mcp.tool()
    def frametrail_cut_range(
        start_sec: float, end_sec: float, time_space: str = "source"
    ) -> dict[str, Any]:
        """Split and remove a time range [start_sec, end_sec]."""
        return bridge.cut_range(start_sec, end_sec, time_space)

    @mcp.tool()
    def frametrail_update_canvas(
        aspect_ratio: str | None = None,
        padding: float | None = None,
        corner_radius: float | None = None,
        frame_style: str | None = None,
    ) -> dict[str, Any]:
        """Update canvas aspect ratio, padding, corner radius, and mockup frameStyle."""
        return bridge.update_canvas(aspect_ratio, padding, corner_radius, frame_style)

    @mcp.tool()
    def frametrail_add_transition(
        from_clip_index: int = 1,
        to_clip_index: int | None = None,
        style: str = "crossfade",
        duration_sec: float = 0.6,
        easing: str = "cubic",
        effect_id: str | None = None,
    ) -> dict[str, Any]:
        """Add or update a clip-to-clip transition between adjacent timeline clips."""
        return bridge.add_transition(
            from_clip_index=from_clip_index,
            to_clip_index=to_clip_index,
            style=style,
            duration_sec=duration_sec,
            easing=easing,
            effect_id=effect_id,
        )

    @mcp.tool()
    def frametrail_remove_transition(
        from_clip_index: int | None = None,
        effect_id: str | None = None,
    ) -> dict[str, Any]:
        """Remove a clip-to-clip transition effect by clip seam or effect_id."""
        return bridge.remove_transition(from_clip_index=from_clip_index, effect_id=effect_id)

    @mcp.tool()
    def frametrail_list_whisper_models() -> dict[str, Any]:
        """List local Whisper speech-to-text models and their installation status."""
        return bridge.list_whisper_models()

    @mcp.tool()
    def frametrail_download_whisper_model(model_id: str | None = None) -> dict[str, Any]:
        """Download a local Whisper GGML model (prompts for user confirmation in Review mode)."""
        return bridge.download_whisper_model(model_id=model_id)

    @mcp.tool()
    def frametrail_generate_subtitles(
        clip_indices: list[int] | None = None,
        start_sec: float | None = None,
        end_sec: float | None = None,
        language: str = "zh",
        style_preset: str | None = None,
        position_y: float | None = None,
        vocabulary_prompt: str = "",
        max_chars_per_line: int = 18,
        force_refresh: bool = False,
    ) -> dict[str, Any]:
        """Transcribe speech from project audio/video tracks and apply styled subtitles."""
        return bridge.generate_subtitles(
            clip_indices=clip_indices,
            start_sec=start_sec,
            end_sec=end_sec,
            language=language,
            style_preset=style_preset,
            position_y=position_y,
            vocabulary_prompt=vocabulary_prompt,
            max_chars_per_line=max_chars_per_line,
            force_refresh=force_refresh,
        )

    @mcp.tool()
    def frametrail_upsert_subtitle(
        start_sec: float,
        end_sec: float,
        text: str,
        secondary_text: str = "",
        subtitle_id: str | None = None,
    ) -> dict[str, Any]:
        """Add or update a single subtitle cue over [start_sec, end_sec]."""
        return bridge.upsert_subtitle(
            start_sec=start_sec,
            end_sec=end_sec,
            text=text,
            secondary_text=secondary_text,
            subtitle_id=subtitle_id,
        )

    @mcp.tool()
    def frametrail_update_subtitle_style(
        preset: str | None = None,
        font_size: float | None = None,
        position_y: float | None = None,
        text_color: str | None = None,
        outline_color: str | None = None,
        background_opacity: float | None = None,
        highlight_mode: str | None = None,
        bilingual_enabled: bool | None = None,
        font_family: str | None = None,
        position_x: float | None = None,
        alignment: str | None = None,
        max_chars_per_line: int | None = None,
    ) -> dict[str, Any]:
        """Update global subtitle styling preset, typography, position, colors, or karaoke mode."""
        return bridge.update_subtitle_style(
            preset=preset,
            font_size=font_size,
            position_y=position_y,
            text_color=text_color,
            outline_color=outline_color,
            background_opacity=background_opacity,
            highlight_mode=highlight_mode,
            bilingual_enabled=bilingual_enabled,
            font_family=font_family,
            position_x=position_x,
            alignment=alignment,
            max_chars_per_line=max_chars_per_line,
        )

    @mcp.tool()
    def frametrail_delete_subtitles(
        subtitle_ids: list[str] | None = None,
        start_sec: float | None = None,
        end_sec: float | None = None,
    ) -> dict[str, Any]:
        """Delete subtitles by IDs, by time range, or clear all subtitles."""
        return bridge.delete_subtitles(
            subtitle_ids=subtitle_ids,
            start_sec=start_sec,
            end_sec=end_sec,
        )

    mcp.run(transport="stdio")
    return True


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="FrameTrail MCP stdio server")
    parser.add_argument(
        "--project-dir",
        default=os.environ.get("FRAMETRAIL_PROJECT_DIR", "."),
        help="Path to the .frametrailproject directory containing project.json",
    )
    parser.add_argument(
        "--callback-url",
        default=os.environ.get("FRAMETRAIL_CALLBACK_URL", ""),
        help="Optional C# host HTTP callback URL to queue live editor commands",
    )
    parser.add_argument(
        "--transport",
        choices=("auto", "fastmcp", "jsonrpc"),
        default="auto",
        help="MCP transport implementation to use (default: auto)",
    )
    args = parser.parse_args(argv)

    bridge = FrameTrailProjectBridge(args.project_dir, args.callback_url)
    if args.transport in ("auto", "fastmcp") and run_fastmcp_server(bridge):
        return 0
    if args.transport == "fastmcp":
        print(
            "Error: 'mcp' package is not installed for --transport fastmcp.",
            file=sys.stderr,
        )
        return 1

    run_builtin_stdio_server(bridge)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
