using System.Text.Json;
using System.Security.Cryptography;
using FrameTrail.Domain;
using FrameTrail.Interop;

namespace FrameTrail.Workspace;

public static class VisualResources
{
    public static long Ticks(MediaTime time) => checked((long)decimal.Round((decimal)time.Ticks * 10_000_000 / time.Timescale, 0, MidpointRounding.AwayFromZero));
    public static IEnumerable<string> Paths(ProjectSnapshot project)
    {
        if (project.Editor?.Backdrop is { Kind: "image" or "video" } backdrop) yield return backdrop.Path;
        if (!string.IsNullOrEmpty(project.Editor?.BgmPath)) yield return project.Editor.BgmPath;
        foreach (var effect in project.Effects)
            if (effect.Parameters.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String && path.GetString() is { Length: > 0 } value) yield return value;
    }

    public static IReadOnlyList<(long StartTicks, long EndTicks)> ResolveSubtitleCutRanges(
        ProjectSnapshot snapshot,
        IEnumerable<string> subtitleIds,
        string? matchingSearchQuery = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        const long minKeepTicks = 1_000_000L; // 0.10s
        long totalSource = Math.Max(0L, Ticks(snapshot.SourceDuration));
        if (totalSource <= minKeepTicks) return Array.Empty<(long, long)>();

        HashSet<string>? idSet = null;
        if (matchingSearchQuery is not null)
        {
            if (string.IsNullOrWhiteSpace(matchingSearchQuery))
                return Array.Empty<(long, long)>();
        }
        else
        {
            idSet = subtitleIds is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(subtitleIds, StringComparer.Ordinal);
            if (idSet.Count == 0) return Array.Empty<(long, long)>();
        }

        var raw = new List<(long StartTicks, long EndTicks)>();
        foreach (var eff in snapshot.Effects)
        {
            if (!string.Equals(eff.Kind, "subtitle", StringComparison.Ordinal)) continue;
            if (matchingSearchQuery is not null)
            {
                var p = SubtitleCueParameters.FromEffect(eff);
                bool matchesPrimary = p.Text.Contains(matchingSearchQuery, StringComparison.OrdinalIgnoreCase);
                bool matchesSecondary = !string.IsNullOrEmpty(p.SecondaryText) &&
                    p.SecondaryText.Contains(matchingSearchQuery, StringComparison.OrdinalIgnoreCase);
                if (!matchesPrimary && !matchesSecondary) continue;
            }
            else if (!idSet!.Contains(eff.Id))
            {
                continue;
            }

            long start = Math.Clamp(Ticks(eff.SourceStart), 0L, totalSource);
            long end = Math.Clamp(start + Ticks(eff.SourceDuration), 0L, totalSource);
            if (end > start) raw.Add((start, end));
        }

        if (raw.Count == 0) return Array.Empty<(long, long)>();
        raw.Sort((a, b) => a.StartTicks != b.StartTicks ? a.StartTicks.CompareTo(b.StartTicks) : a.EndTicks.CompareTo(b.EndTicks));

        var merged = new List<(long StartTicks, long EndTicks)>(raw.Count);
        foreach (var r in raw)
        {
            if (merged.Count > 0 && r.StartTicks <= merged[^1].EndTicks)
                merged[^1] = (merged[^1].StartTicks, Math.Max(merged[^1].EndTicks, r.EndTicks));
            else
                merged.Add(r);
        }

        var segments = snapshot.Segments.Count > 0
            ? snapshot.Segments.Select(s =>
            {
                long sStart = Math.Clamp(Ticks(s.SourceStart), 0L, totalSource);
                long sEnd = Math.Clamp(sStart + Ticks(s.SourceDuration), 0L, totalSource);
                return (Start: sStart, End: sEnd);
            }).Where(s => s.End > s.Start).ToList()
            : new List<(long Start, long End)> { (0L, totalSource) };

        if (HasSurvivingTimeline(segments, merged, minKeepTicks))
            return merged;

        // Clamp the tail of the cut ranges so at least 0.10s remains on the last valid segment.
        int lastIdx = segments.FindLastIndex(s => s.End - s.Start >= minKeepTicks);
        if (lastIdx < 0) return Array.Empty<(long, long)>();
        long safeEnd = segments[lastIdx].End - minKeepTicks;
        var clamped = new List<(long StartTicks, long EndTicks)>(merged.Count);
        foreach (var r in merged)
        {
            if (r.StartTicks >= safeEnd) continue;
            long end = Math.Min(r.EndTicks, safeEnd);
            if (end > r.StartTicks) clamped.Add((r.StartTicks, end));
        }
        return clamped;
    }

    private static bool HasSurvivingTimeline(
        IReadOnlyList<(long Start, long End)> segments,
        IReadOnlyList<(long StartTicks, long EndTicks)> cuts,
        long minKeepTicks)
    {
        foreach (var (segStart, segEnd) in segments)
        {
            var segCuts = new List<(long Start, long End)>();
            foreach (var (cs, ce) in cuts)
            {
                if (ce <= segStart) continue;
                if (cs >= segEnd) break;
                long s = Math.Max(cs, segStart);
                long e = Math.Min(ce, segEnd);
                if (e <= s) continue;
                if (s - segStart < minKeepTicks) s = segStart;
                if (segEnd - e < minKeepTicks) e = segEnd;
                if (segCuts.Count > 0 && s <= segCuts[^1].End + minKeepTicks)
                    segCuts[^1] = (segCuts[^1].Start, Math.Max(segCuts[^1].End, e));
                else
                    segCuts.Add((s, e));
            }
            if (segCuts.Count == 0 && segEnd - segStart >= minKeepTicks) return true;
            long cursor = segStart;
            foreach (var (cs, ce) in segCuts)
            {
                if (cs > cursor && cs - cursor >= minKeepTicks) return true;
                cursor = Math.Max(cursor, ce);
            }
            if (segEnd > cursor && segEnd - cursor >= minKeepTicks) return true;
        }
        return false;
    }
}
public sealed partial class EditorSession
{
    public Task<string> ImportVisualAsync(string source, bool video, CancellationToken cancellationToken = default) =>
        ImportVisualCoreAsync(source, video, false, cancellationToken);

    // Keep bundled backgrounds in the project so reopening and export never depend on the installation.
    public Task<string> ImportWallpaperAsync(string source, CancellationToken cancellationToken = default) =>
        ImportVisualCoreAsync(source, false, true, cancellationToken);

    public async Task<string> ImportBgmAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new IOException(L.T("SB0C847A65D31"));
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not (".mp3" or ".wav" or ".m4a" or ".aac" or ".flac"))
            throw new IOException(L.T("SB0C847A65D31"));
        CheckSourceAncestors(sourcePath);
        var inputInfo = new FileInfo(sourcePath);
        if (!inputInfo.Exists)
            throw new FileNotFoundException("Source BGM audio file not found.", sourcePath);
        if (inputInfo.Length <= 0 || inputInfo.Length > 256L * 1024 * 1024)
            throw new IOException(L.T("S1B19EF3E70E4"));

        await gate.WaitAsync(cancellationToken);
        string? created = null;
        try
        {
            EnsureEditable();
            await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
            input.Position = 0;
            var relative = $"assets/bgm-{hash[..16]}{extension}";
            var destination = workspace.Resolve(relative);
            if (File.Exists(destination))
            {
                await using var existing = File.OpenRead(destination);
                string existingHash = Convert.ToHexString(await SHA256.HashDataAsync(existing, cancellationToken)).ToLowerInvariant();
                if (string.Equals(existingHash, hash, StringComparison.OrdinalIgnoreCase))
                {
                    long existingDur = await Task.Run(() => RecordingUtilities.ProbeMedia(destination, false), cancellationToken);
                    if (existingDur <= 0) throw new IOException($"Invalid audio file: {sourcePath}");
                    return relative;
                }
                relative = $"assets/bgm-{hash}{extension}";
                destination = workspace.Resolve(relative);
                if (File.Exists(destination))
                {
                    long existingDur = await Task.Run(() => RecordingUtilities.ProbeMedia(destination, false), cancellationToken);
                    if (existingDur <= 0) throw new IOException($"Invalid audio file: {sourcePath}");
                    return relative;
                }
            }

            created = destination;
            Directory.CreateDirectory(Path.GetDirectoryName(created)!);
            await using (var output = new FileStream(created, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                await input.CopyToAsync(output, cancellationToken);
            long duration = await Task.Run(() => RecordingUtilities.ProbeMedia(created, false), cancellationToken);
            if (duration <= 0)
                throw new IOException($"Invalid audio file: {sourcePath}");
            created = null;
            return relative;
        }
        finally
        {
            try
            {
                if (created is not null && File.Exists(created)) File.Delete(created);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private static void CheckSourceAncestors(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(L.T("SFD13984392A6"));
            current = Path.GetDirectoryName(current)!;
        }
    }

    public async Task<IReadOnlyList<string>> ImportVideoClipsAsync(
        IReadOnlyList<string> sourcePaths,
        int? insertIndex = null,
        CancellationToken cancellationToken = default,
        Func<string, (uint Width, uint Height, long Duration, bool HasAudio, uint SampleRate, uint Channels)>? clipProbe = null)
    {
        if (sourcePaths is null || sourcePaths.Count == 0 || sourcePaths.Count > 100)
            throw new ArgumentException("Source clip count must be between 1 and 100.", nameof(sourcePaths));
        await gate.WaitAsync(cancellationToken);
        var createdFiles = new List<string>();
        try
        {
            EnsureEditable();
            if (transaction is not null) throw new InvalidOperationException("Finish the current drag transaction first.");
            var probeFn = clipProbe ?? MediaSession.ProbeClip;
            var commands = new List<object>(sourcePaths.Count);
            var segmentIds = new List<string>(sourcePaths.Count);
            for (int i = 0; i < sourcePaths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = sourcePaths[i];
                if (string.IsNullOrWhiteSpace(source)) throw new IOException(L.T("SB0C847A65D31"));
                var extension = Path.GetExtension(source).ToLowerInvariant();
                if (extension is not (".mp4" or ".mov" or ".m4v")) throw new IOException(L.T("SB0C847A65D31"));
                CheckSourceAncestors(source);
                var inputInfo = new FileInfo(source);
                if (!inputInfo.Exists) throw new FileNotFoundException("Source video file not found.", source);
                if (inputInfo.Length <= 0 || inputInfo.Length > 2L * 1024 * 1024 * 1024) throw new IOException(L.T("S1B19EF3E70E4"));
                var relative = "media/import-" + Guid.NewGuid().ToString("N") + extension;
                var destination = workspace.Resolve(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                createdFiles.Add(destination);
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
                await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                    await input.CopyToAsync(output, cancellationToken);
                var info = await Task.Run(() => probeFn(destination), cancellationToken);
                if (info.Width == 0 || info.Height == 0 || info.Duration <= 0)
                    throw new IOException($"Invalid video clip metadata: {source}");
                var segmentId = "clip-" + Guid.NewGuid().ToString("N");
                segmentIds.Add(segmentId);
                var screenMedia = new
                {
                    id = "media-" + Guid.NewGuid().ToString("N"),
                    path = relative,
                    duration = MediaTime.FromTicks(info.Duration),
                    width = info.Width,
                    height = info.Height
                };
                var audioMedia = info.HasAudio
                    ? new
                    {
                        id = "media-" + Guid.NewGuid().ToString("N"),
                        path = relative,
                        duration = MediaTime.FromTicks(info.Duration),
                        sampleRate = info.SampleRate > 0 ? info.SampleRate : 48000u,
                        channels = info.Channels > 0 ? info.Channels : 2u
                    }
                    : null;
                int? targetIndex = insertIndex.HasValue
                    ? Math.Clamp(insertIndex.Value + i, 0, Snapshot.Segments.Count + i)
                    : null;
                commands.Add(new
                {
                    op = "appendMedia",
                    segmentId,
                    screenMedia,
                    audioMedia,
                    insertIndex = targetIndex
                });
            }
            var command = commands.Count == 1 ? commands[0] : new { op = "batch", commands };
            var candidate = EngineSession.Edit(Snapshot, command);
            Push(undo, Snapshot);
            redo.Clear();
            await AcceptAsync(candidate);
            createdFiles.Clear();
            return segmentIds;
        }
        finally
        {
            try
            {
                foreach (var file in createdFiles)
                    if (File.Exists(file)) File.Delete(file);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task<string> ImportVisualCoreAsync(string source, bool video, bool reuse, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        string? created = null;
        try
        {
            EnsureEditable();
            var extension = Path.GetExtension(source).ToLowerInvariant();
            if (!(video ? new[] { ".mp4", ".mov", ".m4v" } : new[] { ".png", ".jpg", ".jpeg", ".bmp" }).Contains(extension)) throw new IOException(L.T("SB0C847A65D31"));
            var inputInfo = new FileInfo(source);
            if (inputInfo.Length <= 0 || inputInfo.Length > (video ? 2L * 1024 * 1024 * 1024 : 32L * 1024 * 1024)) throw new IOException(L.T("S1B19EF3E70E4"));
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            string? hash = reuse ? Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant() : null;
            input.Position = 0;
            var relative = "assets/" + (reuse ? "wallpaper-" + hash : Guid.NewGuid().ToString("N")) + extension;
            var destination = workspace.Resolve(relative);
            if (reuse && File.Exists(destination)) {
                await using var existing = File.OpenRead(destination);
                if (Convert.ToHexString(await SHA256.HashDataAsync(existing, cancellationToken)).Equals(hash, StringComparison.OrdinalIgnoreCase)) return relative;
                throw new IOException(L.T("WallpaperAssetChanged"));
            }
            created = destination; Directory.CreateDirectory(Path.GetDirectoryName(created)!);
            await using (var output = new FileStream(created, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                await input.CopyToAsync(output, cancellationToken);
            await Task.Run(() => MediaSession.ProbeVisual(created, video), cancellationToken);
            created = null; return relative;
        }
        finally { try { if (created is not null) File.Delete(created); } finally { gate.Release(); } }
    }
    public Task UpsertEffectAsync(SourceEffect effect) => ExecuteAsync(new { op = "effectUpsert", effectId = effect.Id, effect });
    public Task DeleteEffectAsync(string id) => ExecuteAsync(new { op = "effectDelete", effectId = id });
    public async Task PasteEffectAttributesAsync(SourceEffect source, string targetId)
    {
        await gate.WaitAsync();
        try {
            EnsureEditable();
            if (transaction is not null) throw new InvalidOperationException(L.T("SFF8CA4F44F59"));
            var target = Snapshot.Effects.SingleOrDefault(e => e.Id == targetId) ?? throw new ArgumentException(L.T("SelectTargetEffect"));
            if (target.Kind != source.Kind) throw new ArgumentException(L.T("SelectCompatibleEffect"));
            var properties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(source.Parameters.GetRawText())!;
            // Source tracks and sticker content belong to the target, not its style.
            foreach (var key in new[] { "path", "text", "layer", "focusPoints", "focusTransition", "target" }) {
                properties.Remove(key);
                if (target.Parameters.TryGetProperty(key, out var value)) properties[key] = value.Clone();
            }
            properties["origin"] = JsonSerializer.SerializeToElement("manual");
            properties.Remove("plannerState"); properties.Remove("planner");
            var effect = target with { Parameters = JsonSerializer.SerializeToElement(properties) };
            if (effect.Parameters.GetRawText() == target.Parameters.GetRawText()) return;
            var candidate = EngineSession.Edit(Snapshot, new { op = "effectUpsert", effectId = targetId, effect });
            Push(undo, Snapshot); redo.Clear(); await AcceptAsync(candidate);
        } finally { gate.Release(); }
    }
    public Task PasteEffectAsync(SourceEffect effect, long sourceTicks)
    {
        var duration = Math.Min(VisualResources.Ticks(effect.SourceDuration), VisualResources.Ticks(Snapshot.SourceDuration) - sourceTicks);
        if (sourceTicks < 0 || duration <= 0) throw new ArgumentOutOfRangeException(nameof(sourceTicks));
        var parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(effect.Parameters.GetRawText())!;
        parameters["origin"] = JsonSerializer.SerializeToElement("manual");
        parameters.Remove("plannerState"); parameters.Remove("planner");
        if (parameters.TryGetValue("focusPoints", out var points))
        {
            var delta = sourceTicks - VisualResources.Ticks(effect.SourceStart);
            var shifted = new List<Dictionary<string, JsonElement>>();
            foreach (var point in points.EnumerateArray())
            {
                long ticks = checked(point.GetProperty("ticks").GetInt64() + delta);
                if (ticks < sourceTicks || ticks >= sourceTicks + duration) continue;
                var key = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(point.GetRawText())!;
                key["ticks"] = JsonSerializer.SerializeToElement(ticks);
                shifted.Add(key);
            }
            parameters["focusPoints"] = JsonSerializer.SerializeToElement(shifted);
        }
        return UpsertEffectAsync(effect with { Id = "effect-" + Guid.NewGuid().ToString("N"), SourceStart = MediaTime.FromTicks(sourceTicks), SourceDuration = MediaTime.FromTicks(duration), Parameters = JsonSerializer.SerializeToElement(parameters) });
    }
    public async Task AutoZoomAsync(double scale = 1.6)
    {
        if (Snapshot.PrimarySource?.Kind == "androidUsb") return;
        await ExecuteAsync(await AutomaticZoomPlanner.CreateCommandAsync(workspace, Snapshot, scale));
    }
    public Task ApplyPresetAsync(string name)
    {
        var prior = Snapshot.Editor ?? new();
        var settings = name switch
        {
            "studio" => prior with { FrameStyle = "browser", CornerRadius = 18, Shadow = 0.45, CameraShape = "circle", Backdrop = new() { Kind = "gradient" } },
            "device" => prior with { FrameStyle = "device", CornerRadius = 34, Shadow = 0.4, Backdrop = new() { Kind = "solid" } },
            "clean" => prior with { FrameStyle = "none", CornerRadius = 12, Shadow = 0.2, CameraShape = "rounded", Backdrop = new() { Kind = "solid" } },
            _ => throw new ArgumentException("Unknown preset.", nameof(name))
        };
        return ExecuteAsync(new { op = "settings", editor = settings, canvas = Snapshot.Canvas with { Padding = name == "device" ? 0.08 : 0.06 } });
    }
    public async Task SaveVisualPresetAsync()
    {
        await gate.WaitAsync();
        try { EnsureEditable(); var path = workspace.Resolve("assets/presets/saved.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp"; await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new { canvas = Snapshot.Canvas, editor = Snapshot.Editor ?? new() }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })); File.Move(temporary, path, true); }
        finally { gate.Release(); }
    }
    public async Task LoadVisualPresetAsync()
    {
        var path = workspace.Resolve("assets/presets/saved.json");
        if (new FileInfo(path).Length > ProjectJson.MaximumJsonBytes) throw new IOException(L.T("S980FE8B29671"));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var saved = json.RootElement.GetProperty("editor").Deserialize<EditorSettings>(options) ?? throw new IOException(L.T("S2A8DF38D0486"));
        var prior = Snapshot.Editor ?? new();
        var visual = saved with { SystemGain = prior.SystemGain, MicrophoneGain = prior.MicrophoneGain, SystemMuted = prior.SystemMuted, MicrophoneMuted = prior.MicrophoneMuted, ClipAudio = prior.ClipAudio };
        await ExecuteAsync(new { op = "settings", canvas = json.RootElement.GetProperty("canvas"), editor = visual });
    }
    public async Task ApplyLibraryPresetAsync(VisualPresetLibrary library, string id)
    {
        await gate.WaitAsync();
        try {
            EnsureEditable(); if (transaction is not null) throw new InvalidOperationException(L.T("SFF8CA4F44F59"));
            var applied = await library.ApplyAsync(id, Snapshot, DirectoryPath);
            var candidate = EngineSession.Edit(Snapshot, new { op = "settings", canvas = applied.Canvas, editor = applied.Editor });
            Push(undo, Snapshot); redo.Clear(); await AcceptAsync(candidate);
        } finally { gate.Release(); }
    }
}
