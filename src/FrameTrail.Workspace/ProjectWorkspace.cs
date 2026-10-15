using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using FrameTrail.Domain;
using FrameTrail.Interop;

namespace FrameTrail.Workspace;

public sealed record RecoveryManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string ProjectId { get; init; } = "";
    public Guid RunId { get; init; }
    public string Status { get; init; } = "recording";
    public string? CheckpointJson { get; init; }
    public IReadOnlyList<string> PendingFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
public sealed record RecoveryCandidate(string Directory, string Status, long Revision, IReadOnlyList<string> Warnings);

/// <summary>One writable lease. All mutations pass through a single reader queue; close/move/delete are barriers.</summary>
public sealed class ProjectWorkspace : IAsyncDisposable
{
    private static readonly JsonSerializerOptions ManifestJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true };
    private readonly Channel<Func<Task>> queue = Channel.CreateUnbounded<Func<Task>>(new() { SingleReader = true });
    private readonly object gate = new();
    private readonly FileStream lease;
    private readonly Task worker;
    private readonly Action<ProjectSnapshot> validate;
    private readonly Action<string, bool> probe;
    private readonly Guid epoch = Guid.NewGuid();
    private bool closing;
    private long acceptedRevision = -1;
    private string? projectId;
    private ProjectSnapshot? snapshot;
    public string DirectoryPath { get; private set; }
    public Guid SessionEpoch => epoch;
    public ProjectSnapshot? Snapshot => snapshot;

    private ProjectWorkspace(string path, FileStream lease, Action<ProjectSnapshot>? validator, Action<string, bool>? mediaProbe)
    {
        DirectoryPath = path; this.lease = lease;
        validate = validator ?? EngineSession.ValidateProject;
        probe = mediaProbe ?? ((file, video) => RecordingUtilities.ProbeMedia(file, video));
        worker = Task.Run(async () => { await foreach (var action in queue.Reader.ReadAllAsync()) await action(); });
    }

    public static ProjectWorkspace Create(string path, string id, Action<ProjectSnapshot>? validator = null, Action<string, bool>? mediaProbe = null)
    {
        path = NormalizeRoot(path);
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Project destination already exists.");
        CheckAncestors(Path.GetDirectoryName(path)!);
        Directory.CreateDirectory(path);
        foreach (var name in new[] { "media", "events", "assets", "recovery", "cache" }) Directory.CreateDirectory(Path.Combine(path, name));
        var result = Acquire(path, validator, mediaProbe); result.projectId = id; return result;
    }

    private static ProjectWorkspace Acquire(string path, Action<ProjectSnapshot>? validator, Action<string, bool>? mediaProbe)
    {
        CheckAncestors(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        var leasePath = Path.Combine(path, ".write.lock");
        if (File.Exists(leasePath) && (File.GetAttributes(leasePath) & FileAttributes.ReparsePoint) != 0) throw new IOException("Project lock cannot be a link.");
        var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        return new(path, lease, validator, mediaProbe);
    }

    public static async Task<ProjectWorkspace> OpenAsync(string path, Action<ProjectSnapshot>? validator = null, Action<string, bool>? mediaProbe = null)
    {
        path = NormalizeRoot(path); var result = Acquire(path, validator, mediaProbe);
        try
        {
            var current = await result.ReadSnapshotAsync("project.json");
            result.projectId = current.ProjectId; result.acceptedRevision = current.Revision; result.snapshot = current;
            return result;
        }
        catch { await result.DisposeAsync(); throw; }
    }

    public string Resolve(string relative)
    {
        if (!ProjectJson.IsProjectRelativePath(relative)) throw new ProjectFormatException("Unsafe project-relative path.");
        var absolute = Path.GetFullPath(Path.Combine(DirectoryPath, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!absolute.StartsWith(DirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ProjectFormatException("Path escapes project.");
        CheckAncestors(absolute); return absolute;
    }

    public Task<bool> SaveAsync(ProjectSnapshot value, Guid sessionEpoch)
    {
        // Freeze arrays/objects before queuing so a later UI change cannot mutate this revision.
        var json = ProjectJson.Serialize(value); var frozen = ProjectJson.Deserialize(json);
        validate(frozen);
        lock (gate)
        {
            ThrowIfClosing();
            if (sessionEpoch != epoch || value.Revision <= acceptedRevision) return Task.FromResult(false);
            if (projectId is not null && projectId != value.ProjectId) throw new ProjectFormatException("Project identity cannot change in a writable session.");
            projectId = value.ProjectId; acceptedRevision = value.Revision;
            return EnqueueLocked(async () =>
            {
                try
                {
                    await CheckMediaAsync(frozen);
                    await AtomicWriteAsync(Resolve("project.json"), json, Resolve("project.previous.json"));
                    snapshot = frozen; return true;
                }
                catch
                {
                    lock (gate) { if (acceptedRevision == frozen.Revision) acceptedRevision = snapshot?.Revision ?? -1; }
                    throw;
                }
            });
        }
    }

    public Task WriteManifestAsync(RecoveryManifest value)
    {
        if (value.SchemaVersion != 1 || value.ProjectId != projectId) throw new ProjectFormatException("Recovery manifest identity mismatch.");
        foreach (var path in value.PendingFiles) _ = Resolve(path);
        var json = JsonSerializer.Serialize(value, ManifestJson);
        lock (gate) { ThrowIfClosing(); return EnqueueLocked(async () => { await AtomicWriteAsync(Resolve("recovery/manifest.json"), json); return true; }); }
    }

    public Task BarrierAsync() { lock (gate) { ThrowIfClosing(); return EnqueueLocked(() => Task.FromResult(true)); } }

    public async Task MoveAsync(string destination)
    {
        destination = NormalizeRoot(destination); CheckAncestors(Path.GetDirectoryName(destination)!);
        // Close the lease only after all accepted writes have finished. No background task can recreate the old directory.
        await DisposeAsync(); Directory.Move(DirectoryPath, destination); DirectoryPath = destination;
    }
    public async Task DeleteAsync()
    {
        var target = DirectoryPath; await DisposeAsync(); CheckAncestors(target);
        if (!target.EndsWith(".frametrailproject", StringComparison.OrdinalIgnoreCase)) throw new IOException("Refusing an invalid project directory.");
        Directory.Delete(target, true);
    }

    public static async Task<IReadOnlyList<RecoveryCandidate>> ScanRecoveryAsync(string parent)
    {
        if (!Directory.Exists(parent)) return Array.Empty<RecoveryCandidate>();
        var results = new List<RecoveryCandidate>();
        foreach (var path in Directory.EnumerateDirectories(parent, "*.frametrailproject", SearchOption.TopDirectoryOnly))
        {
            try
            {
                CheckAncestors(path);
                var manifest = await ReadManifestAsync(path);
                if (manifest.Status == "complete") continue;
                var warnings = manifest.Warnings.ToList();
                foreach (var pending in manifest.PendingFiles) warnings.Add($"Unfinished segment excluded: {pending}");
                var revision = manifest.CheckpointJson is null ? -1 : ProjectJson.Deserialize(manifest.CheckpointJson).Revision;
                results.Add(new(path, manifest.Status, revision, warnings));
            }
            catch (Exception error) when (error is IOException or JsonException or ProjectFormatException or UnauthorizedAccessException)
            { results.Add(new(path, "unreadable", -1, new[] { error.Message })); }
        }
        return results;
    }

    public static async Task<(ProjectWorkspace Workspace, IReadOnlyList<string> Warnings)> RecoverAsync(string path, Action<ProjectSnapshot>? validator = null, Action<string, bool>? mediaProbe = null)
    {
        path = NormalizeRoot(path); var result = Acquire(path, validator, mediaProbe);
        try
        {
            var manifest = await ReadManifestAsync(path);
            if (manifest.SchemaVersion != 1) throw new ProjectFormatException("Unsupported recovery schema.");
            var warnings = manifest.Warnings.ToList();
            foreach (var pending in manifest.PendingFiles) { _ = result.Resolve(pending); warnings.Add($"Unfinished segment excluded: {pending}"); }
            var candidates = new List<ProjectSnapshot>();
            if (manifest.CheckpointJson is not null)
                try { candidates.Add(ProjectJson.Deserialize(manifest.CheckpointJson)); }
                catch (ProjectFormatException error) { warnings.Add("Manifest checkpoint: " + error.Message); }
            foreach (var filename in new[] { "project.json", "project.previous.json" })
                try { candidates.Add(await result.ReadSnapshotAsync(filename)); }
                catch (Exception error) when (error is IOException or ProjectFormatException or EngineException or COMException) { warnings.Add($"{filename}: {error.Message}"); }
            ProjectSnapshot? good = null;
            foreach (var candidate in candidates.OrderByDescending(p => p.Revision))
            {
                if (candidate.ProjectId != manifest.ProjectId) { warnings.Add("Skipped checkpoint with mismatched project identity."); continue; }
                try { result.validate(candidate); await result.CheckMediaAsync(candidate); good = candidate; break; }
                catch (Exception error) when (error is IOException or ProjectFormatException or EngineException or COMException) { warnings.Add($"Checkpoint {candidate.Revision} rejected: {error.Message}"); }
            }
            if (good is null) throw new ProjectFormatException("No readable completed segment exists. Unfinished media was preserved for inspection.");
            result.projectId = good.ProjectId;
            await result.SaveAsync(good, result.SessionEpoch);
            await result.WriteManifestAsync(manifest with { Status = "recovered", CheckpointJson = ProjectJson.Serialize(good), PendingFiles = Array.Empty<string>(), Warnings = warnings });
            return (result, warnings);
        }
        catch { await result.DisposeAsync(); throw; }
    }

    private async Task<ProjectSnapshot> ReadSnapshotAsync(string relative)
    {
        var path = Resolve(relative);
        if (new FileInfo(path).Length > ProjectJson.MaximumJsonBytes) throw new ProjectFormatException("Project JSON exceeds 16 MiB.");
        var value = ProjectJson.Deserialize(await File.ReadAllTextAsync(path));
        validate(value); await CheckMediaAsync(value); return value;
    }
    private async Task CheckMediaAsync(ProjectSnapshot value)
    {
        foreach (var asset in VisualResources.Paths(value).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using var file = new FileStream(Resolve(asset), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length == 0) throw new IOException($"Empty visual asset: {asset}");
        }
        foreach (var media in value.Media)
        {
            await using var stream = new FileStream(Resolve(media.Path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length == 0) throw new IOException($"Empty media: {media.Path}");
            // Finalized MP4 needs a moov box; truncated last fragments must not be declared recoverable.
            if (media.Kind is MediaKind.Screen or MediaKind.Camera)
            {
                if (!await HasMp4MoovAsync(stream)) throw new IOException($"Unfinalized or unreadable MP4: {media.Path}");
                await Task.Run(() => probe(Resolve(media.Path), true));
            }
            if (media.Kind is MediaKind.Microphone or MediaKind.SystemAudio)
            {
                var header = new byte[12]; if (await stream.ReadAsync(header) != 12 || Encoding.ASCII.GetString(header, 0, 4) != "RIFF" || Encoding.ASCII.GetString(header, 8, 4) != "WAVE")
                    throw new IOException($"Unreadable WAV: {media.Path}");
                var declared = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
                if (declared == 0 || declared + 8L > stream.Length) throw new IOException($"Unfinished WAV: {media.Path}");
                await Task.Run(() => probe(Resolve(media.Path), false));
            }
            if (media.Kind == MediaKind.PointerEvents)
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? line;
                while ((line = await reader.ReadLineAsync()) is not null)
                {
                    if (line.Length > 32768) throw new IOException("Oversized pointer event.");
                    try { using var entry = JsonDocument.Parse(line); if (entry.RootElement.ValueKind != JsonValueKind.Object) throw new IOException("Invalid pointer event."); }
                    catch (JsonException error) { throw new IOException("Invalid pointer event JSON.", error); }
                }
            }
        }
    }
    private static async Task<bool> HasMp4MoovAsync(FileStream stream)
    {
        var box = new byte[16];
        while (stream.Position + 8 <= stream.Length)
        {
            var start = stream.Position;
            if (await stream.ReadAsync(box.AsMemory(0, 8)) != 8) return false;
            ulong size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(0, 4));
            int header = 8;
            if (size == 1) { if (await stream.ReadAsync(box.AsMemory(8, 8)) != 8) return false; size = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(box.AsSpan(8, 8)); header = 16; }
            if (size == 0) size = (ulong)(stream.Length - start);
            if (size < (ulong)header || size > (ulong)(stream.Length - start)) return false;
            if (Encoding.ASCII.GetString(box, 4, 4) == "moov") return size > (ulong)header;
            stream.Position = start + (long)size;
        }
        return false;
    }
    private static async Task<RecoveryManifest> ReadManifestAsync(string path)
    {
        var file = Path.Combine(path, "recovery", "manifest.json"); CheckAncestors(file);
        if (new FileInfo(file).Length > ProjectJson.MaximumJsonBytes * 2L) throw new ProjectFormatException("Recovery manifest exceeds limit.");
        var manifest = JsonSerializer.Deserialize<RecoveryManifest>(await File.ReadAllTextAsync(file), ManifestJson) ?? throw new ProjectFormatException("Missing recovery manifest.");
        if (manifest.SchemaVersion != 1 || manifest.PendingFiles is null || manifest.Warnings is null) throw new ProjectFormatException("Invalid recovery schema.");
        return manifest;
    }

    private static async Task AtomicWriteAsync(string path, string json, string? previous = null)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Project was moved or deleted.");
        CheckAncestors(path);
        var temporary = Path.Combine(directory, ".save-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await stream.WriteAsync(Encoding.UTF8.GetBytes(json)); await stream.FlushAsync(); stream.Flush(true); }
            CheckAncestors(path);
            if (File.Exists(path)) File.Replace(temporary, path, previous);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private Task<T> EnqueueLocked<T>(Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.Writer.TryWrite(async () => { try { completion.SetResult(await action()); } catch (Exception error) { completion.SetException(error); } }))
            completion.SetException(new ObjectDisposedException(nameof(ProjectWorkspace)));
        return completion.Task;
    }
    private static string NormalizeRoot(string path)
    {
        path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!path.EndsWith(".frametrailproject", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Project directory must end in .frametrailproject.");
        return path;
    }
    private static void CheckAncestors(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Project paths must not traverse junctions or symbolic links.");
            current = Path.GetDirectoryName(current)!;
        }
    }
    private void ThrowIfClosing() => ObjectDisposedException.ThrowIf(closing, this);
    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (!closing) { closing = true; queue.Writer.TryComplete(); } }
        await worker; lease.Dispose();
    }
}
