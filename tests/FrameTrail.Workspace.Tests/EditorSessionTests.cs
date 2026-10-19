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
    [Fact] public async Task WallpaperIsCopiedOnceAndSurvivesUndoRedoAndReopeningWithoutItsSource()
    {
        string source = Path.Combine(root, "wallpaper.jpg"), saved;
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "wallpaper.jpg"), source);
        await using (var workspace = await CreateAsync()) {
            var editor = new EditorSession(workspace);
            saved = await editor.ImportWallpaperAsync(source);
            Assert.Equal(saved, await editor.ImportWallpaperAsync(source));
            Assert.Single(Directory.GetFiles(workspace.Resolve("assets")));
            await editor.ExecuteAsync(new { op = "settings", editor = new EditorSettings { Backdrop = new() { Kind = "image", Path = saved } } });
            await editor.UndoAsync(); Assert.NotEqual(saved, editor.Snapshot.Editor?.Backdrop?.Path);
            await editor.RedoAsync(); Assert.Equal(saved, editor.Snapshot.Editor?.Backdrop?.Path);
            await editor.FlushAsync(); File.Delete(source);
        }
        await using var reopened = await ProjectWorkspace.OpenAsync(Path.Combine(root, "edit.frametrailproject"), EngineSession.ValidateProject, (_, _) => { });
        Assert.Equal(saved, reopened.Snapshot!.Editor!.Backdrop!.Path);
        MediaSession.ProbeVisual(reopened.Resolve(saved), false);
    }

    [Fact] public async Task InvalidWallpaperLeavesNoProjectAssetOrHistory()
    {
        await using var workspace = await CreateAsync(); var editor = new EditorSession(workspace);
        string source = Path.Combine(root, "invalid.jpg"); await File.WriteAllTextAsync(source, "invalid image");
        await Assert.ThrowsAnyAsync<Exception>(() => editor.ImportWallpaperAsync(source));
        Assert.Empty(Directory.GetFiles(workspace.Resolve("assets"))); Assert.False(editor.CanUndo);
    }

    [Fact] public async Task ReusingDamagedWallpaperDoesNotDeleteTheExistingProjectAsset()
    {
        await using var workspace = await CreateAsync(); var editor = new EditorSession(workspace);
        string source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "wallpaper.jpg");
        string saved = await editor.ImportWallpaperAsync(source);
        await File.WriteAllTextAsync(workspace.Resolve(saved), "damaged");
        await Assert.ThrowsAsync<IOException>(() => editor.ImportWallpaperAsync(source));
        Assert.Equal("damaged", await File.ReadAllTextAsync(workspace.Resolve(saved)));
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
    [Fact] public async Task UnchangedPreviewDoesNotAddHistoryOrRevision()
    {
        await using var workspace = await CreateAsync(); var editor = new EditorSession(workspace);
        editor.BeginTransaction(); editor.PreviewCommand(new { op = "rate", segmentId = "clip", numerator = 1, denominator = 1 }); await editor.CommitTransactionAsync();
        Assert.Equal(1, editor.Snapshot.Revision); Assert.False(editor.CanUndo); Assert.False(editor.IsDirty);
    }
    [Fact] public async Task NewEditAfterUndoClearsRedoAndReopenMatchesContent()
    {
        var workspace = await CreateAsync(); var editor = new EditorSession(workspace);
        await editor.ExecuteAsync(new { op = "split", segmentId = "clip", sourceTicks = 10_000_000L, newId = "right" }); await editor.UndoAsync();
        await editor.ExecuteAsync(new { op = "rate", segmentId = "clip", numerator = 1, denominator = 2 }); Assert.False(editor.CanRedo);
        var json = ProjectJson.Serialize(editor.Snapshot); var path = workspace.DirectoryPath; await workspace.DisposeAsync();
        await using var reopened = await ProjectWorkspace.OpenAsync(path, EngineSession.ValidateProject, (_, _) => { }); Assert.Equal(json, ProjectJson.Serialize(reopened.Snapshot!));
    }
    [Theory]
    [InlineData("zoom")][InlineData("mask")][InlineData("focus")][InlineData("tilt")][InlineData("sticker")][InlineData("intro")]
    public async Task AttributePasteKeepsTargetPlacementAndContentAndIsOneUndo(string kind)
    {
        await using var workspace=await CreateAsync();var editor=new EditorSession(workspace);
        var target=new SourceEffect{Id="target",Kind=kind,SourceStart=MediaTime.FromTicks(10_000_000),SourceDuration=MediaTime.FromTicks(10_000_000),
            Parameters=System.Text.Json.JsonSerializer.SerializeToElement(new{scale=1.2,opacity=0.7,text="Authored target",target="screen"})};
        var source=target with{Id="copied",SourceStart=MediaTime.FromTicks(0),SourceDuration=MediaTime.FromTicks(20_000_000),Parameters=System.Text.Json.JsonSerializer.SerializeToElement(new{scale=1.8,opacity=0.4,text="Copied text",path="assets/never-imported.png",target="camera"})};
        await editor.UpsertEffectAsync(target);var before=editor.Snapshot.Effects.Single();long revision=editor.Snapshot.Revision;
        await editor.PasteEffectAttributesAsync(source,target.Id);var result=editor.Snapshot.Effects.Single();
        Assert.Equal(before.Id,result.Id);Assert.Equal(before.SourceStart,result.SourceStart);Assert.Equal(before.SourceDuration,result.SourceDuration);
        Assert.Equal("Authored target",result.Parameters.GetProperty("text").GetString());Assert.Equal("screen",result.Parameters.GetProperty("target").GetString());Assert.False(result.Parameters.TryGetProperty("path",out _));
        Assert.Equal(1.8,result.Parameters.GetProperty("scale").GetDouble());Assert.Equal(revision+1,editor.Snapshot.Revision);
        await editor.UndoAsync();Assert.Equal(before.Parameters.GetRawText(),editor.Snapshot.Effects.Single().Parameters.GetRawText());
        await editor.RedoAsync();Assert.Equal(result.Parameters.GetRawText(),editor.Snapshot.Effects.Single().Parameters.GetRawText());
        Assert.Equal(result.Parameters.GetRawText(),workspace.Snapshot!.Effects.Single().Parameters.GetRawText());
    }
    [Fact] public async Task IncompatibleAttributePasteDoesNotChangeRevisionOrHistory()
    {
        await using var workspace=await CreateAsync();var editor=new EditorSession(workspace);
        var effect=new SourceEffect{Id="target",Kind="zoom",SourceStart=MediaTime.FromTicks(0),SourceDuration=MediaTime.FromTicks(10_000_000),Parameters=System.Text.Json.JsonSerializer.SerializeToElement(new{scale=1.2})};
        await editor.UpsertEffectAsync(effect);var before=ProjectJson.Serialize(editor.Snapshot);
        await Assert.ThrowsAsync<ArgumentException>(()=>editor.PasteEffectAttributesAsync(effect with{Kind="mask"},effect.Id));
        Assert.Equal(before,ProjectJson.Serialize(editor.Snapshot));await editor.UndoAsync();Assert.Empty(editor.Snapshot.Effects);
    }
}
