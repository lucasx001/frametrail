using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FrameTrail.Domain;

namespace FrameTrail.Workspace;

public sealed record SilentInterval(
    long StartTicks,
    long EndTicks,
    double StartSeconds,
    double DurationSeconds,
    double AverageDb);

public sealed record PointerHotspot(
    long Ticks,
    double Seconds,
    double X,
    double Y,
    int ClickKind,
    double SuggestedZoomScale);

public sealed record TranscriptSegment(
    long StartTicks,
    long EndTicks,
    string Text,
    string TrackKind);

public sealed record TranscriptHotspotMatch(
    TranscriptSegment Segment,
    IReadOnlyList<PointerHotspot> Hotspots,
    double FocusX,
    double FocusY);

/// <summary>Native .NET multimodal analysis for WAV silence detection, pointer hotspots, transcript cache, and silence-removal batch commands.</summary>
public sealed class AiMultimodalService
{
    public const long TicksPerSecond = 10_000_000L;
    private const long MinKeepSegmentTicks = 1_000_000L; // 0.1s minimum kept clip duration
    private const int MaxBatchCommands = 100;
    private const double WindowSeconds = 0.05;

    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>Parses a RIFF/RF64 WAV file (16-bit PCM, 24-bit PCM, 32-bit PCM, or 32-bit IEEE float) in 50ms windows and returns silent spans.</summary>
    public IReadOnlyList<SilentInterval> DetectWavSilence(
        string wavFilePath,
        double minSilenceSeconds = 1.0,
        double thresholdDb = -38.0)
    {
        if (string.IsNullOrWhiteSpace(wavFilePath))
            throw new ArgumentException("WAV file path is required.", nameof(wavFilePath));
        if (!File.Exists(wavFilePath))
            throw new FileNotFoundException("WAV file not found.", wavFilePath);
        if (!double.IsFinite(minSilenceSeconds) || minSilenceSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(minSilenceSeconds), "Minimum silence duration must be positive.");
        if (!double.IsFinite(thresholdDb))
            throw new ArgumentOutOfRangeException(nameof(thresholdDb), "Threshold dB must be finite.");

        using var stream = new FileStream(wavFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        var (formatTag, channels, sampleRate, bitsPerSample, dataOffset, dataSize) = ReadWavHeader(stream);

        int bytesPerSample = bitsPerSample / 8;
        int frameBytes = checked(bytesPerSample * channels);
        long totalFrames = dataSize / frameBytes;
        if (totalFrames <= 0)
            return Array.Empty<SilentInterval>();

        int windowFrames = Math.Max(1, (int)Math.Round(sampleRate * WindowSeconds));
        byte[] buffer = new byte[windowFrames * frameBytes];

        stream.Position = dataOffset;
        long frameCursor = 0;
        long? silentStartFrame = null;
        long silentEndFrame = 0;
        double silentSumSq = 0;
        long silentSampleCount = 0;
        var results = new List<SilentInterval>();

        void FlushSilence()
        {
            if (silentStartFrame is null)
                return;

            long startFrame = silentStartFrame.Value;
            long endFrame = silentEndFrame;
            double durationSec = (endFrame - startFrame) / (double)sampleRate;
            if (durationSec >= minSilenceSeconds)
            {
                long startTicks = FrameToTicks(startFrame, sampleRate);
                long endTicks = FrameToTicks(endFrame, sampleRate);
                double avgRms = silentSampleCount > 0 ? Math.Sqrt(silentSumSq / silentSampleCount) : 0.0;
                double avgDb = RmsToDb(avgRms);
                results.Add(new SilentInterval(
                    StartTicks: startTicks,
                    EndTicks: endTicks,
                    StartSeconds: Math.Round(startTicks / (double)TicksPerSecond, 4),
                    DurationSeconds: Math.Round((endTicks - startTicks) / (double)TicksPerSecond, 4),
                    AverageDb: Math.Round(avgDb, 2)));
            }

            silentStartFrame = null;
            silentSumSq = 0;
            silentSampleCount = 0;
        }

        while (frameCursor < totalFrames)
        {
            int framesToRead = (int)Math.Min(windowFrames, totalFrames - frameCursor);
            int bytesToRead = framesToRead * frameBytes;
            int readTotal = ReadExactOrEof(stream, buffer.AsSpan(0, bytesToRead));
            int actualFrames = readTotal / frameBytes;
            if (actualFrames <= 0)
                break;

            int sampleCount = actualFrames * channels;
            double sumSq = ComputeWindowSumSquares(buffer.AsSpan(0, actualFrames * frameBytes), formatTag, bitsPerSample, sampleCount);
            double rms = Math.Sqrt(sumSq / Math.Max(1, sampleCount));
            double db = RmsToDb(rms);

            if (db <= thresholdDb)
            {
                silentStartFrame ??= frameCursor;
                silentEndFrame = frameCursor + actualFrames;
                silentSumSq += sumSq;
                silentSampleCount += sampleCount;
            }
            else
            {
                FlushSilence();
            }

            frameCursor += actualFrames;
        }

        FlushSilence();
        return results;
    }

    /// <summary>Reads PointerEvents (events/*.jsonl) from snapshot.Media and clusters clicks and dwell hotspots.</summary>
    public async Task<IReadOnlyList<PointerHotspot>> ExtractPointerHotspotsAsync(
        ProjectWorkspace workspace,
        ProjectSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(snapshot);

        var rawSamples = new List<(long Ticks, double X, double Y, int Kind)>();

        foreach (var media in snapshot.Media
            .Where(m => m.Kind == MediaKind.PointerEvents)
            .OrderBy(m => VisualResources.Ticks(m.StartOffset)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            long offset = VisualResources.Ticks(media.StartOffset);
            long duration = VisualResources.Ticks(media.Duration);
            string filePath = workspace.Resolve(media.Path);
            if (!File.Exists(filePath))
                continue;

            using var reader = new StreamReader(filePath);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line) || line.Length > 32768)
                    continue;

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "pointer")
                    continue;
                if (root.TryGetProperty("inside", out var insideProp) && insideProp.ValueKind == JsonValueKind.False)
                    continue;
                if (!root.TryGetProperty("timescale", out var tsProp) || !tsProp.TryGetInt32(out int timescale) || timescale <= 0)
                    continue;
                if (!root.TryGetProperty("ticks", out var ticksProp) || !ticksProp.TryGetInt64(out long rawTicks))
                    continue;
                if (!root.TryGetProperty("x", out var xProp) || !xProp.TryGetDouble(out double x) ||
                    !root.TryGetProperty("y", out var yProp) || !yProp.TryGetDouble(out double y))
                    continue;
                if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || x > 1 || y < 0 || y > 1)
                    continue;

                long localTicks = VisualResources.Ticks(new MediaTime { Ticks = rawTicks, Timescale = timescale });
                if (localTicks < 0 || localTicks >= duration)
                    continue;

                int kind = root.TryGetProperty("kind", out var kindProp) && kindProp.TryGetInt32(out int k) ? k : 0;
                rawSamples.Add((checked(offset + localTicks), x, y, kind));
            }
        }

        if (rawSamples.Count == 0)
            return Array.Empty<PointerHotspot>();

        rawSamples.Sort((a, b) => a.Ticks.CompareTo(b.Ticks));

        var hotspots = new List<PointerHotspot>();
        const long clickClusterWindowTicks = 4_000_000L; // 400ms
        const double clickClusterRadius = 0.06;

        // 1. Cluster click events (kind 1 = left click, kind 3 = right click).
        var clickIndices = new List<int>();
        for (int i = 0; i < rawSamples.Count; i++)
        {
            if (rawSamples[i].Kind is 1 or 3)
                clickIndices.Add(i);
        }

        int ci = 0;
        while (ci < clickIndices.Count)
        {
            var first = rawSamples[clickIndices[ci]];
            int clusterCount = 1;
            double sumX = first.X;
            double sumY = first.Y;
            long lastTicks = first.Ticks;

            int cj = ci + 1;
            while (cj < clickIndices.Count)
            {
                var next = rawSamples[clickIndices[cj]];
                if (next.Ticks - lastTicks <= clickClusterWindowTicks &&
                    Distance(first.X, first.Y, next.X, next.Y) <= clickClusterRadius)
                {
                    clusterCount++;
                    sumX += next.X;
                    sumY += next.Y;
                    lastTicks = next.Ticks;
                    cj++;
                }
                else
                {
                    break;
                }
            }

            double scale = clusterCount >= 2 ? 2.0 : 1.7;
            hotspots.Add(new PointerHotspot(
                Ticks: first.Ticks,
                Seconds: Math.Round(first.Ticks / (double)TicksPerSecond, 4),
                X: Math.Round(sumX / clusterCount, 4),
                Y: Math.Round(sumY / clusterCount, 4),
                ClickKind: first.Kind,
                SuggestedZoomScale: scale));

            ci = cj;
        }

        // 2. Detect high-activity dwell points (stationary pointer >= 0.8s without nearby click).
        const long minDwellTicks = 8_000_000L; // 0.8s
        const double dwellRadius = 0.03;

        int di = 0;
        while (di < rawSamples.Count)
        {
            var anchor = rawSamples[di];
            int dj = di + 1;
            double sumX = anchor.X;
            double sumY = anchor.Y;
            while (dj < rawSamples.Count &&
                   Distance(anchor.X, anchor.Y, rawSamples[dj].X, rawSamples[dj].Y) <= dwellRadius)
            {
                sumX += rawSamples[dj].X;
                sumY += rawSamples[dj].Y;
                dj++;
            }

            int count = dj - di;
            long span = rawSamples[dj - 1].Ticks - anchor.Ticks;
            if (count >= 2 && span >= minDwellTicks)
            {
                long midTicks = (anchor.Ticks + rawSamples[dj - 1].Ticks) / 2;
                bool overlapsClick = hotspots.Any(h => Math.Abs(h.Ticks - midTicks) <= minDwellTicks);
                if (!overlapsClick)
                {
                    hotspots.Add(new PointerHotspot(
                        Ticks: midTicks,
                        Seconds: Math.Round(midTicks / (double)TicksPerSecond, 4),
                        X: Math.Round(sumX / count, 4),
                        Y: Math.Round(sumY / count, 4),
                        ClickKind: 0,
                        SuggestedZoomScale: 1.5));
                }
            }

            di = dj;
        }

        hotspots.Sort((a, b) => a.Ticks.CompareTo(b.Ticks));
        return hotspots;
    }

    /// <summary>
    /// Generates a valid FrameTrail op:"batch" command (split + delete) removing silent intervals
    /// while preserving at least one valid segment and staying within engine validation constraints.
    /// </summary>
    public object BuildRemoveSilenceBatchCommand(
        ProjectSnapshot snapshot,
        IReadOnlyList<SilentInterval> silentIntervals,
        double paddingSeconds = 0.15)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(silentIntervals);

        if (snapshot.Segments.Count == 0 || silentIntervals.Count == 0)
            return new { op = "batch", commands = Array.Empty<object>() };

        long padTicks = (long)Math.Round(Math.Max(0.0, paddingSeconds) * TicksPerSecond);
        var rawCuts = new List<(long Start, long End)>();
        foreach (var interval in silentIntervals)
        {
            long start = interval.StartTicks + padTicks;
            long end = interval.EndTicks - padTicks;
            if (end - start >= MinKeepSegmentTicks)
                rawCuts.Add((start, end));
        }

        if (rawCuts.Count == 0)
            return new { op = "batch", commands = Array.Empty<object>() };

        rawCuts.Sort((a, b) => a.Start.CompareTo(b.Start));
        var mergedCuts = new List<(long Start, long End)>();
        foreach (var cut in rawCuts)
        {
            if (mergedCuts.Count > 0 && cut.Start <= mergedCuts[^1].End + MinKeepSegmentTicks)
            {
                var prev = mergedCuts[^1];
                mergedCuts[^1] = (prev.Start, Math.Max(prev.End, cut.End));
            }
            else
            {
                mergedCuts.Add(cut);
            }
        }

        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in snapshot.Media) usedIds.Add(m.Id);
        foreach (var s in snapshot.Segments) usedIds.Add(s.Id);
        foreach (var e in snapshot.Effects) usedIds.Add(e.Id);
        foreach (var mk in snapshot.Markers) usedIds.Add(mk.Id);

        string NextSegmentId()
        {
            string id;
            do
            {
                id = "ai-seg-" + Guid.NewGuid().ToString("N")[..12];
            } while (!usedIds.Add(id));
            return id;
        }

        var commands = new List<object>();
        int remainingSegments = snapshot.Segments.Count;

        foreach (var segment in snapshot.Segments)
        {
            long segStart = VisualResources.Ticks(segment.SourceStart);
            long segDuration = VisualResources.Ticks(segment.SourceDuration);
            long segEnd = segStart + segDuration;

            // Ensure cuts leave valid minimum durations on any kept fragments.
            var segCuts = new List<(long Start, long End)>();
            foreach (var (cutStart, cutEnd) in mergedCuts)
            {
                long s = Math.Max(cutStart, segStart);
                long e = Math.Min(cutEnd, segEnd);
                if (e <= s)
                    continue;
                if (s - segStart < MinKeepSegmentTicks)
                    s = segStart;
                if (segEnd - e < MinKeepSegmentTicks)
                    e = segEnd;
                if (e - s >= MinKeepSegmentTicks)
                {
                    if (segCuts.Count > 0 && s <= segCuts[^1].End + MinKeepSegmentTicks)
                    {
                        var prev = segCuts[^1];
                        segCuts[^1] = (prev.Start, Math.Max(prev.End, e));
                    }
                    else
                    {
                        segCuts.Add((s, e));
                    }
                }
            }

            string activeId = segment.Id;
            long activeStart = segStart;

            foreach (var (rawStart, rawEnd) in segCuts)
            {
                long cs = Math.Max(rawStart, activeStart);
                long ce = Math.Min(rawEnd, segEnd);
                if (ce <= cs)
                    continue;
                if (cs - activeStart < MinKeepSegmentTicks)
                    cs = activeStart;

                if (cs > activeStart && ce < segEnd)
                {
                    if (commands.Count + 3 > MaxBatchCommands)
                        break;
                    string midId = NextSegmentId();
                    string rightId = NextSegmentId();
                    commands.Add(new { op = "split", segmentId = activeId, sourceTicks = cs, newId = midId });
                    commands.Add(new { op = "split", segmentId = midId, sourceTicks = ce, newId = rightId });
                    commands.Add(new { op = "delete", segmentId = midId });
                    remainingSegments++;
                    activeId = rightId;
                    activeStart = ce;
                }
                else if (cs == activeStart && ce < segEnd)
                {
                    if (commands.Count + 2 > MaxBatchCommands)
                        break;
                    string rightId = NextSegmentId();
                    commands.Add(new { op = "split", segmentId = activeId, sourceTicks = ce, newId = rightId });
                    commands.Add(new { op = "delete", segmentId = activeId });
                    activeId = rightId;
                    activeStart = ce;
                }
                else if (cs > activeStart && ce == segEnd)
                {
                    if (commands.Count + 2 > MaxBatchCommands)
                        break;
                    string rightId = NextSegmentId();
                    commands.Add(new { op = "split", segmentId = activeId, sourceTicks = cs, newId = rightId });
                    commands.Add(new { op = "delete", segmentId = rightId });
                    activeStart = segEnd;
                }
                else
                {
                    if (remainingSegments > 1)
                    {
                        if (commands.Count + 1 > MaxBatchCommands)
                            break;
                        commands.Add(new { op = "delete", segmentId = activeId });
                        remainingSegments--;
                    }
                    else if (segEnd - activeStart > MinKeepSegmentTicks)
                    {
                        if (commands.Count + 2 > MaxBatchCommands)
                            break;
                        string rightId = NextSegmentId();
                        long keepEnd = activeStart + MinKeepSegmentTicks;
                        commands.Add(new { op = "split", segmentId = activeId, sourceTicks = keepEnd, newId = rightId });
                        commands.Add(new { op = "delete", segmentId = rightId });
                    }
                    activeStart = segEnd;
                }
            }

            if (commands.Count >= MaxBatchCommands)
                break;
        }

        return new { op = "batch", commands = commands.ToArray() };
    }

    public async Task SaveTranscriptCacheAsync(
        string projectDirectory,
        IReadOnlyList<TranscriptSegment> segments,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
            throw new ArgumentException("Project directory is required.", nameof(projectDirectory));
        ArgumentNullException.ThrowIfNull(segments);

        string cacheDir = Path.Combine(projectDirectory, "cache");
        Directory.CreateDirectory(cacheDir);
        string cacheFile = Path.Combine(cacheDir, "ai-transcript.json");
        string tempFile = cacheFile + "." + Guid.NewGuid().ToString("N") + ".tmp";

        var payload = segments.Select(s => new TranscriptCacheItem
        {
            StartSec = Math.Round(s.StartTicks / (double)TicksPerSecond, 4),
            EndSec = Math.Round(s.EndTicks / (double)TicksPerSecond, 4),
            StartTicks = s.StartTicks,
            EndTicks = s.EndTicks,
            Speaker = ToSpeakerLabel(s.TrackKind),
            TrackKind = s.TrackKind,
            Text = s.Text
        }).ToArray();

        string json = JsonSerializer.Serialize(payload, CacheJsonOptions);
        try
        {
            await File.WriteAllTextAsync(tempFile, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            File.Move(tempFile, cacheFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    public async Task<IReadOnlyList<TranscriptSegment>> LoadTranscriptCacheAsync(
        string projectDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
            throw new ArgumentException("Project directory is required.", nameof(projectDirectory));

        string cacheFile = Path.Combine(projectDirectory, "cache", "ai-transcript.json");
        if (!File.Exists(cacheFile))
            return Array.Empty<TranscriptSegment>();

        string json = await File.ReadAllTextAsync(cacheFile, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<TranscriptSegment>();

        using var document = JsonDocument.Parse(json);
        JsonElement itemsElement;
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            itemsElement = document.RootElement;
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Object &&
                 TryGetPropertyIgnoreCase(document.RootElement, "segments", out var segs) &&
                 segs.ValueKind == JsonValueKind.Array)
        {
            itemsElement = segs;
        }
        else
        {
            return Array.Empty<TranscriptSegment>();
        }

        var list = new List<TranscriptSegment>();
        foreach (var item in itemsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            long startTicks = ReadTicksOrSec(item, "startTicks", "startSec");
            long endTicks = ReadTicksOrSec(item, "endTicks", "endSec");
            string text = TryGetPropertyIgnoreCase(item, "text", out var textProp) && textProp.ValueKind == JsonValueKind.String
                ? textProp.GetString() ?? ""
                : "";
            string trackKind = TryGetPropertyIgnoreCase(item, "trackKind", out var tkProp) && tkProp.ValueKind == JsonValueKind.String
                ? tkProp.GetString() ?? ""
                : TryGetPropertyIgnoreCase(item, "speaker", out var spProp) && spProp.ValueKind == JsonValueKind.String
                    ? NormalizeSpeakerToTrackKind(spProp.GetString() ?? "")
                    : "microphone";

            list.Add(new TranscriptSegment(startTicks, endTicks, text, trackKind));
        }

        list.Sort((a, b) => a.StartTicks.CompareTo(b.StartTicks));
        return list;
    }

    public IReadOnlyList<TranscriptHotspotMatch> MatchTranscriptAndHotspots(
        IReadOnlyList<TranscriptSegment> transcript,
        IReadOnlyList<PointerHotspot> hotspots,
        string query,
        long paddingTicks = TicksPerSecond)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(hotspots);

        string normalizedQuery = (query ?? string.Empty).Trim();
        string[] keywords = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = new List<TranscriptHotspotMatch>();

        foreach (var segment in transcript)
        {
            bool textMatches = string.IsNullOrEmpty(normalizedQuery) ||
                segment.Text.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                (keywords.Length > 1 && keywords.All(k => segment.Text.Contains(k, StringComparison.OrdinalIgnoreCase)));
            if (!textMatches)
                continue;

            var nearby = hotspots
                .Where(h => h.Ticks >= segment.StartTicks - paddingTicks && h.Ticks <= segment.EndTicks + paddingTicks)
                .ToArray();

            var inside = nearby
                .Where(h => h.Ticks >= segment.StartTicks && h.Ticks <= segment.EndTicks)
                .ToArray();

            var primary = inside.Length > 0 ? inside[0] : (nearby.Length > 0 ? nearby[0] : null);
            matches.Add(new TranscriptHotspotMatch(
                Segment: segment,
                Hotspots: nearby,
                FocusX: primary?.X ?? 0.5,
                FocusY: primary?.Y ?? 0.5));
        }

        return matches;
    }

    public async Task<IReadOnlyList<TranscriptHotspotMatch>> SearchTranscriptAndHotspotsAsync(
        ProjectWorkspace workspace,
        ProjectSnapshot snapshot,
        string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(snapshot);

        var transcript = await LoadTranscriptCacheAsync(workspace.DirectoryPath, cancellationToken).ConfigureAwait(false);
        var hotspots = await ExtractPointerHotspotsAsync(workspace, snapshot, cancellationToken).ConfigureAwait(false);
        return MatchTranscriptAndHotspots(transcript, hotspots, query);
    }

    private sealed class TranscriptCacheItem
    {
        [JsonPropertyName("startSec")] public double StartSec { get; init; }
        [JsonPropertyName("endSec")] public double EndSec { get; init; }
        [JsonPropertyName("StartTicks")] public long StartTicks { get; init; }
        [JsonPropertyName("EndTicks")] public long EndTicks { get; init; }
        [JsonPropertyName("speaker")] public string Speaker { get; init; } = "mic";
        [JsonPropertyName("trackKind")] public string TrackKind { get; init; } = "microphone";
        [JsonPropertyName("text")] public string Text { get; init; } = "";
    }

    private static string ToSpeakerLabel(string trackKind) => trackKind switch
    {
        "microphone" or "mic" => "mic",
        "systemAudio" or "system" => "system",
        "androidAudio" or "android" => "android",
        _ => string.IsNullOrWhiteSpace(trackKind) ? "mic" : trackKind
    };

    private static string NormalizeSpeakerToTrackKind(string speaker) => speaker.ToLowerInvariant() switch
    {
        "mic" or "microphone" => "microphone",
        "system" or "systemaudio" => "systemAudio",
        "android" or "androidaudio" => "androidAudio",
        _ => string.IsNullOrWhiteSpace(speaker) ? "microphone" : speaker
    };

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
            return true;
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static long ReadTicksOrSec(JsonElement obj, string ticksName, string secName)
    {
        if (TryGetPropertyIgnoreCase(obj, ticksName, out var ticksProp) && ticksProp.TryGetInt64(out long ticks))
            return ticks;
        if (TryGetPropertyIgnoreCase(obj, secName, out var secProp) && secProp.TryGetDouble(out double sec) && double.IsFinite(sec))
            return (long)Math.Round(sec * TicksPerSecond);
        return 0;
    }

    private static (ushort FormatTag, int Channels, int SampleRate, int BitsPerSample, long DataOffset, long DataSize) ReadWavHeader(Stream stream)
    {
        Span<byte> riffHeader = stackalloc byte[12];
        if (ReadExactOrEof(stream, riffHeader) < 12)
            throw new InvalidDataException("Truncated WAV header.");

        bool isRiff = riffHeader[0..4].SequenceEqual("RIFF"u8);
        bool isRf64 = riffHeader[0..4].SequenceEqual("RF64"u8);
        if ((!isRiff && !isRf64) || !riffHeader[8..12].SequenceEqual("WAVE"u8))
            throw new InvalidDataException("File is not a valid RIFF/RF64 WAVE audio file.");

        ushort formatTag = 0;
        int channels = 0;
        int sampleRate = 0;
        int bitsPerSample = 0;
        long? rf64DataSize = null;
        long dataOffset = -1;
        long dataSize = 0;
        bool hasFmt = false;

        Span<byte> chunkHeader = stackalloc byte[8];
        Span<byte> ds64 = stackalloc byte[24];
        while (ReadExactOrEof(stream, chunkHeader) == 8)
        {
            string chunkId = Encoding.ASCII.GetString(chunkHeader[0..4]);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..8]);
            long chunkPos = stream.Position;

            if (chunkId == "ds64" && chunkSize >= 24)
            {
                if (ReadExactOrEof(stream, ds64) == 24)
                    rf64DataSize = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(ds64[8..16]));
            }
            else if (chunkId == "fmt " && chunkSize >= 16)
            {
                byte[] fmt = new byte[Math.Min(chunkSize, 1024u)];
                if (ReadExactOrEof(stream, fmt) < 16)
                    throw new InvalidDataException("Truncated WAV fmt chunk.");

                formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(0, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2, 2));
                sampleRate = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4, 4)));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14, 2));

                if (formatTag == 0xFFFE && fmt.Length >= 40)
                {
                    // KSDATAFORMAT_SUBTYPE_* starts with little-endian uint16 format code (1=PCM, 3=IEEE_FLOAT)
                    formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(24, 2));
                }

                hasFmt = true;
            }
            else if (chunkId == "data")
            {
                dataOffset = chunkPos;
                dataSize = chunkSize == uint.MaxValue && rf64DataSize.HasValue
                    ? rf64DataSize.Value
                    : chunkSize;
                break;
            }

            long nextPos = chunkPos + chunkSize + (chunkSize & 1);
            if (nextPos > stream.Length)
                break;
            stream.Position = nextPos;
        }

        if (!hasFmt || dataOffset < 0 || sampleRate <= 0 || channels <= 0 || bitsPerSample is not (8 or 16 or 24 or 32 or 64))
            throw new InvalidDataException("Invalid or unsupported WAV format/data chunks.");
        if (formatTag is not (1 or 3))
            throw new NotSupportedException($"Unsupported WAV audio format tag {formatTag} (only PCM=1 and IEEE_FLOAT=3 are supported).");

        return (formatTag, channels, sampleRate, bitsPerSample, dataOffset, dataSize);
    }

    private static double ComputeWindowSumSquares(ReadOnlySpan<byte> bytes, ushort formatTag, int bitsPerSample, int sampleCount)
    {
        double sumSq = 0;
        if (formatTag == 3)
        {
            if (bitsPerSample == 32)
            {
                for (int i = 0; i < sampleCount; i++)
                {
                    float sample = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(i * 4, 4));
                    if (float.IsFinite(sample))
                        sumSq += (double)sample * sample;
                }
                return sumSq;
            }
            if (bitsPerSample == 64)
            {
                for (int i = 0; i < sampleCount; i++)
                {
                    double sample = BinaryPrimitives.ReadDoubleLittleEndian(bytes.Slice(i * 8, 8));
                    if (double.IsFinite(sample))
                        sumSq += sample * sample;
                }
                return sumSq;
            }
            throw new NotSupportedException($"Unsupported IEEE float bit depth: {bitsPerSample}");
        }

        switch (bitsPerSample)
        {
            case 8:
            {
                const double inv = 1.0 / 128.0;
                for (int i = 0; i < sampleCount; i++)
                {
                    double sample = (bytes[i] - 128) * inv;
                    sumSq += sample * sample;
                }
                return sumSq;
            }
            case 16:
            {
                const double inv = 1.0 / 32768.0;
                for (int i = 0; i < sampleCount; i++)
                {
                    short raw = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i * 2, 2));
                    double sample = raw * inv;
                    sumSq += sample * sample;
                }
                return sumSq;
            }
            case 24:
            {
                const double inv = 1.0 / 8388608.0;
                for (int i = 0; i < sampleCount; i++)
                {
                    int idx = i * 3;
                    int raw = bytes[idx] | (bytes[idx + 1] << 8) | (bytes[idx + 2] << 16);
                    if ((raw & 0x800000) != 0)
                        raw -= 0x1000000;
                    double sample = raw * inv;
                    sumSq += sample * sample;
                }
                return sumSq;
            }
            case 32:
            {
                const double inv = 1.0 / 2147483648.0;
                for (int i = 0; i < sampleCount; i++)
                {
                    int raw = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(i * 4, 4));
                    double sample = raw * inv;
                    sumSq += sample * sample;
                }
                return sumSq;
            }
            default:
                throw new NotSupportedException($"Unsupported PCM bit depth: {bitsPerSample}");
        }
    }

    private static int ReadExactOrEof(Stream stream, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = stream.Read(destination[total..]);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }

    private static long FrameToTicks(long frameIndex, int sampleRate) =>
        checked((long)Math.Round(frameIndex * (double)TicksPerSecond / sampleRate));

    private static double RmsToDb(double rms) =>
        rms <= 1e-12 ? -120.0 : Math.Max(-120.0, 20.0 * Math.Log10(rms));

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        double dx = x1 - x2;
        double dy = y1 - y2;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
