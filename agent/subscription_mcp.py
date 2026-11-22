"""Local Subscription CLI Bridge and LangGraph Checkpointer / HITL Manager.

Phase 3 capabilities for FrameTrail AI Video Editing Agent:
1. ``SubscriptionCliBridge``:
   - Discovers locally installed subscription CLIs (``claude``, ``codex``,
     ``gh``/``gh copilot``) and local LLM servers (Ollama :11434, LM Studio :1234).
   - Generates temporary MCP configuration files pointing to ``agent/mcp_server.py``.
   - Invokes ``claude`` or ``codex`` (or ``gh copilot``) non-interactively and
     normalizes tool calls + assistant output.
2. ``AgentCheckpointManager``:
   - Wraps LangGraph ``MemorySaver`` / optional ``SqliteSaver`` (with a pure-Python
     fallback if LangGraph is not installed) to persist multi-turn conversation
     state per ``projectId`` / ``thread_id``.
   - Tracks ``pending_batches[thread_id]`` for Human-in-the-loop (HITL) preview
     approval, rejection, and multi-turn refinement of recent effects.
"""

from __future__ import annotations

import asyncio
from copy import deepcopy
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
import uuid
from typing import Any, AsyncIterator

TICKS_PER_SECOND = 10_000_000

DEFAULT_CLI_MODELS = {
    "",
    "default",
    "auto",
    "agy-cli",
    "agy-default",
    "codex-cli",
    "codex-default",
    "claude-code-cli",
    "claude-default",
}


def _agent_dir() -> Path:
    return Path(__file__).resolve().parent


def _mcp_server_script() -> Path:
    return _agent_dir() / "mcp_server.py"


def _probe_http_json(url: str, timeout: float = 0.8) -> dict[str, Any] | None:
    req = urllib.request.Request(url, method="GET")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            if resp.status < 200 or resp.status >= 300:
                return None
            raw = resp.read().decode("utf-8", errors="replace")
            data = json.loads(raw) if raw.strip() else {}
            return data if isinstance(data, dict) else {"data": data}
    except (urllib.error.URLError, TimeoutError, OSError, ValueError):
        return None


def _find_executable(names: list[str], extra_dirs: list[Path]) -> str | None:
    for name in names:
        hit = shutil.which(name)
        if hit:
            return str(Path(hit).resolve())
    for directory in extra_dirs:
        if not directory.is_dir():
            continue
        for name in names:
            candidate = directory / name
            if candidate.is_file():
                return str(candidate.resolve())
    return None


def _windows_common_cli_dirs() -> list[Path]:
    dirs: list[Path] = []
    env = os.environ
    for var, subpaths in (
        ("APPDATA", ["npm"]),
        ("LOCALAPPDATA", ["agy\\bin", "Programs", "Programs\\Antigravity", "Programs\\Antigravity\\bin", "Microsoft\\WinGet\\Links", "npm"]),
        ("USERPROFILE", [".local\\bin", ".claude\\local", ".codex\\bin", ".gemini\\antigravity-cli", ".gemini\\antigravity\\bin"]),
        ("ProgramFiles", ["GitHub CLI", "nodejs", "Google\\Antigravity"]),
        ("ProgramFiles(x86)", ["GitHub CLI"]),
    ):
        base = env.get(var, "").strip()
        if not base:
            continue
        for sub in subpaths:
            dirs.append(Path(base) / sub)
    return dirs


class SubscriptionCliBridge:
    """Discovers local AI subscription CLIs and runs MCP-enabled turns."""

    def __init__(
        self,
        mcp_script_path: str | Path | None = None,
        python_executable: str | None = None,
    ) -> None:
        self.mcp_script_path = Path(mcp_script_path or _mcp_server_script()).resolve()
        self.python_executable = python_executable or sys.executable
        self._last_agy_mcp_sync: tuple[str, str, str, tuple[str, ...]] | None = None

    def discover_local_subscription_clis(self) -> dict[str, Any]:
        """Probe Windows PATH, common install locations, and local model ports."""
        extra_dirs = _windows_common_cli_dirs()

        claude_path = _find_executable(
            ["claude.exe", "claude.cmd", "claude"], extra_dirs
        )
        codex_path = _find_executable(
            ["codex.exe", "codex.cmd", "codex"], extra_dirs
        )
        agy_path = _find_executable(
            ["agy.exe", "agy.cmd", "agy.ps1", "agy"], extra_dirs
        )
        agy_sdk_available = self._check_antigravity_sdk()
        agy_settings_path = Path.home() / ".gemini" / "antigravity-cli" / "settings.json"
        gh_path = _find_executable(["gh.exe", "gh"], extra_dirs)
        gh_copilot_available = False
        if gh_path:
            gh_copilot_available = self._check_gh_copilot(gh_path)

        ollama_data = _probe_http_json("http://127.0.0.1:11434/api/tags")
        ollama_models: list[str] = []
        if ollama_data and isinstance(ollama_data.get("models"), list):
            ollama_models = [
                str(m.get("name", ""))
                for m in ollama_data["models"]
                if isinstance(m, dict) and m.get("name")
            ]

        lm_studio_data = _probe_http_json("http://127.0.0.1:1234/v1/models")
        lm_studio_models: list[str] = []
        if lm_studio_data and isinstance(lm_studio_data.get("data"), list):
            lm_studio_models = [
                str(m.get("id", ""))
                for m in lm_studio_data["data"]
                if isinstance(m, dict) and m.get("id")
            ]

        return {
            "claude": {
                "available": claude_path is not None,
                "path": claude_path,
                "subscription": "Claude Pro / Max (Claude Code CLI)",
            },
            "codex": {
                "available": codex_path is not None,
                "path": codex_path,
                "subscription": "ChatGPT Plus / Pro (OpenAI Codex CLI)",
            },
            "antigravity": {
                "available": agy_path is not None or agy_sdk_available,
                "path": agy_path,
                "sdk_available": agy_sdk_available,
                "settings_path": str(agy_settings_path) if agy_settings_path.is_file() else None,
                "subscription": "Google Antigravity CLI (agy) / Python SDK",
            },
            "gh_copilot": {
                "available": gh_path is not None and gh_copilot_available,
                "gh_path": gh_path,
                "copilot_extension": gh_copilot_available,
                "subscription": "GitHub Copilot (gh copilot)",
            },
            "ollama": {
                "available": ollama_data is not None,
                "endpoint": "http://127.0.0.1:11434/v1",
                "models": ollama_models,
            },
            "lm_studio": {
                "available": lm_studio_data is not None,
                "endpoint": "http://127.0.0.1:1234/v1",
                "models": lm_studio_models,
            },
        }

    @staticmethod
    def _check_antigravity_sdk() -> bool:
        try:
            import importlib.util

            return importlib.util.find_spec("google.antigravity") is not None
        except Exception:
            return False

    @staticmethod
    def _check_gh_copilot(gh_path: str) -> bool:
        try:
            kwargs: dict[str, Any] = {
                "stdin": subprocess.DEVNULL,
                "capture_output": True,
                "text": True,
                "timeout": 2.5,
            }
            if os.name == "nt":
                kwargs["creationflags"] = subprocess.CREATE_NO_WINDOW
            proc = subprocess.run([gh_path, "copilot", "--help"], **kwargs)
            return proc.returncode == 0
        except (OSError, subprocess.SubprocessError):
            return False

    def _mcp_command_and_args(
        self, project_dir: str, callback_url: str = ""
    ) -> tuple[str, list[str]]:
        resolved_project = str(Path(project_dir).resolve())
        if getattr(sys, "frozen", False):
            cmd_exe = sys.executable
            args = ["--mcp", "--project-dir", resolved_project]
        else:
            cmd_exe = self.python_executable
            args = [str(self.mcp_script_path), "--project-dir", resolved_project]
        if callback_url.strip():
            args.extend(["--callback-url", callback_url.strip()])
        return cmd_exe, args

    def generate_mcp_config_file(
        self, project_dir: str, callback_url: str = ""
    ) -> str:
        """Write a temporary ``frametrail-mcp-config.json`` pointing to the MCP server."""
        resolved_project = str(Path(project_dir).resolve())
        cmd_exe, args = self._mcp_command_and_args(resolved_project, callback_url)

        config = {
            "mcpServers": {
                "frametrail": {
                    "command": cmd_exe,
                    "args": args,
                    "env": {
                        "FRAMETRAIL_PROJECT_DIR": resolved_project,
                        **(
                            {"FRAMETRAIL_CALLBACK_URL": callback_url.strip()}
                            if callback_url.strip()
                            else {}
                        ),
                    },
                }
            }
        }

        target_dir = Path(tempfile.gettempdir()) / "FrameTrail" / "mcp-configs"
        target_dir.mkdir(parents=True, exist_ok=True)
        fd, tmp_path = tempfile.mkstemp(
            prefix="frametrail-mcp-config-", suffix=".json", dir=str(target_dir)
        )
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(config, fh, indent=2, ensure_ascii=False)
        return tmp_path

    def _build_prompt_with_context(
        self,
        prompt: str,
        thread_id: str,
        project_dir: str = "",
        playhead_sec: float = 0.0,
        timeline_overview: dict[str, Any] | None = None,
    ) -> str:
        lines = [
            "You are FrameTrail AI Editing Copilot running inside the FrameTrail desktop video editor.",
            f"Active project directory: {str(Path(project_dir).resolve()) if project_dir else '.'}",
            f"Current playhead: {float(playhead_sec or 0.0):.2f}s",
        ]
        if isinstance(timeline_overview, dict):
            total_dur = timeline_overview.get("totalDurationSec") or timeline_overview.get("total_duration_sec")
            clips = timeline_overview.get("clips") or []
            effects = timeline_overview.get("effects") or []
            canvas = timeline_overview.get("canvas") or {}
            clip_bits = []
            for idx, c in enumerate(clips[:12], start=1):
                if isinstance(c, dict):
                    cid = c.get("id", f"clip-{idx}")
                    cs = c.get("startSec", c.get("start_sec", 0))
                    cd = c.get("durationSec", c.get("duration_sec", 0))
                    cr = c.get("rate", 1.0)
                    clip_bits.append(f"#{idx} {cid} ({cs:.2f}s..{float(cs)+float(cd):.2f}s, {cr}x)")
            lines.append(
                f"Timeline overview: duration={total_dur}s, canvas={canvas.get('width', '?')}x{canvas.get('height', '?')}, "
                f"clips=[{', '.join(clip_bits)}], effects={len(effects)}."
            )

        lines.extend(
            [
                "Available FrameTrail MCP server ('frametrail') tools:",
                "- frametrail_get_timeline: inspect clips, effects, canvas, markers",
                "- frametrail_add_zoom: add/update zoom effect",
                "- frametrail_add_focus_or_mask: add spotlight focus blur or privacy mask",
                "- frametrail_add_3d_tilt: add 3D perspective tilt",
                "- frametrail_add_transition: add or update a clip-to-clip transition between adjacent clips (from_clip_index=1 for seam between clip #1 and #2; styles: crossfade, fadeBlack, fadeWhite, slideLeft, slideRight, wipeLeft, wipeRight, zoomIn; duration_sec=0.1..5.0)",
                "- frametrail_remove_transition: remove a clip-to-clip transition by from_clip_index or effect_id",
                "- frametrail_set_clip_speed: set clip speed (0.25x..4.0x)",
                "- frametrail_cut_range: split/remove a time range",
                "- frametrail_update_canvas: change aspect ratio, padding, corner radius, frame style",
                "- frametrail_list_whisper_models: inspect local Whisper speech-to-text models ('small-q5', 'large-v3-turbo-q5_0') and whether any is installed",
                "- frametrail_download_whisper_model: download a local Whisper model via FrameTrail (automatically asks the user for confirmation in Review mode before downloading)",
                "- frametrail_generate_subtitles: transcribe speech from project audio/video tracks (whole timeline, 1-based clip_indices like [2, 3], or [start_sec, end_sec]) and apply styled subtitles",
                "- frametrail_upsert_subtitle: add or update a single subtitle cue (text and optional secondary_text) over [start_sec, end_sec]",
                "- frametrail_update_subtitle_style: customize subtitle preset (classicOutline/darkPill/karaokePop/bilingualClean/custom), font size, vertical position, colors, background pill, karaoke highlight, or bilingual display",
                "- frametrail_delete_subtitles: remove subtitles by subtitle_ids, by [start_sec, end_sec], or clear the entire subtitle track",
                "Strict operating rules:",
                "1. Stay strictly inside the active FrameTrail project and the 'frametrail' MCP tools. "
                "NEVER inspect, grep, read, or search FrameTrail application source code, binaries, project files, or directories outside the 'frametrail' MCP tools (do NOT call read_file/grep/shell to debug MCP errors).",
                "2. For speech-to-subtitle requests (including specific clips like '第2段和第3段' -> clip_indices=[2, 3] or placing subtitles at the bottom -> position_y=0.88), verify the actual clip count from Timeline overview or frametrail_get_timeline FIRST. If the user references a clip index beyond the timeline's clip count (for example clip #3 when only 2 clips exist), do NOT pass out-of-range clip_indices and do NOT call read_file — clearly tell the user how many clips exist (with their time ranges) and ask whether to transcribe the available clip(s), or transcribe only the explicitly valid clip(s) if unambiguous.",
                "3. When generating subtitles and no local Whisper model is installed yet (either detected via frametrail_list_whisper_models or reported by frametrail_generate_subtitles), do NOT merely tell the user to download it manually — call frametrail_download_whisper_model directly (defaulting to the selected/recommended model unless the user prefers 'small-q5'). FrameTrail automatically enforces the user's Review confirmation permission before downloading. If the download succeeds, immediately continue by calling frametrail_generate_subtitles; if the user declines the download, stop and acknowledge cleanly without retrying.",
                "4. If any frametrail_* MCP tool returns an unrecoverable error, or if the user requests a capability that FrameTrail's MCP tools genuinely do NOT support, "
                "do NOT run exploratory read_file/shell/search commands looking for workarounds. Immediately stop and give a clear, concise reply in the user's language explaining the exact error or limitation.",
                "5. Keep tool calls minimal and reply concisely in the user's language.",
            ]
        )

        history = checkpoint_manager.get_thread_history(thread_id)
        if history:
            recent = history[-4:]
            lines.append("Previous conversation context in this FrameTrail editing thread:")
            for item in recent:
                role = item.get("role", "user")
                content = str(item.get("content", "")).strip()
                if content:
                    lines.append(f"- {role}: {content}")
        last_eff = checkpoint_manager.get_last_effect(thread_id)
        if last_eff:
            lines.append(
                f"Most recent effect on thread: {json.dumps(last_eff, ensure_ascii=False)}"
            )
        lines.append(f"Current user request: {prompt}")
        return "\n".join(lines)

    @staticmethod
    def _wrap_exe(exe: str) -> list[str]:
        if exe.lower().endswith(".ps1"):
            return ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", exe]
        return [exe]

    @staticmethod
    def _kill_process_tree_sync(pid: int | None) -> None:
        if not pid or pid <= 0:
            return
        if os.name == "nt":
            try:
                subprocess.run(
                    ["taskkill", "/F", "/T", "/PID", str(pid)],
                    stdin=subprocess.DEVNULL,
                    capture_output=True,
                    timeout=5.0,
                    creationflags=subprocess.CREATE_NO_WINDOW,
                )
                return
            except (OSError, subprocess.SubprocessError):
                pass
        try:
            os.kill(pid, 9)
        except OSError:
            pass

    @classmethod
    async def _kill_async_proc(cls, proc: asyncio.subprocess.Process | None) -> None:
        if proc is None or proc.returncode is not None:
            return
        pid = proc.pid
        try:
            if os.name == "nt" and pid:
                await asyncio.to_thread(cls._kill_process_tree_sync, pid)
            else:
                proc.kill()
        except ProcessLookupError:
            return
        except Exception:
            try:
                proc.kill()
            except Exception:
                pass
        try:
            await asyncio.wait_for(proc.wait(), timeout=5.0)
        except Exception:
            pass

    def _sync_agy_mcp_server(self, agy_exe: str, project_dir: str, callback_url: str = "") -> None:
        """Register or update the FrameTrail stdio MCP server in Antigravity CLI (`agy mcp add`)."""
        resolved_project = str(Path(project_dir).resolve())
        mcp_cmd, mcp_args = self._mcp_command_and_args(resolved_project, callback_url)
        cache_key = (agy_exe, resolved_project, callback_url.strip(), tuple(mcp_args))
        if self._last_agy_mcp_sync == cache_key:
            return

        add_cmd = [
            *self._wrap_exe(agy_exe),
            "mcp",
            "add",
            "-t",
            "stdio",
            "-e",
            f"FRAMETRAIL_PROJECT_DIR={resolved_project}",
        ]
        if callback_url.strip():
            add_cmd.extend(["-e", f"FRAMETRAIL_CALLBACK_URL={callback_url.strip()}"])
        add_cmd.extend(["frametrail", "--", mcp_cmd, *mcp_args])

        kwargs: dict[str, Any] = {
            "stdin": subprocess.DEVNULL,
            "capture_output": True,
            "text": True,
            "timeout": 10.0,
        }
        if os.name == "nt":
            kwargs["creationflags"] = subprocess.CREATE_NO_WINDOW
        try:
            res = subprocess.run(add_cmd, **kwargs)
            if res.returncode == 0:
                self._last_agy_mcp_sync = cache_key
        except (OSError, subprocess.SubprocessError):
            pass

    def _build_cli_invocation(
        self,
        cli_kind: str,
        prompt: str,
        project_dir: str,
        mcp_config_path: str,
        callback_url: str = "",
        model_name: str = "",
        stream: bool = False,
        timeout_sec: float = 900.0,
    ) -> tuple[list[str], dict[str, str]]:
        discovered = self.discover_local_subscription_clis()
        kind = cli_kind.strip().lower()
        env = dict(os.environ)
        env["FRAMETRAIL_PROJECT_DIR"] = str(Path(project_dir).resolve())
        cleaned_model = (model_name or "").strip()
        has_custom_model = (
            bool(cleaned_model)
            and cleaned_model.lower() not in DEFAULT_CLI_MODELS
        )

        if kind in ("claude", "claude_code", "claude-code"):
            exe = discovered["claude"]["path"]
            if not exe:
                raise FileNotFoundError(
                    "Claude Code CLI ('claude') was not found on this machine."
                )
            cmd = [
                *self._wrap_exe(exe),
                "-p",
                prompt,
                "--output-format",
                "stream-json" if stream else "json",
                "--mcp-config",
                mcp_config_path,
            ]
            if stream:
                cmd.append("--verbose")
            if has_custom_model:
                cmd.extend(["--model", cleaned_model])
            return cmd, env

        if kind in ("codex", "openai_codex", "codex-cli"):
            exe = discovered["codex"]["path"]
            if not exe:
                raise FileNotFoundError(
                    "OpenAI Codex CLI ('codex') was not found on this machine."
                )
            # Codex CLI supports non-interactive `codex exec --json` and MCP server overrides via -c
            mcp_cmd, mcp_args = self._mcp_command_and_args(project_dir, callback_url)
            mcp_args_json = json.dumps(mcp_args)
            cmd = [
                *self._wrap_exe(exe),
                "exec",
                "--json",
                "-c",
                f"mcp_servers.frametrail.command={json.dumps(mcp_cmd)}",
                "-c",
                f"mcp_servers.frametrail.args={mcp_args_json}",
            ]
            if has_custom_model:
                cmd.extend(["-m", cleaned_model])
            cmd.append(prompt)
            return cmd, env

        if kind in ("agy", "antigravity", "antigravity_cli", "antigravity-cli"):
            exe = discovered["antigravity"]["path"]
            if exe:
                self._sync_agy_mcp_server(exe, project_dir, callback_url)
                agy_timeout_s = max(15, int(timeout_sec) - 5)
                cmd = [
                    *self._wrap_exe(exe),
                    "-p",
                    prompt,
                    "--output-format",
                    "stream-json" if stream else "json",
                    "--dangerously-skip-permissions",
                    "--disable-slash-commands",
                    "--print-timeout",
                    f"{agy_timeout_s}s",
                ]
                if has_custom_model:
                    cmd.extend(["--model", cleaned_model])
                return cmd, env
            if discovered["antigravity"].get("sdk_available"):
                sdk_runner = (
                    "import asyncio, json, sys\n"
                    "from google.antigravity import Agent, LocalAgentConfig, CapabilitiesConfig\n"
                    "async def _run():\n"
                    "    cfg = LocalAgentConfig(capabilities=CapabilitiesConfig())\n"
                    "    async with Agent(cfg) as ag:\n"
                    "        resp = await ag.chat(sys.argv[1])\n"
                    "        chunks = [t async for t in resp]\n"
                    "        tools = [{'name': c.name, 'args': c.args} async for c in getattr(resp, 'tool_calls', [])]\n"
                    "        print(json.dumps({'result': ''.join(chunks), 'tool_calls': tools}, ensure_ascii=False))\n"
                    "asyncio.run(_run())\n"
                )
                return [self.python_executable, "-c", sdk_runner, prompt], env
            raise FileNotFoundError(
                "Antigravity CLI ('agy') or 'google-antigravity' Python SDK was not found on this machine."
            )

        if kind in ("gh", "gh_copilot", "copilot"):
            exe = discovered["gh_copilot"]["gh_path"]
            if not exe:
                raise FileNotFoundError(
                    "GitHub CLI ('gh') with Copilot was not found on this machine."
                )
            cmd = [exe, "copilot", "suggest", "-t", "shell", prompt]
            return cmd, env

        raise ValueError(
            f"Unsupported subscription CLI '{cli_kind}'. Expected 'claude', 'codex', 'agy', or 'gh'."
        )

    @staticmethod
    def _clean_stderr_summary(stderr: str) -> str:
        lines = [
            ln.strip()
            for ln in (stderr or "").splitlines()
            if ln.strip() and not ln.strip().lower().startswith("warning:")
        ]
        if not lines:
            return (stderr or "").strip()[:300]
        return " | ".join(lines[-3:])[:300]

    @staticmethod
    def _format_tool_summary(tool_name: str, params: Any, output: Any = None, error: Any = None) -> str:
        if error:
            return str(error).strip()[:160]
        if output is not None:
            text = output if isinstance(output, str) else json.dumps(output, ensure_ascii=False)
            compact = " ".join(text.split())
            return compact[:160] if compact else "Completed"
        if isinstance(params, dict):
            if tool_name == "call_mcp_tool":
                inner_args = params.get("Arguments")
                if inner_args:
                    return json.dumps(inner_args, ensure_ascii=False)[:140]
                return str(params.get("ToolName") or "frametrail")
            path_val = params.get("Path") or params.get("path") or params.get("CommandLine")
            if path_val:
                return str(path_val)[:140]
            if params:
                return json.dumps(params, ensure_ascii=False)[:140]
        return ""

    @classmethod
    def _parse_stream_json_line(cls, line: str) -> list[dict[str, Any]]:
        """Parse one NDJSON line from agy/codex/claude into normalized stream events."""
        stripped = (line or "").strip()
        if not stripped:
            return []
        try:
            evt = json.loads(stripped)
        except ValueError:
            return [{"type": "token", "text": stripped + "\n"}]

        if not isinstance(evt, dict):
            return []

        out: list[dict[str, Any]] = []

        # 1. Antigravity CLI (`agy --output-format stream-json`)
        agy_event = evt.get("event")
        if agy_event == "init":
            cid = evt.get("conversation_id")
            out.append({"type": "init", "session_id": cid})
            return out

        if agy_event == "step_update" and isinstance(evt.get("step_update"), dict):
            su = evt["step_update"]
            cid = su.get("conversation_id")
            if cid:
                out.append({"type": "session", "session_id": cid})
            step_type = str(su.get("step_type") or "")
            state = str(su.get("state") or "").upper()

            if step_type == "tool":
                raw_tool_name = str(su.get("tool_name") or "tool")
                tool_info = su.get("tool_info") if isinstance(su.get("tool_info"), dict) else {}
                params = tool_info.get("parameters") if isinstance(tool_info.get("parameters"), dict) else {}
                display_name = (
                    str(params.get("ToolName"))
                    if raw_tool_name == "call_mcp_tool" and params.get("ToolName")
                    else raw_tool_name
                )
                if state == "ACTIVE":
                    out.append(
                        {
                            "type": "tool_start",
                            "tool_name": display_name,
                            "summary": cls._format_tool_summary(raw_tool_name, params),
                        }
                    )
                elif state in ("DONE", "ERROR", "FAILED", "CANCELLED"):
                    err_val = tool_info.get("error") or (
                        "Tool failed" if state != "DONE" or tool_info.get("is_error") else None
                    )
                    succeeded = state == "DONE" and not err_val
                    out.append(
                        {
                            "type": "tool_end",
                            "tool_name": display_name,
                            "summary": cls._format_tool_summary(
                                raw_tool_name,
                                params,
                                output=tool_info.get("output"),
                                error=err_val,
                            ),
                            "succeeded": succeeded,
                        }
                    )
                return out

            if step_type == "agent_response":
                delta = su.get("text_delta")
                if isinstance(delta, str) and delta:
                    out.append({"type": "token", "text": delta})
                return out

            return out

        if agy_event == "result" and isinstance(evt.get("result"), dict):
            res = evt["result"]
            status = str(res.get("status") or "").upper()
            out.append(
                {
                    "type": "result",
                    "session_id": res.get("conversation_id"),
                    "response": str(res.get("response") or ""),
                    "ok": status in ("", "SUCCESS", "OK"),
                    "error": res.get("error") or (None if status in ("", "SUCCESS", "OK") else f"agy status: {status}"),
                }
            )
            return out

        # 2. OpenAI Codex CLI (`codex exec --json`) & Claude Code CLI (`claude --output-format stream-json`)
        etype = str(evt.get("type") or "")
        sid = evt.get("session_id") or evt.get("thread_id") or evt.get("conversation_id")
        if sid:
            out.append({"type": "session", "session_id": sid})

        if etype in ("item.started", "item.completed") and isinstance(evt.get("item"), dict):
            item = evt["item"]
            itype = str(item.get("type") or "")
            if itype in ("mcp_tool_call", "function_call", "command_execution"):
                tname = str(item.get("tool") or item.get("name") or itype)
                if etype == "item.started":
                    out.append(
                        {
                            "type": "tool_start",
                            "tool_name": tname,
                            "summary": str(item.get("command") or item.get("arguments") or "")[:140],
                        }
                    )
                else:
                    ok = item.get("status") not in ("failed", "error") and item.get("exit_code", 0) in (0, None)
                    out.append(
                        {
                            "type": "tool_end",
                            "tool_name": tname,
                            "summary": str(item.get("output") or item.get("result") or item.get("error") or "")[:160],
                            "succeeded": ok,
                        }
                    )
            elif etype == "item.completed" and isinstance(item.get("text"), str) and item["text"]:
                out.append({"type": "token", "text": item["text"]})
            return out

        if etype == "assistant" and isinstance(evt.get("message"), dict):
            content = evt["message"].get("content")
            if isinstance(content, list):
                for block in content:
                    if not isinstance(block, dict):
                        continue
                    btype = block.get("type")
                    if btype == "tool_use":
                        out.append(
                            {
                                "type": "tool_start",
                                "tool_name": str(block.get("name") or "tool"),
                                "summary": json.dumps(block.get("input", {}), ensure_ascii=False)[:140],
                            }
                        )
                    elif btype == "text" and block.get("text"):
                        out.append({"type": "token", "text": str(block["text"])})
            return out

        if etype == "result" or ("response" in evt and "status" in evt):
            text = str(evt.get("result") or evt.get("response") or "")
            is_err = bool(evt.get("is_error")) or str(evt.get("status", "")).upper() in ("ERROR", "FAILED")
            out.append(
                {
                    "type": "result",
                    "session_id": sid,
                    "response": text,
                    "ok": not is_err,
                    "error": evt.get("error") or (text if is_err else None),
                }
            )
            return out

        if isinstance(evt.get("text"), str) and evt["text"]:
            out.append({"type": "token", "text": evt["text"]})
        elif isinstance(evt.get("result"), str) and evt["result"]:
            out.append({"type": "token", "text": evt["result"]})
        return out

    @staticmethod
    def _normalize_cli_output(
        cli_kind: str,
        stdout: str,
        stderr: str,
        returncode: int,
        thread_id: str,
        mcp_config_path: str,
    ) -> dict[str, Any]:
        tool_calls: list[dict[str, Any]] = []
        session_id: str | None = None
        response_text = ""
        parsed_raw: Any = None
        result_error: str | None = None

        text = stdout.strip()
        if text:
            try:
                parsed = json.loads(text)
                parsed_raw = parsed
                if isinstance(parsed, dict):
                    session_id = parsed.get("session_id") or parsed.get("sessionId") or parsed.get("conversation_id")
                    response_text = str(
                        parsed.get("result")
                        or parsed.get("response")
                        or parsed.get("text")
                        or parsed.get("content")
                        or parsed.get("message")
                        or parsed.get("output")
                        or ""
                    )
                    for key in ("tool_uses", "tool_calls", "toolCalls"):
                        if isinstance(parsed.get(key), list):
                            tool_calls.extend(parsed[key])
            except ValueError:
                # Try NDJSON stream lines (e.g. agy stream-json, codex exec --json, claude stream-json)
                events: list[Any] = []
                streamed_chunks: list[str] = []
                final_result_text = ""
                for line in text.splitlines():
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        events.append(json.loads(line))
                    except ValueError:
                        pass
                    for item in SubscriptionCliBridge._parse_stream_json_line(line):
                        itype = item.get("type")
                        if item.get("session_id"):
                            session_id = session_id or str(item["session_id"])
                        if itype == "tool_end":
                            tool_calls.append(
                                {
                                    "tool_name": item.get("tool_name", "mcp_tool"),
                                    "summary": item.get("summary", ""),
                                    "succeeded": bool(item.get("succeeded", True)),
                                }
                            )
                        elif itype == "token" and item.get("text"):
                            streamed_chunks.append(str(item["text"]))
                        elif itype == "result":
                            if item.get("response"):
                                final_result_text = str(item["response"])
                            if item.get("ok") is False and item.get("error"):
                                result_error = str(item["error"])
                parsed_raw = events if events else text
                response_text = (final_result_text or "".join(streamed_chunks)).strip() or text

        ok = returncode == 0 and result_error is None
        clean_stderr = SubscriptionCliBridge._clean_stderr_summary(stderr)
        error = None if ok else (result_error or clean_stderr or f"{cli_kind} exited with code {returncode}")
        if not response_text and not ok:
            response_text = error or ""

        return {
            "ok": ok,
            "cli": cli_kind,
            "thread_id": thread_id,
            "response": response_text,
            "tool_calls": tool_calls,
            "session_id": session_id,
            "mcp_config_path": mcp_config_path,
            "raw": parsed_raw,
            "stderr": stderr.strip(),
            "returncode": returncode,
            "error": error,
        }

    @staticmethod
    def _is_transient_cli_transport_error(detail: str) -> bool:
        low = (detail or "").strip().lower()
        if not low:
            return False
        markers = (
            ": eof",
            "unexpected eof",
            "connection reset",
            "broken pipe",
            "tls handshake timeout",
            "i/o timeout",
            "http2: client connection lost",
            "status code 502",
            "status code 503",
            "status code 504",
            "502 bad gateway",
            "503 service unavailable",
            "504 gateway timeout",
        )
        return any(m in low for m in markers)

    @staticmethod
    def _is_read_only_tool_name(tool_name: str) -> bool:
        low = (tool_name or "").strip().lower()
        return low in (
            "frametrail_get_timeline",
            "frametrail_list_whisper_models",
            "read_file",
            "view_file",
            "list_dir",
            "find_by_name",
            "grep_search",
        )

    def run_subscription_cli_turn(
        self,
        cli_kind: str,
        prompt: str,
        project_dir: str,
        thread_id: str = "default",
        callback_url: str = "",
        timeout_sec: float = 900.0,
        model_name: str = "",
        playhead_sec: float = 0.0,
        timeline_overview: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        """Synchronously execute one turn against a local subscription CLI."""
        mcp_config_path = self.generate_mcp_config_file(project_dir, callback_url)
        full_prompt = self._build_prompt_with_context(
            prompt,
            thread_id,
            project_dir=project_dir,
            playhead_sec=playhead_sec,
            timeline_overview=timeline_overview,
        )
        try:
            cmd, env = self._build_cli_invocation(
                cli_kind,
                full_prompt,
                project_dir,
                mcp_config_path,
                callback_url,
                model_name=model_name,
                stream=False,
                timeout_sec=timeout_sec,
            )
        except (FileNotFoundError, ValueError) as exc:
            return {
                "ok": False,
                "cli": cli_kind,
                "thread_id": thread_id,
                "response": "",
                "tool_calls": [],
                "session_id": None,
                "mcp_config_path": mcp_config_path,
                "raw": None,
                "stderr": "",
                "returncode": -1,
                "error": str(exc),
            }

        checkpoint_manager.record_turn(thread_id, "user", prompt)
        popen_kwargs: dict[str, Any] = {
            "cwd": str(Path(project_dir).resolve()),
            "env": env,
            "stdin": subprocess.DEVNULL,
            "stdout": subprocess.PIPE,
            "stderr": subprocess.PIPE,
            "text": True,
            "encoding": "utf-8",
            "errors": "replace",
        }
        if os.name == "nt":
            popen_kwargs["creationflags"] = subprocess.CREATE_NO_WINDOW

        proc: subprocess.Popen[str] | None = None
        try:
            proc = subprocess.Popen(cmd, **popen_kwargs)
            stdout_s, stderr_s = proc.communicate(timeout=timeout_sec)
            normalized = self._normalize_cli_output(
                cli_kind,
                stdout_s or "",
                stderr_s or "",
                proc.returncode or 0,
                thread_id,
                mcp_config_path,
            )
        except subprocess.TimeoutExpired:
            partial_err = ""
            if proc is not None:
                self._kill_process_tree_sync(proc.pid)
                try:
                    _, err_tail = proc.communicate(timeout=3.0)
                    partial_err = self._clean_stderr_summary(err_tail or "")
                except Exception:
                    pass
            detail = f"{cli_kind} timed out after {timeout_sec:.0f}s."
            if partial_err:
                detail = f"{detail} ({partial_err})"
            normalized = {
                "ok": False,
                "cli": cli_kind,
                "thread_id": thread_id,
                "response": "",
                "tool_calls": [],
                "session_id": None,
                "mcp_config_path": mcp_config_path,
                "raw": None,
                "stderr": partial_err,
                "returncode": -1,
                "error": detail,
            }
        except OSError as exc:
            normalized = {
                "ok": False,
                "cli": cli_kind,
                "thread_id": thread_id,
                "response": "",
                "tool_calls": [],
                "session_id": None,
                "mcp_config_path": mcp_config_path,
                "raw": None,
                "stderr": "",
                "returncode": -1,
                "error": str(exc),
            }

        if normalized["ok"]:
            checkpoint_manager.record_turn(
                thread_id,
                "assistant",
                normalized["response"],
                tool_calls=normalized["tool_calls"],
            )
        return normalized

    async def arun_subscription_cli_turn(
        self,
        cli_kind: str,
        prompt: str,
        project_dir: str,
        thread_id: str = "default",
        callback_url: str = "",
        timeout_sec: float = 900.0,
        model_name: str = "",
        playhead_sec: float = 0.0,
        timeline_overview: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        """Asynchronously execute one turn via ``asyncio.create_subprocess_exec``."""
        mcp_config_path = self.generate_mcp_config_file(project_dir, callback_url)
        full_prompt = self._build_prompt_with_context(
            prompt,
            thread_id,
            project_dir=project_dir,
            playhead_sec=playhead_sec,
            timeline_overview=timeline_overview,
        )
        try:
            cmd, env = await asyncio.to_thread(
                self._build_cli_invocation,
                cli_kind,
                full_prompt,
                project_dir,
                mcp_config_path,
                callback_url,
                model_name,
                False,
                timeout_sec,
            )
        except (FileNotFoundError, ValueError) as exc:
            return {
                "ok": False,
                "cli": cli_kind,
                "thread_id": thread_id,
                "response": "",
                "tool_calls": [],
                "session_id": None,
                "mcp_config_path": mcp_config_path,
                "raw": None,
                "stderr": "",
                "returncode": -1,
                "error": str(exc),
            }

        checkpoint_manager.record_turn(thread_id, "user", prompt)
        create_kwargs: dict[str, Any] = {
            "cwd": str(Path(project_dir).resolve()),
            "env": env,
            "stdin": asyncio.subprocess.DEVNULL,
            "stdout": asyncio.subprocess.PIPE,
            "stderr": asyncio.subprocess.PIPE,
            "limit": 8 * 1024 * 1024,
        }
        if os.name == "nt":
            create_kwargs["creationflags"] = subprocess.CREATE_NO_WINDOW

        proc: asyncio.subprocess.Process | None = None
        try:
            proc = await asyncio.create_subprocess_exec(*cmd, **create_kwargs)
            stdout_b, stderr_b = await asyncio.wait_for(
                proc.communicate(), timeout=timeout_sec
            )
            normalized = self._normalize_cli_output(
                cli_kind,
                stdout_b.decode("utf-8", errors="replace"),
                stderr_b.decode("utf-8", errors="replace"),
                proc.returncode or 0,
                thread_id,
                mcp_config_path,
            )
        except asyncio.TimeoutError:
            await self._kill_async_proc(proc)
            normalized = {
                "ok": False,
                "cli": cli_kind,
                "thread_id": thread_id,
                "response": "",
                "tool_calls": [],
                "session_id": None,
                "mcp_config_path": mcp_config_path,
                "raw": None,
                "stderr": "",
                "returncode": -1,
                "error": f"{cli_kind} timed out after {timeout_sec:.0f}s.",
            }
        except asyncio.CancelledError:
            await self._kill_async_proc(proc)
            raise
        except OSError as exc:
            normalized = {
                "ok": False,
                "cli": cli_kind,
                "thread_id": thread_id,
                "response": "",
                "tool_calls": [],
                "session_id": None,
                "mcp_config_path": mcp_config_path,
                "raw": None,
                "stderr": "",
                "returncode": -1,
                "error": str(exc),
            }

        if normalized["ok"]:
            checkpoint_manager.record_turn(
                thread_id,
                "assistant",
                normalized["response"],
                tool_calls=normalized["tool_calls"],
            )
        return normalized

    async def astream_subscription_cli_turn(
        self,
        cli_kind: str,
        prompt: str,
        project_dir: str,
        thread_id: str = "default",
        callback_url: str = "",
        timeout_sec: float = 900.0,
        model_name: str = "",
        playhead_sec: float = 0.0,
        timeline_overview: dict[str, Any] | None = None,
    ) -> AsyncIterator[dict[str, Any]]:
        """Stream real-time NDJSON events (thinking, tool_start, tool_end, token, done/error) from a local CLI."""
        mcp_config_path = self.generate_mcp_config_file(project_dir, callback_url)
        full_prompt = self._build_prompt_with_context(
            prompt,
            thread_id,
            project_dir=project_dir,
            playhead_sec=playhead_sec,
            timeline_overview=timeline_overview,
        )
        try:
            cmd, env = await asyncio.to_thread(
                self._build_cli_invocation,
                cli_kind,
                full_prompt,
                project_dir,
                mcp_config_path,
                callback_url,
                model_name,
                True,
                timeout_sec,
            )
        except (FileNotFoundError, ValueError) as exc:
            yield {"type": "error", "detail": str(exc)}
            return

        checkpoint_manager.record_turn(thread_id, "user", prompt)
        create_kwargs: dict[str, Any] = {
            "cwd": str(Path(project_dir).resolve()),
            "env": env,
            "stdin": asyncio.subprocess.DEVNULL,
            "stdout": asyncio.subprocess.PIPE,
            "stderr": asyncio.subprocess.PIPE,
            "limit": 8 * 1024 * 1024,
        }
        if os.name == "nt":
            create_kwargs["creationflags"] = subprocess.CREATE_NO_WINDOW

        max_attempts = 2
        for attempt in range(max_attempts):
            proc: asyncio.subprocess.Process | None = None
            stderr_chunks: list[str] = []
            stderr_task: asyncio.Task[None] | None = None

            async def _drain_stderr(stream: asyncio.StreamReader) -> None:
                try:
                    while True:
                        chunk = await stream.read(4096)
                        if not chunk:
                            break
                        stderr_chunks.append(chunk.decode("utf-8", errors="replace"))
                except Exception:
                    pass

            try:
                proc = await asyncio.create_subprocess_exec(*cmd, **create_kwargs)
                assert proc.stdout is not None and proc.stderr is not None
                stderr_task = asyncio.create_task(_drain_stderr(proc.stderr))

                deadline = time.monotonic() + float(timeout_sec)
                streamed_tokens: list[str] = []
                tool_calls: list[dict[str, Any]] = []
                session_id: str | None = None
                final_result_text = ""
                result_error: str | None = None
                mutating_tool_succeeded = False

                while True:
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        raise asyncio.TimeoutError()
                    raw_line = await asyncio.wait_for(proc.stdout.readline(), timeout=remaining)
                    if not raw_line:
                        break
                    line = raw_line.decode("utf-8", errors="replace")
                    for parsed in self._parse_stream_json_line(line):
                        ptype = parsed.get("type")
                        if parsed.get("session_id"):
                            session_id = session_id or str(parsed["session_id"])

                        if ptype == "init":
                            yield {
                                "type": "thinking",
                                "text": f"Local {cli_kind} session connected.",
                            }
                        elif ptype == "tool_start":
                            yield {
                                "type": "tool_start",
                                "tool_name": str(parsed.get("tool_name") or "tool"),
                                "summary": str(parsed.get("summary") or ""),
                            }
                        elif ptype == "tool_end":
                            tname = str(parsed.get("tool_name") or "tool")
                            succeeded = bool(parsed.get("succeeded", True))
                            if succeeded and not self._is_read_only_tool_name(tname):
                                mutating_tool_succeeded = True
                            rec = {
                                "tool_name": tname,
                                "summary": str(parsed.get("summary") or ""),
                                "succeeded": succeeded,
                            }
                            tool_calls.append(rec)
                            yield {"type": "tool_end", **rec}
                        elif ptype == "token":
                            delta = str(parsed.get("text") or "")
                            if delta:
                                streamed_tokens.append(delta)
                                yield {"type": "token", "text": delta}
                        elif ptype == "result":
                            if parsed.get("response"):
                                final_result_text = str(parsed["response"])
                            if parsed.get("ok") is False and parsed.get("error"):
                                result_error = str(parsed["error"])

                remaining = max(1.0, deadline - time.monotonic())
                await asyncio.wait_for(proc.wait(), timeout=remaining)
                if stderr_task is not None:
                    await asyncio.wait_for(stderr_task, timeout=2.0)

                stderr_text = "".join(stderr_chunks)
                returncode = proc.returncode or 0
                if returncode != 0 or result_error:
                    clean_err = self._clean_stderr_summary(stderr_text)
                    detail = result_error or clean_err or f"{cli_kind} exited with code {returncode}"
                    can_retry = (
                        attempt + 1 < max_attempts
                        and not mutating_tool_succeeded
                        and not streamed_tokens
                        and self._is_transient_cli_transport_error(detail)
                    )
                    if can_retry:
                        yield {
                            "type": "thinking",
                            "text": f"Upstream {cli_kind} connection dropped unexpectedly; retrying automatically...",
                        }
                        await asyncio.sleep(1.5)
                        continue
                    yield {"type": "error", "detail": detail, "tool_calls": tool_calls}
                    return

                reply = (final_result_text or "".join(streamed_tokens)).strip()
                if reply and not streamed_tokens:
                    yield {"type": "token", "text": reply}

                checkpoint_manager.record_turn(
                    thread_id,
                    "assistant",
                    reply,
                    tool_calls=tool_calls,
                )
                yield {
                    "type": "done",
                    "reply": reply,
                    "tool_calls": tool_calls,
                    "session_id": session_id,
                    "proposed_command": None,
                }
                return
            except asyncio.TimeoutError:
                await self._kill_async_proc(proc)
                if stderr_task is not None:
                    stderr_task.cancel()
                clean_err = self._clean_stderr_summary("".join(stderr_chunks))
                detail = f"{cli_kind} timed out after {timeout_sec:.0f}s."
                if clean_err:
                    detail = f"{detail} ({clean_err})"
                yield {"type": "error", "detail": detail}
                return
            except asyncio.CancelledError:
                await self._kill_async_proc(proc)
                if stderr_task is not None:
                    stderr_task.cancel()
                raise
            except OSError as exc:
                await self._kill_async_proc(proc)
                if stderr_task is not None:
                    stderr_task.cancel()
                yield {"type": "error", "detail": str(exc)}
                return


# --- LangGraph Checkpointer & Human-in-the-loop State Manager ---


class _FallbackMemorySaver:
    """Minimal thread-safe checkpointer used when ``langgraph`` is unavailable."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self.storage: dict[str, dict[str, Any]] = {}

    def get(self, config: dict[str, Any]) -> Any:
        thread_id = str(config.get("configurable", {}).get("thread_id", "default"))
        with self._lock:
            return deepcopy(self.storage.get(thread_id))

    def put(self, config: dict[str, Any], checkpoint: Any, metadata: Any = None) -> dict[str, Any]:
        thread_id = str(config.get("configurable", {}).get("thread_id", "default"))
        with self._lock:
            self.storage[thread_id] = deepcopy(
                {"checkpoint": checkpoint, "metadata": metadata or {}}
            )
        return config


def _create_langgraph_checkpointer(db_path: str | None = None) -> Any:
    if db_path:
        try:
            from langgraph.checkpoint.sqlite import SqliteSaver  # type: ignore

            conn = sqlite3.connect(db_path, check_same_thread=False)
            return SqliteSaver(conn)
        except Exception:
            pass

    try:
        from langgraph.checkpoint.memory import MemorySaver  # type: ignore

        return MemorySaver()
    except Exception:
        return _FallbackMemorySaver()


class AgentCheckpointManager:
    """Manages LangGraph checkpointers, multi-turn thread memory, and HITL preview batches."""

    def __init__(self, db_path: str | None = None) -> None:
        self._lock = threading.RLock()
        self._db_path = db_path
        self._checkpointer = _create_langgraph_checkpointer(db_path)
        self.pending_batches: dict[str, dict[str, Any]] = {}
        self.thread_histories: dict[str, list[dict[str, Any]]] = {}
        self.last_effects: dict[str, list[dict[str, Any]]] = {}

    def get_checkpointer(self, db_path: str | None = None) -> Any:
        with self._lock:
            if db_path and db_path != self._db_path:
                self._db_path = db_path
                self._checkpointer = _create_langgraph_checkpointer(db_path)
            return self._checkpointer

    def make_thread_config(
        self, project_id: str, thread_id: str = "default"
    ) -> dict[str, Any]:
        scoped = f"{project_id}:{thread_id}" if project_id else thread_id
        return {"configurable": {"thread_id": scoped}}

    def record_turn(
        self,
        thread_id: str,
        role: str,
        content: str,
        effects: list[dict[str, Any]] | None = None,
        tool_calls: list[dict[str, Any]] | None = None,
    ) -> None:
        with self._lock:
            history = self.thread_histories.setdefault(thread_id, [])
            history.append(
                {
                    "role": role,
                    "content": content,
                    "timestamp": time.time(),
                    "tool_calls": deepcopy(tool_calls or []),
                }
            )
            if len(history) > 100:
                del history[:-100]
            if effects:
                self.last_effects[thread_id] = deepcopy(effects)

    def get_thread_history(self, thread_id: str) -> list[dict[str, Any]]:
        with self._lock:
            return deepcopy(self.thread_histories.get(thread_id, []))

    def get_last_effect(self, thread_id: str) -> dict[str, Any] | None:
        with self._lock:
            effects = self.last_effects.get(thread_id)
            return deepcopy(effects[-1]) if effects else None

    def stage_pending_batch(
        self,
        thread_id: str,
        commands: list[dict[str, Any]],
        summary: str,
        batch_id: str | None = None,
        project_id: str = "",
        snapshot_revision: int | None = None,
        metadata: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        with self._lock:
            bid = batch_id or f"batch-{uuid.uuid4().hex[:10]}"
            extracted_effects = self._extract_effects_from_commands(commands)
            if extracted_effects:
                self.last_effects[thread_id] = deepcopy(extracted_effects)

            batch = {
                "batch_id": bid,
                "thread_id": thread_id,
                "project_id": project_id,
                "status": "pending",
                "summary": summary,
                "commands": deepcopy(commands),
                "snapshot_revision": snapshot_revision,
                "metadata": deepcopy(metadata or {}),
                "created_at": time.time(),
            }
            self.pending_batches[thread_id] = batch
            return deepcopy(batch)

    def get_pending_batch(self, thread_id: str) -> dict[str, Any] | None:
        with self._lock:
            batch = self.pending_batches.get(thread_id)
            return deepcopy(batch) if batch else None

    def approve_pending_batch(
        self, thread_id: str, batch_id: str | None = None
    ) -> dict[str, Any]:
        with self._lock:
            batch = self.pending_batches.get(thread_id)
            if not batch:
                return {
                    "ok": False,
                    "thread_id": thread_id,
                    "error": "No pending preview batch for this thread.",
                }
            if batch_id and batch["batch_id"] != batch_id:
                return {
                    "ok": False,
                    "thread_id": thread_id,
                    "error": f"Pending batch mismatch: expected '{batch['batch_id']}', got '{batch_id}'.",
                }
            approved = deepcopy(batch)
            approved["status"] = "approved"
            approved["resolved_at"] = time.time()
            del self.pending_batches[thread_id]
            extracted = self._extract_effects_from_commands(approved["commands"])
            if extracted:
                self.last_effects[thread_id] = deepcopy(extracted)
            return {"ok": True, "batch": approved}

    def reject_pending_batch(
        self, thread_id: str, batch_id: str | None = None, reason: str = ""
    ) -> dict[str, Any]:
        with self._lock:
            batch = self.pending_batches.get(thread_id)
            if not batch:
                return {
                    "ok": False,
                    "thread_id": thread_id,
                    "error": "No pending preview batch for this thread.",
                }
            if batch_id and batch["batch_id"] != batch_id:
                return {
                    "ok": False,
                    "thread_id": thread_id,
                    "error": f"Pending batch mismatch: expected '{batch['batch_id']}', got '{batch_id}'.",
                }
            rejected = deepcopy(batch)
            rejected["status"] = "rejected"
            rejected["reason"] = reason
            rejected["resolved_at"] = time.time()
            del self.pending_batches[thread_id]
            return {"ok": True, "batch": rejected}

    def refine_last_effect(
        self,
        thread_id: str,
        updates: dict[str, Any],
        effect_id: str | None = None,
    ) -> dict[str, Any]:
        """Refine the most recent effect in ``pending_batches`` or ``last_effects``.

        Supports natural multi-turn follow-ups such as changing a zoom scale
        from 1.6 to 2.2 or shifting focus coordinates without losing prior context.
        """
        with self._lock:
            target_effect: dict[str, Any] | None = None
            pending = self.pending_batches.get(thread_id)

            if pending:
                for cmd in self._iter_leaf_commands(pending["commands"]):
                    if cmd.get("op") == "effectUpsert" and isinstance(cmd.get("effect"), dict):
                        eff = cmd["effect"]
                        if effect_id is None or eff.get("id") == effect_id:
                            target_effect = eff
            if target_effect is None:
                history_effects = self.last_effects.get(thread_id, [])
                for eff in reversed(history_effects):
                    if effect_id is None or eff.get("id") == effect_id:
                        target_effect = deepcopy(eff)
                        break

            if target_effect is None:
                return {
                    "ok": False,
                    "thread_id": thread_id,
                    "error": "No prior effect found on this thread to refine.",
                }

            params = dict(target_effect.get("parameters") or {})
            param_updates = dict(updates.get("parameters") or {})
            for key, value in updates.items():
                if key in ("parameters", "start_sec", "end_sec", "sourceStart", "sourceDuration"):
                    continue
                param_updates[key] = value

            params.update(param_updates)
            params["origin"] = "edited"
            params.pop("plannerState", None)
            target_effect["parameters"] = params

            if "start_sec" in updates:
                start_ticks = int(round(float(updates["start_sec"]) * TICKS_PER_SECOND))
                target_effect["sourceStart"] = {
                    "ticks": start_ticks,
                    "timescale": TICKS_PER_SECOND,
                }
            if "end_sec" in updates:
                start_ticks = int(
                    (target_effect.get("sourceStart") or {}).get("ticks", 0)
                )
                end_ticks = int(round(float(updates["end_sec"]) * TICKS_PER_SECOND))
                if end_ticks <= start_ticks:
                    return {
                        "ok": False,
                        "thread_id": thread_id,
                        "error": "end_sec must be greater than start_sec.",
                    }
                target_effect["sourceDuration"] = {
                    "ticks": end_ticks - start_ticks,
                    "timescale": TICKS_PER_SECOND,
                }

            upsert_cmd = {
                "op": "effectUpsert",
                "effectId": target_effect["id"],
                "effect": deepcopy(target_effect),
            }

            if pending:
                # Pending batch was mutated in-place if target_effect came from it;
                # if it came from last_effects, append/replace in pending batch.
                replaced = False
                for idx, cmd in enumerate(pending["commands"]):
                    if (
                        cmd.get("op") == "effectUpsert"
                        and cmd.get("effectId") == target_effect["id"]
                    ):
                        pending["commands"][idx] = upsert_cmd
                        replaced = True
                        break
                if not replaced:
                    pending["commands"].append(upsert_cmd)
                self.last_effects[thread_id] = [deepcopy(target_effect)]
                return {
                    "ok": True,
                    "thread_id": thread_id,
                    "command": upsert_cmd,
                    "effect": deepcopy(target_effect),
                    "batch": deepcopy(pending),
                }

            batch = self.stage_pending_batch(
                thread_id=thread_id,
                commands=[upsert_cmd],
                summary=f"Refined effect '{target_effect['id']}'.",
            )
            return {
                "ok": True,
                "thread_id": thread_id,
                "command": upsert_cmd,
                "effect": deepcopy(target_effect),
                "batch": batch,
            }

    @classmethod
    def _iter_leaf_commands(cls, commands: list[dict[str, Any]]) -> list[dict[str, Any]]:
        flat: list[dict[str, Any]] = []
        for cmd in commands:
            if cmd.get("op") == "batch" and isinstance(cmd.get("commands"), list):
                flat.extend(cls._iter_leaf_commands(cmd["commands"]))
            else:
                flat.append(cmd)
        return flat

    @classmethod
    def _extract_effects_from_commands(
        cls, commands: list[dict[str, Any]]
    ) -> list[dict[str, Any]]:
        effects: list[dict[str, Any]] = []
        for cmd in cls._iter_leaf_commands(commands):
            if cmd.get("op") == "effectUpsert" and isinstance(cmd.get("effect"), dict):
                effects.append(deepcopy(cmd["effect"]))
        return effects


# Module-level singletons & convenience wrappers for graph.py / server.py
checkpoint_manager = AgentCheckpointManager()
_default_cli_bridge = SubscriptionCliBridge()


def get_checkpointer(db_path: str | None = None) -> Any:
    return checkpoint_manager.get_checkpointer(db_path)


def discover_local_subscription_clis() -> dict[str, Any]:
    return _default_cli_bridge.discover_local_subscription_clis()


def generate_mcp_config_file(project_dir: str, callback_url: str = "") -> str:
    return _default_cli_bridge.generate_mcp_config_file(project_dir, callback_url)


def run_subscription_cli_turn(
    cli_kind: str,
    prompt: str,
    project_dir: str,
    thread_id: str = "default",
    callback_url: str = "",
    model_name: str = "",
    timeout_sec: float = 900.0,
    playhead_sec: float = 0.0,
    timeline_overview: dict[str, Any] | None = None,
) -> dict[str, Any]:
    return _default_cli_bridge.run_subscription_cli_turn(
        cli_kind=cli_kind,
        prompt=prompt,
        project_dir=project_dir,
        thread_id=thread_id,
        callback_url=callback_url,
        timeout_sec=timeout_sec,
        model_name=model_name,
        playhead_sec=playhead_sec,
        timeline_overview=timeline_overview,
    )


async def arun_subscription_cli_turn(
    cli_kind: str,
    prompt: str,
    project_dir: str,
    thread_id: str = "default",
    callback_url: str = "",
    model_name: str = "",
    timeout_sec: float = 900.0,
    playhead_sec: float = 0.0,
    timeline_overview: dict[str, Any] | None = None,
) -> dict[str, Any]:
    return await _default_cli_bridge.arun_subscription_cli_turn(
        cli_kind=cli_kind,
        prompt=prompt,
        project_dir=project_dir,
        thread_id=thread_id,
        callback_url=callback_url,
        timeout_sec=timeout_sec,
        model_name=model_name,
        playhead_sec=playhead_sec,
        timeline_overview=timeline_overview,
    )


async def astream_subscription_cli_turn(
    cli_kind: str,
    prompt: str,
    project_dir: str,
    thread_id: str = "default",
    callback_url: str = "",
    model_name: str = "",
    timeout_sec: float = 900.0,
    playhead_sec: float = 0.0,
    timeline_overview: dict[str, Any] | None = None,
) -> AsyncIterator[dict[str, Any]]:
    async for evt in _default_cli_bridge.astream_subscription_cli_turn(
        cli_kind=cli_kind,
        prompt=prompt,
        project_dir=project_dir,
        thread_id=thread_id,
        callback_url=callback_url,
        timeout_sec=timeout_sec,
        model_name=model_name,
        playhead_sec=playhead_sec,
        timeline_overview=timeline_overview,
    ):
        yield evt
