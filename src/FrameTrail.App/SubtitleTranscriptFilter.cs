using System.Text;
using FrameTrail.Domain;
using FrameTrail.Workspace;

namespace FrameTrail.App;

/// <summary>Represents an extracted subtitle cue's source tick range and decoded transcript text.</summary>
internal readonly record struct SubtitleCueRange(
    string CueId,
    long StartTicks,
    long EndTicks,
    string PrimaryText,
    string? SecondaryText)
{
    /// <summary>Duration of the cue range in 10MHz ticks.</summary>
    public long DurationTicks => Math.Max(0L, EndTicks - StartTicks);
}

/// <summary>Represents a single text search match inside a subtitle cue's primary or secondary line.</summary>
internal readonly record struct SubtitleMatchSpan(
    string CueId,
    bool IsSecondary,
    int StartIndex,
    int Length);

/// <summary>
/// Pure subtitle transcript filtering, search/replace matching, and cue range extraction helpers
/// decoupled from the WinUI <see cref="EditorWindow"/> inspector UI.
/// </summary>
internal static class SubtitleTranscriptFilter
{
    /// <summary>Returns all subtitle effects ordered chronologically by source start ticks and ID.</summary>
    internal static IReadOnlyList<SourceEffect> OrderSubtitles(IEnumerable<SourceEffect> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        return effects
            .Where(e => string.Equals(e.Kind, "subtitle", StringComparison.Ordinal))
            .OrderBy(e => VisualResources.Ticks(e.SourceStart))
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Filters subtitle cues by an optional search query and/or overlapping source tick window
    /// <c>[rangeStartTicks, rangeEndTicks)</c>.
    /// </summary>
    internal static IReadOnlyList<SourceEffect> FilterCues(
        IReadOnlyList<SourceEffect> cues,
        string? query = null,
        long? rangeStartTicks = null,
        long? rangeEndTicks = null,
        bool includeSecondaryText = true,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        ArgumentNullException.ThrowIfNull(cues);
        string trimmedQuery = (query ?? "").Trim();
        bool hasQuery = trimmedQuery.Length > 0;
        bool hasWindow = rangeStartTicks.HasValue || rangeEndTicks.HasValue;

        if (!hasQuery && !hasWindow)
            return cues;

        long windowStart = rangeStartTicks ?? 0L;
        long windowEnd = rangeEndTicks ?? long.MaxValue;
        if (windowEnd < windowStart)
            return Array.Empty<SourceEffect>();

        var filtered = new List<SourceEffect>(cues.Count);
        foreach (var cue in cues)
        {
            if (hasWindow)
            {
                long cueStart = VisualResources.Ticks(cue.SourceStart);
                long cueEnd = cueStart + Math.Max(0L, VisualResources.Ticks(cue.SourceDuration));
                if (cueEnd <= windowStart || cueStart >= windowEnd)
                    continue;
            }

            if (hasQuery)
            {
                var p = SubtitleCueParameters.FromEffect(cue);
                bool matchedPrimary = (p.Text ?? "").Contains(trimmedQuery, comparison);
                bool matchedSecondary = includeSecondaryText &&
                    !string.IsNullOrEmpty(p.SecondaryText) &&
                    p.SecondaryText.Contains(trimmedQuery, comparison);
                if (!matchedPrimary && !matchedSecondary)
                    continue;
            }

            filtered.Add(cue);
        }

        return filtered;
    }

    /// <summary>Finds all non-overlapping occurrences of <paramref name="query"/> across subtitle cues.</summary>
    internal static IReadOnlyList<SubtitleMatchSpan> FindMatches(
        IReadOnlyList<SourceEffect> cues,
        string query,
        bool includeSecondaryText = true,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        ArgumentNullException.ThrowIfNull(cues);
        if (string.IsNullOrEmpty(query))
            return Array.Empty<SubtitleMatchSpan>();

        var matches = new List<SubtitleMatchSpan>();
        foreach (var cue in cues)
        {
            var p = SubtitleCueParameters.FromEffect(cue);
            CollectMatches(cue.Id, isSecondary: false, p.Text ?? "", query, comparison, matches);
            if (includeSecondaryText && !string.IsNullOrEmpty(p.SecondaryText))
                CollectMatches(cue.Id, isSecondary: true, p.SecondaryText, query, comparison, matches);
        }

        return matches;
    }

    /// <summary>Counts total non-overlapping occurrences of <paramref name="query"/> across subtitle cues.</summary>
    internal static int CountMatches(
        IReadOnlyList<SourceEffect> cues,
        string query,
        bool includeSecondaryText = true,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase) =>
        FindMatches(cues, query, includeSecondaryText, comparison).Count;

    /// <summary>Replaces all occurrences of <paramref name="find"/> across subtitle cues via <see cref="SubtitleCueMath"/>.</summary>
    internal static (IReadOnlyList<SourceEffect> Updated, int Replacements) ReplaceMatches(
        IReadOnlyList<SourceEffect> cues,
        string find,
        string replace,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase) =>
        SubtitleCueMath.FindAndReplace(cues, find, replace, comparison);

    /// <summary>
    /// Extracts ordered <see cref="SubtitleCueRange"/> entries from subtitle cues, optionally clamping
    /// boundaries to <c>[clampMinTicks, clampMaxTicks]</c>.
    /// </summary>
    internal static IReadOnlyList<SubtitleCueRange> ExtractCueRanges(
        IEnumerable<SourceEffect> cues,
        long? clampMinTicks = null,
        long? clampMaxTicks = null)
    {
        ArgumentNullException.ThrowIfNull(cues);
        long minBound = clampMinTicks ?? 0L;
        long maxBound = clampMaxTicks ?? long.MaxValue;
        if (maxBound < minBound)
            return Array.Empty<SubtitleCueRange>();

        var ranges = new List<SubtitleCueRange>();
        foreach (var cue in OrderSubtitles(cues))
        {
            long rawStart = VisualResources.Ticks(cue.SourceStart);
            long rawEnd = rawStart + Math.Max(0L, VisualResources.Ticks(cue.SourceDuration));
            long start = Math.Clamp(rawStart, minBound, maxBound);
            long end = Math.Clamp(rawEnd, minBound, maxBound);
            if (end <= start)
                continue;

            var p = SubtitleCueParameters.FromEffect(cue);
            ranges.Add(new SubtitleCueRange(
                cue.Id,
                start,
                end,
                p.Text ?? "",
                string.IsNullOrWhiteSpace(p.SecondaryText) ? null : p.SecondaryText));
        }

        return ranges;
    }

    /// <summary>Builds a deterministic signature string for subtitle cue list diffing in the inspector.</summary>
    internal static string BuildTranscriptSignature(IReadOnlyList<SourceEffect> cues, bool bilingualEnabled)
    {
        ArgumentNullException.ThrowIfNull(cues);
        var sb = new StringBuilder(cues.Count * 48);
        sb.Append(bilingualEnabled ? "B1|" : "B0|");
        foreach (var cue in cues)
        {
            var p = SubtitleCueParameters.FromEffect(cue);
            sb.Append(cue.Id).Append(':')
                .Append(VisualResources.Ticks(cue.SourceStart)).Append(':')
                .Append(VisualResources.Ticks(cue.SourceDuration)).Append(':')
                .Append(p.Text).Append('|')
                .Append(p.SecondaryText ?? "").Append(';');
        }
        return sb.ToString();
    }

    private static void CollectMatches(
        string cueId,
        bool isSecondary,
        string text,
        string query,
        StringComparison comparison,
        List<SubtitleMatchSpan> output)
    {
        int offset = 0;
        while (offset <= text.Length - query.Length)
        {
            int idx = text.IndexOf(query, offset, comparison);
            if (idx < 0)
                break;
            output.Add(new SubtitleMatchSpan(cueId, isSecondary, idx, query.Length));
            offset = idx + query.Length;
        }
    }
}
