using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;

namespace FrameTrail.Workspace;

public static class VisualResources
{
    public static long Ticks(MediaTime time) => checked((long)decimal.Round((decimal)time.Ticks * 10_000_000 / time.Timescale, 0, MidpointRounding.AwayFromZero));
    public static IEnumerable<string> Paths(ProjectSnapshot project)
    {
        if (project.Editor?.Backdrop is { Kind: "image" or "video" } backdrop) yield return backdrop.Path;
        foreach (var effect in project.Effects)
            if (effect.Parameters.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String && path.GetString() is { Length: > 0 } value) yield return value;
    }
}
public sealed partial class EditorSession
{
    public async Task<string> ImportVisualAsync(string source, bool video, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        string? created = null;
        try
        {
            EnsureEditable();
            var extension = Path.GetExtension(source).ToLowerInvariant();
            if (!(video ? new[] { ".mp4", ".mov", ".m4v" } : new[] { ".png", ".jpg", ".jpeg", ".bmp" }).Contains(extension)) throw new IOException("请选择支持的图片或视频格式。");
            var inputInfo = new FileInfo(source);
            if (inputInfo.Length <= 0 || inputInfo.Length > (video ? 2L * 1024 * 1024 * 1024 : 32L * 1024 * 1024)) throw new IOException("图片需小于 32 MB，背景视频需小于 2 GB。");
            var relative = "assets/" + Guid.NewGuid().ToString("N") + extension; created = workspace.Resolve(relative); Directory.CreateDirectory(Path.GetDirectoryName(created)!);
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
            await using (var output = new FileStream(created, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                await input.CopyToAsync(output, cancellationToken);
            await Task.Run(() => MediaSession.ProbeVisual(created, video), cancellationToken);
            created = null; return relative;
        }
        finally { try { if (created is not null) File.Delete(created); } finally { gate.Release(); } }
    }
    public Task UpsertEffectAsync(SourceEffect effect) => ExecuteAsync(new { op = "effectUpsert", effectId = effect.Id, effect });
    public Task DeleteEffectAsync(string id) => ExecuteAsync(new { op = "effectDelete", effectId = id });
    public Task PasteEffectAsync(SourceEffect effect, long sourceTicks)
    {
        var duration = Math.Min(VisualResources.Ticks(effect.SourceDuration), VisualResources.Ticks(Snapshot.SourceDuration) - sourceTicks);
        if (sourceTicks < 0 || duration <= 0) throw new ArgumentOutOfRangeException(nameof(sourceTicks));
        var parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(effect.Parameters.GetRawText())!;
        parameters["origin"] = JsonSerializer.SerializeToElement("manual");
        if (parameters.TryGetValue("focusPoints", out var points))
        {
            var delta = sourceTicks - VisualResources.Ticks(effect.SourceStart);
            parameters["focusPoints"] = JsonSerializer.SerializeToElement(points.EnumerateArray().Select(p => new { ticks = p.GetProperty("ticks").GetInt64() + delta, x = p.GetProperty("x").GetDouble(), y = p.GetProperty("y").GetDouble() }).Where(p => p.ticks >= sourceTicks && p.ticks < sourceTicks + duration));
        }
        return UpsertEffectAsync(effect with { Id = "effect-" + Guid.NewGuid().ToString("N"), SourceStart = MediaTime.FromTicks(sourceTicks), SourceDuration = MediaTime.FromTicks(duration), Parameters = JsonSerializer.SerializeToElement(parameters) });
    }
    public async Task AutoZoomAsync(double scale = 1.6)
    {
        var events = new List<object>();
        foreach (var media in Snapshot.Media.Where(m => m.Kind == MediaKind.PointerEvents))
        {
            using var reader = new StreamReader(workspace.Resolve(media.Path)); string? line; long lastActivity = -1;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                if (line.Length > 32768) throw new IOException("光标事件过长。");
                using var document = JsonDocument.Parse(line); var e = document.RootElement;
                if (!e.TryGetProperty("type", out var type) || type.GetString() != "pointer" || e.TryGetProperty("inside", out var inside) && !inside.GetBoolean()) continue;
                long ticks = checked(VisualResources.Ticks(new() { Ticks = e.GetProperty("ticks").GetInt64(), Timescale = e.GetProperty("timescale").GetInt32() }) + VisualResources.Ticks(media.StartOffset));
                int kind = e.TryGetProperty("kind", out var k) ? k.GetInt32() : 0;
                if (kind is not (1 or 3) && ticks - lastActivity < 1_000_000) continue;
                lastActivity = ticks;
                events.Add(new { ticks, x = e.GetProperty("x").GetDouble(), y = e.GetProperty("y").GetDouble(), kind });
                if (events.Count > 100000) throw new IOException("自动运镜事件超过本次规划上限。");
            }
        }
        await ExecuteAsync(new { op = "autoZoom", events, scale });
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
        if (new FileInfo(path).Length > ProjectJson.MaximumJsonBytes) throw new IOException("预设文件过大。");
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var saved = json.RootElement.GetProperty("editor").Deserialize<EditorSettings>(options) ?? throw new IOException("预设缺少画面设置。");
        var prior = Snapshot.Editor ?? new();
        var visual = saved with { SystemGain = prior.SystemGain, MicrophoneGain = prior.MicrophoneGain, SystemMuted = prior.SystemMuted, MicrophoneMuted = prior.MicrophoneMuted, ClipAudio = prior.ClipAudio };
        await ExecuteAsync(new { op = "settings", canvas = json.RootElement.GetProperty("canvas"), editor = visual });
    }
}
