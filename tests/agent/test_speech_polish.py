"""Unit tests for FrameTrail AI speech expression polish (pauses + fillers + repeats + cutRanges)."""

from __future__ import annotations

import json
import math
import struct
import sys
import tempfile
import unittest
import wave
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
if str(REPO_ROOT) not in sys.path:
    sys.path.insert(0, str(REPO_ROOT))

from unittest import mock

from agent import mcp_server, multimodal_tools, speech_rules
from agent.editor_tools import cut_time_ranges, reset_project_context, set_project_context
from agent.graph import validate_commands_dry_run
from agent.mcp_server import FrameTrailProjectBridge
from agent.multimodal_tools import (
    TICKS_PER_SECOND,
    _detect_speech_fillers_and_repeats,
    analyze_speech_polish,
)


def _write_test_wav(path: Path, duration_sec: float, loud_spans: list[tuple[float, float]], sr: int = 16000) -> None:
    total = int(round(duration_sec * sr))
    samples = [0] * total
    for s0, s1 in loud_spans:
        i0 = max(0, int(round(s0 * sr)))
        i1 = min(total, int(round(s1 * sr)))
        for i in range(i0, i1):
            samples[i] = int(12000 * math.sin(2.0 * math.pi * 440.0 * i / sr))
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(sr)
        w.writeframes(struct.pack(f"<{len(samples)}h", *samples))


def _make_project(root: Path, duration_sec: float = 12.0) -> dict:
    (root / "media").mkdir(parents=True, exist_ok=True)
    dur_ticks = int(round(duration_sec * TICKS_PER_SECOND))
    snapshot = {
        "schemaVersion": 1,
        "projectId": "test-polish",
        "revision": 1,
        "sourceDuration": {"ticks": dur_ticks, "timescale": TICKS_PER_SECOND},
        "media": [
            {
                "id": "screen-1",
                "kind": "screen",
                "path": "media/screen-001.mp4",
                "duration": {"ticks": dur_ticks, "timescale": TICKS_PER_SECOND},
                "startOffset": {"ticks": 0, "timescale": TICKS_PER_SECOND},
                "width": 1920,
                "height": 1080,
            },
            {
                "id": "mic-1",
                "kind": "microphone",
                "path": "media/mic-001.wav",
                "duration": {"ticks": dur_ticks, "timescale": TICKS_PER_SECOND},
                "startOffset": {"ticks": 0, "timescale": TICKS_PER_SECOND},
                "sampleRate": 16000,
                "channels": 1,
            },
        ],
        "segments": [
            {
                "id": "clip-1",
                "sourceStart": {"ticks": 0, "timescale": TICKS_PER_SECOND},
                "sourceDuration": {"ticks": dur_ticks, "timescale": TICKS_PER_SECOND},
                "rate": {"numerator": 1, "denominator": 1},
            }
        ],
        "canvas": {
            "width": 1920,
            "height": 1080,
            "padding": 0.05,
            "background": {"type": "solid", "color": "#111114"},
            "screen": {"x": 0.05, "y": 0.05, "width": 0.9, "height": 0.9},
            "camera": {"enabled": False, "x": 0.7, "y": 0.7, "width": 0.25, "height": 0.25, "mirror": False},
        },
        "effects": [],
        "markers": [],
        "export": {
            "width": 1920,
            "height": 1080,
            "fps": {"numerator": 30, "denominator": 1},
            "videoCodec": "h264",
            "audioCodec": "aac",
            "container": "mp4",
        },
        "editor": {},
    }
    (root / "project.json").write_text(json.dumps(snapshot, indent=2), encoding="utf-8")
    return snapshot


class SpeechPolishTests(unittest.TestCase):
    def test_detect_speech_fillers_and_repeats_zh_and_en(self) -> None:
        segments = [
            {
                "startSec": 0.0,
                "endSec": 10.0,
                "text": "嗯，大家好，那个，点击那个按钮，我 我 我们慢慢看，um ok",
                "words": [
                    {"text": "嗯，", "startSec": 0.2, "endSec": 0.55},
                    {"text": "大家好，", "startSec": 0.65, "endSec": 1.2},
                    {"text": "那", "startSec": 1.5, "endSec": 1.65},
                    {"text": "个，", "startSec": 1.65, "endSec": 1.85},
                    {"text": "点击", "startSec": 2.1, "endSec": 2.4},
                    {"text": "那个", "startSec": 2.42, "endSec": 2.65},
                    {"text": "按钮，", "startSec": 2.66, "endSec": 3.0},
                    {"text": "我", "startSec": 3.3, "endSec": 3.45},
                    {"text": "我", "startSec": 3.5, "endSec": 3.65},
                    {"text": "我们", "startSec": 3.7, "endSec": 4.0},
                    {"text": "慢", "startSec": 4.05, "endSec": 4.2},
                    {"text": "慢", "startSec": 4.21, "endSec": 4.35},
                    {"text": "看，", "startSec": 4.36, "endSec": 4.6},
                    {"text": "um", "startSec": 5.0, "endSec": 5.3},
                    {"text": "ok", "startSec": 5.4, "endSec": 5.7},
                ],
            }
        ]
        cuts = _detect_speech_fillers_and_repeats(segments, intensity="standard")
        labels = [c["label"] for c in cuts]
        self.assertIn("「嗯」", labels)
        self.assertIn("「那个」", labels)
        self.assertIn("「我」", labels)
        self.assertIn("「um」", labels)
        self.assertNotIn("「慢」", labels)
        self.assertFalse(any(c["start"] >= 23_500_000 and c["end"] <= 27_500_000 for c in cuts))

    def test_analyze_speech_polish_combines_wav_pauses_and_cached_fillers(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            snapshot = _make_project(root, duration_sec=10.0)
            _write_test_wav(root / "media" / "mic-001.wav", 10.0, [(0.0, 2.0), (4.0, 10.0)])
            cache_dir = root / "cache"
            cache_dir.mkdir(parents=True, exist_ok=True)
            cached_asr = [
                {
                    "startSec": 4.0,
                    "endSec": 9.5,
                    "StartTicks": 40_000_000,
                    "EndTicks": 95_000_000,
                    "speaker": "mic",
                    "text": "呃，我们开始演示",
                    "words": [
                        {"text": "呃，", "startSec": 4.2, "endSec": 4.6, "StartTicks": 42_000_000, "EndTicks": 46_000_000},
                        {"text": "我们开始演示", "startSec": 4.8, "endSec": 6.5, "StartTicks": 48_000_000, "EndTicks": 65_000_000},
                    ],
                }
            ]
            (cache_dir / "ai-transcript.json").write_text(json.dumps(cached_asr, ensure_ascii=False), encoding="utf-8")

            plan = analyze_speech_polish(str(root), intensity="standard", project_snapshot=snapshot)
            self.assertEqual(plan["cutCount"], 2)
            self.assertEqual(plan["pauseCount"], 1)
            self.assertEqual(plan["fillerCount"], 1)
            self.assertEqual(plan["command"]["op"], "cutRanges")
            self.assertIsNone(validate_commands_dry_run(snapshot, [plan["command"]]))

    def test_cut_time_ranges_tool_and_mcp_apply(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            snapshot = _make_project(root, duration_sec=20.0)
            tok = set_project_context(snapshot, project_dir=str(root))
            try:
                raw = cut_time_ranges.invoke(
                    {
                        "ranges": [
                            {"start_sec": 2.0, "end_sec": 3.0, "reason": "pause", "label": "1.0s"},
                            {"start_sec": 6.0, "end_sec": 6.5, "reason": "filler", "label": "「嗯」"},
                        ]
                    }
                )
                cmd = json.loads(raw)
                self.assertEqual(cmd["op"], "cutRanges")
                self.assertEqual(len(cmd["ranges"]), 2)
                self.assertIsNone(validate_commands_dry_run(snapshot, [cmd]))
            finally:
                reset_project_context(tok)

            bridge = FrameTrailProjectBridge(str(root))
            res = bridge.cut_ranges(
                ranges=[
                    {"start_sec": 2.0, "end_sec": 3.0, "reason": "pause"},
                    {"start_sec": 6.0, "end_sec": 6.5, "reason": "filler"},
                ]
            )
            self.assertTrue(res["ok"])
            updated = json.loads((root / "project.json").read_text(encoding="utf-8"))
            self.assertEqual(len(updated["segments"]), 3)
            self.assertEqual(updated["segments"][0]["id"], "clip-1")

    def test_collapse_commands_deduplicates_cut_ranges_and_drops_stale_splits(self) -> None:
        from agent.server import _collapse_commands

        c1 = {
            "op": "cutRanges",
            "ranges": [{"start": 850_000, "end": 2_550_000, "reason": "filler", "label": "「Uh」"}],
            "minKeepTicks": 1_000_000,
        }
        c2 = {
            "op": "cutRanges",
            "ranges": [
                {"start": 850_000, "end": 2_550_000, "reason": "filler", "label": "「Uh」"},
                {"start": 5_000_000, "end": 6_000_000, "reason": "pause", "label": "1.0s"},
            ],
            "minKeepTicks": 1_000_000,
        }
        c3 = {
            "op": "batch",
            "commands": [
                {"op": "split", "segmentId": "clip-1", "sourceTicks": 850_000, "newId": "clip-r"},
                {"op": "trim", "segmentId": "clip-r", "sourceStart": 2_550_000, "sourceEnd": 10_000_000},
            ],
        }
        collapsed = _collapse_commands([c1, c2, c3])
        self.assertIsNotNone(collapsed)
        assert collapsed is not None
        self.assertEqual(collapsed["op"], "cutRanges")
        self.assertEqual(len(collapsed["ranges"]), 2)
        self.assertEqual(collapsed["ranges"][0]["start"], 850_000)
        self.assertEqual(collapsed["ranges"][1]["start"], 5_000_000)

    def test_speech_rules_intensity_presets_and_lexicons_match_csharp_spec(self) -> None:
        self.assertEqual(
            speech_rules.INTENSITY_PRESETS,
            {
                "gentle": {"min_pause_sec": 1.00, "padding_sec": 0.24, "remove_repeats": False},
                "standard": {"min_pause_sec": 0.70, "padding_sec": 0.16, "remove_repeats": True},
                "tight": {"min_pause_sec": 0.45, "padding_sec": 0.10, "remove_repeats": True},
            },
        )
        self.assertEqual(speech_rules.normalize_intensity("轻度"), "gentle")
        self.assertEqual(speech_rules.normalize_intensity("conservative"), "gentle")
        self.assertEqual(speech_rules.normalize_intensity("紧凑"), "tight")
        self.assertEqual(speech_rules.normalize_intensity("aggressive"), "tight")
        self.assertEqual(speech_rules.normalize_intensity(None), "standard")

        self.assertEqual(
            speech_rules.resolve_intensity_options("gentle"),
            ("gentle", 1.00, 0.24, False),
        )
        self.assertEqual(
            speech_rules.resolve_intensity_options("standard"),
            ("standard", 0.70, 0.16, True),
        )
        self.assertEqual(
            speech_rules.resolve_intensity_options("tight", min_pause_sec=0.55),
            ("tight", 0.55, 0.10, True),
        )

        self.assertTrue({"嗯", "呃", "额", "um", "uh", "erm", "hmm"}.issubset(speech_rules.STRONG_FILLERS))
        self.assertEqual(speech_rules.BOUNDARY_FILLERS, frozenset({"啊", "哦", "哎", "呐", "嘛", "呢", "呀"}))
        self.assertIn("Verbatim speech transcript", speech_rules.VERBATIM_FILLER_PROMPT)
        self.assertEqual(speech_rules.clean_speech_token("“嗯，”"), "嗯")
        self.assertEqual(speech_rules.clean_speech_token("(um...)"), "um")

    def test_speech_rules_merge_and_clamp_cut_ranges_preserves_timeline(self) -> None:
        snapshot = {
            "sourceDuration": {"ticks": 40_000_000, "timescale": TICKS_PER_SECOND},
            "segments": [
                {
                    "id": "clip-1",
                    "sourceStart": {"ticks": 0, "timescale": TICKS_PER_SECOND},
                    "sourceDuration": {"ticks": 40_000_000, "timescale": TICKS_PER_SECOND},
                }
            ],
        }
        raw_cuts = [
            {"start": 5_000_000, "end": 12_000_000, "reason": "pause", "label": "0.7s"},
            {"start": 12_500_000, "end": 16_000_000, "reason": "filler", "label": "「嗯」"},
            {"start": 30_000_000, "end": 35_000_000, "reason": "repeat", "label": "「我」"},
        ]
        merged = speech_rules.merge_and_clamp_cut_ranges(snapshot, raw_cuts)
        self.assertEqual(len(merged), 2)
        self.assertEqual(merged[0], {"start": 5_000_000, "end": 16_000_000, "reason": "filler", "label": "「嗯」"})
        self.assertEqual(merged[1], {"start": 30_000_000, "end": 35_000_000, "reason": "repeat", "label": "「我」"})

        # Never erase 100% of timeline segments
        full_wipe = [{"start": 0, "end": 40_000_000, "reason": "pause", "label": "4.0s"}]
        self.assertEqual(speech_rules.merge_and_clamp_cut_ranges(snapshot, full_wipe), [])

    def test_multimodal_tools_and_mcp_server_share_speech_rules(self) -> None:
        self.assertIs(multimodal_tools._normalize_intensity, speech_rules.normalize_intensity)
        self.assertIs(multimodal_tools._clean_speech_token, speech_rules.clean_speech_token)
        self.assertIs(multimodal_tools._detect_speech_fillers_and_repeats, speech_rules.detect_speech_fillers_and_repeats)
        self.assertIs(multimodal_tools._merge_and_clamp_cut_ranges, speech_rules.merge_and_clamp_cut_ranges)
        self.assertIs(multimodal_tools._STRONG_FILLERS, speech_rules.STRONG_FILLERS)
        self.assertIs(multimodal_tools._BOUNDARY_FILLERS, speech_rules.BOUNDARY_FILLERS)
        self.assertIs(multimodal_tools._VERBATIM_FILLER_PROMPT, speech_rules.VERBATIM_FILLER_PROMPT)

        self.assertIs(mcp_server.normalize_intensity, speech_rules.normalize_intensity)
        self.assertIs(mcp_server.clean_speech_token, speech_rules.clean_speech_token)
        self.assertIs(mcp_server.detect_speech_fillers_and_repeats, speech_rules.detect_speech_fillers_and_repeats)
        self.assertIs(mcp_server.merge_and_clamp_cut_ranges, speech_rules.merge_and_clamp_cut_ranges)
        self.assertIs(mcp_server.STRONG_FILLERS, speech_rules.STRONG_FILLERS)
        self.assertIs(mcp_server.BOUNDARY_FILLERS, speech_rules.BOUNDARY_FILLERS)
        self.assertIs(mcp_server.INTENSITY_PRESETS, speech_rules.INTENSITY_PRESETS)

    def test_mcp_bridge_prioritizes_host_callback_and_falls_back_only_offline(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            _make_project(root, duration_sec=10.0)
            live_bridge = FrameTrailProjectBridge(str(root), callback_url="http://127.0.0.1:18765/api/commands/")

            with mock.patch.object(
                live_bridge,
                "_post_action_callback",
                return_value={
                    "supported": True,
                    "ok": True,
                    "response": {
                        "revision": 7,
                        "engineUsed": "whisper-local",
                        "message": "Applied via host",
                        "data": {"cutCount": 2},
                    },
                },
            ) as mock_action:
                res = live_bridge.optimize_speech_flow(intensity="轻度")
            self.assertEqual(res["appliedVia"], "host_callback")
            self.assertEqual(res["revision"], 7)
            self.assertEqual(mock_action.call_args.args[0], "optimize_speech_flow")
            self.assertEqual(mock_action.call_args.args[1]["intensity"], "gentle")

            cmd = {"op": "cutRanges", "ranges": [{"start": 1_000_000, "end": 2_000_000}], "minKeepTicks": 1_000_000}
            with mock.patch.object(
                live_bridge,
                "_post_callback",
                return_value={"dispatched": True, "status": 200, "response": {"ok": True}},
            ):
                cb_res = live_bridge._commit_command(cmd, "cut via callback")
            self.assertEqual(cb_res["mode"], "callback")
            disk_after_cb = json.loads((root / "project.json").read_text(encoding="utf-8"))
            self.assertEqual(disk_after_cb["revision"], 1)

            with mock.patch.object(
                live_bridge,
                "_post_callback",
                return_value={"dispatched": False, "error": "connection refused"},
            ):
                with self.assertRaises(ValueError):
                    live_bridge._commit_command(cmd, "should not mutate disk")
            disk_after_err = json.loads((root / "project.json").read_text(encoding="utf-8"))
            self.assertEqual(disk_after_err["revision"], 1)

            offline_bridge = FrameTrailProjectBridge(str(root), callback_url="")
            direct_res = offline_bridge._commit_command(cmd, "cut offline")
            self.assertEqual(direct_res["mode"], "direct")
            self.assertEqual(direct_res["revision"], 2)


if __name__ == "__main__":
    unittest.main()
