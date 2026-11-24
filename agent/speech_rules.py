"""Authoritative Python speech polish rules aligned with C# SpeechPolishAnalyzer.

This module is the single Python source of truth for speech polish intensity
presets, Chinese/English filler and verbal-tic lexicons, token normalization,
stutter/repeat detection, and timeline cut-range merging/clamping.

Keep constants and behavior synchronized with:
    src/FrameTrail.Workspace/AiSpeechPolishService.cs
"""

from __future__ import annotations

from typing import Any

TICKS_PER_SECOND = 10_000_000
MIN_KEEP_TICKS = 1_000_000  # 0.10s minimum kept segment duration
MAX_CUT_RANGES = 2000

# Matches C# SpeechPolishOptions effective presets.
INTENSITY_PRESETS: dict[str, dict[str, Any]] = {
    "gentle": {
        "min_pause_sec": 1.00,
        "padding_sec": 0.24,
        "remove_repeats": False,
    },
    "standard": {
        "min_pause_sec": 0.70,
        "padding_sec": 0.16,
        "remove_repeats": True,
    },
    "tight": {
        "min_pause_sec": 0.45,
        "padding_sec": 0.10,
        "remove_repeats": True,
    },
}

VERBATIM_FILLER_PROMPT = (
    "请逐字如实转录，保留所有嗯、呃、额、啊、哦、噢、唔、诶、那个、就是说、然后呢等口语语气词与卡顿重复，不要自动删改。"
    "Verbatim speech transcript keeping um, uh, er, erm, ah, hmm, like, you know, and stutters."
)

# Unambiguous hesitation sounds (matches C# SpeechPolishAnalyzer.StrongFillers).
STRONG_FILLERS: frozenset[str] = frozenset(
    {
        "嗯",
        "嗯嗯",
        "呃",
        "呃呃",
        "额",
        "额额",
        "唔",
        "噢",
        "诶",
        "欸",
        "um",
        "umm",
        "ummm",
        "uh",
        "uhh",
        "uhhh",
        "erm",
        "er",
        "err",
        "hmm",
        "hmmm",
        "mm",
        "mmm",
        "ah",
        "ahh",
    }
)

# Context-sensitive boundary particles (matches C# SpeechPolishAnalyzer.ContextualParticles).
BOUNDARY_FILLERS: frozenset[str] = frozenset({"啊", "哦", "哎", "呐", "嘛", "呢", "呀"})
CONTEXTUAL_PARTICLES: frozenset[str] = BOUNDARY_FILLERS

# Multi-char Chinese verbal tics (matches C# SpeechPolishAnalyzer.ChineseVerbalTics).
ZH_VERBAL_TICS: frozenset[str] = frozenset(
    {"那个", "就是说", "怎么说呢", "然后呢", "就是那个", "这个这个", "那个那个"}
)

# Multi-word English verbal tics (matches C# SpeechPolishAnalyzer.EnglishPhraseTics).
EN_PHRASE_TICS: frozenset[str] = frozenset({"you know", "i mean"})

# Legitimate Chinese single-character reduplications (matches C# AllowedChineseReduplications).
ALLOWED_ZH_REDUPLICATIONS: frozenset[str] = frozenset(
    {
        "看", "试", "慢", "好", "刚", "常", "往", "仅", "渐", "轻", "小", "大", "多", "少",
        "天", "年", "人", "处", "家", "件", "个", "条", "点", "滴", "谢", "爸", "妈", "哥",
        "姐", "弟", "妹", "爷", "奶", "宝", "哈", "嘻", "呵", "呜", "听", "说", "想", "走",
        "查", "找", "写", "读", "学", "练", "玩", "等", "歇", "聊", "转", "扫", "拉", "推",
        "按", "切", "换", "改", "调", "测", "算", "比", "对", "清", "简", "高", "低", "早",
        "晚", "长", "短", "新", "老", "真", "实", "稳", "准", "快", "近", "远", "满", "全",
    }
)

TRAILING_PUNCT = "，,、。.！!？?；;：:…—-~～\"'”’）)》】"
LEADING_PUNCT = "“‘\"'（(《【—-"
EDGE_PUNCT = TRAILING_PUNCT + "“‘（(《【"


def sec_to_ticks(sec: float) -> int:
    """Convert seconds to canonical 10MHz ticks."""
    return int(round(max(0.0, float(sec)) * TICKS_PER_SECOND))


def ticks_to_sec(ticks: int) -> float:
    """Convert 10MHz ticks to seconds rounded to 4 decimals."""
    return round(int(ticks) / TICKS_PER_SECOND, 4)


def media_time_to_ticks(value: Any, default: int = 0) -> int:
    """Normalize a FrameTrail MediaTime object or numeric tick count into 10MHz ticks."""
    if isinstance(value, (int, float)):
        return int(round(value))
    if not isinstance(value, dict):
        return default
    ticks = int(value.get("ticks", 0))
    timescale = int(value.get("timescale", TICKS_PER_SECOND))
    if timescale <= 0:
        return default
    return int(round(ticks * TICKS_PER_SECOND / timescale))


def normalize_intensity(raw: str | None) -> str:
    """Normalize user/agent intensity aliases into 'gentle', 'standard', or 'tight'."""
    val = (raw or "standard").strip().lower()
    if val in ("gentle", "light", "conservative", "轻度", "保守", "轻微"):
        return "gentle"
    if val in ("tight", "aggressive", "compact", "紧凑", "激进", "强力"):
        return "tight"
    return "standard"


def resolve_intensity_options(
    intensity: str = "standard",
    min_pause_sec: float | None = None,
    pause_padding_sec: float | None = None,
    remove_repeats: bool = True,
) -> tuple[str, float, float, bool]:
    """Resolve normalized intensity and effective pause/padding/repeat settings."""
    norm = normalize_intensity(intensity)
    preset = INTENSITY_PRESETS[norm]
    eff_min_pause = float(min_pause_sec) if min_pause_sec is not None else float(preset["min_pause_sec"])
    eff_pad = float(pause_padding_sec) if pause_padding_sec is not None else float(preset["padding_sec"])
    eff_repeats = bool(remove_repeats) and bool(preset["remove_repeats"])
    return norm, eff_min_pause, eff_pad, eff_repeats


def clean_speech_token(raw: str) -> str:
    """Strip leading/trailing Chinese and English punctuation around a speech token."""
    return (raw or "").strip().strip(EDGE_PUNCT).strip()


def _join_clean_tokens(words: list[dict[str, Any]], start_i: int, length: int) -> str:
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


def detect_speech_fillers_and_repeats(
    segments: list[dict[str, Any]],
    intensity: str = "standard",
    remove_fillers: bool = True,
    remove_repeats: bool = True,
) -> list[dict[str, Any]]:
    """Detect filler words, boundary particles, verbal tics, stutters, and phrase restarts."""
    norm_intensity = normalize_intensity(intensity)
    words: list[dict[str, Any]] = []
    for seg_idx, seg in enumerate(segments):
        for w in seg.get("words") or []:
            raw = str(w.get("text") or "").strip()
            if not raw:
                continue
            clean = clean_speech_token(raw)
            s_sec = max(0.0, float(w.get("startSec", int(w.get("StartTicks", 0)) / TICKS_PER_SECOND)))
            e_sec = max(s_sec + 0.02, float(w.get("endSec", int(w.get("EndTicks", 0)) / TICKS_PER_SECOND)))
            words.append(
                {
                    "raw": raw,
                    "clean": clean,
                    "lower": clean.lower(),
                    "startSec": s_sec,
                    "endSec": e_sec,
                    "segIdx": seg_idx,
                    "trailingPunct": bool(raw and raw[-1] in TRAILING_PUNCT),
                }
            )
    words.sort(key=lambda item: item["startSec"])
    if not words:
        return []

    consumed = [False] * len(words)
    cuts: list[dict[str, Any]] = []
    allow_repeats = bool(remove_repeats) and bool(INTENSITY_PRESETS[norm_intensity]["remove_repeats"])

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
                        match_phrase = _join_clean_tokens(words, i, length)
                        break
                if match_j > i:
                    break
            if match_j > i:
                s_ticks = sec_to_ticks(words[i]["startSec"])
                e_ticks = sec_to_ticks(words[match_j]["startSec"])
                if e_ticks - s_ticks >= 1_200_000:
                    cuts.append({"start": s_ticks, "end": e_ticks, "reason": "repeat", "label": f"「{match_phrase}」"})
                    for k in range(i, match_j):
                        consumed[k] = True
                    i = match_j
                    continue
            i += 1

    # 2. Immediate single-token stutters
    if allow_repeats:
        for i in range(len(words) - 1):
            if consumed[i] or consumed[i + 1]:
                continue
            a = words[i]["clean"]
            b = words[i + 1]["clean"]
            if not a or a.lower() != b.lower():
                continue
            gap = words[i + 1]["startSec"] - words[i]["endSec"]
            if gap > 0.70 or a.lower() in STRONG_FILLERS:
                continue
            triple = i + 2 < len(words) and words[i + 2]["lower"] == a.lower()
            if len(a) == 1 and a in ALLOWED_ZH_REDUPLICATIONS and gap < 0.14 and not triple:
                continue
            s_ticks = sec_to_ticks(words[i]["startSec"])
            e_ticks = sec_to_ticks(words[i + 1]["startSec"])
            if e_ticks - s_ticks >= 800_000:
                cuts.append({"start": s_ticks, "end": e_ticks, "reason": "repeat", "label": f"「{a}」"})
                consumed[i] = True

    # 3. Filler words & boundary verbal tics
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

            # Multi-token tics (Standard / Tight)
            if norm_intensity != "gentle" and i + 1 < len(words) and not consumed[i + 1]:
                matched_tic = None
                for length in (3, 2):
                    if i + length > len(words) or any(consumed[i + k] for k in range(length)):
                        continue
                    if any(words[i + k]["startSec"] - words[i + k - 1]["endSec"] > 0.25 for k in range(1, length)):
                        continue
                    zh_join = "".join(words[i + k]["clean"] for k in range(length))
                    if zh_join in ZH_VERBAL_TICS:
                        last_i = i + length - 1
                        ga = 1.0 if last_i == len(words) - 1 else max(0.0, words[last_i + 1]["startSec"] - words[last_i]["endSec"])
                        ec = words[last_i]["trailingPunct"] or (last_i + 1 < len(words) and words[last_i + 1]["segIdx"] != words[last_i]["segIdx"])
                        if ((prev_clause or gap_before >= 0.08) and (ec or ga >= 0.12)) or (norm_intensity == "tight" and (prev_clause or ga >= 0.10)):
                            matched_tic = (length, zh_join)
                            break
                if matched_tic is None and words[i + 1]["startSec"] - w["endSec"] <= 0.25:
                    en_join = f"{w['lower']} {words[i + 1]['lower']}"
                    if en_join in EN_PHRASE_TICS:
                        ga = 1.0 if i + 2 >= len(words) else max(0.0, words[i + 2]["startSec"] - words[i + 1]["endSec"])
                        if ((prev_clause or gap_before >= 0.10) and (words[i + 1]["trailingPunct"] or ga >= 0.10)) or norm_intensity == "tight":
                            matched_tic = (2, en_join)
                if matched_tic is not None:
                    span_len, tic_text = matched_tic
                    s_ticks = sec_to_ticks(words[i]["startSec"])
                    e_ticks = sec_to_ticks(words[i + span_len - 1]["endSec"])
                    if e_ticks - s_ticks >= 1_000_000:
                        cuts.append({"start": s_ticks, "end": e_ticks, "reason": "filler", "label": f"「{tic_text}」"})
                        for k in range(i, i + span_len):
                            consumed[k] = True
                        i += span_len
                        continue

            low = w["lower"]
            is_strong = low in STRONG_FILLERS
            is_boundary = not is_strong and w["clean"] in BOUNDARY_FILLERS
            is_zh_tic = not is_strong and not is_boundary and norm_intensity != "gentle" and w["clean"] in ZH_VERBAL_TICS

            should_cut = False
            if is_strong:
                should_cut = norm_intensity != "gentle" or prev_clause or ends_clause or gap_before >= 0.08 or gap_after >= 0.10
            elif is_boundary:
                dur = w["endSec"] - w["startSec"]
                isolated = (prev_clause or gap_before >= 0.08) and (ends_clause or gap_after >= 0.08 or dur >= 0.18)
                opening = prev_clause and (ends_clause or gap_after >= 0.10)
                should_cut = isolated or opening or (norm_intensity == "tight" and gap_before >= 0.06)
            elif is_zh_tic:
                repeated = i + 1 < len(words) and words[i + 1]["clean"] == w["clean"]
                should_cut = repeated or ((prev_clause or gap_before >= 0.10) and (ends_clause or gap_after >= 0.12)) or (norm_intensity == "tight" and (prev_clause or gap_after >= 0.10))

            if should_cut:
                end_i = i
                while (
                    end_i + 1 < len(words)
                    and not consumed[end_i + 1]
                    and words[end_i + 1]["lower"] in STRONG_FILLERS
                    and words[end_i + 1]["startSec"] - words[end_i]["endSec"] <= 0.35
                ):
                    end_i += 1
                s_ticks = sec_to_ticks(words[i]["startSec"])
                e_ticks = sec_to_ticks(words[end_i]["endSec"])
                if e_ticks - s_ticks >= 800_000:
                    lbl = _join_clean_tokens(words, i, end_i - i + 1)
                    cuts.append({"start": s_ticks, "end": e_ticks, "reason": "filler", "label": f"「{lbl}」"})
                    for k in range(i, end_i + 1):
                        consumed[k] = True
                    i = end_i + 1
                    continue
            i += 1

    return cuts


def merge_and_clamp_cut_ranges(
    project_data: dict[str, Any],
    raw_cuts: list[dict[str, Any]],
    min_keep_ticks: int = MIN_KEEP_TICKS,
) -> list[dict[str, Any]]:
    """Clip raw cuts against active timeline segments, merge near-adjacent cuts, and preserve >=1 segment."""
    segments = project_data.get("segments", []) or []
    total_ticks = media_time_to_ticks(project_data.get("sourceDuration"), 0)
    if not segments or total_ticks <= 0 or not raw_cuts:
        return []

    clipped: list[dict[str, Any]] = []
    for item in raw_cuts:
        cs = max(0, min(total_ticks, int(item["start"])))
        ce = max(0, min(total_ticks, int(item["end"])))
        if ce <= cs:
            continue
        for seg in segments:
            s0 = media_time_to_ticks(seg.get("sourceStart"), 0)
            s1 = s0 + media_time_to_ticks(seg.get("sourceDuration"), 0)
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
            s0 = media_time_to_ticks(seg.get("sourceStart"), 0)
            s1 = s0 + media_time_to_ticks(seg.get("sourceDuration"), 0)
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

    return merged[:MAX_CUT_RANGES]
