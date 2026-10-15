using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;

namespace FrameTrail.Workspace;

/// <summary>One editor per writable lease. Undo restores content with a newer revision.</summary>
public sealed class EditorSession
{
    private readonly ProjectWorkspace workspace;
    private readonly List<string> undo = new(), redo = new();
    private readonly SemaphoreSlim gate = new(1);
    private ProjectSnapshot? transaction;
    public ProjectSnapshot Snapshot { get; private set; }
    public ProjectSnapshot? Preview { get; private set; }
    public ResolvedTimeline Timeline { get; private set; }
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public bool IsDirty { get; private set; }
    public bool IsExporting { get; private set; }
    public string? SaveError { get; private set; }
    public event EventHandler? Changed;
    public string DirectoryPath => workspace.DirectoryPath;

    public EditorSession(ProjectWorkspace workspace)
    {
        this.workspace = workspace;
        Snapshot = Freeze(workspace.Snapshot ?? throw new InvalidOperationException("Project has no saved snapshot."));
        Timeline = EngineSession.ResolveTimeline(Snapshot);
    }
    private static ProjectSnapshot Freeze(ProjectSnapshot p) => ProjectJson.Deserialize(ProjectJson.Serialize(p));
    private void EnsureEditable() { if (IsExporting) throw new InvalidOperationException("导出期间请等待或取消导出后再编辑。"); }
    private void Push(List<string> stack, ProjectSnapshot value)
    {
        stack.Add(ProjectJson.Serialize(value));
        long bytes = 0; for (int i = stack.Count - 1; i >= 0; --i) { bytes += stack[i].Length * 2L; if (stack.Count - i > 100 || bytes > 64 * 1024 * 1024) { stack.RemoveRange(0, i + 1); break; } }
    }
    public async Task ExecuteAsync(object command)
    {
        await gate.WaitAsync();
        try { EnsureEditable(); if (transaction is not null) throw new InvalidOperationException("Finish the current drag transaction first."); var candidate = EngineSession.Edit(Snapshot, command); Push(undo, Snapshot); redo.Clear(); await AcceptAsync(candidate); }
        finally { gate.Release(); }
    }
    private async Task AcceptAsync(ProjectSnapshot candidate)
    {
        EngineSession.ValidateProject(candidate); var timeline = EngineSession.ResolveTimeline(candidate);
        Snapshot = Freeze(candidate); Timeline = timeline; IsDirty = true; Changed?.Invoke(this, EventArgs.Empty); await SaveCoreAsync();
    }
    public void BeginTransaction() { EnsureEditable(); if (transaction is not null) throw new InvalidOperationException("A transaction is already active."); transaction = Freeze(Snapshot); Preview = null; }
    public ProjectSnapshot PreviewCommand(object command)
    {
        EnsureEditable(); if (transaction is null) throw new InvalidOperationException("No active transaction.");
        return Preview = EngineSession.Edit(transaction, command);
    }
    public void CancelTransaction() { transaction = Preview = null; Changed?.Invoke(this, EventArgs.Empty); }
    public async Task CommitTransactionAsync()
    {
        await gate.WaitAsync();
        try { EnsureEditable(); if (transaction is null) return; var candidate = Preview; transaction = Preview = null; if (candidate is null) return; Push(undo, Snapshot); redo.Clear(); await AcceptAsync(candidate); }
        finally { gate.Release(); }
    }
    public async Task UndoAsync() => await RestoreAsync(undo, redo);
    public async Task RedoAsync() => await RestoreAsync(redo, undo);
    private async Task RestoreAsync(List<string> source, List<string> destination)
    {
        await gate.WaitAsync();
        try { EnsureEditable(); if (transaction is not null) throw new InvalidOperationException("Finish the current drag first."); if (source.Count == 0) return;
            var candidate = ProjectJson.Deserialize(source[^1]) with { Revision = checked(Snapshot.Revision + 1) }; EngineSession.ValidateProject(candidate); Push(destination, Snapshot); source.RemoveAt(source.Count - 1); await AcceptAsync(candidate); }
        finally { gate.Release(); }
    }
    private async Task SaveCoreAsync()
    {
        try { if (!await workspace.SaveAsync(Snapshot, workspace.SessionEpoch)) throw new IOException("该修订未被保存；请重试。"); IsDirty = false; SaveError = null; }
        catch (Exception error) { IsDirty = true; SaveError = error.Message; }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public async Task FlushAsync()
    {
        await gate.WaitAsync();
        try { if (IsDirty) await SaveCoreAsync(); if (IsDirty) throw new IOException(SaveError); await workspace.BarrierAsync(); }
        finally { gate.Release(); }
    }
    public async Task ExportAsync(string path, bool audioOnly, IProgress<MediaStatus>? progress, CancellationToken cancellationToken)
    {
        await gate.WaitAsync();
        try {
            EnsureEditable(); if (transaction is not null) throw new InvalidOperationException("Finish the current drag first.");
            if (IsDirty) await SaveCoreAsync(); if (IsDirty) throw new IOException(SaveError); await workspace.BarrierAsync(); IsExporting = true;
        } finally { gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
        try {
            var frozen = Freeze(Snapshot);
            using var media = await Task.Run(() => new MediaSession(frozen, DirectoryPath), cancellationToken);
            using var registration = cancellationToken.Register(media.Cancel);
            var task = Task.Run(() => media.Export(path, audioOnly), CancellationToken.None);
            while (!task.IsCompleted) { var status = media.Status; progress?.Report(status); if (status.Total > 0 && Environment.TickCount64 - (long)status.LastProgressMilliseconds > 120_000) media.Cancel(); await Task.WhenAny(task, Task.Delay(150, CancellationToken.None)); }
            await task; progress?.Report(media.Status);
        } finally { IsExporting = false; Changed?.Invoke(this, EventArgs.Empty); }
    }
    public async Task CopySourcesAsync(string destination, CancellationToken cancellationToken)
    {
        await gate.WaitAsync();
        try { EnsureEditable(); if (transaction is not null) throw new InvalidOperationException("Finish the current drag first."); if (IsDirty) await SaveCoreAsync(); if (IsDirty) throw new IOException(SaveError); await workspace.BarrierAsync(); IsExporting = true; }
        finally { gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
        string? staging = null;
        try {
            var target = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar); staging = target + ".frametrail-copy-" + Guid.NewGuid().ToString("N");
            if (Directory.Exists(target) || File.Exists(target)) throw new IOException("请选择尚不存在的素材文件夹。");
            for (var ancestor = Path.GetDirectoryName(target); !string.IsNullOrEmpty(ancestor); ancestor = Path.GetDirectoryName(ancestor))
                if (Directory.Exists(ancestor) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0) throw new IOException("素材副本路径不能经过目录联接或符号链接。");
            var root = Path.GetFullPath(DirectoryPath).TrimEnd(Path.DirectorySeparatorChar);
            if (target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || string.Equals(target, root, StringComparison.OrdinalIgnoreCase)) throw new IOException("请选择工程目录之外的位置。");
            Directory.CreateDirectory(staging);
            var paths = Snapshot.Media.Select(m => m.Path).Append("project.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in new[] { "events", "assets" }) {
                var directory = workspace.Resolve(folder + "/placeholder"); directory = Path.GetDirectoryName(directory)!;
                if (Directory.Exists(directory)) foreach (var file in Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })) paths.Add(Path.GetRelativePath(DirectoryPath, file).Replace('\\', '/'));
            }
            foreach (var relative in paths) {
                cancellationToken.ThrowIfCancellationRequested(); var source = workspace.Resolve(relative); var output = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
                await using var writer = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous); await input.CopyToAsync(writer, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested(); Directory.Move(staging, target);
        } finally { try { if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, true); } finally { IsExporting = false; Changed?.Invoke(this, EventArgs.Empty); } }
    }
}
