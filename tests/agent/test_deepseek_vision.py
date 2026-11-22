"""Unit tests for DeepSeek Vision (deepseek-flash) video frame inspection."""

from __future__ import annotations

import asyncio
import io
import json
import sys
import tempfile
import unittest
import urllib.error
from pathlib import Path
from typing import Any
from unittest.mock import patch

REPO_ROOT = Path(__file__).resolve().parents[2]
if str(REPO_ROOT) not in sys.path:
    sys.path.insert(0, str(REPO_ROOT))

from agent.editor_tools import get_project_context, reset_project_context, set_project_context
from agent.graph import build_agent_graph, collect_tools
from agent.multimodal_tools import (
    _parse_vision_json,
    _query_vision_llm,
    _resolve_chat_completions_url,
    _resolve_vision_candidates,
    inspect_video_frames,
)


class _FakeHttpResponse:
    def __init__(self, payload: dict[str, Any]) -> None:
        self._body = json.dumps(payload).encode("utf-8")

    def read(self) -> bytes:
        return self._body

    def __enter__(self) -> "_FakeHttpResponse":
        return self

    def __exit__(self, *_args: Any) -> bool:
        return False


class DeepSeekVisionTests(unittest.TestCase):
    def test_chat_completions_url_normalization(self) -> None:
        self.assertEqual(
            _resolve_chat_completions_url("", "https://api.deepseek.com"),
            "https://api.deepseek.com/chat/completions",
        )
        self.assertEqual(
            _resolve_chat_completions_url("https://api.deepseek.com/v1", "https://api.deepseek.com"),
            "https://api.deepseek.com/v1/chat/completions",
        )
        self.assertEqual(
            _resolve_chat_completions_url("https://api.deepseek.com/v1/", "https://api.deepseek.com"),
            "https://api.deepseek.com/v1/chat/completions",
        )
        self.assertEqual(
            _resolve_chat_completions_url(
                "https://proxy.example.com/v1/chat/completions",
                "https://api.deepseek.com",
            ),
            "https://proxy.example.com/v1/chat/completions",
        )

    def test_parse_vision_json_fenced_and_clamped(self) -> None:
        raw = """Here is the result:
```json
{
  "answer": "Export button is in the top-right corner.",
  "regions": [
    {"label": "Export button", "timestampSec": 3.5, "x": 0.85, "y": -0.1, "width": 0.4, "height": 0.12}
  ]
}
```
"""
        parsed = _parse_vision_json(raw)
        self.assertEqual(parsed["answer"], "Export button is in the top-right corner.")
        self.assertEqual(len(parsed["regions"]), 1)
        region = parsed["regions"][0]
        self.assertEqual(region["label"], "Export button")
        self.assertEqual(region["timestampSec"], 3.5)
        self.assertGreaterEqual(region["x"], 0.0)
        self.assertGreaterEqual(region["y"], 0.0)
        self.assertLessEqual(region["x"] + region["width"], 1.0 + 1e-6)
        self.assertLessEqual(region["y"] + region["height"], 1.0 + 1e-6)

    def test_resolve_vision_candidates_prioritizes_active_deepseek_and_routes_to_flash(self) -> None:
        with patch.dict(
            "os.environ",
            {"OPENAI_API_KEY": "sk-openai-env", "DEEPSEEK_API_KEY": "", "ANTHROPIC_API_KEY": ""},
            clear=False,
        ):
            cands = _resolve_vision_candidates(
                {
                    "provider": "deepseek",
                    "api_key": "sk-ds-session",
                    "base_url": "https://api.deepseek.com/v1",
                    "model_name": "deepseek-chat",
                }
            )
        self.assertGreaterEqual(len(cands), 2)
        self.assertEqual(cands[0]["provider"], "deepseek")
        self.assertEqual(cands[0]["model"], "deepseek-flash")
        self.assertEqual(cands[0]["api_key"], "sk-ds-session")
        self.assertEqual(cands[0]["url"], "https://api.deepseek.com/v1/chat/completions")
        self.assertEqual(cands[1]["provider"], "openai")

    def test_query_vision_llm_deepseek_payload_matches_vision_api_spec(self) -> None:
        captured: list[tuple[str, dict[str, Any], dict[str, Any]]] = []

        def fake_urlopen(req: Any, timeout: int = 60) -> _FakeHttpResponse:
            body = json.loads(req.data.decode("utf-8"))
            captured.append((req.full_url, dict(req.header_items()), body))
            return _FakeHttpResponse(
                {
                    "choices": [
                        {
                            "message": {
                                "content": json.dumps(
                                    {
                                        "answer": "Found the blue Save button at top right.",
                                        "regions": [
                                            {
                                                "label": "Save button",
                                                "timestampSec": 2.5,
                                                "x": 0.82,
                                                "y": 0.04,
                                                "width": 0.12,
                                                "height": 0.06,
                                            }
                                        ],
                                    }
                                )
                            }
                        }
                    ]
                }
            )

        frames = [
            {
                "timestampSec": 2.5,
                "ticks": 25_000_000,
                "mimeType": "image/jpeg",
                "width": 1920,
                "height": 1080,
                "imageBase64": "AQIDBA==",
            }
        ]
        ctx = {
            "provider": "deepseek",
            "api_key": "sk-ds-test",
            "base_url": "https://api.deepseek.com",
            "model_name": "deepseek-chat",
        }
        with patch.dict("os.environ", {"OPENAI_API_KEY": "", "ANTHROPIC_API_KEY": "", "DEEPSEEK_API_KEY": ""}):
            with patch("urllib.request.urlopen", side_effect=fake_urlopen):
                result = _query_vision_llm("Where is the Save button?", frames, ctx=ctx)

        self.assertIsNotNone(result)
        assert result is not None
        self.assertEqual(result["visionProvider"], "deepseek")
        self.assertEqual(result["visionModel"], "deepseek-flash")
        self.assertEqual(result["answer"], "Found the blue Save button at top right.")
        self.assertEqual(len(result["regions"]), 1)

        self.assertEqual(len(captured), 1)
        url, headers, body = captured[0]
        self.assertEqual(url, "https://api.deepseek.com/chat/completions")
        self.assertEqual(headers.get("Authorization"), "Bearer sk-ds-test")
        self.assertEqual(body["model"], "deepseek-flash")
        self.assertEqual(body["response_format"], {"type": "json_object"})
        blocks = body["messages"][0]["content"]
        image_blocks = [b for b in blocks if b.get("type") == "image_url"]
        self.assertEqual(len(image_blocks), 1)
        self.assertEqual(image_blocks[0]["image_url"], {"url": "data:image/jpeg;base64,AQIDBA=="})
        # DeepSeek Vision spec does not use OpenAI's detail parameter
        self.assertNotIn("detail", image_blocks[0]["image_url"])

    def test_query_vision_llm_retries_without_response_format_on_http_400(self) -> None:
        calls: list[dict[str, Any]] = []

        def fake_urlopen(req: Any, timeout: int = 60) -> _FakeHttpResponse:
            body = json.loads(req.data.decode("utf-8"))
            calls.append(body)
            if "response_format" in body:
                raise urllib.error.HTTPError(
                    req.full_url,
                    400,
                    "Bad Request",
                    hdrs={},
                    fp=io.BytesIO(b'{"error":"response_format unsupported"}'),
                )
            return _FakeHttpResponse(
                {"choices": [{"message": {"content": '{"answer":"Retried OK","regions":[]}'}}]}
            )

        frames = [{"timestampSec": 1.0, "mimeType": "image/jpeg", "width": 1280, "height": 720, "imageBase64": "AAAA"}]
        ctx = {"provider": "deepseek", "api_key": "sk-ds-test", "base_url": "", "model_name": "deepseek-flash"}
        with patch.dict("os.environ", {"OPENAI_API_KEY": "", "ANTHROPIC_API_KEY": "", "DEEPSEEK_API_KEY": ""}):
            with patch("urllib.request.urlopen", side_effect=fake_urlopen):
                result = _query_vision_llm("Describe screen", frames, ctx=ctx)

        self.assertIsNotNone(result)
        assert result is not None
        self.assertEqual(result["answer"], "Retried OK")
        self.assertEqual(len(calls), 2)
        self.assertIn("response_format", calls[0])
        self.assertNotIn("response_format", calls[1])

    def test_inspect_video_frames_uses_context_project_dir_and_strips_base64_by_default(self) -> None:
        with tempfile.TemporaryDirectory(prefix="ft-vision-test-") as tmp:
            project_root = Path(tmp)
            media_dir = project_root / "media"
            media_dir.mkdir(parents=True, exist_ok=True)
            screen_file = media_dir / "screen-001.mp4"
            screen_file.write_bytes(b"fake-mp4")
            (project_root / "project.json").write_text(
                json.dumps(
                    {
                        "media": [
                            {
                                "kind": "screen",
                                "path": "media/screen-001.mp4",
                                "startOffset": {"ticks": 0, "timescale": 10_000_000},
                                "duration": {"ticks": 100_000_000, "timescale": 10_000_000},
                                "width": 1920,
                                "height": 1080,
                            }
                        ]
                    }
                ),
                encoding="utf-8",
            )

            def fake_run(cmd: list[str], **_kwargs: Any) -> Any:
                out_path = Path(cmd[-1])
                out_path.write_bytes(b"\xff\xd8\xff\xd9")

                class _Proc:
                    returncode = 0
                    stderr = ""

                return _Proc()

            def fake_urlopen(req: Any, timeout: int = 60) -> _FakeHttpResponse:
                return _FakeHttpResponse(
                    {
                        "choices": [
                            {
                                "message": {
                                    "content": json.dumps(
                                        {
                                            "answer": "Terminal window is centered on screen.",
                                            "regions": [
                                                {
                                                    "label": "Terminal",
                                                    "timestampSec": 4.0,
                                                    "x": 0.2,
                                                    "y": 0.15,
                                                    "width": 0.6,
                                                    "height": 0.7,
                                                }
                                            ],
                                        }
                                    )
                                }
                            }
                        ]
                    }
                )

            token = set_project_context(
                snapshot={},
                project_dir=str(project_root),
                playhead_sec=4.0,
                api_key="sk-ds-ctx",
                base_url="https://api.deepseek.com/v1",
                provider="deepseek",
                model_name="deepseek-chat",
            )
            try:
                with (
                    patch("agent.multimodal_tools._locate_ffmpeg", return_value="ffmpeg"),
                    patch("subprocess.run", side_effect=fake_run),
                    patch("urllib.request.urlopen", side_effect=fake_urlopen),
                ):
                    raw_out = inspect_video_frames.invoke(
                        {
                            "start_sec": 4.0,
                            "end_sec": 4.0,
                            "question": "Locate the terminal window",
                        }
                    )
                    parsed = json.loads(raw_out)
                    self.assertEqual(parsed["frameCount"], 1)
                    self.assertEqual(parsed["visionProvider"], "deepseek")
                    self.assertEqual(parsed["visionModel"], "deepseek-flash")
                    self.assertEqual(parsed["answer"], "Terminal window is centered on screen.")
                    self.assertEqual(len(parsed["regions"]), 1)
                    # Base64 omitted from ToolMessage by default to avoid context overflow
                    self.assertNotIn("imageBase64", parsed["frames"][0])
                    self.assertEqual(parsed["frames"][0]["byteSize"], 4)

                    raw_with_b64 = inspect_video_frames.invoke(
                        {
                            "start_sec": 4.0,
                            "end_sec": 4.0,
                            "question": "Locate the terminal window",
                            "include_base64": True,
                        }
                    )
                    parsed_b64 = json.loads(raw_with_b64)
                    self.assertIn("imageBase64", parsed_b64["frames"][0])
            finally:
                reset_project_context(token)

    def test_graph_propagates_provider_and_model_into_tool_context(self) -> None:
        from langchain_core.messages import AIMessage, HumanMessage

        observed_ctx: dict[str, Any] = {}

        class _FakeToolBoundLlm:
            def __init__(self) -> None:
                self.calls = 0

            async def ainvoke(self, messages: list[Any]) -> AIMessage:
                self.calls += 1
                if self.calls == 1:
                    observed_ctx.update(get_project_context())
                    return AIMessage(content="Inspected context successfully.")
                return AIMessage(content="Done.")

        class _FakeChatModel:
            def bind_tools(self, tools: list[Any]) -> _FakeToolBoundLlm:
                names = {t.name for t in tools}
                assert "inspect_video_frames" in names
                return _FakeToolBoundLlm()

        tools = collect_tools()
        self.assertTrue(any(t.name == "inspect_video_frames" for t in tools))

        graph = build_agent_graph(_FakeChatModel())  # type: ignore[arg-type]
        result = asyncio.run(
            graph.ainvoke(
                {
                    "messages": [HumanMessage(content="What is on screen?")],
                    "project_snapshot": {"sourceDuration": {"ticks": 100_000_000, "timescale": 10_000_000}},
                    "project_dir": "C:/tmp/demo",
                    "playhead_sec": 2.0,
                    "provider": "deepseek",
                    "model_name": "deepseek-flash",
                    "api_key": "sk-ds-graph",
                    "base_url": "https://api.deepseek.com/v1",
                    "proposed_commands": [],
                    "tool_calls": [],
                    "validation_error": None,
                    "retry_count": 0,
                },
                config={"configurable": {"thread_id": "test-deepseek-vision"}},
            )
        )
        self.assertEqual(observed_ctx.get("provider"), "deepseek")
        self.assertEqual(observed_ctx.get("model_name"), "deepseek-flash")
        self.assertEqual(observed_ctx.get("api_key"), "sk-ds-graph")
        self.assertEqual(observed_ctx.get("project_dir"), "C:/tmp/demo")
        self.assertTrue(any("inspect_video_frames" in str(getattr(m, "content", "")) for m in result["messages"]))

    def test_server_defaults_and_migrates_deepseek_to_flash(self) -> None:
        from agent.server import ChatRequest, _create_chat_model

        for raw_model in ("", "deepseek-chat", "deepseek-reasoner", "deepseek-flash"):
            llm = _create_chat_model(
                ChatRequest(
                    prompt="hi",
                    provider="deepseek",
                    model_name=raw_model,
                    api_key="sk-ds-test",
                )
            )
            self.assertEqual(getattr(llm, "model_name", ""), "deepseek-flash")


if __name__ == "__main__":
    unittest.main()
