using System.Text.Json;
using FrameTrail.Domain;

namespace FrameTrail.Workspace;

/// <summary>Converts sampled cursor tracks into actual activity for the shared native camera planner.</summary>
internal static class AutomaticZoomPlanner
{
    private sealed record Activity(long Ticks, double X, double Y, int Kind);

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
                if (line.Length > 32768) throw new IOException("光标事件过长。");
                using var document = JsonDocument.Parse(line); var e = document.RootElement;
                if (!e.TryGetProperty("type", out var type) || type.GetString() != "pointer" || e.TryGetProperty("inside", out var inside) && !inside.GetBoolean()) continue;
                int timescale = e.GetProperty("timescale").GetInt32();
                if (timescale <= 0) throw new IOException("光标时间单位无效。");
                var local = VisualResources.Ticks(new() { Ticks = e.GetProperty("ticks").GetInt64(), Timescale = timescale });
                if (local < 0 || local >= duration) continue;
                long ticks = checked(local + offset);
                double x = e.GetProperty("x").GetDouble(), y = e.GetProperty("y").GetDouble();
                if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || x > 1 || y < 0 || y > 1) throw new IOException("光标坐标无效。");
                int kind = e.TryGetProperty("kind", out var k) ? k.GetInt32() : 0;
                bool click = kind is 1 or 3;
                // The recorder samples at 60 Hz even when stationary. Only movement
                // of at least one source pixel extends a click's automatic hold.
                bool moved = lastX is null || Math.Abs(x - lastX.Value) >= pixelX || Math.Abs(y - lastY!.Value) >= pixelY;
                if (!click && (!moved || ticks - lastActivity < 1_000_000)) continue;
                lastX = x; lastY = y; lastActivity = ticks;
                events.Add(new(ticks, x, y, click ? kind : 0));
                if (events.Count > 100000) throw new IOException("自动运镜事件超过本次规划上限。");
            }
        }
        return new { op = "autoZoom", events, scale };
    }
}
