using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FrameTrail.Domain;
using FrameTrail.Interop;

namespace FrameTrail.Workspace;

public enum AiProviderKind
{
    OpenAiApi,
    AnthropicApi,
    DeepSeekApi,
    OllamaLocal,
    ClaudeCodeCli,
    CodexCli,
    AntigravityCli
}

public sealed record AiAgentConfig(
    AiProviderKind Provider = AiProviderKind.OpenAiApi,
    string ModelName = "gpt-4o",
    string ApiKey = "",
    string BaseUrl = "",
    string SidecarUrl = "http://127.0.0.1:18765");

public sealed record AiToolCallRecord(string ToolName, string Summary, bool Succeeded);

public enum AiStreamEventKind
{
    Thinking,
    ToolStart,
    ToolEnd,
    Token,
    Done,
    Error
}

public sealed record AiStreamEvent(
    AiStreamEventKind Kind,
    string Text = "",
    string? ToolName = null,
    bool Succeeded = true);

public sealed record AiProposedChangeItem(
    string Badge,
    string Label,
    string Detail,
    double? SeekSeconds,
    string TimeLabel);

public sealed record AiAgentTurnResult(
    string ReplyText,
    IReadOnlyList<AiToolCallRecord> ToolCalls,
    object? ProposedCommand = null,
    long? AppliedRevision = null,
    long ElapsedMilliseconds = 0);

public sealed record AiChatMessage(string Role, string Content);

public sealed record AiTimelineClipSummary(
    string Id,
    double StartSec,
    double DurationSec,
    double SourceStartSec,
    double SourceDurationSec,
    double Rate);

public sealed record AiTimelineEffectSummary(
    string Id,
    string Kind,
    double StartSec,
    double DurationSec);

public sealed record AiCanvasSummary(
    uint Width,
    uint Height,
    double Padding,
    double CornerRadius,
    string FrameStyle);

public sealed record AiTimelineOverview(
    double TotalDurationSec,
    double SourceDurationSec,
    IReadOnlyList<AiTimelineClipSummary> Clips,
    IReadOnlyList<AiTimelineEffectSummary> Effects,
    AiCanvasSummary Canvas);

/// <summary>Builds validated FrameTrail native engine commands from high-level editing parameters.</summary>
public static class AiAgentCommandBuilder
{
    public const long TicksPerSecond = 10_000_000L;

    private static readonly HashSet<string> ValidFrameStyles = new(StringComparer.Ordinal)
    {
        "none", "window", "browser", "device",
        "windowLight", "windowDark", "browserLight", "browserDark", "devicePhone"
    };

    public static AiTimelineOverview BuildTimelineOverview(ProjectSnapshot snapshot, ResolvedTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(timeline);

        var editor = snapshot.Editor ?? new EditorSettings();
        var clips = timeline.Clips
            .Select(c => new AiTimelineClipSummary(
                c.Id,
                Math.Round(c.OutputStart / (double)TicksPerSecond, 4),
                Math.Round(c.OutputDuration / (double)TicksPerSecond, 4),
                Math.Round(c.SourceStart / (double)TicksPerSecond, 4),
                Math.Round(c.SourceDuration / (double)TicksPerSecond, 4),
                Math.Round((double)c.Numerator / Math.Max(1, c.Denominator), 4)))
            .ToArray();

        var effects = timeline.Effects
            .Select(e => new AiTimelineEffectSummary(
                e.Id,
                e.Kind,
                Math.Round(e.OutputStart / (double)TicksPerSecond, 4),
                Math.Round(e.OutputDuration / (double)TicksPerSecond, 4)))
            .ToArray();

        var canvas = new AiCanvasSummary(
            snapshot.Canvas.Width,
            snapshot.Canvas.Height,
            snapshot.Canvas.Padding,
            editor.CornerRadius,
            editor.FrameStyle);

        return new AiTimelineOverview(
            Math.Round(timeline.Duration / (double)TicksPerSecond, 4),
            Math.Round(VisualResources.Ticks(snapshot.SourceDuration) / (double)TicksPerSecond, 4),
            clips,
            effects,
            canvas);
    }

    public static object BuildCutTimeRangeCommand(ProjectSnapshot snapshot, ResolvedTimeline timeline, double startSec, double endSec)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(timeline);
        if (!double.IsFinite(startSec) || !double.IsFinite(endSec) || endSec <= startSec)
            throw new ArgumentException("Cut time range must have finite bounds with endSec > startSec.");

        long cutStart = Math.Max(0L, (long)Math.Round(startSec * TicksPerSecond, MidpointRounding.AwayFromZero));
        long cutEnd = Math.Min(timeline.Duration, (long)Math.Round(endSec * TicksPerSecond, MidpointRounding.AwayFromZero));
        if (cutEnd <= cutStart)
            throw new ArgumentException("Cut time range does not overlap the timeline.");

        var commands = new List<object>();
        int deletedClips = 0;

        foreach (var clip in timeline.Clips)
        {
            long clipOutStart = clip.OutputStart;
            long clipOutEnd = clip.OutputStart + clip.OutputDuration;
            long overlapOutStart = Math.Max(cutStart, clipOutStart);
            long overlapOutEnd = Math.Min(cutEnd, clipOutEnd);
            if (overlapOutEnd <= overlapOutStart) continue;

            long MapOutputOffsetToSource(long outTicks)
            {
                long deltaOut = outTicks - clipOutStart;
                long deltaSrc = (long)Math.Round((decimal)deltaOut * clip.Numerator / Math.Max(1, clip.Denominator), MidpointRounding.AwayFromZero);
                return Math.Clamp(clip.SourceStart + deltaSrc, clip.SourceStart, clip.SourceStart + clip.SourceDuration);
            }

            long clipSrcStart = clip.SourceStart;
            long clipSrcEnd = clip.SourceStart + clip.SourceDuration;
            long srcCutStart = MapOutputOffsetToSource(overlapOutStart);
            long srcCutEnd = MapOutputOffsetToSource(overlapOutEnd);
            if (srcCutEnd <= srcCutStart) continue;

            if (srcCutStart <= clipSrcStart && srcCutEnd >= clipSrcEnd)
            {
                commands.Add(new { op = "delete", segmentId = clip.Id });
                deletedClips++;
            }
            else if (srcCutStart <= clipSrcStart)
            {
                commands.Add(new { op = "trim", segmentId = clip.Id, sourceStart = srcCutEnd, sourceEnd = clipSrcEnd });
            }
            else if (srcCutEnd >= clipSrcEnd)
            {
                commands.Add(new { op = "trim", segmentId = clip.Id, sourceStart = clipSrcStart, sourceEnd = srcCutStart });
            }
            else
            {
                string rightId = "clip-" + Guid.NewGuid().ToString("N")[..12];
                commands.Add(new { op = "split", segmentId = clip.Id, sourceTicks = srcCutStart, newId = rightId });
                commands.Add(new { op = "trim", segmentId = rightId, sourceStart = srcCutEnd, sourceEnd = clipSrcEnd });
            }
        }

        if (commands.Count == 0)
            throw new ArgumentException("Cut time range does not overlap any timeline clip.");
        if (deletedClips >= timeline.Clips.Count)
            throw new InvalidOperationException("Cannot cut the entire timeline; at least one clip must remain.");

        return new { op = "batch", commands = commands.ToArray() };
    }

    public static object BuildSpeedCommand(string segmentId, double rate)
    {
        if (string.IsNullOrWhiteSpace(segmentId))
            throw new ArgumentException("Segment ID is required.", nameof(segmentId));
        if (!double.IsFinite(rate))
            throw new ArgumentOutOfRangeException(nameof(rate));

        double clamped = Math.Clamp(rate, 0.25, 4.0);
        const int precision = 1000;
        int numerator = Math.Max(1, (int)Math.Round(clamped * precision, MidpointRounding.AwayFromZero));
        int denominator = precision;
        int gcd = Gcd(numerator, denominator);
        numerator /= gcd;
        denominator /= gcd;

        return new { op = "rate", segmentId, numerator, denominator };
    }

    public static object BuildZoomEffectCommand(
        ProjectSnapshot snapshot,
        double startSec,
        double endSec,
        double scale = 1.6,
        double focusX = 0.5,
        double focusY = 0.5,
        string? effectId = null)
    {
        var (startTicks, durationTicks) = ResolveSourceEffectRange(snapshot, startSec, endSec);
        string id = NormalizeEffectId(effectId, "ai-zoom-");
        double clampedScale = Math.Clamp(FiniteOr(scale, 1.6), 1.05, 4.0);
        double clampedX = Math.Clamp(FiniteOr(focusX, 0.5), 0.0, 1.0);
        double clampedY = Math.Clamp(FiniteOr(focusY, 0.5), 0.0, 1.0);

        return new
        {
            op = "effectUpsert",
            effectId = id,
            effect = new
            {
                id,
                kind = "zoom",
                sourceStart = MediaTime.FromTicks(startTicks),
                sourceDuration = MediaTime.FromTicks(durationTicks),
                parameters = new
                {
                    origin = "manual",
                    scale = clampedScale,
                    focusX = clampedX,
                    focusY = clampedY,
                    easing = "cubic",
                    enter = 0.7,
                    exit = 0.7
                }
            }
        };
    }

    public static object BuildFocusOrMaskEffectCommand(
        ProjectSnapshot snapshot,
        string kind,
        double startSec,
        double endSec,
        double x = 0.25,
        double y = 0.25,
        double width = 0.5,
        double height = 0.5,
        double strength = 16,
        string? effectId = null)
    {
        string normalizedKind = kind?.Trim().ToLowerInvariant() ?? "";
        if (normalizedKind is not ("focus" or "mask"))
            throw new ArgumentException("Effect kind must be 'focus' or 'mask'.", nameof(kind));

        var (startTicks, durationTicks) = ResolveSourceEffectRange(snapshot, startSec, endSec);
        string id = NormalizeEffectId(effectId, $"ai-{normalizedKind}-");
        double clampedX = Math.Clamp(FiniteOr(x, 0.25), 0.0, 0.99);
        double clampedY = Math.Clamp(FiniteOr(y, 0.25), 0.0, 0.99);
        double clampedW = Math.Clamp(FiniteOr(width, 0.5), 0.01, 1.0 - clampedX);
        double clampedH = Math.Clamp(FiniteOr(height, 0.5), 0.01, 1.0 - clampedY);
        double clampedStrength = Math.Clamp(FiniteOr(strength, 16.0), 1.0, 64.0);

        object parameters = normalizedKind == "mask"
            ? new
            {
                origin = "manual",
                x = clampedX,
                y = clampedY,
                width = clampedW,
                height = clampedH,
                strength = clampedStrength,
                opacity = 1.0,
                mode = "blur",
                attachment = "screen"
            }
            : new
            {
                origin = "manual",
                x = clampedX,
                y = clampedY,
                width = clampedW,
                height = clampedH,
                strength = clampedStrength,
                opacity = 1.0,
                attachment = "screen"
            };

        return new
        {
            op = "effectUpsert",
            effectId = id,
            effect = new
            {
                id,
                kind = normalizedKind,
                sourceStart = MediaTime.FromTicks(startTicks),
                sourceDuration = MediaTime.FromTicks(durationTicks),
                parameters
            }
        };
    }

    public static object BuildTiltEffectCommand(
        ProjectSnapshot snapshot,
        double startSec,
        double endSec,
        double angleX = 8,
        double angleY = -10,
        string? effectId = null)
    {
        var (startTicks, durationTicks) = ResolveSourceEffectRange(snapshot, startSec, endSec);
        string id = NormalizeEffectId(effectId, "ai-tilt-");
        double clampedX = Math.Clamp(FiniteOr(angleX, 8.0), -35.0, 35.0);
        double clampedY = Math.Clamp(FiniteOr(angleY, -10.0), -35.0, 35.0);

        return new
        {
            op = "effectUpsert",
            effectId = id,
            effect = new
            {
                id,
                kind = "tilt",
                sourceStart = MediaTime.FromTicks(startTicks),
                sourceDuration = MediaTime.FromTicks(durationTicks),
                parameters = new
                {
                    origin = "manual",
                    angleX = clampedX,
                    angleY = clampedY,
                    easing = "cubic",
                    enter = 0.7,
                    exit = 0.7,
                    target = "screen"
                }
            }
        };
    }

    public static object BuildCanvasStyleCommand(
        ProjectSnapshot snapshot,
        uint? width = null,
        uint? height = null,
        double? padding = null,
        double? cornerRadius = null,
        string? frameStyle = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        uint nextWidth = width.HasValue ? EvenDimension(Math.Clamp(width.Value, 2u, 7680u)) : snapshot.Canvas.Width;
        uint nextHeight = height.HasValue ? EvenDimension(Math.Clamp(height.Value, 2u, 7680u)) : snapshot.Canvas.Height;
        double nextPadding = padding.HasValue ? Math.Clamp(FiniteOr(padding.Value, snapshot.Canvas.Padding), 0.0, 0.45) : snapshot.Canvas.Padding;

        var existingEditor = snapshot.Editor ?? new EditorSettings();
        double nextCorner = cornerRadius.HasValue ? Math.Clamp(FiniteOr(cornerRadius.Value, existingEditor.CornerRadius), 0.0, 200.0) : existingEditor.CornerRadius;
        string nextFrameStyle = existingEditor.FrameStyle;
        if (!string.IsNullOrWhiteSpace(frameStyle))
        {
            string trimmed = frameStyle.Trim();
            if (!ValidFrameStyles.Contains(trimmed))
                throw new ArgumentException($"Unsupported frameStyle: '{trimmed}'.", nameof(frameStyle));
            nextFrameStyle = trimmed;
        }

        var nextCanvas = snapshot.Canvas with
        {
            Width = nextWidth,
            Height = nextHeight,
            Padding = nextPadding
        };
        var nextEditor = existingEditor with
        {
            CornerRadius = nextCorner,
            FrameStyle = nextFrameStyle
        };

        return new { op = "settings", canvas = nextCanvas, editor = nextEditor };
    }

    public static object BuildSubtitlesReplaceCommand(
        IReadOnlyList<SourceEffect> subtitles,
        long? rangeStartTicks = null,
        long? rangeEndTicks = null,
        SubtitleStyleSettings? subtitleStyle = null)
    {
        ArgumentNullException.ThrowIfNull(subtitles);
        if (subtitles.Count > 10_000)
            throw new ArgumentOutOfRangeException(nameof(subtitles), "Subtitles replace supports at most 10,000 cues.");
        foreach (var cue in subtitles)
        {
            if (cue is null || !string.Equals(cue.Kind, "subtitle", StringComparison.Ordinal))
                throw new ArgumentException("All items in subtitlesReplace must be non-null effects with kind == 'subtitle'.", nameof(subtitles));
        }

        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["op"] = "subtitlesReplace",
            ["subtitles"] = subtitles.ToArray()
        };

        if (rangeStartTicks.HasValue && rangeEndTicks.HasValue)
        {
            if (rangeEndTicks.Value <= rangeStartTicks.Value)
                throw new ArgumentException("rangeEndTicks must be greater than rangeStartTicks.");
            payload["rangeStart"] = Math.Max(0L, rangeStartTicks.Value);
            payload["rangeEnd"] = rangeEndTicks.Value;
        }

        if (subtitleStyle is not null)
        {
            ProjectJson.ValidateSubtitleStyle(subtitleStyle);
            payload["subtitleStyle"] = subtitleStyle;
        }

        return payload;
    }

    public static object BuildSubtitleUpsertCommand(
        ProjectSnapshot snapshot,
        double startSec,
        double endSec,
        string text,
        string? secondaryText = null,
        IReadOnlyList<SubtitleWordTiming>? words = null,
        SubtitleCueStyleOverride? styleOverride = null,
        string? effectId = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Subtitle text is required.", nameof(text));

        var (startTicks, durationTicks) = ResolveSourceEffectRange(snapshot, startSec, endSec);
        string id = NormalizeEffectId(effectId, "sub-");
        var parameters = new SubtitleCueParameters
        {
            Text = text.Trim(),
            SecondaryText = string.IsNullOrWhiteSpace(secondaryText) ? null : secondaryText.Trim(),
            Words = words is { Count: > 0 } ? words : null,
            StyleOverride = styleOverride
        };

        return new
        {
            op = "effectUpsert",
            effectId = id,
            effect = new
            {
                id,
                kind = "subtitle",
                sourceStart = MediaTime.FromTicks(startTicks),
                sourceDuration = MediaTime.FromTicks(durationTicks),
                parameters = parameters.ToJsonElement()
            }
        };
    }

    public static object BuildSubtitleStyleCommand(ProjectSnapshot snapshot, SubtitleStyleSettings subtitleStyle)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(subtitleStyle);
        ProjectJson.ValidateSubtitleStyle(subtitleStyle);

        var existingEditor = snapshot.Editor ?? new EditorSettings();
        var nextEditor = existingEditor with { SubtitleStyle = subtitleStyle };
        return new { op = "settings", canvas = snapshot.Canvas, editor = nextEditor };
    }

    private static (long StartTicks, long DurationTicks) ResolveSourceEffectRange(ProjectSnapshot snapshot, double startSec, double endSec)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!double.IsFinite(startSec) || !double.IsFinite(endSec) || endSec <= startSec)
            throw new ArgumentException("Effect time range must have finite bounds with endSec > startSec.");

        long totalTicks = VisualResources.Ticks(snapshot.SourceDuration);
        if (totalTicks <= 0)
            throw new InvalidOperationException("Project source duration must be positive.");

        long startTicks = Math.Max(0L, (long)Math.Round(startSec * TicksPerSecond, MidpointRounding.AwayFromZero));
        if (startTicks >= totalTicks)
            throw new ArgumentOutOfRangeException(nameof(startSec), "Effect start time must be within the project source duration.");

        long endTicks = Math.Min(totalTicks, (long)Math.Round(endSec * TicksPerSecond, MidpointRounding.AwayFromZero));
        if (endTicks <= startTicks)
            endTicks = Math.Min(totalTicks, startTicks + 1);

        return (startTicks, endTicks - startTicks);
    }

    private static string NormalizeEffectId(string? effectId, string prefix)
    {
        if (string.IsNullOrWhiteSpace(effectId))
            return prefix + Guid.NewGuid().ToString("N")[..12];
        return effectId.Trim();
    }

    private static uint EvenDimension(uint value) => Math.Max(2u, (value / 2u) * 2u);
    private static double FiniteOr(double value, double fallback) => double.IsFinite(value) ? value : fallback;

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            int t = a % b;
            a = b;
            b = t;
        }
        return Math.Max(1, a);
    }

    public static string FormatTimestamp(double seconds)
    {
        double clamped = Math.Max(0.0, double.IsFinite(seconds) ? seconds : 0.0);
        int mins = (int)(clamped / 60.0);
        double secs = clamped - mins * 60.0;
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00.0}", mins, secs);
    }

    /// <summary>Converts any FrameTrail native edit command into structured UI diff rows with clickable seek timestamps.</summary>
    public static IReadOnlyList<AiProposedChangeItem> DescribeProposedChanges(
        object? command,
        ProjectSnapshot snapshot,
        ResolvedTimeline timeline)
    {
        if (command is null) return Array.Empty<AiProposedChangeItem>();
        JsonElement root;
        if (command is JsonElement existing)
        {
            root = existing;
        }
        else
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(command, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            using var doc = JsonDocument.Parse(bytes);
            root = doc.RootElement.Clone();
        }

        var items = new List<AiProposedChangeItem>();
        CollectChangeItems(root, snapshot, timeline, items);
        return items;
    }

    private static void CollectChangeItems(
        JsonElement cmd,
        ProjectSnapshot snapshot,
        ResolvedTimeline timeline,
        List<AiProposedChangeItem> output)
    {
        if (cmd.ValueKind != JsonValueKind.Object || !cmd.TryGetProperty("op", out var opEl)) return;
        string op = opEl.GetString() ?? "";

        if (op == "batch" && cmd.TryGetProperty("commands", out var sub) && sub.ValueKind == JsonValueKind.Array)
        {
            foreach (var inner in sub.EnumerateArray())
                CollectChangeItems(inner, snapshot, timeline, output);
            return;
        }

        switch (op)
        {
            case "effectUpsert":
                if (cmd.TryGetProperty("effect", out var eff) && eff.ValueKind == JsonValueKind.Object)
                {
                    string kind = eff.TryGetProperty("kind", out var kEl) ? kEl.GetString() ?? "effect" : "effect";
                    double startSec = ReadTimeSec(eff, "sourceStart");
                    double durSec = ReadTimeSec(eff, "sourceDuration");
                    double endSec = startSec + durSec;
                    string detail = kind switch
                    {
                        "zoom" when eff.TryGetProperty("parameters", out var p) && p.TryGetProperty("scale", out var sc) && sc.TryGetDouble(out var sv)
                            => $"{sv:0.##}×",
                        "tilt" => "3D perspective",
                        "focus" => "Spotlight blur",
                        "mask" => "Privacy blur",
                        "subtitle" when eff.TryGetProperty("parameters", out var sp) && sp.TryGetProperty("text", out var st) && st.ValueKind == JsonValueKind.String
                            => TruncatePreviewText(st.GetString() ?? "", 36),
                        _ => kind
                    };
                    string label = kind switch
                    {
                        "zoom" => "Zoom",
                        "focus" => "Focus",
                        "mask" => "Mask",
                        "tilt" => "3D Tilt",
                        "sticker" => "Sticker",
                        "intro" => "Intro",
                        "subtitle" => "Subtitle",
                        _ => kind
                    };
                    output.Add(new AiProposedChangeItem("+", label, detail, startSec, $"{FormatTimestamp(startSec)}–{FormatTimestamp(endSec)}"));
                }
                break;

            case "subtitlesReplace":
                {
                    int count = 0;
                    double minStart = double.PositiveInfinity;
                    double maxEnd = 0.0;
                    if (cmd.TryGetProperty("subtitles", out var subsEl) && subsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var subEl in subsEl.EnumerateArray())
                        {
                            count++;
                            double st = ReadTimeSec(subEl, "sourceStart");
                            double dur = ReadTimeSec(subEl, "sourceDuration");
                            if (st < minStart) minStart = st;
                            if (st + dur > maxEnd) maxEnd = st + dur;
                        }
                    }

                    if (count == 0)
                    {
                        double? rangeStart = cmd.TryGetProperty("rangeStart", out var rsEl) && rsEl.TryGetInt64(out var rsTicks)
                            ? rsTicks / (double)TicksPerSecond
                            : null;
                        double? rangeEnd = cmd.TryGetProperty("rangeEnd", out var reEl) && reEl.TryGetInt64(out var reTicks)
                            ? reTicks / (double)TicksPerSecond
                            : null;
                        string timeLabel = rangeStart.HasValue && rangeEnd.HasValue
                            ? $"{FormatTimestamp(rangeStart.Value)}–{FormatTimestamp(rangeEnd.Value)}"
                            : "";
                        output.Add(new AiProposedChangeItem("−", "Subtitles", "Cleared", rangeStart, timeLabel));
                    }
                    else
                    {
                        double seekSec = double.IsFinite(minStart) ? minStart : 0.0;
                        string timeLabel = double.IsFinite(minStart) && maxEnd > minStart
                            ? $"{FormatTimestamp(minStart)}–{FormatTimestamp(maxEnd)}"
                            : "";
                        string detail = $"{count} cue{(count == 1 ? "" : "s")}";
                        output.Add(new AiProposedChangeItem("+", "Subtitles", detail, seekSec, timeLabel));
                    }
                }
                break;

            case "effectDelete":
                string delEffId = cmd.TryGetProperty("effectId", out var de) ? de.GetString() ?? "" : "";
                output.Add(new AiProposedChangeItem("−", "Effect", delEffId, null, ""));
                break;

            case "delete":
                string delSeg = cmd.TryGetProperty("segmentId", out var ds) ? ds.GetString() ?? "" : "";
                var delClip = timeline.Clips.FirstOrDefault(c => c.Id == delSeg);
                double? delStart = delClip is null ? null : delClip.OutputStart / (double)TicksPerSecond;
                string delTime = delClip is null ? "" : $"{FormatTimestamp(delStart!.Value)}–{FormatTimestamp((delClip.OutputStart + delClip.OutputDuration) / (double)TicksPerSecond)}";
                output.Add(new AiProposedChangeItem("−", "Cut clip", delSeg, delStart, delTime));
                break;

            case "split":
                long cutTicks = cmd.TryGetProperty("sourceTicks", out var ct) && ct.TryGetInt64(out var ctVal) ? ctVal : 0;
                double cutSec = cutTicks / (double)TicksPerSecond;
                output.Add(new AiProposedChangeItem("~", "Split", cmd.TryGetProperty("segmentId", out var ss) ? ss.GetString() ?? "" : "", cutSec, FormatTimestamp(cutSec)));
                break;

            case "trim":
                long trimStart = cmd.TryGetProperty("sourceStart", out var ts) && ts.TryGetInt64(out var tsVal) ? tsVal : 0;
                long trimEnd = cmd.TryGetProperty("sourceEnd", out var te) && te.TryGetInt64(out var teVal) ? teVal : 0;
                double tStartSec = trimStart / (double)TicksPerSecond;
                double tEndSec = trimEnd / (double)TicksPerSecond;
                output.Add(new AiProposedChangeItem("~", "Trim", cmd.TryGetProperty("segmentId", out var trs) ? trs.GetString() ?? "" : "", tStartSec, $"{FormatTimestamp(tStartSec)}–{FormatTimestamp(tEndSec)}"));
                break;

            case "rate":
                int num = cmd.TryGetProperty("numerator", out var nEl) && nEl.TryGetInt32(out var nv) ? nv : 1;
                int den = cmd.TryGetProperty("denominator", out var dEl) && dEl.TryGetInt32(out var dv) ? Math.Max(1, dv) : 1;
                string rateSeg = cmd.TryGetProperty("segmentId", out var rs) ? rs.GetString() ?? "" : "";
                var rateClip = timeline.Clips.FirstOrDefault(c => c.Id == rateSeg);
                double? rStart = rateClip is null ? null : rateClip.OutputStart / (double)TicksPerSecond;
                output.Add(new AiProposedChangeItem("~", "Speed", $"{(double)num / den:0.##}×", rStart, rStart.HasValue ? FormatTimestamp(rStart.Value) : ""));
                break;

            case "settings":
                var parts = new List<string>();
                if (cmd.TryGetProperty("canvas", out var cv) && cv.ValueKind == JsonValueKind.Object)
                {
                    if (cv.TryGetProperty("width", out var w) && cv.TryGetProperty("height", out var h))
                        parts.Add($"{w.GetUInt32()}×{h.GetUInt32()}");
                    if (cv.TryGetProperty("padding", out var pad) && pad.TryGetDouble(out var pv))
                        parts.Add($"pad {pv * 100:0}%");
                }
                if (cmd.TryGetProperty("editor", out var ed) && ed.ValueKind == JsonValueKind.Object)
                {
                    if (ed.TryGetProperty("frameStyle", out var fs) && fs.ValueKind == JsonValueKind.String && fs.GetString() is { Length: > 0 } style && style != "none")
                        parts.Add(style);
                    if (ed.TryGetProperty("subtitleStyle", out var subStyle) && subStyle.ValueKind == JsonValueKind.Object)
                    {
                        string preset = subStyle.TryGetProperty("preset", out var pr) && pr.ValueKind == JsonValueKind.String
                            ? pr.GetString() ?? "custom"
                            : "custom";
                        parts.Add($"subtitles:{preset}");
                    }
                }
                output.Add(new AiProposedChangeItem("~", "Canvas", parts.Count > 0 ? string.Join(" · ", parts) : "Updated", null, ""));
                break;
        }
    }

    private static string TruncatePreviewText(string text, int maxChars)
    {
        string trimmed = text.Trim().Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ');
        return trimmed.Length <= maxChars ? trimmed : trimmed[..maxChars] + "…";
    }

    private static double ReadTimeSec(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.Object) return 0;
        long ticks = el.TryGetProperty("ticks", out var t) && t.TryGetInt64(out var tv) ? tv : 0;
        int scale = el.TryGetProperty("timescale", out var s) && s.TryGetInt32(out var sv) && sv > 0 ? sv : (int)TicksPerSecond;
        return ticks / (double)scale;
    }
}

/// <summary>Bridges FrameTrail's EditorSession with the Python LangGraph sidecar and provides deterministic local NL fallback.</summary>
public sealed class AiAgentBridge
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly Regex TimeRangeRegex = new(
        @"(?<start>\d+(?::\d{1,2})?(?:\.\d+)?)\s*(?:s|sec|secs|second|seconds|秒)?\s*(?:到|至|~|-|—|to)\s*(?<end>\d+(?::\d{1,2})?(?:\.\d+)?)\s*(?:s|sec|secs|second|seconds|秒)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SingleTimeRegex = new(
        @"(?:@|at|在)?\s*(?<time>\d+(?::\d{1,2})?(?:\.\d+)?)\s*(?:s|sec|secs|second|seconds|秒)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ColonTimeRegex = new(
        @"(?<min>\d{1,2}):(?<sec>\d{2}(?:\.\d+)?)",
        RegexOptions.Compiled);

    private static readonly Regex ScaleRegex = new(
        @"(?<scale>\d+(?:\.\d+)?)\s*(?:x|×|倍)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PercentRegex = new(
        @"(?<pct>\d+(?:\.\d+)?)\s*%",
        RegexOptions.Compiled);

    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;

    public AiAgentBridge(HttpClient? httpClient = null)
    {
        ownsHttpClient = httpClient is null;
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(240) };
    }

    public async Task<bool> IsSidecarHealthyAsync(string sidecarUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sidecarUrl)) return false;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await httpClient.GetAsync(sidecarUrl.TrimEnd('/') + "/api/health", cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    public async Task<IReadOnlyList<string>> GetLocalModelsAsync(string sidecarUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sidecarUrl)) return Array.Empty<string>();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await httpClient.GetAsync(sidecarUrl.TrimEnd('/') + "/api/models/local", cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Array.Empty<string>();
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false), cancellationToken: cts.Token).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                return models.EnumerateArray()
                    .Select(m => m.ValueKind == JsonValueKind.String ? m.GetString() : m.TryGetProperty("name", out var n) ? n.GetString() : null)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Cast<string>()
                    .ToArray();
            }
            return Array.Empty<string>();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return Array.Empty<string>(); }
    }

    public Task<AiAgentTurnResult> RunTurnAsync(
        EditorSession session,
        AiAgentConfig config,
        string userPrompt,
        double? playheadSec = null,
        IReadOnlyList<AiChatMessage>? history = null,
        bool dryRunOnly = false,
        CancellationToken ct = default) =>
        RunTurnStreamingAsync(session, config, userPrompt, playheadSec, history, dryRunOnly, callbackUrl: null, onEvent: null, ct);

    public async Task<AiAgentTurnResult> RunTurnStreamingAsync(
        EditorSession session,
        AiAgentConfig config,
        string userPrompt,
        double? playheadSec = null,
        IReadOnlyList<AiChatMessage>? history = null,
        bool dryRunOnly = false,
        string? callbackUrl = null,
        Action<AiStreamEvent>? onEvent = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(userPrompt))
            throw new ArgumentException("Prompt cannot be empty.", nameof(userPrompt));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var snapshot = session.Snapshot;
        var timeline = session.Timeline;
        var overview = AiAgentCommandBuilder.BuildTimelineOverview(snapshot, timeline);

        if (!string.IsNullOrWhiteSpace(config.SidecarUrl))
        {
            try
            {
                AiAgentTurnResult? sidecarResult = onEvent is not null
                    ? await TryRunSidecarStreamingTurnAsync(session, config, userPrompt, playheadSec, history, overview, dryRunOnly, callbackUrl, onEvent, sw, ct).ConfigureAwait(false)
                    : await TryRunSidecarTurnAsync(session, config, userPrompt, playheadSec, history, overview, dryRunOnly, callbackUrl, sw, ct).ConfigureAwait(false);
                if (sidecarResult is not null)
                    return sidecarResult;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException)
            {
                // Sidecar offline: fall through to deterministic local intent parser.
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // Sidecar timed out / unreachable: fall through to deterministic local intent parser.
            }
        }

        return await RunLocalFallbackTurnAsync(session, userPrompt, playheadSec, dryRunOnly, onEvent, sw, ct).ConfigureAwait(false);
    }

    private static object BuildSidecarPayload(
        EditorSession session,
        AiAgentConfig config,
        string userPrompt,
        double? playheadSec,
        IReadOnlyList<AiChatMessage>? history,
        AiTimelineOverview overview,
        string? callbackUrl)
    {
        string providerKey = config.Provider switch
        {
            AiProviderKind.OpenAiApi => "openai",
            AiProviderKind.AnthropicApi => "anthropic",
            AiProviderKind.DeepSeekApi => "deepseek",
            AiProviderKind.OllamaLocal => "ollama",
            AiProviderKind.ClaudeCodeCli => "claude_code_cli",
            AiProviderKind.CodexCli => "codex_cli",
            AiProviderKind.AntigravityCli => "antigravity_cli",
            _ => "openai"
        };

        return new
        {
            session_id = session.Snapshot.ProjectId,
            project_dir = session.DirectoryPath,
            prompt = userPrompt,
            playhead_sec = playheadSec ?? 0.0,
            provider = providerKey,
            model_name = config.ModelName,
            api_key = config.ApiKey,
            base_url = config.BaseUrl,
            callback_url = callbackUrl ?? "",
            project_snapshot = JsonSerializer.Deserialize<JsonElement>(ProjectJson.Serialize(session.Snapshot)),
            timeline_overview = overview,
            history = (history ?? Array.Empty<AiChatMessage>()).Select(m => new { role = m.Role, content = m.Content }).ToArray()
        };
    }

    private async Task<AiAgentTurnResult?> TryRunSidecarStreamingTurnAsync(
        EditorSession session,
        AiAgentConfig config,
        string userPrompt,
        double? playheadSec,
        IReadOnlyList<AiChatMessage>? history,
        AiTimelineOverview overview,
        bool dryRunOnly,
        string? callbackUrl,
        Action<AiStreamEvent> onEvent,
        System.Diagnostics.Stopwatch sw,
        CancellationToken ct)
    {
        var payload = BuildSidecarPayload(session, config, userPrompt, playheadSec, history, overview, callbackUrl);
        var endpoint = config.SidecarUrl.TrimEnd('/') + "/api/chat/stream";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed)
        {
            return await TryRunSidecarTurnAsync(session, config, userPrompt, playheadSec, history, overview, dryRunOnly, callbackUrl, sw, ct).ConfigureAwait(false);
        }
        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"Sidecar error ({(int)response.StatusCode}): {errBody}");
        }

        var tokenBuilder = new StringBuilder();
        var toolCalls = new List<AiToolCallRecord>();
        object? proposedCommand = null;
        string? finalReply = null;
        string currentEvent = "message";

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (line.Length == 0)
            {
                currentEvent = "message";
                continue;
            }
            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                currentEvent = line[6..].Trim();
                continue;
            }
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            string dataJson = line[5..].Trim();
            if (string.IsNullOrEmpty(dataJson)) continue;

            using var doc = JsonDocument.Parse(dataJson);
            var root = doc.RootElement;

            switch (currentEvent)
            {
                case "thinking":
                    string thought = root.TryGetProperty("text", out var th) ? th.GetString() ?? "" : "";
                    onEvent(new AiStreamEvent(AiStreamEventKind.Thinking, thought));
                    break;

                case "tool_start":
                    string tsName = root.TryGetProperty("tool_name", out var tsn) ? tsn.GetString() ?? "tool" : "tool";
                    string tsSummary = root.TryGetProperty("summary", out var tss) ? tss.GetString() ?? "" : "";
                    onEvent(new AiStreamEvent(AiStreamEventKind.ToolStart, tsSummary, tsName, true));
                    break;

                case "tool_end":
                    string teName = root.TryGetProperty("tool_name", out var ten) ? ten.GetString() ?? "tool" : "tool";
                    string teSummary = root.TryGetProperty("summary", out var tes) ? tes.GetString() ?? "" : "";
                    bool teOk = !root.TryGetProperty("succeeded", out var teOkEl) || teOkEl.ValueKind != JsonValueKind.False;
                    toolCalls.Add(new AiToolCallRecord(teName, teSummary, teOk));
                    onEvent(new AiStreamEvent(AiStreamEventKind.ToolEnd, teSummary, teName, teOk));
                    break;

                case "token":
                    string delta = root.TryGetProperty("text", out var tok) ? tok.GetString() ?? "" : "";
                    if (delta.Length > 0)
                    {
                        tokenBuilder.Append(delta);
                        onEvent(new AiStreamEvent(AiStreamEventKind.Token, delta));
                    }
                    break;

                case "error":
                    string detail = root.TryGetProperty("detail", out var err) ? err.GetString() ?? "Unknown sidecar error." : "Unknown sidecar error.";
                    onEvent(new AiStreamEvent(AiStreamEventKind.Error, detail));
                    throw new InvalidOperationException(detail);

                case "done":
                    if (root.TryGetProperty("reply", out var repEl) && repEl.ValueKind == JsonValueKind.String)
                        finalReply = repEl.GetString();
                    if (toolCalls.Count == 0 && root.TryGetProperty("tool_calls", out var tcArr) && tcArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in tcArr.EnumerateArray())
                        {
                            string name = item.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "tool" : "tool";
                            string summary = item.TryGetProperty("summary", out var sm) ? sm.GetString() ?? "" : "";
                            bool ok = !item.TryGetProperty("succeeded", out var sc) || sc.ValueKind != JsonValueKind.False;
                            toolCalls.Add(new AiToolCallRecord(name, summary, ok));
                        }
                    }
                    proposedCommand = ExtractProposedCommand(root);
                    break;
            }
        }

        string reply = !string.IsNullOrWhiteSpace(finalReply) ? finalReply! : tokenBuilder.ToString();
        long? appliedRevision = null;
        if (proposedCommand is not null)
        {
            var candidate = EngineSession.Edit(session.Snapshot, proposedCommand);
            EngineSession.ValidateProject(candidate);
            if (!dryRunOnly)
            {
                await session.ExecuteAsync(proposedCommand).ConfigureAwait(false);
                appliedRevision = session.Snapshot.Revision;
            }
        }

        onEvent(new AiStreamEvent(AiStreamEventKind.Done, reply));
        return new AiAgentTurnResult(reply, toolCalls, proposedCommand, appliedRevision, sw.ElapsedMilliseconds);
    }

    private async Task<AiAgentTurnResult?> TryRunSidecarTurnAsync(
        EditorSession session,
        AiAgentConfig config,
        string userPrompt,
        double? playheadSec,
        IReadOnlyList<AiChatMessage>? history,
        AiTimelineOverview overview,
        bool dryRunOnly,
        string? callbackUrl,
        System.Diagnostics.Stopwatch sw,
        CancellationToken ct)
    {
        var payload = BuildSidecarPayload(session, config, userPrompt, playheadSec, history, overview, callbackUrl);
        var endpoint = config.SidecarUrl.TrimEnd('/') + "/api/chat";
        using var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync(endpoint, content, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"Sidecar error ({(int)response.StatusCode}): {errBody}");
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;
        string reply = root.TryGetProperty("reply", out var replyEl) && replyEl.ValueKind == JsonValueKind.String
            ? replyEl.GetString() ?? ""
            : "";

        var toolCalls = new List<AiToolCallRecord>();
        if (root.TryGetProperty("tool_calls", out var tcEl) && tcEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in tcEl.EnumerateArray())
            {
                string name = item.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "tool" : "tool";
                string summary = item.TryGetProperty("summary", out var sm) ? sm.GetString() ?? "" : "";
                bool ok = !item.TryGetProperty("succeeded", out var sc) || sc.ValueKind != JsonValueKind.False;
                toolCalls.Add(new AiToolCallRecord(name, summary, ok));
            }
        }

        object? proposedCommand = ExtractProposedCommand(root);
        long? appliedRevision = null;
        if (proposedCommand is not null)
        {
            var candidate = EngineSession.Edit(session.Snapshot, proposedCommand);
            EngineSession.ValidateProject(candidate);
            if (!dryRunOnly)
            {
                await session.ExecuteAsync(proposedCommand).ConfigureAwait(false);
                appliedRevision = session.Snapshot.Revision;
            }
        }

        return new AiAgentTurnResult(reply, toolCalls, proposedCommand, appliedRevision, sw.ElapsedMilliseconds);
    }

    private static object? ExtractProposedCommand(JsonElement root)
    {
        if (root.TryGetProperty("proposed_command", out var cmd) && cmd.ValueKind == JsonValueKind.Object)
            return cmd.Clone();

        if (root.TryGetProperty("proposed_commands", out var cmds) && cmds.ValueKind == JsonValueKind.Array)
        {
            var list = cmds.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(e => e.Clone()).ToArray();
            if (list.Length == 1) return list[0];
            if (list.Length > 1) return new { op = "batch", commands = list };
        }

        return null;
    }

    private static async Task<AiAgentTurnResult> RunLocalFallbackTurnAsync(
        EditorSession session,
        string userPrompt,
        double? playheadSec,
        bool dryRunOnly,
        Action<AiStreamEvent>? onEvent,
        System.Diagnostics.Stopwatch sw,
        CancellationToken ct)
    {
        var snapshot = session.Snapshot;
        var timeline = session.Timeline;
        onEvent?.Invoke(new AiStreamEvent(AiStreamEventKind.Thinking, "Parsing editing intent locally against timeline context..."));

        // Multimodal local intents (silence removal & click-driven auto zoom) powered by AiMultimodalService
        var multimodalResult = await TryRunLocalMultimodalIntentAsync(session, userPrompt, onEvent, ct).ConfigureAwait(false);
        (string ToolName, string Summary, string ReplyText, object? Command)? parsed = multimodalResult ?? TryParseLocalIntent(snapshot, timeline, userPrompt, playheadSec);

        if (parsed is null)
        {
            string help = L.T("AiStatusOfflineFallback") + " — " +
                "你可以这样描述需求：\n" +
                "- `在 1s 到 3s 添加 1.8 倍放大特效`\n" +
                "- `自动清理静音停顿` / `在关键点击处添加放大`\n" +
                "- `把画布改为 9:16 竖屏` / `把片段倍速设为 2x`\n" +
                "- `在 @当前时间 添加聚焦虚化` 或 `剪掉 1s 到 2s`";
            onEvent?.Invoke(new AiStreamEvent(AiStreamEventKind.Token, help));
            onEvent?.Invoke(new AiStreamEvent(AiStreamEventKind.Done, help));
            return new AiAgentTurnResult(help, Array.Empty<AiToolCallRecord>(), null, null, sw.ElapsedMilliseconds);
        }

        var (toolName, summary, replyText, command) = parsed.Value;
        onEvent?.Invoke(new AiStreamEvent(AiStreamEventKind.ToolStart, summary, toolName, true));
        long? appliedRevision = null;

        if (command is not null)
        {
            var candidate = EngineSession.Edit(snapshot, command);
            EngineSession.ValidateProject(candidate);
            if (!dryRunOnly)
            {
                await session.ExecuteAsync(command).ConfigureAwait(false);
                appliedRevision = session.Snapshot.Revision;
            }
        }

        onEvent?.Invoke(new AiStreamEvent(AiStreamEventKind.ToolEnd, summary, toolName, true));
        onEvent?.Invoke(new AiStreamEvent(AiStreamEventKind.Token, replyText));
        onEvent?.Invoke(new AiStreamEvent(AiStreamEventKind.Done, replyText));

        return new AiAgentTurnResult(
            replyText,
            new[] { new AiToolCallRecord(toolName, summary, true) },
            command,
            appliedRevision,
            sw.ElapsedMilliseconds);
    }

    private static async Task<(string ToolName, string Summary, string ReplyText, object? Command)?> TryRunLocalMultimodalIntentAsync(
        EditorSession session,
        string prompt,
        Action<AiStreamEvent>? onEvent,
        CancellationToken ct)
    {
        string lower = prompt.ToLowerInvariant();
        var snapshot = session.Snapshot;

        // A. Silence / pause removal via native WAV analyzer (AiMultimodalService)
        if (ContainsAny(lower, "静音", "停顿", "气口", "silence", "pause"))
        {
            onEvent?.Invoke(new AiStreamEvent(AiStreamEventKind.ToolStart, "Scanning project WAV tracks for silent pauses", "detect_silence_and_pauses", true));
            var multimodal = new AiMultimodalService();
            var audioTracks = snapshot.Media
                .Where(m => m.Kind is MediaKind.Microphone or MediaKind.SystemAudio or MediaKind.AndroidAudio)
                .ToArray();

            var allSilences = new List<SilentInterval>();
            foreach (var track in audioTracks)
            {
                ct.ThrowIfCancellationRequested();
                string fullPath = Path.Combine(session.DirectoryPath, track.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fullPath) || !fullPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var offsetTicks = VisualResources.Ticks(track.StartOffset);
                    foreach (var s in multimodal.DetectWavSilence(fullPath, minSilenceSeconds: 1.0, thresholdDb: -38.0))
                    {
                        allSilences.Add(s with
                        {
                            StartTicks = s.StartTicks + offsetTicks,
                            EndTicks = s.EndTicks + offsetTicks,
                            StartSeconds = (s.StartTicks + offsetTicks) / (double)AiAgentCommandBuilder.TicksPerSecond
                        });
                    }
                }
                catch
                {
                    // Skip unreadable tracks.
                }
            }

            if (allSilences.Count == 0)
            {
                return ("detect_silence_and_pauses", "No silence > 1.0s found", "已扫描工程音轨，未检测到超过 **1.0s** 的明显静音停顿片段。", null);
            }

            var batch = multimodal.BuildRemoveSilenceBatchCommand(snapshot, allSilences);
            if (batch is null)
            {
                return ("detect_silence_and_pauses", $"{allSilences.Count} span(s), nothing removable", $"检测到 **{allSilences.Count}** 段静音，但都在可保留片段边界之外（至少需保留一个有效片段）。", null);
            }

            return (
                "detect_silence_and_pauses",
                $"Found {allSilences.Count} silent span(s)",
                $"已在音轨中检测到 **{allSilences.Count}** 处超过 1.0s 的静音停顿，并生成了非破坏性裁剪方案。",
                batch);
        }

        // B. Pointer-click driven smart zoom via PointerEvents (events/*.jsonl)
        if (ContainsAny(lower, "点击", "鼠标", "click", "cursor") && ContainsAny(lower, "放大", "运镜", "zoom", "聚焦", "focus"))
        {
            var pointerMedia = snapshot.Media.Where(m => m.Kind == MediaKind.PointerEvents).ToArray();
            if (pointerMedia.Length > 0)
            {
                onEvent?.Invoke(new AiStreamEvent(AiStreamEventKind.ToolStart, "Analyzing pointer click hotspots", "extract_pointer_hotspots", true));
                var commands = new List<object>();
                double totalSourceSec = VisualResources.Ticks(snapshot.SourceDuration) / (double)AiAgentCommandBuilder.TicksPerSecond;
                foreach (var media in pointerMedia)
                {
                    ct.ThrowIfCancellationRequested();
                    string fullPath = Path.Combine(session.DirectoryPath, media.Path.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(fullPath)) continue;
                    var offsetTicks = VisualResources.Ticks(media.StartOffset);
                    long lastClickTicks = long.MinValue;
                    await foreach (var line in ReadLinesAsync(fullPath, ct).ConfigureAwait(false))
                    {
                        if (string.IsNullOrWhiteSpace(line) || line.Length > 32768) continue;
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            var e = doc.RootElement;
                            if (!e.TryGetProperty("kind", out var k) || k.GetInt32() is not (1 or 3)) continue;
                            long rawTicks = e.GetProperty("ticks").GetInt64();
                            int scale = e.TryGetProperty("timescale", out var ts) ? Math.Max(1, ts.GetInt32()) : (int)AiAgentCommandBuilder.TicksPerSecond;
                            long srcTicks = checked(offsetTicks + (long)Math.Round(rawTicks * (double)AiAgentCommandBuilder.TicksPerSecond / scale));
                            if (srcTicks - lastClickTicks < 25_000_000L) continue; // cluster within 2.5s
                            lastClickTicks = srcTicks;
                            double x = Math.Clamp(e.GetProperty("x").GetDouble(), 0.0, 1.0);
                            double y = Math.Clamp(e.GetProperty("y").GetDouble(), 0.0, 1.0);
                            double sSec = Math.Max(0.0, srcTicks / (double)AiAgentCommandBuilder.TicksPerSecond - 0.35);
                            double eSec = Math.Min(totalSourceSec, sSec + 2.0);
                            if (eSec - sSec >= 0.25)
                            {
                                commands.Add(AiAgentCommandBuilder.BuildZoomEffectCommand(snapshot, sSec, eSec, 1.8, x, y));
                                if (commands.Count >= 8) break;
                            }
                        }
                        catch
                        {
                            // Skip malformed pointer event line.
                        }
                    }
                    if (commands.Count >= 8) break;
                }

                if (commands.Count > 0)
                {
                    object finalCmd = commands.Count == 1 ? commands[0] : new { op = "batch", commands = commands.ToArray() };
                    return (
                        "extract_pointer_hotspots",
                        $"Generated {commands.Count} click-targeted zoom(s)",
                        $"已从录制光标事件中识别出 **{commands.Count}** 处关键点击坐标，并在对应时间点生成了 `1.8×` 精准聚焦放大运镜。",
                        finalCmd);
                }
            }
        }

        return null;
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(path);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            yield return line;
    }

    public static (string ToolName, string Summary, string ReplyText, object? Command)? TryParseLocalIntent(
        ProjectSnapshot snapshot,
        ResolvedTimeline timeline,
        string prompt,
        double? playheadSec = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(timeline);
        if (string.IsNullOrWhiteSpace(prompt)) return null;

        string lower = prompt.ToLowerInvariant();
        double totalSourceSec = Math.Max(0.1, VisualResources.Ticks(snapshot.SourceDuration) / (double)AiAgentCommandBuilder.TicksPerSecond);
        double totalTimelineSec = Math.Max(0.1, timeline.Duration / (double)AiAgentCommandBuilder.TicksPerSecond);
        double anchor = Math.Clamp(playheadSec ?? 0.0, 0.0, Math.Max(0.0, totalSourceSec - 0.1));

        (double startSec, double endSec) ExtractSourceWindow()
        {
            if (TryExtractTimeRange(prompt, out var s, out var e))
            {
                double cs = Math.Clamp(s, 0.0, Math.Max(0.0, totalSourceSec - 0.05));
                double ce = Math.Clamp(e, cs + 0.05, totalSourceSec);
                return (cs, ce);
            }
            if (TryExtractSingleTime(prompt, out var t))
            {
                double cs = Math.Clamp(t, 0.0, Math.Max(0.0, totalSourceSec - 0.05));
                double ce = Math.Min(totalSourceSec, cs + 2.0);
                return (cs, Math.Max(cs + 0.05, ce));
            }
            double ds = anchor;
            double de = Math.Min(totalSourceSec, ds + 2.0);
            if (de <= ds) ds = Math.Max(0.0, de - 1.0);
            return (ds, de);
        }

        // 1. Timeline overview query
        if (ContainsAny(lower, "overview", "概览", "查看时间线", "时间线信息", "多少秒", "duration"))
        {
            var ov = AiAgentCommandBuilder.BuildTimelineOverview(snapshot, timeline);
            string msg = $"Timeline: {ov.TotalDurationSec:0.##}s across {ov.Clips.Count} clip(s), {ov.Effects.Count} effect(s), canvas {ov.Canvas.Width}x{ov.Canvas.Height}.";
            return ("get_timeline_overview", $"{ov.Clips.Count} clips, {ov.TotalDurationSec:0.##}s", msg, null);
        }

        // 2. Cut / delete time range
        if (ContainsAny(lower, "剪掉", "剪除", "裁掉", "删除", "去掉", "cut", "remove", "trim out"))
        {
            if (!TryExtractTimeRange(prompt, out var s, out var e))
            {
                s = anchor;
                e = Math.Min(totalTimelineSec, s + 1.0);
            }
            s = Math.Clamp(s, 0.0, Math.Max(0.0, totalTimelineSec - 0.05));
            e = Math.Clamp(e, s + 0.05, totalTimelineSec);
            var cmd = AiAgentCommandBuilder.BuildCutTimeRangeCommand(snapshot, timeline, s, e);
            return ("cut_time_range", $"Cut {s:0.##}s - {e:0.##}s", $"Cut time range {s:0.##}s to {e:0.##}s.", cmd);
        }

        // 3. Zoom effect
        if (ContainsAny(lower, "放大", "缩放", "运镜", "zoom"))
        {
            var (s, e) = ExtractSourceWindow();
            double scale = 1.6;
            var sm = ScaleRegex.Match(prompt);
            if (sm.Success && double.TryParse(sm.Groups["scale"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedScale))
                scale = Math.Clamp(parsedScale, 1.05, 4.0);
            var cmd = AiAgentCommandBuilder.BuildZoomEffectCommand(snapshot, s, e, scale);
            return ("add_zoom_effect", $"Zoom {scale:0.##}x ({s:0.##}s-{e:0.##}s)", $"Added {scale:0.##}x zoom effect from {s:0.##}s to {e:0.##}s.", cmd);
        }

        // 4. Focus / Spotlight
        if (ContainsAny(lower, "聚焦", "聚光灯", "虚化", "spotlight", "focus"))
        {
            var (s, e) = ExtractSourceWindow();
            var cmd = AiAgentCommandBuilder.BuildFocusOrMaskEffectCommand(snapshot, "focus", s, e);
            return ("add_spotlight_or_blur", $"Focus ({s:0.##}s-{e:0.##}s)", $"Added focus spotlight effect from {s:0.##}s to {e:0.##}s.", cmd);
        }

        // 5. Mask / Blur / Redact
        if (ContainsAny(lower, "打码", "遮挡", "马赛克", "模糊", "mask", "pixelate", "redact", "blur"))
        {
            var (s, e) = ExtractSourceWindow();
            var cmd = AiAgentCommandBuilder.BuildFocusOrMaskEffectCommand(snapshot, "mask", s, e);
            return ("add_spotlight_or_blur", $"Mask ({s:0.##}s-{e:0.##}s)", $"Added mask blur effect from {s:0.##}s to {e:0.##}s.", cmd);
        }

        // 6. 3D Tilt
        if (ContainsAny(lower, "倾斜", "透视", "tilt", "3d"))
        {
            var (s, e) = ExtractSourceWindow();
            var cmd = AiAgentCommandBuilder.BuildTiltEffectCommand(snapshot, s, e);
            return ("add_3d_tilt_effect", $"3D Tilt ({s:0.##}s-{e:0.##}s)", $"Added 3D tilt effect from {s:0.##}s to {e:0.##}s.", cmd);
        }

        // 7. Canvas style / aspect ratio
        if (ContainsAny(lower, "画布", "竖屏", "横屏", "9:16", "16:9", "1:1", "4:3", "圆角", "留白", "样机", "canvas", "aspect", "portrait", "landscape", "padding", "corner", "frame"))
        {
            uint? w = null, h = null;
            if (lower.Contains("9:16", StringComparison.Ordinal) || ContainsAny(lower, "竖屏", "portrait", "抖音", "tiktok", "shorts"))
            {
                w = 1080; h = 1920;
            }
            else if (lower.Contains("16:9", StringComparison.Ordinal) || ContainsAny(lower, "横屏", "landscape"))
            {
                w = 1920; h = 1080;
            }
            else if (lower.Contains("1:1", StringComparison.Ordinal) || ContainsAny(lower, "正方形", "square"))
            {
                w = 1080; h = 1080;
            }
            else if (lower.Contains("4:3", StringComparison.Ordinal))
            {
                w = 1440; h = 1080;
            }

            double? padding = null;
            if (ContainsAny(lower, "留白", "padding"))
            {
                var pm = PercentRegex.Match(prompt);
                if (pm.Success && double.TryParse(pm.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                    padding = Math.Clamp(pct / 100.0, 0.0, 0.45);
            }

            string? frameStyle = null;
            if (ContainsAny(lower, "browser", "浏览器")) frameStyle = "browser";
            else if (ContainsAny(lower, "window", "窗口")) frameStyle = "window";
            else if (ContainsAny(lower, "device", "样机", "手机框")) frameStyle = "device";
            else if (ContainsAny(lower, "无边框", "no frame")) frameStyle = "none";

            var cmd = AiAgentCommandBuilder.BuildCanvasStyleCommand(snapshot, w, h, padding, null, frameStyle);
            string desc = w.HasValue ? $"{w}x{h}" : (frameStyle ?? "canvas settings");
            return ("update_canvas_style", $"Canvas -> {desc}", $"Updated canvas style ({desc}).", cmd);
        }

        // 8. Speed / rate
        if (ContainsAny(lower, "倍速", "加速", "慢放", "速度", "speed", "rate") || ScaleRegex.IsMatch(prompt))
        {
            double rate = ContainsAny(lower, "慢放", "slow") ? 0.5 : 2.0;
            var rm = ScaleRegex.Match(prompt);
            if (rm.Success && double.TryParse(rm.Groups["scale"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedRate))
                rate = Math.Clamp(parsedRate, 0.25, 4.0);

            string targetSegmentId = ResolveSegmentIdAtOutputSec(timeline, playheadSec) ?? snapshot.Segments[0].Id;
            var cmd = AiAgentCommandBuilder.BuildSpeedCommand(targetSegmentId, rate);
            return ("change_segment_speed", $"Clip {targetSegmentId} -> {rate:0.##}x", $"Set clip speed to {rate:0.##}x.", cmd);
        }

        return null;
    }

    private static string? ResolveSegmentIdAtOutputSec(ResolvedTimeline timeline, double? playheadSec)
    {
        if (timeline.Clips.Count == 0) return null;
        if (!playheadSec.HasValue) return timeline.Clips[0].Id;
        long ticks = (long)Math.Round(playheadSec.Value * AiAgentCommandBuilder.TicksPerSecond);
        var match = timeline.Clips.FirstOrDefault(c => ticks >= c.OutputStart && ticks < c.OutputStart + c.OutputDuration);
        return match?.Id ?? timeline.Clips[0].Id;
    }

    private static bool TryExtractTimeRange(string input, out double startSec, out double endSec)
    {
        var match = TimeRangeRegex.Match(input);
        if (match.Success &&
            TryParseTimestamp(match.Groups["start"].Value, out startSec) &&
            TryParseTimestamp(match.Groups["end"].Value, out endSec) &&
            endSec > startSec)
        {
            return true;
        }
        startSec = endSec = 0;
        return false;
    }

    private static bool TryExtractSingleTime(string input, out double sec)
    {
        var match = SingleTimeRegex.Match(input);
        if (match.Success && TryParseTimestamp(match.Groups["time"].Value, out sec))
            return true;
        var colon = ColonTimeRegex.Match(input);
        if (colon.Success && TryParseTimestamp(colon.Value, out sec))
            return true;
        sec = 0;
        return false;
    }

    private static bool TryParseTimestamp(string raw, out double seconds)
    {
        var colon = ColonTimeRegex.Match(raw);
        if (colon.Success &&
            int.TryParse(colon.Groups["min"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mins) &&
            double.TryParse(colon.Groups["sec"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double secs))
        {
            seconds = mins * 60.0 + secs;
            return double.IsFinite(seconds) && seconds >= 0;
        }
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && double.IsFinite(seconds) && seconds >= 0;
    }

    private static bool ContainsAny(string text, params string[] tokens) =>
        tokens.Any(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
}
