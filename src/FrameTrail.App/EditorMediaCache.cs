using System.Security.Cryptography;
using System.Text.Json;
using FrameTrail.Domain;

namespace FrameTrail.App;

// Only rebuildable editor-* files belong to this cache. Source assets never do.
internal sealed class EditorMediaCache
{
    internal const long DefaultBudget = 1024L * 1024 * 1024;
    private readonly string directory;
    private readonly long budget;
    private readonly object gate = new();
    private readonly HashSet<string> pinned = new(StringComparer.OrdinalIgnoreCase);
    internal EditorMediaCache(string project, long budget = DefaultBudget)
    {
        directory = Path.Combine(Path.GetFullPath(project), "cache");
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("缓存目录不能是目录联接或符号链接。");
        this.budget = budget;
    }
    internal string FilePath(string key, string suffix) => Path.Combine(directory, "editor-v2-" + key + "-" + suffix);
    internal void Pin(string path) { lock (gate) pinned.Add(path); }
    internal void Unpin(string? path) { if (path is not null) lock (gate) pinned.Remove(path); }
    internal bool Use(string path)
    {
        lock (gate) {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || (info.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) {
                using var input = File.OpenRead(path); Span<byte> header = stackalloc byte[44];
                if (input.Read(header) < (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? 24 : 44)) return false;
                if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) {
                    if (!header[..8].SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})) return false;
                } else if (!header[..4].SequenceEqual("RIFF"u8) || !header.Slice(8,8).SequenceEqual("WAVEfmt "u8) || !header.Slice(36,4).SequenceEqual("data"u8)
                    || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(40,4)) != info.Length - 44) return false;
            }
            info.LastWriteTimeUtc = DateTime.UtcNow; return true;
        }
    }
    internal void Trim()
    {
        lock (gate) {
            var files = new DirectoryInfo(directory).EnumerateFiles("editor-*", SearchOption.TopDirectoryOnly)
                .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0).OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            long bytes = files.Sum(f => f.Length); int count = files.Length;
            foreach (var file in files.Reverse()) {
                if (bytes <= budget && count <= 400) break;
                if (pinned.Contains(file.FullName)) continue;
                try { long size = file.Length; file.Delete(); bytes -= size; count--; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
    internal static string AudioKey(ProjectSnapshot p, string project)
    {
        var settings = p.Editor ?? new();
        return Hash(new { version = 2, p.Segments, Media = Identity(p, project, true), settings.SystemGain, settings.MicrophoneGain, settings.SystemMuted, settings.MicrophoneMuted, settings.ClipAudio });
    }
    internal static string PictureKey(ProjectSnapshot p, string project) => Hash(new {
        version = 2, p.Segments, p.Canvas, p.Effects, Media = Identity(p, project, false),
        Assets = FrameTrail.Workspace.VisualResources.Paths(p).Select(path => Stamp(project, path)),
        Editor = (p.Editor ?? new()) with { SystemGain = 1, MicrophoneGain = 1, SystemMuted = false, MicrophoneMuted = false, ClipAudio = new Dictionary<string, AudioLevel>() }
    });
    private static object[] Identity(ProjectSnapshot p, string project, bool audio) => p.Media
        .Where(m => audio == (m.Kind is MediaKind.SystemAudio or MediaKind.Microphone or MediaKind.AndroidAudio))
        .Select(m => (object)new { Media = m, File = Stamp(project, m.Path) }).ToArray();
    private static object Stamp(string project, string relative)
    { var file = new FileInfo(Path.Combine(project, relative)); return new { relative, size = file.Exists ? file.Length : -1, modified = file.Exists ? file.LastWriteTimeUtc.Ticks : 0 }; }
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value))).ToLowerInvariant();
}
