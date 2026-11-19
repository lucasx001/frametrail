using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FrameTrail.Domain;
using FrameTrail.Interop;

namespace FrameTrail.Workspace;

public enum SubtitleExportFormat
{
    Srt,
    Vtt,
    Txt
}

public sealed record SubtitleCueDraft(
    long StartTicks,
    long EndTicks,
    string Text,
    string? SecondaryText = null);

/// <summary>Lossless .srt / .vtt / .txt subtitle codec with ResolvedTimeline output <-> source projection.</summary>
public static class SrtVttCodec
{
    public const long TicksPerSecond = 10_000_000L;
    private const long TicksPerMillisecond = 10_000L;

    private static readonly Regex TimingLineRegex = new(
        @"^\s*(?<start>(?:\d{1,2}:)?\d{1,2}:\d{2}[,.]\d{1,3})\s*-->\s*(?<end>(?:\d{1,2}:)?\d{1,2}:\d{2}[,.]\d{1,3})(?:\s+.*)?$",
        RegexOptions.Compiled);

    private static readonly Regex HtmlTagRegex = new(
        @"</?(?:i|b|u|s|font|ruby|rt|v|c|lang)(?:\.[^>\s]*)*(?:\s+[^>]*)?>|<\d{1,2}:\d{2}(?::\d{2})?[.,]\d{1,3}>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Parses .srt or WEBVTT (.vtt) subtitle content into normalized output-timeline cues.</summary>
    public static IReadOnlyList<SubtitleCueDraft> Parse(string content, bool splitBilingualLines = true)
    {
        if (string.IsNullOrWhiteSpace(content))
            return Array.Empty<SubtitleCueDraft>();

        // Strip UTF-8 BOM if present and normalize newlines.
        string normalized = content.Length > 0 && content[0] == '\uFEFF' ? content[1..] : content;
        normalized = normalized.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        string[] lines = normalized.Split('\n');
        var cues = new List<SubtitleCueDraft>();
        int i = 0;

        // Skip optional WEBVTT header block.
        if (lines.Length > 0 && lines[0].TrimStart().StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase))
        {
            i = 1;
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
                i++;
        }

        while (i < lines.Length)
        {
            while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i]))
                i++;
            if (i >= lines.Length)
                break;

            string firstLine = lines[i].Trim();

            // Skip WEBVTT NOTE / STYLE / REGION blocks.
            if (firstLine.StartsWith("NOTE", StringComparison.OrdinalIgnoreCase) &&
                (firstLine.Length == 4 || char.IsWhiteSpace(firstLine[4])) ||
                firstLine.Equals("STYLE", StringComparison.OrdinalIgnoreCase) ||
                firstLine.Equals("REGION", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
                    i++;
                continue;
            }

            // Locate timing line (may be preceded by a numeric SRT index or VTT cue identifier).
            Match timingMatch = TimingLineRegex.Match(lines[i]);
            if (!timingMatch.Success)
            {
                if (i + 1 < lines.Length && TimingLineRegex.IsMatch(lines[i + 1]))
                {
                    i++;
                    timingMatch = TimingLineRegex.Match(lines[i]);
                }
                else
                {
                    // Unrecognized block; advance to next blank line.
                    i++;
                    while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
                        i++;
                    continue;
                }
            }

            long startTicks = ParseTimestampTicks(timingMatch.Groups["start"].Value);
            long endTicks = ParseTimestampTicks(timingMatch.Groups["end"].Value);
            i++;

            var textLines = new List<string>();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
            {
                string cleaned = CleanCueLine(lines[i]);
                if (!string.IsNullOrWhiteSpace(cleaned))
                    textLines.Add(cleaned);
                i++;
            }

            if (endTicks <= startTicks || textLines.Count == 0)
                continue;

            string primary;
            string? secondary = null;
            if (splitBilingualLines && textLines.Count == 2 && IsLikelyBilingualPair(textLines[0], textLines[1]))
            {
                primary = textLines[0];
                secondary = textLines[1];
            }
            else
            {
                primary = string.Join("\n", textLines);
            }

            cues.Add(new SubtitleCueDraft(startTicks, endTicks, primary, secondary));
        }

        cues.Sort((a, b) =>
        {
            int cmp = a.StartTicks.CompareTo(b.StartTicks);
            return cmp != 0 ? cmp : a.EndTicks.CompareTo(b.EndTicks);
        });
        return cues;
    }

    /// <summary>
    /// Parses .srt / .vtt content and maps output-time cues back onto source-time <see cref="SourceEffect"/> entries
    /// using the active <see cref="ResolvedTimeline"/> (accounting for trims, cuts, reordering, and speed rates).
    /// </summary>
    public static IReadOnlyList<SourceEffect> ImportToSourceEffects(
        string content,
        ResolvedTimeline timeline,
        long? maxSourceDurationTicks = null,
        bool splitBilingualLines = true)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var cues = Parse(content, splitBilingualLines);
        if (cues.Count == 0 || timeline.Clips.Count == 0)
            return Array.Empty<SourceEffect>();

        var effects = new List<SourceEffect>(cues.Count);
        foreach (var cue in cues)
        {
            foreach (var clip in timeline.Clips)
            {
                long clipOutStart = clip.OutputStart;
                long clipOutEnd = clip.OutputStart + clip.OutputDuration;
                long ovOutStart = Math.Max(cue.StartTicks, clipOutStart);
                long ovOutEnd = Math.Min(cue.EndTicks, clipOutEnd);
                if (ovOutEnd <= ovOutStart)
                    continue;

                long srcStart = MapOutputTicksToSource(clip, ovOutStart);
                long srcEnd = MapOutputTicksToSource(clip, ovOutEnd);

                if (maxSourceDurationTicks.HasValue)
                {
                    srcStart = Math.Clamp(srcStart, 0L, maxSourceDurationTicks.Value);
                    srcEnd = Math.Clamp(srcEnd, 0L, maxSourceDurationTicks.Value);
                }

                if (srcEnd <= srcStart)
                    continue;

                var parameters = new SubtitleCueParameters
                {
                    Text = TruncateUtf8(cue.Text, 1024),
                    SecondaryText = string.IsNullOrWhiteSpace(cue.SecondaryText) ? null : TruncateUtf8(cue.SecondaryText, 1024)
                };

                effects.Add(new SourceEffect
                {
                    Id = "sub-" + Guid.NewGuid().ToString("N")[..12],
                    Kind = "subtitle",
                    SourceStart = MediaTime.FromTicks(srcStart),
                    SourceDuration = MediaTime.FromTicks(srcEnd - srcStart),
                    Parameters = parameters.ToJsonElement()
                });
            }
        }

        effects.Sort((a, b) => a.SourceStart.Ticks.CompareTo(b.SourceStart.Ticks));
        return effects;
    }

    /// <summary>
    /// Projects source-time subtitle effects in <paramref name="snapshot"/> onto the output <paramref name="timeline"/>,
    /// respecting clip cuts, trims, reordering, and playback speed ratios.
    /// </summary>
    public static IReadOnlyList<SubtitleCueDraft> ProjectToOutputCues(
        ProjectSnapshot snapshot,
        ResolvedTimeline timeline,
        bool? includeSecondary = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(timeline);

        var subtitleEffects = snapshot.Effects
            .Where(e => string.Equals(e.Kind, "subtitle", StringComparison.Ordinal))
            .ToArray();
        if (subtitleEffects.Length == 0 || timeline.Clips.Count == 0)
            return Array.Empty<SubtitleCueDraft>();

        bool emitSecondary = includeSecondary ?? (snapshot.Editor?.SubtitleStyle is null || snapshot.Editor.SubtitleStyle.BilingualEnabled);
        var projected = new List<(SubtitleCueDraft Cue, string SourceEffectId)>();

        foreach (var clip in timeline.Clips.OrderBy(c => c.OutputStart))
        {
            long clipSrcStart = clip.SourceStart;
            long clipSrcEnd = clip.SourceStart + clip.SourceDuration;

            foreach (var effect in subtitleEffects)
            {
                long effSrcStart = VisualResources.Ticks(effect.SourceStart);
                long effSrcEnd = effSrcStart + VisualResources.Ticks(effect.SourceDuration);

                long ovSrcStart = Math.Max(effSrcStart, clipSrcStart);
                long ovSrcEnd = Math.Min(effSrcEnd, clipSrcEnd);
                if (ovSrcEnd <= ovSrcStart)
                    continue;

                long outStart = MapSourceTicksToOutput(clip, ovSrcStart);
                long outEnd = MapSourceTicksToOutput(clip, ovSrcEnd);
                if (outEnd <= outStart)
                    continue;

                var parameters = SubtitleCueParameters.FromEffect(effect);
                string text = (parameters.Text ?? "").Trim();
                if (string.IsNullOrEmpty(text))
                    continue;

                string? secondary = emitSecondary && !string.IsNullOrWhiteSpace(parameters.SecondaryText)
                    ? parameters.SecondaryText.Trim()
                    : null;

                projected.Add((new SubtitleCueDraft(outStart, outEnd, text, secondary), effect.Id));
            }
        }

        projected.Sort((a, b) =>
        {
            int cmp = a.Cue.StartTicks.CompareTo(b.Cue.StartTicks);
            return cmp != 0 ? cmp : a.Cue.EndTicks.CompareTo(b.Cue.EndTicks);
        });

        // Merge contiguous slices produced when one source subtitle crosses an unedited split boundary.
        var merged = new List<SubtitleCueDraft>(projected.Count);
        string? lastEffectId = null;
        foreach (var (cue, effectId) in projected)
        {
            if (merged.Count > 0 &&
                string.Equals(lastEffectId, effectId, StringComparison.Ordinal) &&
                cue.StartTicks - merged[^1].EndTicks <= 20_000L && // <= 2ms rounding tolerance
                string.Equals(merged[^1].Text, cue.Text, StringComparison.Ordinal) &&
                string.Equals(merged[^1].SecondaryText, cue.SecondaryText, StringComparison.Ordinal))
            {
                merged[^1] = merged[^1] with { EndTicks = Math.Max(merged[^1].EndTicks, cue.EndTicks) };
            }
            else
            {
                merged.Add(cue);
                lastEffectId = effectId;
            }
        }

        return merged;
    }

    /// <summary>Exports projected output-timeline subtitles to .srt, .vtt, or plain .txt format.</summary>
    public static string Export(
        ProjectSnapshot snapshot,
        ResolvedTimeline timeline,
        SubtitleExportFormat format,
        bool? includeSecondary = null)
    {
        var cues = ProjectToOutputCues(snapshot, timeline, includeSecondary);
        return FormatCues(cues, format);
    }

    /// <summary>Formats already-projected output cues into .srt, .vtt, or plain .txt.</summary>
    public static string FormatCues(IReadOnlyList<SubtitleCueDraft> cues, SubtitleExportFormat format)
    {
        ArgumentNullException.ThrowIfNull(cues);
        var sb = new StringBuilder();

        switch (format)
        {
            case SubtitleExportFormat.Srt:
                for (int i = 0; i < cues.Count; i++)
                {
                    var cue = cues[i];
                    sb.Append(i + 1).Append("\r\n");
                    sb.Append(FormatSrtTimestamp(cue.StartTicks))
                      .Append(" --> ")
                      .Append(FormatSrtTimestamp(cue.EndTicks))
                      .Append("\r\n");
                    sb.Append(cue.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal))
                      .Append("\r\n");
                    if (!string.IsNullOrWhiteSpace(cue.SecondaryText))
                    {
                        sb.Append(cue.SecondaryText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal))
                          .Append("\r\n");
                    }
                    sb.Append("\r\n");
                }
                break;

            case SubtitleExportFormat.Vtt:
                sb.Append("WEBVTT\n\n");
                foreach (var cue in cues)
                {
                    sb.Append(FormatVttTimestamp(cue.StartTicks))
                      .Append(" --> ")
                      .Append(FormatVttTimestamp(cue.EndTicks))
                      .Append('\n');
                    sb.Append(cue.Text.Replace("\r\n", "\n", StringComparison.Ordinal)).Append('\n');
                    if (!string.IsNullOrWhiteSpace(cue.SecondaryText))
                        sb.Append(cue.SecondaryText.Replace("\r\n", "\n", StringComparison.Ordinal)).Append('\n');
                    sb.Append('\n');
                }
                break;

            case SubtitleExportFormat.Txt:
                for (int i = 0; i < cues.Count; i++)
                {
                    var cue = cues[i];
                    if (i > 0) sb.AppendLine();
                    sb.Append(cue.Text);
                    if (!string.IsNullOrWhiteSpace(cue.SecondaryText))
                    {
                        sb.AppendLine();
                        sb.Append(cue.SecondaryText);
                    }
                }
                if (cues.Count > 0) sb.AppendLine();
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported subtitle export format.");
        }

        return sb.ToString();
    }

    public static string FormatSrtTimestamp(long ticks)
    {
        long totalMs = Math.Max(0L, (long)Math.Round(ticks / (double)TicksPerMillisecond, MidpointRounding.AwayFromZero));
        long hours = totalMs / 3_600_000L;
        long minutes = (totalMs % 3_600_000L) / 60_000L;
        long seconds = (totalMs % 60_000L) / 1_000L;
        long millis = totalMs % 1_000L;
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00},{3:000}", hours, minutes, seconds, millis);
    }

    public static string FormatVttTimestamp(long ticks)
    {
        long totalMs = Math.Max(0L, (long)Math.Round(ticks / (double)TicksPerMillisecond, MidpointRounding.AwayFromZero));
        long hours = totalMs / 3_600_000L;
        long minutes = (totalMs % 3_600_000L) / 60_000L;
        long seconds = (totalMs % 60_000L) / 1_000L;
        long millis = totalMs % 1_000L;
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}.{3:000}", hours, minutes, seconds, millis);
    }

    public static long ParseTimestampTicks(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new FormatException("Timestamp is empty.");

        string normalized = value.Trim().Replace(',', '.');
        string[] colonParts = normalized.Split(':');
        if (colonParts.Length is not (2 or 3))
            throw new FormatException($"Invalid subtitle timestamp: '{value}'.");

        int hours = 0;
        int minutes;
        string secondsPart;
        if (colonParts.Length == 3)
        {
            hours = int.Parse(colonParts[0], CultureInfo.InvariantCulture);
            minutes = int.Parse(colonParts[1], CultureInfo.InvariantCulture);
            secondsPart = colonParts[2];
        }
        else
        {
            minutes = int.Parse(colonParts[0], CultureInfo.InvariantCulture);
            secondsPart = colonParts[1];
        }

        string[] secMs = secondsPart.Split('.');
        int seconds = int.Parse(secMs[0], CultureInfo.InvariantCulture);
        int millis = 0;
        if (secMs.Length == 2)
        {
            string msStr = secMs[1].PadRight(3, '0');
            if (msStr.Length > 3) msStr = msStr[..3];
            millis = int.Parse(msStr, CultureInfo.InvariantCulture);
        }

        long totalMs = checked((hours * 3_600L + minutes * 60L + seconds) * 1_000L + millis);
        return checked(totalMs * TicksPerMillisecond);
    }

    private static long MapOutputTicksToSource(ResolvedClip clip, long outputTicks)
    {
        long deltaOut = Math.Max(0L, outputTicks - clip.OutputStart);
        int num = Math.Max(1, clip.Numerator);
        int den = Math.Max(1, clip.Denominator);
        long deltaSrc = (long)Math.Round((decimal)deltaOut * num / den, MidpointRounding.AwayFromZero);
        return Math.Clamp(clip.SourceStart + deltaSrc, clip.SourceStart, clip.SourceStart + clip.SourceDuration);
    }

    private static long MapSourceTicksToOutput(ResolvedClip clip, long sourceTicks)
    {
        long deltaSrc = Math.Max(0L, sourceTicks - clip.SourceStart);
        int num = Math.Max(1, clip.Numerator);
        int den = Math.Max(1, clip.Denominator);
        long deltaOut = (long)Math.Round((decimal)deltaSrc * den / num, MidpointRounding.AwayFromZero);
        return Math.Clamp(clip.OutputStart + deltaOut, clip.OutputStart, clip.OutputStart + clip.OutputDuration);
    }

    private static string CleanCueLine(string rawLine)
    {
        string stripped = HtmlTagRegex.Replace(rawLine, string.Empty);
        string decoded = WebUtility.HtmlDecode(stripped);
        return decoded.Trim();
    }

    private static bool IsLikelyBilingualPair(string line1, string line2)
    {
        bool cjk1 = ContainsCjk(line1);
        bool cjk2 = ContainsCjk(line2);
        return cjk1 != cjk2;
    }

    private static bool ContainsCjk(string text)
    {
        foreach (char c in text)
        {
            if ((c >= '\u4E00' && c <= '\u9FFF') ||
                (c >= '\u3400' && c <= '\u4DBF') ||
                (c >= '\u3040' && c <= '\u30FF') ||
                (c >= '\uAC00' && c <= '\uD7AF'))
            {
                return true;
            }
        }
        return false;
    }

    private static string TruncateUtf8(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
            return value;
        var sb = new StringBuilder();
        int used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            int bytes = rune.Utf8SequenceLength;
            if (used + bytes > maxBytes) break;
            sb.Append(rune.ToString());
            used += bytes;
        }
        return sb.ToString();
    }
}
