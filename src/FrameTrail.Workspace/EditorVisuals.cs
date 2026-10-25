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
        foreach (var effect in project.Effects)
            if (effect.Parameters.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String && path.GetString() is { Length: > 0 } value) yield return value;
    }
}
public sealed partial class EditorSession
{
    public Task<string> ImportVisualAsync(string source, bool video, CancellationToken cancellationToken = default) =>
        ImportVisualCoreAsync(source, video, false, cancellationToken);

    // Keep bundled backgrounds in the project so reopening and export never depend on the installation.
    public Task<string> ImportWallpaperAsync(string source, CancellationToken cancellationToken = default) =>
        ImportVisualCoreAsync(source, false, true, cancellationToken);

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
