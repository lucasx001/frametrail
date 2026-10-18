using System.Text.Json;
using FrameTrail.Domain;

namespace FrameTrail.App;

internal sealed record ProjectCatalogEntry(string Directory, string Name, DateTime ModifiedUtc,
    double? RecordingSeconds, uint? Width, uint? Height, string? Error)
{
    internal bool CanOpen => Error is null;
}

// Browse metadata without acquiring a writable project lease or decoding media.
internal sealed class ProjectCatalog
{
    private readonly string settingsFile;
    private readonly object gate = new();
    private sealed record Locations(string[] Projects, string[] Folders);
    internal ProjectCatalog(string? settingsFile = null) => this.settingsFile = settingsFile ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "project-catalog.json");

    private Locations ReadLocations()
    {
        try {
            return JsonSerializer.Deserialize<Locations>(File.ReadAllText(settingsFile)) is { Projects: not null, Folders: not null } saved
                ? saved : new([], []);
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return new([], []); }
    }

    internal void RememberProject(string directory) => Remember(directory, true);
    internal void RememberFolder(string directory) => Remember(directory, false);
    private void Remember(string directory, bool project)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!System.IO.Directory.Exists(directory)) return;
        if (project && !directory.EndsWith(".frametrailproject", StringComparison.OrdinalIgnoreCase)) return;
        lock (gate) {
            var saved = ReadLocations();
            string[] Prepend(string[] values, int limit) => new[] { directory }.Concat(values)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(limit).ToArray();
            saved = project ? saved with { Projects = Prepend(saved.Projects, 100) } : saved with { Folders = Prepend(saved.Folders, 20) };
            var temporary = settingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
                File.WriteAllText(temporary, JsonSerializer.Serialize(saved)); File.Move(temporary, settingsFile, true);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    internal IReadOnlyList<ProjectCatalogEntry> List(IEnumerable<string> folders, CancellationToken cancellation = default)
    {
        Locations saved; lock (gate) saved = ReadLocations();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path) {
            cancellation.ThrowIfCancellationRequested();
            try {
                path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (path.EndsWith(".frametrailproject", StringComparison.OrdinalIgnoreCase) && SafeDirectory(path)) paths.Add(path);
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        foreach (string project in saved.Projects) Add(project);
        foreach (string folder in folders.Concat(saved.Folders).Distinct(StringComparer.OrdinalIgnoreCase)) {
            cancellation.ThrowIfCancellationRequested();
            try {
                if (!SafeDirectory(folder)) continue;
                if (folder.EndsWith(".frametrailproject", StringComparison.OrdinalIgnoreCase)) { Add(folder); continue; }
                foreach (string path in System.IO.Directory.EnumerateDirectories(folder, "*.frametrailproject", SearchOption.TopDirectoryOnly)) Add(path);
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        var entries = new List<ProjectCatalogEntry>();
        foreach (string path in paths) {
            cancellation.ThrowIfCancellationRequested();
            string name = Path.GetFileNameWithoutExtension(path); var modified = DateTime.MinValue;
            try {
                modified = System.IO.Directory.GetLastWriteTimeUtc(path);
                string manifest = Path.Combine(path, "project.json");
                using var file = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (file.Length > ProjectJson.MaximumJsonBytes) throw new IOException("项目信息过大，无法读取。");
                using var reader = new StreamReader(file);
                var snapshot = ProjectJson.Deserialize(reader.ReadToEnd());
                modified = File.GetLastWriteTimeUtc(manifest);
                entries.Add(new(path, name, modified, snapshot.SourceDuration.Ticks / (double)snapshot.SourceDuration.Timescale,
                    snapshot.Canvas.Width, snapshot.Canvas.Height, null));
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ProjectFormatException or ArgumentException) {
                if (!System.IO.Directory.Exists(path)) continue;
                entries.Add(new(path, name, modified, null, null, null,
                    File.Exists(Path.Combine(path, "project.json")) ? "项目信息无法读取，可删除此项目" : "录制未完成，可从录制条恢复或删除"));
            }
        }
        return entries.OrderByDescending(entry => entry.ModifiedUtc).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool SafeDirectory(string path)
    {
        if (!System.IO.Directory.Exists(path)) return false;
        for (string? parent = Path.GetFullPath(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
}
