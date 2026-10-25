using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;

namespace FrameTrail.Workspace;

/// <summary>A recording-run-local reader. Completed pointer files are consumed once, in bounded batches.</summary>
internal sealed class AutomaticZoomPlanner
{
    internal const string Version = "frametrail-incremental-v1";
    private const int BatchSize = 8192;
    private sealed record Activity(long Ticks, double X, double Y, int Kind);
    private readonly HashSet<string> consumed = new(StringComparer.Ordinal);
    private SourceEffect[]? generated;
    private double? lastX, lastY;
    private long lastActivity = -1;
    internal int FilesRead { get; private set; }
    internal int MaximumBatchSize { get; private set; }
    internal long ActivitiesRead { get; private set; }

    internal static bool IsGenerated(SourceEffect effect) => effect.Kind == "zoom" &&
        effect.Parameters.TryGetProperty("origin", out var origin) && origin.GetString() == "automatic" &&
        effect.Parameters.TryGetProperty("planner", out var planner) && planner.GetString() == Version;

    internal async Task<ProjectSnapshot> AppendAsync(ProjectWorkspace workspace, ProjectSnapshot snapshot, CancellationToken token)
    {
        var result = generated is null ? snapshot : snapshot with
        {
            Effects = snapshot.Effects.Where(e => !IsGenerated(e))
                .Concat(generated.Where(g => !snapshot.Effects.Any(e => e.Id == g.Id && !IsGenerated(e)))).ToArray()
        };
        var screen = snapshot.Media.First(m => m.Kind == MediaKind.Screen);
        double pixelX = 1.0 / Math.Max(1u, screen.Width), pixelY = 1.0 / Math.Max(1u, screen.Height);
        foreach (var media in snapshot.Media.Where(m => m.Kind == MediaKind.PointerEvents).OrderBy(m => VisualResources.Ticks(m.StartOffset)))
        {
            if (consumed.Contains(media.Id)) continue;
            token.ThrowIfCancellationRequested();
            var offset = VisualResources.Ticks(media.StartOffset);
            var duration = VisualResources.Ticks(media.Duration);
            var batch = new List<Activity>(BatchSize);
            // Commit filter state only after the complete file succeeds, so malformed
            // input never partially advances the run's cursor on the next checkpoint.
            var xBefore = lastX; var yBefore = lastY; var activityBefore = lastActivity;
            var effectsBefore = result.Effects;
            try
            {
                using var reader = new StreamReader(workspace.Resolve(media.Path));
                FilesRead++;
                while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
                {
                    if (line.Length > 32768) throw new IOException(L.T("S6C9A8253585B"));
                    using var document = JsonDocument.Parse(line);
                    var e = document.RootElement;
                    if (!e.TryGetProperty("type", out var type) || type.GetString() != "pointer" ||
                        e.TryGetProperty("inside", out var inside) && !inside.GetBoolean()) continue;
                    int timescale = e.GetProperty("timescale").GetInt32();
                    if (timescale <= 0) throw new IOException(L.T("SA593155F9D65"));
                    var local = VisualResources.Ticks(new() { Ticks = e.GetProperty("ticks").GetInt64(), Timescale = timescale });
                    if (local < 0 || local >= duration) continue;
                    long ticks = checked(local + offset);
                    double x = e.GetProperty("x").GetDouble(), y = e.GetProperty("y").GetDouble();
                    if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || x > 1 || y < 0 || y > 1)
                        throw new IOException(L.T("S3626EC938A61"));
                    if (ticks < lastActivity) throw new IOException("Pointer samples must be chronological within a finalized recording.");
                    int kind = e.TryGetProperty("kind", out var k) ? k.GetInt32() : 0;
                    bool click = kind is 1 or 3;
                    bool moved = lastX is null || Math.Abs(x - lastX.Value) >= pixelX || Math.Abs(y - lastY!.Value) >= pixelY;
                    if (!click && (!moved || ticks - lastActivity < 1_000_000)) continue;
                    lastX = x; lastY = y; lastActivity = ticks;
                    batch.Add(new(ticks, x, y, click ? kind : 0));
                    ActivitiesRead++;
                    if (batch.Count == BatchSize) result = ApplyBatch(result, batch, offset + duration, token);
                }
                result = ApplyBatch(result, batch, offset + duration, token);
                consumed.Add(media.Id);
                generated = result.Effects.Where(IsGenerated).ToArray();
            }
            catch
            {
                lastX = xBefore; lastY = yBefore; lastActivity = activityBefore;
                result = result with { Effects = effectsBefore };
                throw;
            }
        }
        // A missing pointer tail still advances the last zoom's deterministic exit.
        result = ApplyBatch(result, new(), VisualResources.Ticks(snapshot.SourceDuration), token);
        generated = result.Effects.Where(IsGenerated).ToArray();
        return result;
    }

    private ProjectSnapshot ApplyBatch(ProjectSnapshot project, List<Activity> events, long through, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        MaximumBatchSize = Math.Max(MaximumBatchSize, events.Count);
        var result = EngineSession.Edit(project, new { op = "autoZoomAppend", events, throughTicks = through, scale = 1.6 });
        events.Clear();
        return result;
    }

    // Compatibility entry for the explicit editor API; recording uses AppendAsync.
    internal static async Task<object> CreateCommandAsync(ProjectWorkspace workspace, ProjectSnapshot snapshot, double scale = 1.6)
    {
        var events = new List<Activity>();
        double? lastX = null, lastY = null;
        long lastActivity = -1;
        var screen = snapshot.Media.First(m => m.Kind == MediaKind.Screen);
        double pixelX = 1.0 / Math.Max(1u, screen.Width), pixelY = 1.0 / Math.Max(1u, screen.Height);
        foreach (var media in snapshot.Media.Where(m => m.Kind == MediaKind.PointerEvents).OrderBy(m => VisualResources.Ticks(m.StartOffset)))
        {
            var offset = VisualResources.Ticks(media.StartOffset);
            var duration = VisualResources.Ticks(media.Duration);
            using var reader = new StreamReader(workspace.Resolve(media.Path));
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length > 32768) throw new IOException(L.T("S6C9A8253585B"));
                using var document = JsonDocument.Parse(line); var e = document.RootElement;
                if (!e.TryGetProperty("type", out var type) || type.GetString() != "pointer" || e.TryGetProperty("inside", out var inside) && !inside.GetBoolean()) continue;
                int timescale = e.GetProperty("timescale").GetInt32();
                if (timescale <= 0) throw new IOException(L.T("SA593155F9D65"));
                var local = VisualResources.Ticks(new() { Ticks = e.GetProperty("ticks").GetInt64(), Timescale = timescale });
                if (local < 0 || local >= duration) continue;
                long ticks = checked(local + offset);
                double x = e.GetProperty("x").GetDouble(), y = e.GetProperty("y").GetDouble();
                if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || x > 1 || y < 0 || y > 1) throw new IOException(L.T("S3626EC938A61"));
                int kind = e.TryGetProperty("kind", out var k) ? k.GetInt32() : 0;
                bool click = kind is 1 or 3;
                // The recorder samples at 60 Hz even when stationary. Only movement
                // of at least one source pixel extends a click's automatic hold.
                bool moved = lastX is null || Math.Abs(x - lastX.Value) >= pixelX || Math.Abs(y - lastY!.Value) >= pixelY;
                if (!click && (!moved || ticks - lastActivity < 1_000_000)) continue;
                lastX = x; lastY = y; lastActivity = ticks;
                events.Add(new(ticks, x, y, click ? kind : 0));
                if (events.Count > 100000) throw new IOException(L.T("S7B0D33B7686F"));
            }
        }
        return new { op = "autoZoom", events, scale };
    }
}
