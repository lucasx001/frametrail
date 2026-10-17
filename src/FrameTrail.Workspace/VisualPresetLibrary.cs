using System.Text.Json;
using FrameTrail.Domain;

namespace FrameTrail.Workspace;

public sealed record VisualPreset(string Id, string Name, CanvasSettings Canvas, EditorSettings Editor, int Version = 1);
public sealed record VisualPresetCatalog(IReadOnlyList<VisualPreset> Presets, string? DefaultId, IReadOnlyList<string> Warnings);

/// <summary>User-owned, reusable visual styles. Media, clip gains and timed effects remain with the project.</summary>
public sealed class VisualPresetLibrary
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly string root;
    public VisualPresetLibrary(string? directory = null)
    {
        root = Path.GetFullPath(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "Presets"));
        Directory.CreateDirectory(root); Guard(root);
    }
    private static void Guard(string path)
    { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("预设路径不能是目录联接或符号链接。"); }
    private string Folder(string id) => Guid.TryParseExact(id, "N", out _) ? Path.Combine(root, id) : throw new IOException("无效的预设标识。");
    private FileStream Lock() => new(Path.Combine(root, ".library.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    private static string Name(string? value)
    { value = value?.Trim(); return value is { Length: > 0 and <= 60 } && !value.Any(char.IsControl) ? value : throw new IOException("预设名称需要 1–60 个字符。"); }
    private static async Task AtomicAsync(string path, string text)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, text); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private async Task<VisualPreset> ReadAsync(string id)
    {
        string folder = Folder(id); Guard(folder); string path = Path.Combine(folder, "preset.json"); Guard(path);
        if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("预设文件过大。");
        var preset = System.Text.Json.JsonSerializer.Deserialize<VisualPreset>(await File.ReadAllTextAsync(path), Json) ?? throw new IOException("预设内容为空。");
        if (preset.Version != 1 || preset.Id != id || preset.Canvas is null || preset.Editor is null) throw new IOException("预设版本或内容无效。");
        _ = Name(preset.Name); return preset;
    }
    public async Task<VisualPresetCatalog> ListAsync()
    {
        using var lease = Lock(); var items = new List<VisualPreset>(); var warnings = new List<string>();
        foreach (string folder in Directory.EnumerateDirectories(root).Where(p => Guid.TryParseExact(Path.GetFileName(p), "N", out _)).Take(201)) {
            try { items.Add(await ReadAsync(Path.GetFileName(folder))); }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { warnings.Add(Path.GetFileName(folder) + ": " + error.Message); }
        }
        string? selected = null;
        string defaultFile = Path.Combine(root, "default.txt");
        if (File.Exists(defaultFile)) { Guard(defaultFile); if (new FileInfo(defaultFile).Length <= 128) selected = (await File.ReadAllTextAsync(defaultFile)).Trim(); }
        if (!items.Any(p => p.Id == selected)) selected = null;
        return new(items.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(), selected, warnings);
    }
    public async Task<VisualPreset> SaveAsync(string name, ProjectSnapshot snapshot, string projectDirectory)
    {
        using var lease = Lock(); name = Name(name);
        if (Directory.EnumerateDirectories(root).Count(p => Guid.TryParseExact(Path.GetFileName(p), "N", out _)) >= 200) throw new IOException("最多保存 200 个预设，请先删除不再使用的预设。");
        string id = Guid.NewGuid().ToString("N"), folder = Folder(id); Directory.CreateDirectory(folder);
        try {
            var settings = (snapshot.Editor ?? new()) with { ClipAudio = new Dictionary<string, AudioLevel>() };
            if (settings.Backdrop is { Kind: "image" or "video" } backdrop) {
                if (!ProjectJson.IsProjectRelativePath(backdrop.Path) || !backdrop.Path.StartsWith("assets/", StringComparison.Ordinal)) throw new IOException("背景必须来自项目素材。");
                string source = SafeProjectAsset(projectDirectory, backdrop.Path), filename = "background" + Path.GetExtension(source).ToLowerInvariant();
                await CopyAsync(source, Path.Combine(folder, filename)); settings = settings with { Backdrop = backdrop with { Path = filename } };
            }
            var preset = new VisualPreset(id, name, snapshot.Canvas, settings);
            await AtomicAsync(Path.Combine(folder, "preset.json"), System.Text.Json.JsonSerializer.Serialize(preset, Json)); return preset;
        } catch { Directory.Delete(folder, true); throw; }
    }
    private static string SafeProjectAsset(string project, string relative)
    {
        string path = Path.Combine(Path.GetFullPath(project), relative);
        for (string? part = path; part is not null && !string.Equals(part, Path.GetDirectoryName(Path.GetFullPath(project)), StringComparison.OrdinalIgnoreCase); part = Path.GetDirectoryName(part)) Guard(part);
        return path;
    }
    private static async Task CopyAsync(string source, string target)
    {
        var info = new FileInfo(source); Guard(source);
        if (info.Length <= 0 || info.Length > 2L * 1024 * 1024 * 1024) throw new IOException("预设背景资源需小于 2 GB。");
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        await input.CopyToAsync(output);
    }
    public async Task RenameAsync(string id, string name)
    { using var lease = Lock(); var preset = await ReadAsync(id); await AtomicAsync(Path.Combine(Folder(id), "preset.json"), System.Text.Json.JsonSerializer.Serialize(preset with { Name = Name(name) }, Json)); }
    public async Task SetDefaultAsync(string? id)
    { using var lease = Lock(); if (id is not null) _ = await ReadAsync(id); await AtomicAsync(Path.Combine(root, "default.txt"), id ?? ""); }
    public async Task DeleteAsync(string id)
    {
        using var lease = Lock(); _ = await ReadAsync(id);
        var folder = Folder(id); Guard(folder);
        // Never follow nested directory links during deletion.
        if (Directory.EnumerateDirectories(folder).Any()) throw new IOException("预设含有未知子目录，请检查后重试。");
        foreach (var file in Directory.EnumerateFiles(folder)) Guard(file);
        string defaultFile = Path.Combine(root, "default.txt");
        if (File.Exists(defaultFile) && (await File.ReadAllTextAsync(defaultFile)).Trim() == id) await AtomicAsync(defaultFile, "");
        Directory.Delete(folder, true);
    }
    public async Task<ProjectSnapshot> ApplyAsync(string id, ProjectSnapshot target, string projectDirectory)
    {
        using var lease = Lock(); var preset = await ReadAsync(id); var settings = preset.Editor;
        string? created = null;
        try {
            if (settings.Backdrop is { Kind: "image" or "video" } backdrop) {
                if (Path.GetFileName(backdrop.Path) != backdrop.Path || !backdrop.Path.StartsWith("background.", StringComparison.Ordinal)) throw new IOException("预设背景路径无效。");
                string relative = "assets/preset-" + Guid.NewGuid().ToString("N") + Path.GetExtension(backdrop.Path);
                string assets = Path.Combine(projectDirectory, "assets"); Directory.CreateDirectory(assets); Guard(assets);
                created = Path.Combine(projectDirectory, relative);
                await CopyAsync(Path.Combine(Folder(id), backdrop.Path), created);
                settings = settings with { Backdrop = backdrop with { Path = relative } };
            }
            var candidate = target with { Canvas = preset.Canvas, Editor = settings with { ClipAudio = target.Editor?.ClipAudio ?? new Dictionary<string, AudioLevel>() } };
            FrameTrail.Interop.EngineSession.ValidateProject(candidate); created = null; return candidate;
        } finally { if (created is not null && File.Exists(created)) File.Delete(created); }
    }
}
