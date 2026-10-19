using System.Text.Json;

namespace FrameTrail.App;

internal sealed class ProjectLocations
{
    private readonly string settingsFile;
    private readonly string defaultDirectory;
    internal string RecordingDirectory { get; private set; }
    internal ProjectLocations(string? settingsFile = null, string? defaultDirectory = null)
    {
        this.settingsFile = settingsFile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "project-location.json");
        this.defaultDirectory = defaultDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FrameTrail");
        RecordingDirectory = this.defaultDirectory;
        try {
            var saved = JsonSerializer.Deserialize<string>(File.ReadAllText(this.settingsFile));
            if (!string.IsNullOrWhiteSpace(saved) && Path.IsPathFullyQualified(saved)) RecordingDirectory = Path.GetFullPath(saved);
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { }
    }
    internal void SetRecordingDirectory(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(L.T("SA06546611D66"));
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
        var temporary = settingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(directory)); File.Move(temporary, settingsFile, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        RecordingDirectory = directory;
    }
    internal string OpenDirectory(string? currentProject = null)
    {
        if (currentProject is not null && Directory.Exists(currentProject) && Path.GetDirectoryName(currentProject) is { } parent && Directory.Exists(parent)) return parent;
        if (Directory.Exists(RecordingDirectory)) return RecordingDirectory;
        return Directory.CreateDirectory(defaultDirectory).FullName;
    }
    internal static string ProjectDirectory(string selection)
    {
        string path = Path.GetFullPath(selection).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (File.Exists(path) && string.Equals(Path.GetFileName(path), "project.json", StringComparison.OrdinalIgnoreCase)) path = Path.GetDirectoryName(path)!;
        path = DeletionDirectory(path);
        if (!File.Exists(Path.Combine(path, "project.json"))) throw new IOException(L.T("SC7164CFEFA32"));
        return path;
    }
    internal static string DeletionDirectory(string selection)
    {
        string path = Path.GetFullPath(selection).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!path.EndsWith(".frametrailproject", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(path)) throw new IOException(L.T("S2533F6C51D39"));
        return path;
    }
}
