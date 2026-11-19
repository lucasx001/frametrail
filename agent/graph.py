"""LangGraph StateGraph for FrameTrail AI video editing with deterministic dry-run self-repair."""

from __future__ import annotations

import json
from typing import Annotated, Any, Literal, NotRequired, Optional, TypedDict

try:
    from langchain_core.language_models.chat_models import BaseChatModel
    from langchain_core.messages import AIMessage, AnyMessage, HumanMessage, SystemMessage, ToolMessage
    from langgraph.graph import END, START, StateGraph
    from langgraph.graph.message import add_messages
    _HAS_LANGGRAPH = True
except ImportError:
    BaseChatModel = Any  # type: ignore[misc,assignment]
    AIMessage = AnyMessage = HumanMessage = SystemMessage = ToolMessage = Any  # type: ignore[misc,assignment]
    END = "__end__"
    START = "__start__"
    StateGraph = None  # type: ignore[assignment]
    def add_messages(left, right):  # type: ignore[misc]
        return (left or []) + (right or [])
    _HAS_LANGGRAPH = False

from agent.editor_tools import (
    TICKS_PER_SECOND,
    get_phase1_tools,
    reset_project_context,
    set_project_context,
)


class AgentState(TypedDict):
    messages: Annotated[list[AnyMessage], add_messages]
    project_snapshot: dict[str, Any]
    project_dir: str
    playhead_sec: float
    api_key: NotRequired[str]
    base_url: NotRequired[str]
    proposed_commands: list[dict[str, Any]]
    tool_calls: list[dict[str, Any]]
    validation_error: Optional[str]
    retry_count: int


def collect_tools() -> list[Any]:
    """Collect Phase 1 editing tools and optionally Phase 2 multimodal tools."""
    tools = list(get_phase1_tools())
    try:
        from agent.multimodal_tools import get_phase2_tools

        tools.extend(get_phase2_tools())
    except ImportError:
        pass
    return tools


def resolve_checkpointer() -> Any:
    """Resolve Phase 3 LangGraph checkpointer if available."""
    try:
        from agent.subscription_mcp import get_checkpointer

        return get_checkpointer()
    except ImportError:
        return None


def _ticks(value: Any) -> int:
    if isinstance(value, (int, float)):
        return int(value)
    if not isinstance(value, dict):
        return 0
    return round(int(value.get("ticks", 0)) * TICKS_PER_SECOND / max(1, int(value.get("timescale", TICKS_PER_SECOND))))


_VALID_SUBTITLE_PRESETS = {"classicOutline", "darkPill", "karaokePop", "bilingualClean", "custom"}
_VALID_HIGHLIGHT_MODES = {"none", "wordColor", "wordPill"}
_VALID_ALIGNMENTS = {"left", "center", "right"}


def _validate_subtitle_style(style: Any) -> Optional[str]:
    if not isinstance(style, dict):
        return "subtitleStyle must be a JSON object."
    preset = style.get("preset")
    if preset is not None and str(preset) not in _VALID_SUBTITLE_PRESETS:
        return f"subtitleStyle.preset '{preset}' is not supported."
    hm = style.get("highlightMode")
    if hm is not None and str(hm) not in _VALID_HIGHLIGHT_MODES:
        return f"subtitleStyle.highlightMode '{hm}' is not supported."
    al = style.get("alignment")
    if al is not None and str(al) not in _VALID_ALIGNMENTS:
        return f"subtitleStyle.alignment '{al}' is not supported."
    for key, low, high in (
        ("fontSize", 8.0, 200.0),
        ("outlineWidth", 0.0, 32.0),
        ("shadowBlur", 0.0, 64.0),
        ("shadowOpacity", 0.0, 1.0),
        ("backgroundOpacity", 0.0, 1.0),
        ("backgroundRadius", 0.0, 100.0),
        ("positionX", 0.0, 1.0),
        ("positionY", 0.0, 1.0),
        ("maxWidthRatio", 0.1, 1.0),
        ("secondaryScale", 0.2, 1.5),
        ("fadeSeconds", 0.0, 5.0),
    ):
        if key in style and style[key] is not None:
            val = float(style[key])
            if val < low or val > high:
                return f"subtitleStyle.{key} ({val}) outside allowed range [{low}, {high}]."
    return None


def _validate_subtitle_effect(eff: Any, total_ticks: int, prefix: str) -> Optional[str]:
    if not isinstance(eff, dict):
        return f"{prefix}: subtitle entry must be an object."
    if eff.get("kind") != "subtitle":
        return f"{prefix}: expected kind='subtitle', got '{eff.get('kind')}'."
    eid = eff.get("id")
    if not isinstance(eid, str) or not eid.strip():
        return f"{prefix}: subtitle effect missing valid 'id'."
    start = _ticks(eff.get("sourceStart", {}))
    dur = _ticks(eff.get("sourceDuration", {}))
    if start < 0 or dur <= 0:
        return f"{prefix}: subtitle '{eid}' must have sourceStart >= 0 and sourceDuration > 0."
    if total_ticks > 0 and (start >= total_ticks or start + dur > total_ticks):
        return f"{prefix}: subtitle '{eid}' range [{start}, {start + dur}] outside sourceDuration {total_ticks}."
    params = eff.get("parameters")
    if not isinstance(params, dict):
        return f"{prefix}: subtitle '{eid}' parameters must be an object."
    text = params.get("text")
    if not isinstance(text, str) or not text.strip():
        return f"{prefix}: subtitle '{eid}' requires non-empty parameters.text."
    if len(text.encode("utf-8")) > 1024:
        return f"{prefix}: subtitle '{eid}' text exceeds 1024 UTF-8 bytes."
    secondary = params.get("secondaryText")
    if secondary is not None:
        if not isinstance(secondary, str) or len(secondary.encode("utf-8")) > 1024:
            return f"{prefix}: subtitle '{eid}' secondaryText exceeds 1024 UTF-8 bytes."
    words = params.get("words")
    if words is not None:
        if not isinstance(words, list) or len(words) > 256:
            return f"{prefix}: subtitle '{eid}' words must be a list of at most 256 entries."
        for w in words:
            if not isinstance(w, dict) or not isinstance(w.get("text"), str):
                return f"{prefix}: subtitle '{eid}' word entry invalid."
            so = float(w.get("startOffset", -1.0))
            eo = float(w.get("endOffset", -1.0))
            if so < 0.0 or eo < so:
                return f"{prefix}: subtitle '{eid}' word offsets [{so}, {eo}] invalid."
    override = params.get("styleOverride")
    if override is not None and not isinstance(override, dict):
        return f"{prefix}: subtitle '{eid}' styleOverride must be an object."
    return None


def validate_commands_dry_run(snapshot: dict[str, Any], commands: list[dict[str, Any]]) -> Optional[str]:
    """Validate proposed FrameTrail commands against native engine invariants."""
    if not commands:
        return None

    total_ticks = _ticks(snapshot.get("sourceDuration", {}))
    segment_ids = {s.get("id") for s in snapshot.get("segments", []) if s.get("id")}
    flat: list[dict[str, Any]] = []

    for cmd in commands:
        if not isinstance(cmd, dict) or "op" not in cmd:
            return "Command missing required 'op' field."
        if cmd["op"] == "batch":
            sub = cmd.get("commands")
            if not isinstance(sub, list) or len(sub) > 100:
                return "Batch command must contain a list of 1..100 commands."
            for inner in sub:
                if not isinstance(inner, dict) or inner.get("op") == "batch":
                    return "Nested batch commands are disallowed."
                flat.append(inner)
        else:
            flat.append(cmd)

    if len(flat) > 100:
        return "Too many commands in a single turn (maximum 100)."

    remaining_segments = set(segment_ids)
    for cmd in flat:
        op = cmd.get("op")
        if op == "rate":
            sid = cmd.get("segmentId")
            num = int(cmd.get("numerator", 0))
            den = int(cmd.get("denominator", 0))
            if sid not in remaining_segments:
                return f"rate: unknown segmentId '{sid}'."
            if num <= 0 or den <= 0 or num * 4 < den or num > den * 4:
                return f"rate: ratio {num}/{den} outside allowed 0.25..4.0 range."
        elif op == "split":
            sid = cmd.get("segmentId")
            new_id = cmd.get("newId")
            if sid not in remaining_segments or not new_id:
                return f"split: invalid segmentId '{sid}' or newId."
            remaining_segments.add(new_id)
        elif op == "delete":
            sid = cmd.get("segmentId")
            if sid not in remaining_segments:
                return f"delete: unknown segmentId '{sid}'."
            if len(remaining_segments) <= 1:
                return "delete: cannot delete the last remaining clip."
            remaining_segments.remove(sid)
        elif op == "trim":
            sid = cmd.get("segmentId")
            s_start = int(cmd.get("sourceStart", -1))
            s_end = int(cmd.get("sourceEnd", -1))
            if sid not in remaining_segments:
                return f"trim: unknown segmentId '{sid}'."
            if s_start < 0 or s_end <= s_start or s_end > total_ticks:
                return f"trim: invalid range [{s_start}, {s_end}] for duration {total_ticks}."
        elif op == "effectUpsert":
            eff = cmd.get("effect", {})
            eid = cmd.get("effectId")
            if not eid or not isinstance(eff, dict) or eff.get("id") != eid:
                return "effectUpsert: effectId must match effect.id."
            kind = eff.get("kind")
            if kind == "subtitle":
                err = _validate_subtitle_effect(eff, total_ticks, "effectUpsert")
                if err:
                    return err
            else:
                start = _ticks(eff.get("sourceStart", {}))
                dur = _ticks(eff.get("sourceDuration", {}))
                if start < 0 or dur <= 0 or start >= total_ticks or start + dur > total_ticks:
                    return f"effectUpsert: time range [{start}, {start + dur}] outside sourceDuration {total_ticks}."
                params = eff.get("parameters", {}) or {}
                if kind in ("focus", "mask", "sticker"):
                    x = float(params.get("x", 0.25))
                    y = float(params.get("y", 0.25))
                    w = float(params.get("width", 0.5))
                    h = float(params.get("height", 0.5))
                    if x < 0 or y < 0 or w <= 0 or h <= 0 or x + w > 1.000001 or y + h > 1.000001:
                        return "effectUpsert: normalized rect must fit inside [0, 1] canvas."
        elif op == "effectDelete":
            eid = cmd.get("effectId")
            if not isinstance(eid, str) or not eid.strip():
                return "effectDelete: effectId must be a non-empty string."
        elif op == "subtitlesReplace":
            subtitles = cmd.get("subtitles")
            if not isinstance(subtitles, list):
                return "subtitlesReplace: 'subtitles' must be a list."
            if len(subtitles) > 10_000:
                return "subtitlesReplace: cannot exceed 10000 subtitle cues."
            has_rs = "rangeStart" in cmd and cmd["rangeStart"] is not None
            has_re = "rangeEnd" in cmd and cmd["rangeEnd"] is not None
            if has_rs != has_re:
                return "subtitlesReplace: rangeStart and rangeEnd must both be provided or both omitted."
            if has_rs and has_re:
                rs = int(cmd["rangeStart"])
                re_val = int(cmd["rangeEnd"])
                if rs < 0 or re_val <= rs or (total_ticks > 0 and re_val > total_ticks):
                    return f"subtitlesReplace: invalid range [{rs}, {re_val}] for sourceDuration {total_ticks}."
            for sub in subtitles:
                err = _validate_subtitle_effect(sub, total_ticks, "subtitlesReplace")
                if err:
                    return err
            if "subtitleStyle" in cmd and cmd["subtitleStyle"] is not None:
                style_err = _validate_subtitle_style(cmd["subtitleStyle"])
                if style_err:
                    return f"subtitlesReplace: {style_err}"
        elif op == "settings":
            canvas = cmd.get("canvas")
            if isinstance(canvas, dict):
                pad = float(canvas.get("padding", 0.05))
                if pad < 0 or pad >= 0.5:
                    return "settings: canvas.padding must be in [0, 0.5)."
            editor = cmd.get("editor")
            if isinstance(editor, dict) and editor.get("subtitleStyle") is not None:
                style_err = _validate_subtitle_style(editor["subtitleStyle"])
                if style_err:
                    return f"settings: {style_err}"

    return None


def build_agent_graph(llm: BaseChatModel) -> Any:
    """Compile the LangGraph editing workflow for a given chat model."""
    tools = collect_tools()
    tool_map = {t.name: t for t in tools}
    llm_with_tools = llm.bind_tools(tools)

    def read_project_context(state: AgentState) -> dict[str, Any]:
        snapshot = state.get("project_snapshot", {}) or {}
        segments = snapshot.get("segments", [])
        effects = snapshot.get("effects", [])
        canvas = snapshot.get("canvas", {}) or {}
        dur_sec = _ticks(snapshot.get("sourceDuration", {})) / TICKS_PER_SECOND
        playhead = float(state.get("playhead_sec", 0.0))

        seg_summary = ", ".join(
            f"{s.get('id')} ({_ticks(s.get('sourceDuration', {})) / TICKS_PER_SECOND:.2f}s @ "
            f"{(s.get('rate', {}) or {}).get('numerator', 1)}/{(s.get('rate', {}) or {}).get('denominator', 1)}x)"
            for s in segments[:12]
        )
        system_prompt = (
            "You are FrameTrail AI Editing Copilot. You edit screen-recording projects by calling tools "
            "that produce validated FrameTrail JSON commands.\n"
            f"Current project: sourceDuration={dur_sec:.2f}s, playhead={playhead:.2f}s, "
            f"canvas={canvas.get('width', 1920)}x{canvas.get('height', 1080)}, "
            f"clips=[{seg_summary}], existing_effects={len(effects)}.\n"
            "Rules:\n"
            "1. Use tools to inspect or modify the timeline. Always keep times within [0, sourceDuration].\n"
            "2. If user mentions 'current time' or omits timestamps, use playhead_sec.\n"
            "3. For speech-to-subtitle requests, call generate_subtitles (pass 1-based clip_indices such as [2, 3] when the user names specific clips like '2nd and 3rd clip'), upsert_subtitle, update_subtitle_style, or delete_subtitles.\n"
            "4. After tool execution succeeds, summarize your edits clearly and concisely."
        )

        msgs = list(state.get("messages", []))
        if not msgs or not isinstance(msgs[0], SystemMessage):
            return {"messages": [SystemMessage(content=system_prompt)]}
        return {}

    async def intent_planner(state: AgentState) -> dict[str, Any]:
        token = set_project_context(
            state.get("project_snapshot", {}),
            state.get("project_dir", ""),
            state.get("playhead_sec", 0.0),
            state.get("api_key", ""),
            state.get("base_url", ""),
        )
        try:
            response = await llm_with_tools.ainvoke(state["messages"])
            return {"messages": [response]}
        finally:
            reset_project_context(token)

    async def tools_node(state: AgentState) -> dict[str, Any]:
        last_msg = state["messages"][-1]
        tool_calls_req = getattr(last_msg, "tool_calls", []) or []
        token = set_project_context(
            state.get("project_snapshot", {}),
            state.get("project_dir", ""),
            state.get("playhead_sec", 0.0),
            state.get("api_key", ""),
            state.get("base_url", ""),
        )
        new_messages: list[ToolMessage] = []
        proposed = list(state.get("proposed_commands", []))
        recorded_calls = list(state.get("tool_calls", []))

        try:
            for tc in tool_calls_req:
                name = tc.get("name", "")
                args = tc.get("args", {}) or {}
                call_id = tc.get("id", name)
                tool_fn = tool_map.get(name)
                if tool_fn is None:
                    err = f"Unknown tool '{name}'."
                    new_messages.append(ToolMessage(content=err, tool_call_id=call_id))
                    recorded_calls.append({"tool_name": name, "summary": err, "succeeded": False})
                    continue

                try:
                    raw = await tool_fn.ainvoke(args)
                    text = raw if isinstance(raw, str) else json.dumps(raw, ensure_ascii=False)
                    new_messages.append(ToolMessage(content=text, tool_call_id=call_id))

                    parsed = None
                    try:
                        parsed = json.loads(text)
                    except json.JSONDecodeError:
                        parsed = None

                    if isinstance(parsed, dict) and "op" in parsed:
                        proposed.append(parsed)
                        recorded_calls.append(
                            {
                                "tool_name": name,
                                "summary": f"Generated {parsed.get('op')} command",
                                "succeeded": True,
                            }
                        )
                    else:
                        recorded_calls.append(
                            {
                                "tool_name": name,
                                "summary": text[:120],
                                "succeeded": True,
                            }
                        )
                except Exception as exc:
                    err = f"Tool error: {exc}"
                    new_messages.append(ToolMessage(content=err, tool_call_id=call_id))
                    recorded_calls.append({"tool_name": name, "summary": err, "succeeded": False})
        finally:
            reset_project_context(token)

        return {
            "messages": new_messages,
            "proposed_commands": proposed,
            "tool_calls": recorded_calls,
        }

    def dry_run_validator(state: AgentState) -> dict[str, Any]:
        proposed = state.get("proposed_commands", [])
        error = validate_commands_dry_run(state.get("project_snapshot", {}), proposed)
        if error is None:
            return {"validation_error": None}

        retries = int(state.get("retry_count", 0)) + 1
        if retries > 2:
            return {
                "validation_error": error,
                "retry_count": retries,
                "proposed_commands": [],
                "messages": [AIMessage(content=f"Edit validation failed after retries: {error}")],
            }

        return {
            "validation_error": error,
            "retry_count": retries,
            "proposed_commands": [],
            "messages": [
                HumanMessage(
                    content=(
                        f"Dry-run validation failed: {error}. "
                        "Please fix the parameters and call the editing tool again."
                    )
                )
            ],
        }

    def route_after_planner(state: AgentState) -> Literal["tools_node", "__end__"]:
        last = state["messages"][-1]
        if getattr(last, "tool_calls", None):
            return "tools_node"
        return "__end__"

    def route_after_validator(state: AgentState) -> Literal["intent_planner", "__end__"]:
        if state.get("validation_error") and int(state.get("retry_count", 0)) > 2:
            return "__end__"
        return "intent_planner"

    graph = StateGraph(AgentState)
    graph.add_node("read_project_context", read_project_context)
    graph.add_node("intent_planner", intent_planner)
    graph.add_node("tools_node", tools_node)
    graph.add_node("dry_run_validator", dry_run_validator)

    graph.add_edge(START, "read_project_context")
    graph.add_edge("read_project_context", "intent_planner")
    graph.add_conditional_edges(
        "intent_planner",
        route_after_planner,
        {"tools_node": "tools_node", "__end__": END},
    )
    graph.add_edge("tools_node", "dry_run_validator")
    graph.add_conditional_edges(
        "dry_run_validator",
        route_after_validator,
        {"intent_planner": "intent_planner", "__end__": END},
    )

    checkpointer = resolve_checkpointer()
    return graph.compile(checkpointer=checkpointer) if checkpointer is not None else graph.compile()
