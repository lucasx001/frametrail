"""FastAPI + true SSE streaming sidecar server for FrameTrail AI Video Editing Agent."""

from __future__ import annotations

import asyncio
import ctypes
import inspect
import json
import os
import threading
import time
from typing import Any, AsyncGenerator, Optional

import httpx
from fastapi import FastAPI, HTTPException
from fastapi.responses import StreamingResponse
from langchain_core.language_models.chat_models import BaseChatModel
from langchain_core.messages import AIMessage, BaseMessage, HumanMessage
from pydantic import BaseModel, Field

from agent.graph import build_agent_graph

app = FastAPI(title="FrameTrail AI Agent Sidecar", version="1.1.0")


def _start_parent_pid_watchdog() -> None:
    """Exit automatically if the parent FrameTrail.App.exe process terminates or crashes."""
    raw = os.environ.get("FRAMETRAIL_PARENT_PID", "").strip()
    if not raw.isdigit():
        return
    parent_pid = int(raw)
    if parent_pid <= 0:
        return

    def _watch() -> None:
        if os.name == "nt":
            synchronize = 0x00100000
            infinite = 0xFFFFFFFF
            kernel32 = ctypes.windll.kernel32
            handle = kernel32.OpenProcess(synchronize, False, parent_pid)
            if not handle:
                os._exit(0)
            try:
                kernel32.WaitForSingleObject(handle, infinite)
            finally:
                kernel32.CloseHandle(handle)
            os._exit(0)
        else:
            while True:
                try:
                    os.kill(parent_pid, 0)
                except OSError:
                    os._exit(0)
                time.sleep(1.5)

    threading.Thread(target=_watch, name="frametrail-parent-watchdog", daemon=True).start()


_start_parent_pid_watchdog()


class ChatHistoryItem(BaseModel):
    role: str
    content: str


class ChatRequest(BaseModel):
    session_id: str = "default"
    project_dir: str = ""
    prompt: str
    playhead_sec: float = 0.0
    provider: str = "openai"
    model_name: str = "gpt-4o"
    api_key: str = ""
    base_url: str = ""
    callback_url: str = ""
    project_snapshot: dict[str, Any] = Field(default_factory=dict)
    timeline_overview: Optional[dict[str, Any]] = None
    history: list[ChatHistoryItem] = Field(default_factory=list)


class TranscribeRequest(BaseModel):
    project_dir: str
    language: str = "zh"
    model_size: str = "base"
    clip_indices: Optional[list[int]] = None
    start_sec: Optional[float] = None
    end_sec: Optional[float] = None
    api_key: str = ""
    base_url: str = ""
    vocabulary_prompt: str = ""
    max_chars_per_line: int = 18
    force_refresh: bool = False


def _create_chat_model(req: ChatRequest) -> BaseChatModel:
    provider = req.provider.strip().lower()
    if provider == "anthropic":
        from langchain_anthropic import ChatAnthropic

        kwargs: dict[str, Any] = {
            "model": req.model_name or "claude-sonnet-4-20250514",
            "api_key": req.api_key or None,
        }
        if req.base_url:
            kwargs["base_url"] = req.base_url
        return ChatAnthropic(**kwargs)

    from langchain_openai import ChatOpenAI

    if provider == "deepseek":
        return ChatOpenAI(
            model=req.model_name or "deepseek-chat",
            api_key=req.api_key or "missing-key",
            base_url=req.base_url or "https://api.deepseek.com/v1",
        )
    if provider == "ollama":
        return ChatOpenAI(
            model=req.model_name or "qwen2.5-coder:14b",
            api_key=req.api_key or "ollama",
            base_url=req.base_url or "http://localhost:11434/v1",
        )

    kwargs = {
        "model": req.model_name or "gpt-4o",
        "api_key": req.api_key or None,
    }
    if req.base_url:
        kwargs["base_url"] = req.base_url
    return ChatOpenAI(**kwargs)


def _build_messages(req: ChatRequest) -> list[BaseMessage]:
    msgs: list[BaseMessage] = []
    for item in req.history[-10:]:
        if item.role == "assistant":
            msgs.append(AIMessage(content=item.content))
        elif item.role == "user":
            msgs.append(HumanMessage(content=item.content))
    if not msgs or msgs[-1].content != req.prompt:
        msgs.append(HumanMessage(content=req.prompt))
    return msgs


def _collapse_commands(commands: list[dict[str, Any]]) -> Optional[dict[str, Any]]:
    if not commands:
        return None
    if len(commands) == 1:
        return commands[0]
    flattened: list[dict[str, Any]] = []
    for cmd in commands:
        if cmd.get("op") == "batch" and isinstance(cmd.get("commands"), list):
            flattened.extend(cmd["commands"])
        else:
            flattened.append(cmd)
    return {"op": "batch", "commands": flattened[:100]}


def _resolve_cli_kind(provider: str) -> Optional[str]:
    p = provider.strip().lower()
    if p == "claude_code_cli":
        return "claude"
    if p == "codex_cli":
        return "codex"
    if p in ("antigravity_cli", "agy"):
        return "agy"
    return None


def _sse(event: str, payload: dict[str, Any]) -> str:
    return f"event: {event}\ndata: {json.dumps(payload, ensure_ascii=False)}\n\n"


def _extract_chunk_text(chunk: Any) -> str:
    content = getattr(chunk, "content", "")
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


async def _run_cli_provider(req: ChatRequest, cli_kind: str) -> dict[str, Any]:
    try:
        from agent.subscription_mcp import arun_subscription_cli_turn
    except ImportError as exc:
        raise HTTPException(
            status_code=501,
            detail=f"Subscription CLI bridge not installed: {exc}",
        ) from exc

    raw = await arun_subscription_cli_turn(
        cli_kind=cli_kind,
        prompt=req.prompt,
        project_dir=req.project_dir or ".",
        thread_id=req.session_id,
        callback_url=req.callback_url or "",
        model_name=req.model_name,
        playhead_sec=req.playhead_sec,
        timeline_overview=req.timeline_overview,
    )
    if inspect.isawaitable(raw):
        raw = await raw
    if not isinstance(raw, dict):
        return {"reply": str(raw), "tool_calls": [], "proposed_command": None}
    if raw.get("ok") is False and raw.get("error"):
        raise HTTPException(status_code=502, detail=str(raw["error"]))

    reply_text = raw.get("reply") or raw.get("response") or ""
    normalized_calls: list[dict[str, Any]] = []
    for item in raw.get("tool_calls", []) or []:
        if isinstance(item, dict):
            normalized_calls.append(
                {
                    "tool_name": str(item.get("tool_name") or item.get("name") or "mcp_tool"),
                    "summary": str(item.get("summary") or json.dumps(item.get("args", ""), ensure_ascii=False)[:120]),
                    "succeeded": bool(item.get("succeeded", True)),
                }
            )
    return {
        "reply": reply_text,
        "tool_calls": normalized_calls,
        "proposed_command": raw.get("proposed_command"),
    }


async def _run_turn(req: ChatRequest) -> dict[str, Any]:
    cli_kind = _resolve_cli_kind(req.provider)
    if cli_kind is not None:
        return await _run_cli_provider(req, cli_kind)

    llm = _create_chat_model(req)
    compiled = build_agent_graph(llm)
    initial_state = {
        "messages": _build_messages(req),
        "project_snapshot": req.project_snapshot,
        "project_dir": req.project_dir,
        "playhead_sec": req.playhead_sec,
        "api_key": req.api_key,
        "base_url": req.base_url,
        "proposed_commands": [],
        "tool_calls": [],
        "validation_error": None,
        "retry_count": 0,
    }
    config = {"configurable": {"thread_id": req.session_id}}
    result = await compiled.ainvoke(initial_state, config=config)

    reply = ""
    for msg in reversed(result.get("messages", [])):
        if isinstance(msg, AIMessage) and msg.content:
            reply = msg.content if isinstance(msg.content, str) else json.dumps(msg.content, ensure_ascii=False)
            break

    return {
        "reply": reply,
        "tool_calls": result.get("tool_calls", []),
        "proposed_command": _collapse_commands(result.get("proposed_commands", [])),
    }


@app.get("/api/health")
async def health() -> dict[str, str]:
    return {"status": "ok", "version": "1.1.0"}


@app.get("/api/models/local")
async def list_local_models() -> dict[str, Any]:
    discovered: list[dict[str, str]] = []
    async with httpx.AsyncClient(timeout=2.0) as client:
        try:
            resp = await client.get("http://localhost:11434/api/tags")
            if resp.status_code == 200:
                for m in resp.json().get("models", []):
                    name = m.get("name")
                    if name:
                        discovered.append({"provider": "ollama", "name": name})
        except Exception:
            pass

        try:
            resp = await client.get("http://localhost:1234/v1/models")
            if resp.status_code == 200:
                for m in resp.json().get("data", []):
                    mid = m.get("id")
                    if mid:
                        discovered.append({"provider": "lmstudio", "name": mid})
        except Exception:
            pass

    clis: dict[str, Any] = {}
    try:
        from agent.subscription_mcp import discover_local_subscription_clis

        clis = await asyncio.to_thread(discover_local_subscription_clis)
    except Exception:
        clis = {}

    return {"models": discovered, "clis": clis}


@app.post("/api/transcribe")
async def transcribe_audio(req: TranscribeRequest) -> dict[str, Any]:
    if not (req.project_dir or "").strip():
        raise HTTPException(status_code=400, detail="project_dir is required.")
    try:
        from agent.multimodal_tools import transcribe_audio_for_subtitles

        result = await asyncio.to_thread(
            transcribe_audio_for_subtitles,
            project_dir=req.project_dir,
            language=req.language,
            model_size=req.model_size,
            force_refresh=req.force_refresh,
            api_key=req.api_key,
            base_url=req.base_url,
            clip_indices=req.clip_indices,
            start_sec=req.start_sec,
            end_sec=req.end_sec,
            max_chars_per_line=req.max_chars_per_line,
            vocabulary_prompt=req.vocabulary_prompt,
        )
        return {
            "segments": result.get("segments", []),
            "subtitles": result.get("subtitles", []),
            "rangeStart": result.get("rangeStart"),
            "rangeEnd": result.get("rangeEnd"),
            "engineUsed": result.get("engineUsed", "none"),
            "isFallback": bool(result.get("isFallback", False)),
            "warnings": result.get("warnings", []),
        }
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    except Exception as exc:
        raise HTTPException(status_code=500, detail=str(exc)) from exc


@app.post("/api/chat")
async def chat(req: ChatRequest) -> dict[str, Any]:
    try:
        return await _run_turn(req)
    except HTTPException:
        raise
    except Exception as exc:
        raise HTTPException(status_code=500, detail=str(exc)) from exc


@app.post("/api/chat/stream")
async def chat_stream(req: ChatRequest) -> StreamingResponse:
    async def event_generator() -> AsyncGenerator[str, None]:
        started = time.perf_counter()
        active_tools: list[str] = []
        try:
            cli_kind = _resolve_cli_kind(req.provider)
            if cli_kind is not None:
                cli_tool_name = f"{cli_kind}_cli"
                yield _sse(
                    "thinking",
                    {"text": f"Connecting to local {cli_kind} CLI with FrameTrail MCP tools..."},
                )
                yield _sse(
                    "tool_start",
                    {"tool_name": cli_tool_name, "summary": "Invoking local CLI session"},
                )
                active_tools.append(cli_tool_name)

                try:
                    from agent.subscription_mcp import astream_subscription_cli_turn
                except ImportError as exc:
                    raise HTTPException(
                        status_code=501,
                        detail=f"Subscription CLI bridge not installed: {exc}",
                    ) from exc

                completed_payload: Optional[dict[str, Any]] = None
                stream_error: Optional[str] = None
                async for evt in astream_subscription_cli_turn(
                    cli_kind=cli_kind,
                    prompt=req.prompt,
                    project_dir=req.project_dir or ".",
                    thread_id=req.session_id,
                    callback_url=req.callback_url or "",
                    model_name=req.model_name,
                    playhead_sec=req.playhead_sec,
                    timeline_overview=req.timeline_overview,
                ):
                    etype = evt.get("type", "")
                    if etype == "thinking":
                        text = str(evt.get("text") or "")
                        if text:
                            yield _sse("thinking", {"text": text})
                    elif etype == "tool_start":
                        tname = str(evt.get("tool_name") or "mcp_tool")
                        if tname not in active_tools:
                            active_tools.append(tname)
                        yield _sse(
                            "tool_start",
                            {
                                "tool_name": tname,
                                "summary": str(evt.get("summary") or ""),
                            },
                        )
                    elif etype == "tool_end":
                        tname = str(evt.get("tool_name") or "mcp_tool")
                        if tname in active_tools:
                            active_tools.remove(tname)
                        yield _sse(
                            "tool_end",
                            {
                                "tool_name": tname,
                                "summary": str(evt.get("summary") or ""),
                                "succeeded": bool(evt.get("succeeded", True)),
                            },
                        )
                    elif etype == "token":
                        delta = str(evt.get("text") or "")
                        if delta:
                            yield _sse("token", {"text": delta})
                    elif etype == "error":
                        stream_error = str(evt.get("detail") or f"{cli_kind} CLI failed.")
                        break
                    elif etype == "done":
                        completed_payload = evt

                if stream_error is not None:
                    for open_tool in list(active_tools):
                        yield _sse(
                            "tool_end",
                            {
                                "tool_name": open_tool,
                                "summary": stream_error[:160],
                                "succeeded": False,
                            },
                        )
                    active_tools.clear()
                    yield _sse("error", {"detail": stream_error})
                    return

                result = completed_payload or {"reply": "", "tool_calls": [], "proposed_command": None}
                tool_calls = list(result.get("tool_calls") or [])
                if cli_tool_name in active_tools:
                    active_tools.remove(cli_tool_name)
                    yield _sse(
                        "tool_end",
                        {
                            "tool_name": cli_tool_name,
                            "summary": f"Completed ({len(tool_calls)} tool call(s))",
                            "succeeded": True,
                        },
                    )
                for open_tool in list(active_tools):
                    yield _sse(
                        "tool_end",
                        {
                            "tool_name": open_tool,
                            "summary": "Completed",
                            "succeeded": True,
                        },
                    )
                active_tools.clear()

                done_payload = {
                    "reply": result.get("reply", ""),
                    "tool_calls": tool_calls,
                    "proposed_command": result.get("proposed_command"),
                    "elapsed_ms": int((time.perf_counter() - started) * 1000),
                }
                yield _sse("done", done_payload)
                return

            yield _sse("thinking", {"text": "Analyzing timeline context and planning edits..."})
            llm = _create_chat_model(req)
            compiled = build_agent_graph(llm)
            initial_state = {
                "messages": _build_messages(req),
                "project_snapshot": req.project_snapshot,
                "project_dir": req.project_dir,
                "playhead_sec": req.playhead_sec,
                "api_key": req.api_key,
                "base_url": req.base_url,
                "proposed_commands": [],
                "tool_calls": [],
                "validation_error": None,
                "retry_count": 0,
            }
            config = {"configurable": {"thread_id": req.session_id}}

            streamed_tokens: list[str] = []
            final_state: dict[str, Any] = {}

            if hasattr(compiled, "astream_events"):
                async for evt in compiled.astream_events(initial_state, config=config, version="v2"):
                    kind = evt.get("event", "")
                    name = evt.get("name", "")
                    data = evt.get("data", {}) or {}

                    if kind == "on_chat_model_stream":
                        chunk = data.get("chunk")
                        delta = _extract_chunk_text(chunk)
                        if delta:
                            streamed_tokens.append(delta)
                            yield _sse("token", {"text": delta})
                    elif kind == "on_tool_start":
                        args_preview = json.dumps(data.get("input", {}), ensure_ascii=False)[:100]
                        if name and name not in active_tools:
                            active_tools.append(name)
                        yield _sse("tool_start", {"tool_name": name, "summary": args_preview})
                    elif kind == "on_tool_end":
                        output = data.get("output", "")
                        out_str = output if isinstance(output, str) else getattr(output, "content", str(output))
                        if name in active_tools:
                            active_tools.remove(name)
                        yield _sse(
                            "tool_end",
                            {
                                "tool_name": name,
                                "summary": str(out_str)[:120],
                                "succeeded": True,
                            },
                        )
                    elif kind == "on_chain_end" and isinstance(data.get("output"), dict):
                        out_dict = data["output"]
                        if "proposed_commands" in out_dict or "tool_calls" in out_dict:
                            final_state.update(out_dict)
                        elif "messages" in out_dict:
                            final_state["messages"] = out_dict["messages"]
            else:
                final_state = await compiled.ainvoke(initial_state, config=config)

            reply = "".join(streamed_tokens).strip()
            if not reply:
                for msg in reversed(final_state.get("messages", [])):
                    if isinstance(msg, AIMessage) and msg.content:
                        reply = (
                            msg.content
                            if isinstance(msg.content, str)
                            else json.dumps(msg.content, ensure_ascii=False)
                        )
                        break
                if reply:
                    yield _sse("token", {"text": reply})

            done_payload = {
                "reply": reply,
                "tool_calls": final_state.get("tool_calls", []),
                "proposed_command": _collapse_commands(final_state.get("proposed_commands", [])),
                "elapsed_ms": int((time.perf_counter() - started) * 1000),
            }
            yield _sse("done", done_payload)
        except Exception as exc:
            detail = str(exc.detail if isinstance(exc, HTTPException) else exc)
            for open_tool in list(active_tools):
                yield _sse(
                    "tool_end",
                    {
                        "tool_name": open_tool,
                        "summary": detail[:160],
                        "succeeded": False,
                    },
                )
            active_tools.clear()
            yield _sse("error", {"detail": detail})

    return StreamingResponse(event_generator(), media_type="text/event-stream")


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(app, host="127.0.0.1", port=18765)
