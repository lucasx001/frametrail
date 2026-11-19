"""Unified frozen entrypoint for FrameTrail AI Agent (LangGraph Sidecar + MCP Server).

When packaged with PyInstaller (--onedir) into ``ai-agent-runtime/frametrail-agent.exe``:
  - Default mode: ``frametrail-agent.exe --host 127.0.0.1 --port 18765``
    Runs the FastAPI + LangGraph SSE sidecar server with zero Python installation required.
  - MCP mode:     ``frametrail-agent.exe --mcp --project-dir <dir> [--callback-url <url>]``
    Runs the standard MCP stdio server for external CLIs (Antigravity agy / Claude Code / Codex).
"""

from __future__ import annotations

import argparse
import os
import sys


def _run_mcp(remaining_argv: list[str]) -> int:
    from agent.mcp_server import main as mcp_main

    return int(mcp_main(remaining_argv) or 0)


def _run_server(host: str, port: int) -> int:
    import uvicorn
    from agent.server import app

    uvicorn.run(
        app,
        host=host,
        port=port,
        log_level="warning",
        log_config=None,
        access_log=False,
    )
    return 0


def main(argv: list[str] | None = None) -> int:
    args_list = list(sys.argv[1:] if argv is None else argv)

    # Support MCP sub-mode: frametrail-agent.exe --mcp --project-dir ...
    if "--mcp" in args_list:
        remaining = [a for a in args_list if a != "--mcp"]
        return _run_mcp(remaining)

    parser = argparse.ArgumentParser(description="FrameTrail AI Agent Sidecar")
    parser.add_argument("--host", default="127.0.0.1", help="Bind host (default: 127.0.0.1)")
    parser.add_argument("--port", type=int, default=18765, help="Bind port (default: 18765)")
    parser.add_argument("--parent-pid", type=int, default=0, help="Optional parent process PID watchdog")
    parsed = parser.parse_args(args_list)

    if parsed.parent_pid > 0 and not os.environ.get("FRAMETRAIL_PARENT_PID"):
        os.environ["FRAMETRAIL_PARENT_PID"] = str(parsed.parent_pid)

    return _run_server(parsed.host, parsed.port)


if __name__ == "__main__":
    raise SystemExit(main())
