using FrameTrail.Domain;
using FrameTrail.Interop;
using FrameTrail.Workspace;
using Xunit;

namespace FrameTrail.Workspace.Tests;

public sealed class EditorSessionTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FrameTrail-Editor-" + Guid.NewGuid().ToString("N"));
    public Task InitializeAsync() { Directory.CreateDirectory(root); return Task.CompletedTask; }
    public Task DisposeAsync() { Directory.Delete(root, true); return Task.CompletedTask; }
    private async Task<ProjectWorkspace> CreateAsync()
    {
        var workspace = ProjectWorkspace.Create(Path.Combine(root, "edit.frametrailproject"), "editor-project", EngineSession.ValidateProject, (_, _) => { });
        await File.WriteAllBytesAsync(workspace.Resolve("media/screen.mp4"), PersistenceTests.FinalizedMp4Header);
        var p = new ProjectSnapshot { ProjectId = "editor-project", Revision = 1, SourceDuration = MediaTime.FromTicks(30_000_000),
            Media = new[] { new MediaAsset { Id = "screen", Kind = MediaKind.Screen, Path = "media/screen.mp4", StartOffset = MediaTime.FromTicks(0), Duration = MediaTime.FromTicks(30_000_000), Width = 640, Height = 360 } },
            Segments = new[] { new RecordingSegment { Id = "clip", SourceStart = MediaTime.FromTicks(0), SourceDuration = MediaTime.FromTicks(30_000_000) } } };
        await workspace.SaveAsync(p, workspace.SessionEpoch); return workspace;
    }
    [Fact] public async Task DragPreviewCancelAndCommitAreSingleUndoTransaction()
    {
        await using var workspace = await CreateAsync(); var editor = new EditorSession(workspace); var original = ProjectJson.Serialize(editor.Snapshot);
        editor.BeginTransaction(); editor.PreviewCommand(new { op = "trim", segmentId = "clip", sourceStart = 1_000_000L, sourceEnd = 30_000_000L }); editor.CancelTransaction();
        Assert.Equal(original, ProjectJson.Serialize(editor.Snapshot)); Assert.False(editor.CanUndo);
        editor.BeginTransaction(); editor.PreviewCommand(new { op = "trim", segmentId = "clip", sourceStart = 1_000_000L, sourceEnd = 30_000_000L });
        editor.PreviewCommand(new { op = "trim", segmentId = "clip", sourceStart = 2_000_000L, sourceEnd = 30_000_000L }); await editor.CommitTransactionAsync();
        Assert.True(editor.CanUndo); Assert.False(editor.IsDirty); await editor.UndoAsync();
        Assert.Equal(0, editor.Snapshot.Segments[0].SourceStart.Ticks); Assert.False(editor.CanUndo); Assert.True(editor.CanRedo); Assert.Equal(3, editor.Snapshot.Revision);
        await editor.RedoAsync(); Assert.Equal(2_000_000L, editor.Snapshot.Segments[0].SourceStart.Ticks); Assert.Equal(4, workspace.Snapshot!.Revision);
    }
    [Fact] public async Task FailedAutosaveRetainsMemoryAndRetryPersistsSameRevision()
    {
        await using var workspace = await CreateAsync(); var editor = new EditorSession(workspace);
        using (var locked = new FileStream(workspace.Resolve("project.json"), FileMode.Open, FileAccess.Read, FileShare.Read)) {
            await editor.ExecuteAsync(new { op = "rate", segmentId = "clip", numerator = 2, denominator = 1 });
            Assert.True(editor.IsDirty); Assert.NotNull(editor.SaveError); Assert.Equal(15_000_000L, editor.Timeline.Duration); Assert.Equal(1, workspace.Snapshot!.Revision);
        }
        await editor.FlushAsync(); Assert.False(editor.IsDirty); Assert.Null(editor.SaveError); Assert.Equal(2, workspace.Snapshot!.Revision); Assert.Equal(2, workspace.Snapshot.Segments[0].Rate.Numerator);
    }
    [Fact] public async Task NewEditAfterUndoClearsRedoAndReopenMatchesContent()
    {
        var workspace = await CreateAsync(); var editor = new EditorSession(workspace);
        await editor.ExecuteAsync(new { op = "split", segmentId = "clip", sourceTicks = 10_000_000L, newId = "right" }); await editor.UndoAsync();
        await editor.ExecuteAsync(new { op = "rate", segmentId = "clip", numerator = 1, denominator = 2 }); Assert.False(editor.CanRedo);
        var json = ProjectJson.Serialize(editor.Snapshot); var path = workspace.DirectoryPath; await workspace.DisposeAsync();
        await using var reopened = await ProjectWorkspace.OpenAsync(path, EngineSession.ValidateProject, (_, _) => { }); Assert.Equal(json, ProjectJson.Serialize(reopened.Snapshot!));
    }
}
