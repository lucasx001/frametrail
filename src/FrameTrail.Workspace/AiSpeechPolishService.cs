using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FrameTrail.Domain;

namespace FrameTrail.Workspace;

public enum SpeechPolishIntensity
{
    Gentle,
    Standard,
    Tight
}

public sealed record SpeechPolishOptions(
    SpeechPolishIntensity Intensity = SpeechPolishIntensity.Standard,
    bool RemovePauses = true,
    bool RemoveFillers = true,
    bool RemoveRepeats = true,
    double? MinPauseSeconds = null,
    double? PausePaddingSeconds = null,
    double SilenceThresholdDb = -38.0,
    string Language = "auto",
    string Engine = "whisper-local",
    string ModelId = WhisperModelManager.LargeTurboModelId,
    string TrackFilter = "all",
    IReadOnlyList<string>? ClipIds = null,
    double? StartSeconds = null,
    double? EndSeconds = null,
    bool ForceRefresh = false,
    string? ApiKeyOverride = null,
    string? BaseUrlOverride = null)
{
    public double EffectiveMinPauseSeconds => MinPauseSeconds ?? Intensity switch
    {
        SpeechPolishIntensity.Gentle => 1.00,
        SpeechPolishIntensity.Tight => 0.45,
        _ => 0.70
    };

    public double EffectivePausePaddingSeconds => PausePaddingSeconds ?? Intensity switch
    {
        SpeechPolishIntensity.Gentle => 0.24,
        SpeechPolishIntensity.Tight => 0.10,
        _ => 0.16
    };

    public bool EffectiveRemoveRepeats => RemoveRepeats && Intensity != SpeechPolishIntensity.Gentle;

    public static SpeechPolishIntensity ParseIntensity(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "gentle" or "light" or "conservative" or "轻度" or "保守" or "轻微" => SpeechPolishIntensity.Gentle,
        "tight" or "aggressive" or "compact" or "紧凑" or "激进" or "强力" => SpeechPolishIntensity.Tight,
        _ => SpeechPolishIntensity.Standard
    };
}

public sealed record SpeechCutItem(
    long StartTicks,
    long EndTicks,
    string Reason,
    string Label,
    string? MatchedText = null)
{
    public double StartSeconds => Math.Round(StartTicks / (double)AiSpeechPolishService.TicksPerSecond, 4);
    public double EndSeconds => Math.Round(EndTicks / (double)AiSpeechPolishService.TicksPerSecond, 4);
    public double DurationSeconds => Math.Round((EndTicks - StartTicks) / (double)AiSpeechPolishService.TicksPerSecond, 4);
}

public sealed record SpeechPolishResult(
    IReadOnlyList<SpeechCutItem> Cuts,
    object? Command,
    int PauseCount,
    int FillerCount,
    int RepeatCount,
    double TotalRemovedSeconds,
    string EngineUsed,
    bool UsedAsr,
    string? Warning = null);

/// <summary>
/// Pure deterministic analyzer for detecting pauses, Chinese/English filler words, and stutters/repeats
/// from word-level transcripts and 10ms audio energy envelopes, with energy-valley boundary snapping.
/// </summary>
public static class SpeechPolishAnalyzer
{
    public const long TicksPerSecond = 10_000_000L;
    public const long DefaultMinKeepTicks = 1_000_000L; // 0.10s
    public const int MaxCutRanges = 2000;

    public const string VerbatimFillerPrompt =
        "请逐字如实转录，保留所有嗯、呃、额、啊、哦、噢、唔、诶、那个、就是说、然后呢等口语语气词与卡顿重复，不要自动删改。Verbatim speech transcript keeping um, uh, er, erm, ah, hmm, like, you know, and stutters.";

    // Unambiguous hesitation sounds (almost never carry lexical meaning when standalone).
    private static readonly HashSet<string> StrongFillers = new(StringComparer.OrdinalIgnoreCase)
    {
        "嗯", "嗯嗯", "呃", "呃呃", "额", "额额", "唔", "噢", "诶", "欸",
        "um", "umm", "ummm", "uh", "uhh", "uhhh", "erm", "er", "err", "hmm", "hmmm", "mm", "mmm", "ah", "ahh"
    };

    // Context-sensitive particles: cut when isolated/utterance-initial/pause-bounded (or Tight mode).
    private static readonly HashSet<string> ContextualParticles = new(StringComparer.OrdinalIgnoreCase)
    {
        "啊", "哦", "哎", "呐", "嘛", "呢", "呀"
    };

    // Multi-char hesitation tics (Chinese).
    private static readonly HashSet<string> ChineseVerbalTics = new(StringComparer.Ordinal)
    {
        "那个", "就是说", "怎么说呢", "然后呢", "就是那个", "这个这个", "那个那个"
    };

    // Multi-word hesitation tics (English, space-joined lowercase).
    private static readonly HashSet<string> EnglishPhraseTics = new(StringComparer.OrdinalIgnoreCase)
    {
        "you know", "i mean"
    };

    // Legitimate Chinese single-character reduplications that must NOT be treated as stutters.
    private static readonly HashSet<string> AllowedChineseReduplications = new(StringComparer.Ordinal)
    {
        "看", "试", "慢", "好", "刚", "常", "往", "仅", "渐", "轻", "小", "大", "多", "少",
        "天", "年", "人", "处", "家", "件", "个", "条", "点", "滴", "谢", "爸", "妈", "哥",
        "姐", "弟", "妹", "爷", "奶", "宝", "哈", "嘻", "呵", "呜", "听", "说", "想", "走",
        "查", "找", "写", "读", "学", "练", "玩", "等", "歇", "聊", "转", "扫", "拉", "推",
        "按", "切", "换", "改", "调", "测", "算", "比", "对", "清", "简", "高", "低", "早",
        "晚", "长", "短", "新", "老", "真", "实", "稳", "准", "快", "近", "远", "满", "全"
    };

    private static readonly char[] TrailingPunctuation =
    [
        '，', ',', '、', '。', '.', '！', '!', '？', '?', '；', ';', '：', ':', '…', '—', '-', '~', '～', '"', '\'', '”', '’', '）', ')', '》', '】'
    ];

    private static readonly char[] LeadingPunctuation =
    [
        '“', '‘', '"', '\'', '（', '(', '《', '【', '—', '-'
    ];

    /// <summary>Detects silent pause cuts from 10ms (or arbitrary frame) RMS dBFS track envelopes.</summary>
    public static IReadOnlyList<SpeechCutItem> DetectPauseCuts(
        IReadOnlyList<AudioTrackEnergyEnvelope> envelopes,
        double minPauseSeconds,
        double pausePaddingSeconds,
        double thresholdDb)
    {
        if (envelopes is null || envelopes.Count == 0)
            return Array.Empty<SpeechCutItem>();

        var perTrackSpans = new List<List<(long Start, long End)>>();
        long minPauseTicks = SecToTicks(Math.Max(0.15, minPauseSeconds));
        long padTicks = SecToTicks(Math.Max(0.0, pausePaddingSeconds));

        foreach (var env in envelopes)
        {
            if (env.FrameDb.Length == 0 || env.FrameSeconds <= 0)
                continue;

            var spans = new List<(long Start, long End)>();
            int? runStart = null;
            for (int i = 0; i < env.FrameDb.Length; i++)
            {
                if (env.FrameDb[i] <= thresholdDb)
                {
                    runStart ??= i;
                }
                else if (runStart.HasValue)
                {
                    AddSpanIfLongEnough(spans, env, runStart.Value, i, minPauseTicks);
                    runStart = null;
                }
            }
            if (runStart.HasValue)
                AddSpanIfLongEnough(spans, env, runStart.Value, env.FrameDb.Length, minPauseTicks);

            perTrackSpans.Add(spans);
        }

        if (perTrackSpans.Count == 0)
            return Array.Empty<SpeechCutItem>();

        // Intersect overlapping tracks (same semantics as silence removal across mic + system audio).
        var merged = perTrackSpans[0];
        for (int t = 1; t < perTrackSpans.Count; t++)
        {
            var next = new List<(long Start, long End)>();
            foreach (var a in merged)
            {
                foreach (var b in perTrackSpans[t])
                {
                    long s = Math.Max(a.Start, b.Start);
                    long e = Math.Min(a.End, b.End);
                    if (e - s >= minPauseTicks)
                        next.Add((s, e));
                }
            }
            merged = next;
        }

        var cuts = new List<SpeechCutItem>();
        foreach (var (s, e) in merged)
        {
            long cutS = s + padTicks;
            long cutE = e - padTicks;
            if (cutE - cutS < DefaultMinKeepTicks)
                continue;
            double durSec = (cutE - cutS) / (double)TicksPerSecond;
            cuts.Add(new SpeechCutItem(cutS, cutE, "pause", $"{durSec:0.0}s"));
        }
        return cuts;
    }

    private static void AddSpanIfLongEnough(
        List<(long Start, long End)> spans,
        AudioTrackEnergyEnvelope env,
        int startFrame,
        int endFrame,
        long minPauseTicks)
    {
        long s = env.SourceStartTicks + SecToTicks(startFrame * env.FrameSeconds);
        long e = env.SourceStartTicks + SecToTicks(endFrame * env.FrameSeconds);
        if (e - s >= minPauseTicks)
            spans.Add((s, e));
    }

    /// <summary>
    /// Analyzes word-level ASR segments to identify filler words, contextual verbal tics,
    /// immediate stutters, and short phrase restarts, snapping boundaries to nearby audio energy valleys.
    /// </summary>
    public static IReadOnlyList<SpeechCutItem> DetectFillerAndRepeatCuts(
        IReadOnlyList<TranscribedSegment> segments,
        SpeechPolishOptions options,
        IReadOnlyList<AudioTrackEnergyEnvelope>? envelopes = null)
    {
        if (segments is null || segments.Count == 0)
            return Array.Empty<SpeechCutItem>();

        var words = FlattenWords(segments);
        if (words.Count == 0)
            return Array.Empty<SpeechCutItem>();

        var cuts = new List<SpeechCutItem>();
        var consumed = new bool[words.Count];
        bool allowRepeats = options.EffectiveRemoveRepeats;

        // Pass 1: Short phrase restarts (2..5 token prefix repeated within <= 3.0s).
        if (allowRepeats)
        {
            for (int i = 0; i < words.Count; i++)
            {
                if (consumed[i]) continue;
                int matchedRestartIndex = -1;
                string? matchedPhrase = null;

                for (int len = Math.Min(5, (words.Count - i) / 2); len >= 2; len--)
                {
                    if (!IsValidPhraseStart(words, i, len))
                        continue;

                    int maxJ = Math.Min(words.Count - len, i + len + 3);
                    for (int j = i + len; j <= maxJ; j++)
                    {
                        if (words[j].StartSeconds - words[i].StartSeconds > 3.2)
                            break;
                        if (PhraseMatches(words, i, j, len))
                        {
                            matchedRestartIndex = j;
                            matchedPhrase = JoinCleanTokens(words, i, len);
                            break;
                        }
                    }
                    if (matchedRestartIndex > i)
                        break;
                }

                if (matchedRestartIndex > i)
                {
                    long rawStart = SecToTicks(words[i].StartSeconds);
                    long rawEnd = SecToTicks(words[matchedRestartIndex].StartSeconds);
                    if (rawEnd - rawStart >= 1_200_000L) // >= 120ms
                    {
                        long minBound = i > 0 ? SecToTicks(words[i - 1].EndSeconds) : 0;
                        long maxBound = SecToTicks(words[matchedRestartIndex].StartSeconds);
                        var (snappedS, snappedE) = SnapToEnergyValley(rawStart, rawEnd, minBound, maxBound, envelopes);
                        cuts.Add(new SpeechCutItem(snappedS, snappedE, "repeat", $"「{matchedPhrase}」", matchedPhrase));
                        for (int k = i; k < matchedRestartIndex; k++)
                            consumed[k] = true;
                        i = matchedRestartIndex - 1;
                    }
                }
            }
        }

        // Pass 2: Immediate single-token stutters (e.g. "我 我 我们", "the the").
        if (allowRepeats)
        {
            for (int i = 0; i + 1 < words.Count; i++)
            {
                if (consumed[i] || consumed[i + 1]) continue;
                string a = words[i].Clean;
                string b = words[i + 1].Clean;
                if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) continue;
                double gap = words[i + 1].StartSeconds - words[i].EndSeconds;
                if (gap > 0.70) continue;

                bool isExactStutter = string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
                if (!isExactStutter) continue;

                // Protect legitimate Chinese single-char reduplications (看看, 试试, 慢慢, etc.)
                // unless there is an audible hesitation pause (>= 0.14s) or 3+ repetitions ("我 我 我").
                bool tripleStutter = i + 2 < words.Count && string.Equals(words[i + 2].Clean, a, StringComparison.OrdinalIgnoreCase);
                if (a.Length == 1 && AllowedChineseReduplications.Contains(a) && gap < 0.14 && !tripleStutter)
                    continue;

                // Don't double-classify pure fillers here; let Pass 3 label them as "filler".
                if (StrongFillers.Contains(a))
                    continue;

                long rawStart = SecToTicks(words[i].StartSeconds);
                long rawEnd = SecToTicks(words[i + 1].StartSeconds);
                if (rawEnd - rawStart >= 800_000L)
                {
                    long minBound = i > 0 ? SecToTicks(words[i - 1].EndSeconds) : 0;
                    long maxBound = SecToTicks(words[i + 1].StartSeconds);
                    var (snappedS, snappedE) = SnapToEnergyValley(rawStart, rawEnd, minBound, maxBound, envelopes);
                    cuts.Add(new SpeechCutItem(snappedS, snappedE, "repeat", $"「{a}」", a));
                    consumed[i] = true;
                }
            }
        }

        // Pass 3: Filler words & verbal tics.
        if (options.RemoveFillers)
        {
            for (int i = 0; i < words.Count; i++)
            {
                if (consumed[i]) continue;
                var w = words[i];
                if (string.IsNullOrEmpty(w.Clean)) continue;

                double gapBefore = i == 0 ? 1.0 : Math.Max(0.0, w.StartSeconds - words[i - 1].EndSeconds);
                double gapAfter = i == words.Count - 1 ? 1.0 : Math.Max(0.0, words[i + 1].StartSeconds - w.EndSeconds);
                bool prevEndedClause = i == 0 || words[i - 1].HasTrailingPunctuation || words[i - 1].SegmentIndex != w.SegmentIndex;
                bool endsClause = w.HasTrailingPunctuation || (i + 1 < words.Count && words[i + 1].SegmentIndex != w.SegmentIndex);

                // 3a. Multi-token Chinese/English verbal tics (Standard / Tight)
                if (options.Intensity != SpeechPolishIntensity.Gentle)
                {
                    if (TryMatchMultiTokenTic(words, i, consumed, gapBefore, prevEndedClause, options.Intensity, out int spanLen, out string ticText))
                    {
                        int lastIdx = i + spanLen - 1;
                        long rawStart = SecToTicks(words[i].StartSeconds);
                        long rawEnd = SecToTicks(words[lastIdx].EndSeconds);
                        long minBound = i > 0 ? SecToTicks(words[i - 1].EndSeconds) : 0;
                        long maxBound = lastIdx + 1 < words.Count ? SecToTicks(words[lastIdx + 1].StartSeconds) : rawEnd + 500_000L;
                        var (snappedS, snappedE) = SnapToEnergyValley(rawStart, rawEnd, minBound, maxBound, envelopes);
                        if (snappedE - snappedS >= 1_000_000L)
                        {
                            cuts.Add(new SpeechCutItem(snappedS, snappedE, "filler", $"「{ticText}」", ticText));
                            for (int k = i; k <= lastIdx; k++) consumed[k] = true;
                            i = lastIdx;
                            continue;
                        }
                    }
                }

                // 3b. Single-token strong fillers ("嗯", "呃", "额", "um", "uh", etc.)
                bool isStrong = StrongFillers.Contains(w.Clean);
                bool isContextual = !isStrong && ContextualParticles.Contains(w.Clean);
                bool isSingleChineseTic = !isStrong && !isContextual && options.Intensity != SpeechPolishIntensity.Gentle && ChineseVerbalTics.Contains(w.Clean);

                bool shouldCut = false;
                if (isStrong)
                {
                    // Gentle mode requires slight separation or standalone clause; Standard/Tight cuts all strong fillers.
                    shouldCut = options.Intensity != SpeechPolishIntensity.Gentle
                        || prevEndedClause
                        || endsClause
                        || gapBefore >= 0.08
                        || gapAfter >= 0.10;
                }
                else if (isContextual)
                {
                    // Protect natural sentence-final particles glued directly to the previous word ("太好了啊！").
                    bool isIsolated = (prevEndedClause || gapBefore >= 0.08) && (endsClause || gapAfter >= 0.08 || w.DurationSeconds >= 0.18);
                    bool isUtteranceOpening = prevEndedClause && (endsClause || gapAfter >= 0.10);
                    shouldCut = options.Intensity == SpeechPolishIntensity.Tight
                        ? (isIsolated || isUtteranceOpening || gapBefore >= 0.06)
                        : (isIsolated || isUtteranceOpening);
                }
                else if (isSingleChineseTic)
                {
                    bool repeatedTic = i + 1 < words.Count && string.Equals(words[i + 1].Clean, w.Clean, StringComparison.Ordinal);
                    bool pauseBounded = (prevEndedClause || gapBefore >= 0.10) && (endsClause || gapAfter >= 0.12);
                    shouldCut = repeatedTic || pauseBounded || (options.Intensity == SpeechPolishIntensity.Tight && (prevEndedClause || gapAfter >= 0.10));
                }

                if (!shouldCut) continue;

                // Extend across consecutive strong fillers (e.g., "嗯 呃 啊") as a single clean cut.
                int endIdx = i;
                while (endIdx + 1 < words.Count && !consumed[endIdx + 1] &&
                       StrongFillers.Contains(words[endIdx + 1].Clean) &&
                       words[endIdx + 1].StartSeconds - words[endIdx].EndSeconds <= 0.35)
                {
                    endIdx++;
                }

                long rStart = SecToTicks(words[i].StartSeconds);
                long rEnd = SecToTicks(words[endIdx].EndSeconds);
                long prevBound = i > 0 ? SecToTicks(words[i - 1].EndSeconds) : 0;
                long nextBound = endIdx + 1 < words.Count ? SecToTicks(words[endIdx + 1].StartSeconds) : rEnd + 500_000L;
                var (sTicks, eTicks) = SnapToEnergyValley(rStart, rEnd, prevBound, nextBound, envelopes);
                if (eTicks - sTicks >= 800_000L)
                {
                    string labelText = JoinCleanTokens(words, i, endIdx - i + 1);
                    cuts.Add(new SpeechCutItem(sTicks, eTicks, "filler", $"「{labelText}」", labelText));
                    for (int k = i; k <= endIdx; k++) consumed[k] = true;
                    i = endIdx;
                }
            }
        }

        return cuts;
    }

    /// <summary>
    /// Merges pause, filler, and repeat cuts, clips them safely against active timeline segments
    /// (guaranteeing at least one clip remains), and returns the final ordered cut list.
    /// </summary>
    public static IReadOnlyList<SpeechCutItem> MergeAndClampCuts(
        ProjectSnapshot snapshot,
        IEnumerable<SpeechCutItem> rawCuts,
        long minKeepTicks = DefaultMinKeepTicks)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        long totalTicks = VisualResources.Ticks(snapshot.SourceDuration);
        if (totalTicks <= 0 || snapshot.Segments.Count == 0)
            return Array.Empty<SpeechCutItem>();

        // Keep only portions overlapping existing timeline segments.
        var clipped = new List<SpeechCutItem>();
        foreach (var cut in rawCuts)
        {
            long cs = Math.Clamp(cut.StartTicks, 0, totalTicks);
            long ce = Math.Clamp(cut.EndTicks, 0, totalTicks);
            if (ce <= cs) continue;

            foreach (var seg in snapshot.Segments)
            {
                long segS = VisualResources.Ticks(seg.SourceStart);
                long segE = segS + VisualResources.Ticks(seg.SourceDuration);
                long ovS = Math.Max(cs, segS);
                long ovE = Math.Min(ce, segE);
                if (ovE - ovS >= 800_000L) // >= 80ms
                    clipped.Add(cut with { StartTicks = ovS, EndTicks = ovE });
            }
        }

        if (clipped.Count == 0)
            return Array.Empty<SpeechCutItem>();

        clipped.Sort((a, b) => a.StartTicks.CompareTo(b.StartTicks));

        // Merge overlapping or near-adjacent (< minKeepTicks gap) cuts.
        var merged = new List<SpeechCutItem>();
        foreach (var item in clipped)
        {
            if (merged.Count > 0 && item.StartTicks <= merged[^1].EndTicks + minKeepTicks)
            {
                var prev = merged[^1];
                long newEnd = Math.Max(prev.EndTicks, item.EndTicks);
                string reason = PickPrimaryReason(prev.Reason, item.Reason);
                string label = CombineLabels(prev, item, newEnd - prev.StartTicks);
                merged[^1] = new SpeechCutItem(prev.StartTicks, newEnd, reason, label, prev.MatchedText ?? item.MatchedText);
            }
            else
            {
                merged.Add(item);
            }
        }

        // Enforce engine safety invariant: never delete 100% of timeline segments.
        if (WouldEraseAllSegments(snapshot, merged, minKeepTicks))
        {
            // Trim the longest cut or drop the last cut so at least one segment survives cleanly.
            var safe = new List<SpeechCutItem>(merged);
            while (safe.Count > 0 && WouldEraseAllSegments(snapshot, safe, minKeepTicks))
                safe.RemoveAt(safe.Count - 1);
            merged = safe;
        }

        return merged.Count > MaxCutRanges ? merged.Take(MaxCutRanges).ToArray() : merged;
    }

    private static bool WouldEraseAllSegments(ProjectSnapshot snapshot, IReadOnlyList<SpeechCutItem> cuts, long minKeepTicks)
    {
        foreach (var seg in snapshot.Segments)
        {
            long segS = VisualResources.Ticks(seg.SourceStart);
            long segE = segS + VisualResources.Ticks(seg.SourceDuration);
            long cursor = segS;
            foreach (var cut in cuts)
            {
                if (cut.EndTicks <= segS) continue;
                if (cut.StartTicks >= segE) break;
                long s = Math.Max(cut.StartTicks, segS);
                long e = Math.Min(cut.EndTicks, segE);
                if (s - segS < minKeepTicks) s = segS;
                if (segE - e < minKeepTicks) e = segE;
                if (s - cursor >= minKeepTicks)
                    return false;
                cursor = Math.Max(cursor, e);
            }
            if (segE - cursor >= minKeepTicks)
                return false;
        }
        return true;
    }

    private static string PickPrimaryReason(string a, string b)
    {
        if (a == "filler" || b == "filler") return "filler";
        if (a == "repeat" || b == "repeat") return "repeat";
        return "pause";
    }

    private static string CombineLabels(SpeechCutItem prev, SpeechCutItem next, long totalTicks)
    {
        if (prev.Reason == "pause" && next.Reason == "pause")
            return $"{totalTicks / (double)TicksPerSecond:0.0}s";
        if (prev.Reason != "pause" && next.Reason == "pause")
            return prev.Label;
        if (prev.Reason == "pause" && next.Reason != "pause")
            return next.Label;
        if (string.Equals(prev.Label, next.Label, StringComparison.Ordinal))
            return prev.Label;
        return $"{prev.Label} + {next.Label}";
    }

    private sealed record IndexedWord(
        string Raw,
        string Clean,
        double StartSeconds,
        double EndSeconds,
        int SegmentIndex,
        bool HasTrailingPunctuation)
    {
        public double DurationSeconds => Math.Max(0.0, EndSeconds - StartSeconds);
    }

    private static List<IndexedWord> FlattenWords(IReadOnlyList<TranscribedSegment> segments)
    {
        var list = new List<IndexedWord>();
        for (int segIdx = 0; segIdx < segments.Count; segIdx++)
        {
            var seg = segments[segIdx];
            if (seg.Words is { Count: > 0 })
            {
                foreach (var w in seg.Words)
                {
                    string raw = (w.Text ?? "").Trim();
                    if (string.IsNullOrEmpty(raw)) continue;
                    string clean = CleanToken(raw);
                    bool trailing = raw.Length > 0 && TrailingPunctuation.Contains(raw[^1]);
                    double s = Math.Max(0.0, w.StartSeconds);
                    double e = Math.Max(s + 0.02, w.EndSeconds);
                    list.Add(new IndexedWord(raw, clean, s, e, segIdx, trailing));
                }
            }
        }
        list.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
        return list;
    }

    private static bool TryMatchMultiTokenTic(
        List<IndexedWord> words,
        int index,
        bool[] consumed,
        double gapBefore,
        bool prevEndedClause,
        SpeechPolishIntensity intensity,
        out int spanLen,
        out string ticText)
    {
        spanLen = 0;
        ticText = "";
        if (index + 1 >= words.Count || consumed[index + 1])
            return false;

        // Check 2-token and 3-token Chinese concatenated tics ("那"+"个", "就是"+"说", "怎么"+"说"+"呢")
        for (int len = Math.Min(3, words.Count - index); len >= 2; len--)
        {
            bool anyConsumed = false;
            bool largeInternalGap = false;
            var sb = new StringBuilder();
            for (int k = 0; k < len; k++)
            {
                if (consumed[index + k]) { anyConsumed = true; break; }
                if (k > 0 && words[index + k].StartSeconds - words[index + k - 1].EndSeconds > 0.25)
                {
                    largeInternalGap = true;
                    break;
                }
                sb.Append(words[index + k].Clean);
            }
            if (anyConsumed || largeInternalGap) continue;
            string joined = sb.ToString();
            if (ChineseVerbalTics.Contains(joined))
            {
                int last = index + len - 1;
                double gapAfter = last + 1 < words.Count ? Math.Max(0.0, words[last + 1].StartSeconds - words[last].EndSeconds) : 1.0;
                bool endsClause = words[last].HasTrailingPunctuation || (last + 1 < words.Count && words[last + 1].SegmentIndex != words[last].SegmentIndex);
                bool pauseBounded = (prevEndedClause || gapBefore >= 0.08) && (endsClause || gapAfter >= 0.12);
                if (pauseBounded || (intensity == SpeechPolishIntensity.Tight && (prevEndedClause || gapAfter >= 0.10)))
                {
                    spanLen = len;
                    ticText = joined;
                    return true;
                }
            }
        }

        // Check 2-word English tics ("you know", "i mean")
        if (!consumed[index + 1] && words[index + 1].StartSeconds - words[index].EndSeconds <= 0.25)
        {
            string enPhrase = $"{words[index].Clean} {words[index + 1].Clean}";
            if (EnglishPhraseTics.Contains(enPhrase))
            {
                double gapAfter = index + 2 < words.Count ? Math.Max(0.0, words[index + 2].StartSeconds - words[index + 1].EndSeconds) : 1.0;
                bool endsClause = words[index + 1].HasTrailingPunctuation;
                bool pauseBounded = (prevEndedClause || gapBefore >= 0.10) && (endsClause || gapAfter >= 0.10);
                if (pauseBounded || intensity == SpeechPolishIntensity.Tight)
                {
                    spanLen = 2;
                    ticText = enPhrase;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsValidPhraseStart(List<IndexedWord> words, int start, int len)
    {
        int nonEmpty = 0;
        for (int k = 0; k < len; k++)
        {
            if (string.IsNullOrEmpty(words[start + k].Clean)) return false;
            nonEmpty++;
        }
        return nonEmpty == len;
    }

    private static bool PhraseMatches(List<IndexedWord> words, int a, int b, int len)
    {
        for (int k = 0; k < len; k++)
        {
            if (!string.Equals(words[a + k].Clean, words[b + k].Clean, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static string JoinCleanTokens(List<IndexedWord> words, int start, int count)
    {
        var sb = new StringBuilder();
        for (int k = 0; k < count && start + k < words.Count; k++)
        {
            string tok = words[start + k].Clean;
            if (string.IsNullOrEmpty(tok)) continue;
            if (sb.Length > 0 && IsAsciiAlnum(sb[^1]) && IsAsciiAlnum(tok[0]))
                sb.Append(' ');
            sb.Append(tok);
        }
        return sb.ToString();
    }

    private static bool IsAsciiAlnum(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

    private static string CleanToken(string raw)
    {
        string trimmed = raw.Trim().Trim(LeadingPunctuation).Trim(TrailingPunctuation).Trim();
        return trimmed;
    }

    /// <summary>
    /// Snaps cut boundaries within ±35ms toward the local lowest-RMS-energy frame without encroaching on adjacent kept words.
    /// </summary>
    public static (long StartTicks, long EndTicks) SnapToEnergyValley(
        long rawStartTicks,
        long rawEndTicks,
        long minAllowedStartTicks,
        long maxAllowedEndTicks,
        IReadOnlyList<AudioTrackEnergyEnvelope>? envelopes)
    {
        if (envelopes is null || envelopes.Count == 0 || rawEndTicks <= rawStartTicks)
            return (rawStartTicks, rawEndTicks);

        long windowBefore = 350_000L; // 35ms
        long windowAfter = 200_000L;  // 20ms
        long sLow = Math.Max(minAllowedStartTicks, rawStartTicks - windowBefore);
        long sHigh = Math.Min(rawEndTicks - 200_000L, rawStartTicks + windowAfter);
        long eLow = Math.Max(rawStartTicks + 200_000L, rawEndTicks - windowAfter);
        long eHigh = Math.Min(maxAllowedEndTicks, rawEndTicks + windowBefore);

        long snappedS = sHigh > sLow ? FindLowestEnergyTick(envelopes, sLow, sHigh, rawStartTicks) : rawStartTicks;
        long snappedE = eHigh > eLow ? FindLowestEnergyTick(envelopes, eLow, eHigh, rawEndTicks) : rawEndTicks;
        if (snappedE <= snappedS)
            return (rawStartTicks, rawEndTicks);
        return (snappedS, snappedE);
    }

    private static long FindLowestEnergyTick(
        IReadOnlyList<AudioTrackEnergyEnvelope> envelopes,
        long lowTicks,
        long highTicks,
        long fallbackTicks)
    {
        AudioTrackEnergyEnvelope? chosen = envelopes.FirstOrDefault(e =>
            e.FrameDb.Length > 0 &&
            lowTicks >= e.SourceStartTicks &&
            lowTicks < e.SourceStartTicks + SecToTicks(e.FrameDb.Length * e.FrameSeconds));
        if (chosen is null)
            return fallbackTicks;

        double frameTicks = chosen.FrameSeconds * TicksPerSecond;
        int fStart = Math.Clamp((int)Math.Floor((lowTicks - chosen.SourceStartTicks) / frameTicks), 0, chosen.FrameDb.Length - 1);
        int fEnd = Math.Clamp((int)Math.Ceiling((highTicks - chosen.SourceStartTicks) / frameTicks), fStart, chosen.FrameDb.Length - 1);

        int bestFrame = -1;
        double bestDb = double.PositiveInfinity;
        for (int f = fStart; f <= fEnd; f++)
        {
            if (chosen.FrameDb[f] < bestDb)
            {
                bestDb = chosen.FrameDb[f];
                bestFrame = f;
            }
        }
        if (bestFrame < 0)
            return fallbackTicks;

        long tick = chosen.SourceStartTicks + (long)Math.Round((bestFrame + 0.5) * frameTicks);
        return Math.Clamp(tick, lowTicks, highTicks);
    }

    private static long SecToTicks(double sec) => (long)Math.Round(Math.Max(0.0, sec) * TicksPerSecond, MidpointRounding.AwayFromZero);
}

/// <summary>
/// High-level speech expression optimization service combining 10ms PCM silence detection,
/// verbatim word-level Whisper transcription, filler/repeat detection, and atomic op='cutRanges' generation.
/// </summary>
public sealed class AiSpeechPolishService
{
    public const long TicksPerSecond = 10_000_000L;

    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly AiSubtitleTranscriptionService transcriptionService;

    public AiSubtitleTranscriptionService TranscriptionService => transcriptionService;

    public AiSpeechPolishService(AiSubtitleTranscriptionService? transcriptionService = null)
    {
        this.transcriptionService = transcriptionService ?? new AiSubtitleTranscriptionService();
    }

    public async Task<SpeechPolishResult> AnalyzeAndBuildCommandAsync(
        string projectDirectory,
        ProjectSnapshot snapshot,
        SpeechPolishOptions? options = null,
        IProgress<SubtitleTranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
            throw new ArgumentException("Project directory is required.", nameof(projectDirectory));
        ArgumentNullException.ThrowIfNull(snapshot);

        var opts = options ?? new SpeechPolishOptions();
        bool needsAsr = opts.RemoveFillers || opts.EffectiveRemoveRepeats;

        IReadOnlyList<AudioTrackEnergyEnvelope> envelopes;
        IReadOnlyList<TranscribedSegment> wordSegments = Array.Empty<TranscribedSegment>();
        string engineUsed = "wav-rms";
        bool usedAsr = false;
        string? warning = null;

        bool isFullScope = (opts.ClipIds is null || opts.ClipIds.Count == 0) && !opts.StartSeconds.HasValue && !opts.EndSeconds.HasValue;
        var cachedWords = !opts.ForceRefresh && needsAsr && isFullScope
            ? await LoadVerbatimCacheAsync(projectDirectory, cancellationToken).ConfigureAwait(false)
            : Array.Empty<TranscribedSegment>();

        if (needsAsr && cachedWords.Count > 0)
        {
            progress?.Report(new SubtitleTranscriptionProgress("extracting", 25, "Scanning audio energy envelope..."));
            var (envList, _) = await transcriptionService.ExtractAudioEnvelopesAsync(
                projectDirectory, snapshot, opts.TrackFilter, opts.ClipIds, opts.StartSeconds, opts.EndSeconds, 0.01, cancellationToken)
                .ConfigureAwait(false);
            envelopes = envList;
            wordSegments = cachedWords;
            engineUsed = "cache";
            usedAsr = true;
        }
        else if (needsAsr)
        {
            try
            {
                var asrRequest = new SubtitleTranscriptionRequest(
                    Engine: opts.Engine,
                    ModelId: opts.ModelId,
                    Language: opts.Language,
                    TrackFilter: opts.TrackFilter,
                    ClipIds: opts.ClipIds,
                    VocabularyPrompt: SpeechPolishAnalyzer.VerbatimFillerPrompt,
                    ForceRefresh: opts.ForceRefresh,
                    ApiKeyOverride: opts.ApiKeyOverride,
                    BaseUrlOverride: opts.BaseUrlOverride,
                    StartSeconds: opts.StartSeconds,
                    EndSeconds: opts.EndSeconds);

                var raw = await transcriptionService.TranscribeRawWordsAsync(
                    projectDirectory, snapshot, asrRequest, 0.01, progress, cancellationToken)
                    .ConfigureAwait(false);
                envelopes = raw.Envelopes;
                wordSegments = raw.Segments;
                engineUsed = raw.EngineUsed;
                usedAsr = raw.Segments.Count > 0;

                if (usedAsr && isFullScope)
                    await SaveVerbatimCacheAsync(projectDirectory, wordSegments, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (opts.RemovePauses)
            {
                // Graceful fallback to pause-only optimization when ASR model/API is unavailable.
                warning = ex.Message;
                var (envList, _) = await transcriptionService.ExtractAudioEnvelopesAsync(
                    projectDirectory, snapshot, opts.TrackFilter, opts.ClipIds, opts.StartSeconds, opts.EndSeconds, 0.01, cancellationToken)
                    .ConfigureAwait(false);
                envelopes = envList;
                engineUsed = "wav-rms-fallback";
                usedAsr = false;
            }
        }
        else
        {
            progress?.Report(new SubtitleTranscriptionProgress("extracting", 35, "Scanning audio energy envelope..."));
            var (envList, _) = await transcriptionService.ExtractAudioEnvelopesAsync(
                projectDirectory, snapshot, opts.TrackFilter, opts.ClipIds, opts.StartSeconds, opts.EndSeconds, 0.01, cancellationToken)
                .ConfigureAwait(false);
            envelopes = envList;
        }

        progress?.Report(new SubtitleTranscriptionProgress("analyzing", 92, "Detecting pauses, filler words, and stutters..."));

        var rawCuts = new List<SpeechCutItem>();
        if (opts.RemovePauses)
        {
            rawCuts.AddRange(SpeechPolishAnalyzer.DetectPauseCuts(
                envelopes,
                opts.EffectiveMinPauseSeconds,
                opts.EffectivePausePaddingSeconds,
                opts.SilenceThresholdDb));
        }

        if (usedAsr && (opts.RemoveFillers || opts.EffectiveRemoveRepeats))
        {
            rawCuts.AddRange(SpeechPolishAnalyzer.DetectFillerAndRepeatCuts(wordSegments, opts, envelopes));
        }

        var finalCuts = SpeechPolishAnalyzer.MergeAndClampCuts(snapshot, rawCuts);
        int pauseCount = finalCuts.Count(c => c.Reason == "pause");
        int fillerCount = finalCuts.Count(c => c.Reason == "filler");
        int repeatCount = finalCuts.Count(c => c.Reason == "repeat");
        double totalRemovedSec = Math.Round(finalCuts.Sum(c => c.DurationSeconds), 2);

        object? command = finalCuts.Count > 0
            ? AiAgentCommandBuilder.BuildCutRangesCommand(finalCuts)
            : null;

        progress?.Report(new SubtitleTranscriptionProgress("done", 100, $"Detected {finalCuts.Count} speech cut(s)."));

        return new SpeechPolishResult(
            Cuts: finalCuts,
            Command: command,
            PauseCount: pauseCount,
            FillerCount: fillerCount,
            RepeatCount: repeatCount,
            TotalRemovedSeconds: totalRemovedSec,
            EngineUsed: engineUsed,
            UsedAsr: usedAsr,
            Warning: warning);
    }

    public static async Task SaveVerbatimCacheAsync(
        string projectDirectory,
        IReadOnlyList<TranscribedSegment> segments,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string cacheDir = Path.Combine(projectDirectory, "cache");
            Directory.CreateDirectory(cacheDir);
            string cachePath = Path.Combine(cacheDir, "ai-speech-verbatim.json");
            string tmpPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string json = JsonSerializer.Serialize(segments, CacheJsonOptions);
            await File.WriteAllTextAsync(tmpPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            File.Move(tmpPath, cachePath, overwrite: true);
        }
        catch
        {
            // Non-fatal cache write failure.
        }
    }

    public static async Task<IReadOnlyList<TranscribedSegment>> LoadVerbatimCacheAsync(
        string projectDirectory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string cachePath = Path.Combine(projectDirectory, "cache", "ai-speech-verbatim.json");
            if (!File.Exists(cachePath))
                return Array.Empty<TranscribedSegment>();
            string json = await File.ReadAllTextAsync(cachePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<List<TranscribedSegment>>(json, CacheJsonOptions);
            return parsed is { Count: > 0 } ? parsed : Array.Empty<TranscribedSegment>();
        }
        catch
        {
            return Array.Empty<TranscribedSegment>();
        }
    }
}
