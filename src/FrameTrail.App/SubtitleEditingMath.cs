using System.Globalization;
using System.Text;
using FrameTrail.Domain;
using FrameTrail.Workspace;

namespace FrameTrail.App;

internal readonly record struct SubtitleSnapResult(
    NormalizedRect Rect,
    bool SnappedCenterX,
    double GuideX,
    bool SnappedSafeY,
    double GuideY);

/// <summary>Magnetic center-X and 16:9 / 9:16 safe-zone Y snapping for subtitle canvas overlays.</summary>
internal static class SubtitleCanvasSnap
{
    internal const double DefaultThreshold = 0.015;
    internal const double CenterGuideX = 0.5;
    internal const double LandscapeSafeY = 0.90;
    internal const double PortraitSafeY = 0.80;
    internal const double PortraitAspectCutoff = 0.85;

    internal static double SafeZoneY(double canvasAspect) =>
        double.IsFinite(canvasAspect) && canvasAspect > 0 && canvasAspect < PortraitAspectCutoff
            ? PortraitSafeY
            : LandscapeSafeY;

    internal static SubtitleSnapResult Snap(
        NormalizedRect rect,
        double canvasAspect,
        double snapThreshold = DefaultThreshold)
    {
        ArgumentNullException.ThrowIfNull(rect);
        double threshold = Math.Max(0.0, snapThreshold);
        double x = rect.X;
        double y = rect.Y;
        double width = Math.Clamp(rect.Width, 0.001, 1.0);
        double height = Math.Clamp(rect.Height, 0.001, 1.0);

        bool snappedX = false;
        double centerX = x + width * 0.5;
        if (Math.Abs(centerX - CenterGuideX) <= threshold)
        {
            x = Math.Clamp(CenterGuideX - width * 0.5, 0.0, 1.0 - width);
            snappedX = true;
        }

        double safeY = SafeZoneY(canvasAspect);
        bool snappedY = false;
        double bottomY = y + height;
        double centerY = y + height * 0.5;
        double distBottom = Math.Abs(bottomY - safeY);
        double distCenter = Math.Abs(centerY - safeY);

        if (distBottom <= threshold && distBottom <= distCenter)
        {
            y = Math.Clamp(safeY - height, 0.0, 1.0 - height);
            snappedY = true;
        }
        else if (distCenter <= threshold)
        {
            y = Math.Clamp(safeY - height * 0.5, 0.0, 1.0 - height);
            snappedY = true;
        }

        var snappedRect = new NormalizedRect
        {
            X = Math.Clamp(x, 0.0, 1.0 - width),
            Y = Math.Clamp(y, 0.0, 1.0 - height),
            Width = width,
            Height = height
        };

        return new SubtitleSnapResult(snappedRect, snappedX, CenterGuideX, snappedY, safeY);
    }
}

/// <summary>Pure cue split, merge, and find/replace helpers for the native subtitle inspector and timeline.</summary>
internal static class SubtitleCueMath
{
    public const long TicksPerSecond = 10_000_000L;
    public const long MinCueDurationTicks = 1_000_000L; // 0.1s floor when possible

    /// <summary>
    /// Splits a subtitle cue into two cues at <paramref name="caretIndex"/> inside <paramref name="editedText"/>,
    /// dividing source duration proportionally by character position and preserving word timings / style overrides.
    /// </summary>
    internal static (SourceEffect First, SourceEffect Second)? SplitByCaret(
        SourceEffect cue,
        string editedText,
        int caretIndex,
        string? secondId = null)
    {
        ArgumentNullException.ThrowIfNull(cue);
        if (string.IsNullOrEmpty(editedText) || caretIndex <= 0 || caretIndex >= editedText.Length)
            return null;

        string leftText = editedText[..caretIndex].Trim();
        string rightText = editedText[caretIndex..].Trim();
        if (string.IsNullOrEmpty(leftText) || string.IsNullOrEmpty(rightText))
            return null;

        long start = VisualResources.Ticks(cue.SourceStart);
        long duration = VisualResources.Ticks(cue.SourceDuration);
        if (duration < 2)
            return null;

        double ratio = Math.Clamp(caretIndex / (double)editedText.Length, 0.05, 0.95);
        long firstDuration = ComputeSplitDuration(duration, ratio);
        long secondDuration = duration - firstDuration;
        if (firstDuration <= 0 || secondDuration <= 0)
            return null;

        var priorParams = SubtitleCueParameters.FromEffect(cue);
        var (secLeft, secRight) = SplitSecondaryText(priorParams.SecondaryText, ratio);
        double splitOffsetSec = firstDuration / (double)TicksPerSecond;
        double secondDurSec = secondDuration / (double)TicksPerSecond;
        var (leftWords, rightWords) = SplitWords(priorParams.Words, splitOffsetSec, secondDurSec);

        var firstParams = new SubtitleCueParameters
        {
            Text = leftText,
            SecondaryText = secLeft,
            Words = leftWords,
            StyleOverride = priorParams.StyleOverride
        };
        var secondParams = new SubtitleCueParameters
        {
            Text = rightText,
            SecondaryText = secRight,
            Words = rightWords,
            StyleOverride = priorParams.StyleOverride
        };

        var first = cue with
        {
            SourceStart = MediaTime.FromTicks(start),
            SourceDuration = MediaTime.FromTicks(firstDuration),
            Parameters = firstParams.ToJsonElement()
        };
        var second = new SourceEffect
        {
            Id = string.IsNullOrWhiteSpace(secondId) ? "sub-" + Guid.NewGuid().ToString("N")[..12] : secondId.Trim(),
            Kind = "subtitle",
            SourceStart = MediaTime.FromTicks(start + firstDuration),
            SourceDuration = MediaTime.FromTicks(secondDuration),
            Parameters = secondParams.ToJsonElement()
        };

        return (first, second);
    }

    /// <summary>
    /// Splits a subtitle cue at a source timestamp (e.g. playhead `S` shortcut or timeline context menu).
    /// </summary>
    internal static (SourceEffect First, SourceEffect Second)? SplitAtSourceTime(
        SourceEffect cue,
        long splitSourceTicks,
        string? secondId = null)
    {
        ArgumentNullException.ThrowIfNull(cue);
        long start = VisualResources.Ticks(cue.SourceStart);
        long duration = VisualResources.Ticks(cue.SourceDuration);
        long end = start + duration;
        if (duration < 2 || splitSourceTicks <= start || splitSourceTicks >= end)
            return null;

        long firstDuration = splitSourceTicks - start;
        long secondDuration = end - splitSourceTicks;
        double ratio = Math.Clamp(firstDuration / (double)duration, 0.05, 0.95);
        double splitOffsetSec = firstDuration / (double)TicksPerSecond;
        double secondDurSec = secondDuration / (double)TicksPerSecond;

        var priorParams = SubtitleCueParameters.FromEffect(cue);
        string text = (priorParams.Text ?? "").Trim();
        string leftText;
        string rightText;

        var (leftWords, rightWords) = SplitWords(priorParams.Words, splitOffsetSec, secondDurSec);
        if (leftWords is { Count: > 0 } && rightWords is { Count: > 0 })
        {
            leftText = JoinTokensCleanly(leftWords.Select(w => w.Text));
            rightText = JoinTokensCleanly(rightWords.Select(w => w.Text));
        }
        else if (text.Length >= 2)
        {
            int splitIdx = FindNaturalSplitIndex(text, ratio);
            leftText = text[..splitIdx].Trim();
            rightText = text[splitIdx..].Trim();
            if (string.IsNullOrEmpty(leftText) || string.IsNullOrEmpty(rightText))
            {
                leftText = text;
                rightText = text;
            }
        }
        else
        {
            leftText = text;
            rightText = text;
        }

        var (secLeft, secRight) = SplitSecondaryText(priorParams.SecondaryText, ratio);
        var firstParams = new SubtitleCueParameters
        {
            Text = leftText,
            SecondaryText = secLeft,
            Words = leftWords,
            StyleOverride = priorParams.StyleOverride
        };
        var secondParams = new SubtitleCueParameters
        {
            Text = rightText,
            SecondaryText = secRight,
            Words = rightWords,
            StyleOverride = priorParams.StyleOverride
        };

        var first = cue with
        {
            SourceStart = MediaTime.FromTicks(start),
            SourceDuration = MediaTime.FromTicks(firstDuration),
            Parameters = firstParams.ToJsonElement()
        };
        var second = new SourceEffect
        {
            Id = string.IsNullOrWhiteSpace(secondId) ? "sub-" + Guid.NewGuid().ToString("N")[..12] : secondId.Trim(),
            Kind = "subtitle",
            SourceStart = MediaTime.FromTicks(splitSourceTicks),
            SourceDuration = MediaTime.FromTicks(secondDuration),
            Parameters = secondParams.ToJsonElement()
        };

        return (first, second);
    }

    /// <summary>Merges two subtitle cues chronologically into a single cue keeping the earlier cue's ID.</summary>
    internal static SourceEffect MergeCues(SourceEffect first, SourceEffect second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        var (a, b) = VisualResources.Ticks(first.SourceStart) <= VisualResources.Ticks(second.SourceStart)
            ? (first, second)
            : (second, first);

        long aStart = VisualResources.Ticks(a.SourceStart);
        long aEnd = aStart + VisualResources.Ticks(a.SourceDuration);
        long bStart = VisualResources.Ticks(b.SourceStart);
        long bEnd = bStart + VisualResources.Ticks(b.SourceDuration);
        long mergedEnd = Math.Max(aEnd, bEnd);
        long mergedDuration = Math.Max(1L, mergedEnd - aStart);

        var aParams = SubtitleCueParameters.FromEffect(a);
        var bParams = SubtitleCueParameters.FromEffect(b);

        string mergedText = JoinTwoSegments(aParams.Text, bParams.Text);
        string? mergedSecondary = JoinOptionalSegments(aParams.SecondaryText, bParams.SecondaryText);

        List<SubtitleWordTiming>? mergedWords = null;
        if (aParams.Words is { Count: > 0 } || bParams.Words is { Count: > 0 })
        {
            mergedWords = new List<SubtitleWordTiming>();
            double mergedDurSec = mergedDuration / (double)TicksPerSecond;
            if (aParams.Words is { Count: > 0 })
            {
                foreach (var w in aParams.Words)
                {
                    if (mergedWords.Count >= 256) break;
                    double ws = Math.Clamp(w.StartOffset, 0.0, mergedDurSec);
                    double we = Math.Clamp(Math.Max(w.EndOffset, ws), ws, mergedDurSec);
                    mergedWords.Add(w with { StartOffset = Math.Round(ws, 4), EndOffset = Math.Round(we, 4) });
                }
            }
            if (bParams.Words is { Count: > 0 })
            {
                double shiftSec = (bStart - aStart) / (double)TicksPerSecond;
                foreach (var w in bParams.Words)
                {
                    if (mergedWords.Count >= 256) break;
                    double ws = Math.Clamp(w.StartOffset + shiftSec, 0.0, mergedDurSec);
                    double we = Math.Clamp(Math.Max(w.EndOffset + shiftSec, ws), ws, mergedDurSec);
                    mergedWords.Add(w with { StartOffset = Math.Round(ws, 4), EndOffset = Math.Round(we, 4) });
                }
            }
            if (mergedWords.Count == 0)
                mergedWords = null;
        }

        var mergedParams = new SubtitleCueParameters
        {
            Text = TruncateUtf8(mergedText, 1024),
            SecondaryText = string.IsNullOrWhiteSpace(mergedSecondary) ? null : TruncateUtf8(mergedSecondary, 1024),
            Words = mergedWords,
            StyleOverride = aParams.StyleOverride ?? bParams.StyleOverride
        };

        return a with
        {
            SourceStart = MediaTime.FromTicks(aStart),
            SourceDuration = MediaTime.FromTicks(mergedDuration),
            Parameters = mergedParams.ToJsonElement()
        };
    }

    /// <summary>Replaces all occurrences of <paramref name="find"/> across subtitle cues.</summary>
    internal static (IReadOnlyList<SourceEffect> Updated, int Replacements) FindAndReplace(
        IReadOnlyList<SourceEffect> subtitles,
        string find,
        string replace,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        ArgumentNullException.ThrowIfNull(subtitles);
        if (string.IsNullOrEmpty(find))
            return (subtitles, 0);

        replace ??= "";
        int totalReplacements = 0;
        var updated = new List<SourceEffect>(subtitles.Count);

        foreach (var cue in subtitles)
        {
            var p = SubtitleCueParameters.FromEffect(cue);
            var (newText, primaryCount) = ReplaceCounted(p.Text ?? "", find, replace, comparison);
            int secondaryCount = 0;
            string? newSecondary = p.SecondaryText;
            if (!string.IsNullOrEmpty(p.SecondaryText))
            {
                (newSecondary, secondaryCount) = ReplaceCounted(p.SecondaryText, find, replace, comparison);
            }

            int count = primaryCount + secondaryCount;
            if (count > 0)
            {
                totalReplacements += count;
                var nextParams = p with
                {
                    Text = TruncateUtf8(newText, 1024),
                    SecondaryText = string.IsNullOrWhiteSpace(newSecondary) ? null : TruncateUtf8(newSecondary, 1024),
                    Words = primaryCount > 0 ? null : p.Words
                };
                updated.Add(cue with { Parameters = nextParams.ToJsonElement() });
            }
            else
            {
                updated.Add(cue);
            }
        }

        return (updated, totalReplacements);
    }

    internal static string FormatCueTime(long ticks)
    {
        var ts = TimeSpan.FromTicks(Math.Max(0, ticks));
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss\.ff", CultureInfo.InvariantCulture)
            : ts.ToString(@"mm\:ss\.ff", CultureInfo.InvariantCulture);
    }

    private static long ComputeSplitDuration(long totalDuration, double ratio)
    {
        long raw = (long)Math.Round(totalDuration * ratio, MidpointRounding.AwayFromZero);
        long floor = totalDuration >= MinCueDurationTicks * 2 ? MinCueDurationTicks : 1L;
        return Math.Clamp(raw, floor, totalDuration - floor);
    }

    private static (IReadOnlyList<SubtitleWordTiming>? Left, IReadOnlyList<SubtitleWordTiming>? Right) SplitWords(
        IReadOnlyList<SubtitleWordTiming>? words,
        double splitOffsetSec,
        double secondDurationSec)
    {
        if (words is null || words.Count == 0)
            return (null, null);

        var left = new List<SubtitleWordTiming>();
        var right = new List<SubtitleWordTiming>();
        foreach (var w in words)
        {
            double mid = (w.StartOffset + w.EndOffset) * 0.5;
            if (mid < splitOffsetSec)
            {
                double s = Math.Clamp(w.StartOffset, 0.0, splitOffsetSec);
                double e = Math.Clamp(Math.Max(w.EndOffset, s), s, splitOffsetSec);
                left.Add(w with { StartOffset = Math.Round(s, 4), EndOffset = Math.Round(e, 4) });
            }
            else
            {
                double s = Math.Clamp(w.StartOffset - splitOffsetSec, 0.0, secondDurationSec);
                double e = Math.Clamp(Math.Max(w.EndOffset - splitOffsetSec, s), s, secondDurationSec);
                right.Add(w with { StartOffset = Math.Round(s, 4), EndOffset = Math.Round(e, 4) });
            }
        }

        return (left.Count > 0 ? left : null, right.Count > 0 ? right : null);
    }

    private static (string? Left, string? Right) SplitSecondaryText(string? secondary, double ratio)
    {
        if (string.IsNullOrWhiteSpace(secondary))
            return (null, null);
        string trimmed = secondary.Trim();
        if (trimmed.Length < 4)
            return (trimmed, null);
        int idx = FindNaturalSplitIndex(trimmed, ratio);
        string left = trimmed[..idx].Trim();
        string right = trimmed[idx..].Trim();
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            return (trimmed, null);
        return (left, right);
    }

    private static int FindNaturalSplitIndex(string text, double ratio)
    {
        int target = Math.Clamp((int)Math.Round(text.Length * ratio), 1, text.Length - 1);
        int window = Math.Min(8, text.Length / 3);
        int best = target;
        int bestDist = int.MaxValue;

        for (int i = Math.Max(1, target - window); i <= Math.Min(text.Length - 1, target + window); i++)
        {
            char prev = text[i - 1];
            char curr = text[i];
            if (char.IsWhiteSpace(curr) || prev is '，' or '。' or '、' or ',' or '.' or '！' or '？' or '!' or '?' or '；' or ';')
            {
                int dist = Math.Abs(i - target);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = i;
                }
            }
        }

        return best;
    }

    private static string JoinTwoSegments(string? a, string? b)
    {
        string left = (a ?? "").Trim();
        string right = (b ?? "").Trim();
        if (string.IsNullOrEmpty(left)) return right;
        if (string.IsNullOrEmpty(right)) return left;
        return NeedsSpaceBetween(left[^1], right[0]) ? $"{left} {right}" : $"{left}{right}";
    }

    private static string? JoinOptionalSegments(string? a, string? b)
    {
        string merged = JoinTwoSegments(a, b);
        return string.IsNullOrWhiteSpace(merged) ? null : merged;
    }

    private static string JoinTokensCleanly(IEnumerable<string> tokens)
    {
        var sb = new StringBuilder();
        foreach (var raw in tokens)
        {
            string t = raw.Trim();
            if (string.IsNullOrEmpty(t)) continue;
            if (sb.Length > 0 && NeedsSpaceBetween(sb[^1], t[0]))
                sb.Append(' ');
            sb.Append(t);
        }
        return sb.ToString();
    }

    private static bool NeedsSpaceBetween(char leftLast, char rightFirst)
    {
        if (char.IsWhiteSpace(leftLast) || char.IsWhiteSpace(rightFirst))
            return false;
        if (IsCjkChar(leftLast) || IsCjkChar(rightFirst))
            return false;
        if (rightFirst is '.' or ',' or '!' or '?' or ';' or ':' or ')' or ']' or '”' or '’')
            return false;
        return true;
    }

    private static bool IsCjkChar(char c) =>
        (c >= '\u4E00' && c <= '\u9FFF') ||
        (c >= '\u3400' && c <= '\u4DBF') ||
        (c >= '\u3040' && c <= '\u30FF') ||
        (c >= '\uAC00' && c <= '\uD7AF');

    private static (string Result, int Count) ReplaceCounted(string input, string find, string replace, StringComparison comparison)
    {
        int count = 0;
        int startIndex = 0;
        var sb = new StringBuilder(input.Length);
        while (true)
        {
            int idx = input.IndexOf(find, startIndex, comparison);
            if (idx < 0)
            {
                sb.Append(input, startIndex, input.Length - startIndex);
                break;
            }
            sb.Append(input, startIndex, idx - startIndex);
            sb.Append(replace);
            startIndex = idx + find.Length;
            count++;
        }
        return (sb.ToString(), count);
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
