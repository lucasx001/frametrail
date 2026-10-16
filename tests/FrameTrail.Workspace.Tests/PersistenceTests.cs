using System.Diagnostics;
using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;
using FrameTrail.Workspace;
using Xunit;

namespace FrameTrail.Workspace.Tests;

public sealed class PersistenceTests : IAsyncLifetime
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "FrameTrail-Workspace-" + Guid.NewGuid().ToString("N"));
    private string Project => Path.Combine(temporary, "test.frametrailproject");
    private static void Validate(ProjectSnapshot value) => ProjectJson.ValidateProtocol(value);
    private static void Probe(string _, bool __) { } // Persistence unit tests isolate the native media decoder; hardware harness uses the real probe.
    public Task InitializeAsync() { Directory.CreateDirectory(temporary); return Task.CompletedTask; }
    public Task DisposeAsync() { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); return Task.CompletedTask; }
    internal static byte[] FinalizedMp4Header => new byte[] { 0, 0, 0, 12, 109, 111, 111, 118, 0, 0, 0, 0 };
    private static ProjectSnapshot Snapshot(long revision = 1) => new()
    {
        ProjectId = "project1", Revision = revision, SourceDuration = MediaTime.FromTicks(10_000_000),
        Media = new[] { new MediaAsset { Id = "media1", Kind = MediaKind.Screen, Path = "media/screen.mp4", Duration = MediaTime.FromTicks(10_000_000), StartOffset = MediaTime.FromTicks(0), Width = 640, Height = 360 } },
        Segments = new[] { new RecordingSegment { Id = "segment1", SourceStart = MediaTime.FromTicks(0), SourceDuration = MediaTime.FromTicks(10_000_000), Rate = new() } }
    };
    private async Task<ProjectWorkspace> CreateAsync()
    { var result = ProjectWorkspace.Create(Project, "project1", Validate, Probe); await File.WriteAllBytesAsync(result.Resolve("media/screen.mp4"), FinalizedMp4Header); return result; }

    [Fact] public async Task RevisionsAreOrderedAndPreviousSnapshotIsRetained()
    {
        await using var workspace = await CreateAsync();
        Assert.True(await workspace.SaveAsync(Snapshot(), workspace.SessionEpoch));
        var later = workspace.SaveAsync(Snapshot(3), workspace.SessionEpoch); var stale = workspace.SaveAsync(Snapshot(2), workspace.SessionEpoch);
        Assert.True(await later); Assert.False(await stale); await workspace.BarrierAsync();
        Assert.Equal(3, ProjectJson.Deserialize(await File.ReadAllTextAsync(workspace.Resolve("project.json"))).Revision);
        Assert.Equal(1, ProjectJson.Deserialize(await File.ReadAllTextAsync(workspace.Resolve("project.previous.json"))).Revision);
    }
    [Fact] public async Task FailedSaveCanRetrySameRevision()
    {
        await using var workspace = await CreateAsync(); var path = workspace.Resolve("media/screen.mp4"); File.Delete(path);
        await Assert.ThrowsAsync<FileNotFoundException>(() => workspace.SaveAsync(Snapshot(), workspace.SessionEpoch));
        await File.WriteAllBytesAsync(path, FinalizedMp4Header);
        Assert.True(await workspace.SaveAsync(Snapshot(), workspace.SessionEpoch));
    }
    [Fact] public async Task MissingVisualAssetRejectsSaveAndAllowsSameRevisionRetry()
    {
        await using var workspace = await CreateAsync();
        var snapshot = Snapshot() with { Editor = new() { Backdrop = new() { Kind = "image", Path = "assets/background.png" } } };
        await Assert.ThrowsAsync<FileNotFoundException>(() => workspace.SaveAsync(snapshot, workspace.SessionEpoch));
        await File.WriteAllBytesAsync(workspace.Resolve("assets/background.png"), new byte[] { 1 });
        Assert.True(await workspace.SaveAsync(snapshot, workspace.SessionEpoch));
    }
    [Fact] public async Task MissingStickerFallsBackToPreviousReadableSnapshot()
    {
        var workspace = await CreateAsync();
        await workspace.SaveAsync(Snapshot(), workspace.SessionEpoch);
        var asset = workspace.Resolve("assets/sticker.png"); await File.WriteAllBytesAsync(asset, new byte[] { 1 });
        var effect = new SourceEffect { Id = "sticker", Kind = "sticker", SourceStart = MediaTime.FromTicks(0), SourceDuration = MediaTime.FromTicks(10_000_000), Parameters = JsonSerializer.SerializeToElement(new { path = "assets/sticker.png" }) };
        await workspace.SaveAsync(Snapshot(2) with { Effects = new[] { effect } }, workspace.SessionEpoch);
        await workspace.WriteManifestAsync(new() { ProjectId = "project1", CheckpointJson = ProjectJson.Serialize(Snapshot(2) with { Effects = new[] { effect } }) });
        await workspace.DisposeAsync(); File.Delete(asset);
        await Assert.ThrowsAsync<FileNotFoundException>(() => ProjectWorkspace.OpenAsync(Project, Validate, Probe));
        var recovery = await ProjectWorkspace.RecoverAsync(Project, Validate, Probe); await using var reopened = recovery.Workspace;
        Assert.Equal(1, reopened.Snapshot!.Revision); Assert.Empty(reopened.Snapshot.Effects);
        Assert.Contains(recovery.Warnings, w => w.Contains("sticker.png"));
    }
    [Fact] public async Task EpochAndIdentityRejectStaleWork()
    {
        await using var workspace = await CreateAsync();
        Assert.False(await workspace.SaveAsync(Snapshot(), Guid.NewGuid()));
        await Assert.ThrowsAsync<ProjectFormatException>(() => workspace.SaveAsync(Snapshot() with { ProjectId = "other" }, workspace.SessionEpoch));
        Assert.True(await workspace.SaveAsync(Snapshot(), workspace.SessionEpoch));
    }
    [Fact] public async Task OnlyOneWritableSessionCanOpenProject()
    {
        await using var workspace = await CreateAsync(); await workspace.SaveAsync(Snapshot(), workspace.SessionEpoch);
        await Assert.ThrowsAsync<IOException>(() => ProjectWorkspace.OpenAsync(Project, Validate, Probe));
    }
    [Theory] [InlineData("../escape.mp4")] [InlineData("media/../../escape.mp4")] [InlineData("C:/escape.mp4")] [InlineData("media\\screen.mp4")] [InlineData("media/CON.mp4")]
    public async Task UnsafePathsAreRejected(string path)
    { await using var workspace = await CreateAsync(); Assert.Throws<ProjectFormatException>(() => workspace.Resolve(path)); }
    [Fact] public async Task DeleteWaitsForWritesAndRejectsDelayedSave()
    {
        var workspace = await CreateAsync(); var save = workspace.SaveAsync(Snapshot(), workspace.SessionEpoch);
        await workspace.DeleteAsync(); Assert.True(await save); Assert.False(Directory.Exists(Project));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => workspace.SaveAsync(Snapshot(2), workspace.SessionEpoch));
        Assert.False(Directory.Exists(Project));
    }
    [Fact] public async Task MoveIsASaveBarrier()
    {
        var workspace = await CreateAsync(); var save = workspace.SaveAsync(Snapshot(), workspace.SessionEpoch);
        var destination = Path.Combine(temporary, "moved.frametrailproject"); await workspace.MoveAsync(destination); Assert.True(await save);
        Assert.False(Directory.Exists(Project));
        await using var reopened = await ProjectWorkspace.OpenAsync(destination, Validate, Probe); Assert.Equal(1, reopened.Snapshot!.Revision);
    }
    [Fact] public async Task RecoveryKeepsCompletedCheckpointAndReportsPendingTail()
    {
        var workspace = await CreateAsync(); var value = Snapshot(); await workspace.SaveAsync(value, workspace.SessionEpoch);
        await workspace.WriteManifestAsync(new() { ProjectId = "project1", RunId = Guid.NewGuid(), CheckpointJson = ProjectJson.Serialize(value), PendingFiles = new[] { "media/broken-tail.mp4" } });
        await File.WriteAllTextAsync(workspace.Resolve("media/broken-tail.mp4"), "unfinished"); await workspace.DisposeAsync();
        var candidates = await ProjectWorkspace.ScanRecoveryAsync(temporary); Assert.Single(candidates);
        var recovery = await ProjectWorkspace.RecoverAsync(Project, Validate, Probe); await using var recovered = recovery.Workspace;
        Assert.Single(recovered.Snapshot!.Media); Assert.Contains(recovery.Warnings, w => w.Contains("broken-tail")); Assert.True(File.Exists(recovered.Resolve("media/broken-tail.mp4")));
    }
    [Fact] public async Task CorruptManifestSnapshotFallsBackToProject()
    {
        var workspace = await CreateAsync(); await workspace.SaveAsync(Snapshot(), workspace.SessionEpoch);
        await workspace.WriteManifestAsync(new() { ProjectId = "project1", CheckpointJson = "{bad json" }); await workspace.DisposeAsync();
        var recovery = await ProjectWorkspace.RecoverAsync(Project, Validate, Probe); await using var recovered = recovery.Workspace;
        Assert.Equal(1, recovered.Snapshot!.Revision); Assert.Contains(recovery.Warnings, w => w.StartsWith("Manifest checkpoint:"));
    }
    [Fact] public async Task UnfinalizedMp4CannotBeSavedOrRecovered()
    {
        await using var workspace = await CreateAsync(); await File.WriteAllTextAsync(workspace.Resolve("media/screen.mp4"), "unfinished");
        await Assert.ThrowsAsync<IOException>(() => workspace.SaveAsync(Snapshot(), workspace.SessionEpoch));
        Assert.False(File.Exists(workspace.Resolve("project.json")));
    }
    [Fact] public async Task FutureSchemaIsNotOverwritten()
    {
        var workspace = await CreateAsync(); await workspace.SaveAsync(Snapshot(), workspace.SessionEpoch); await workspace.DisposeAsync();
        var path = Path.Combine(Project, "project.json"); var json = (await File.ReadAllTextAsync(path)).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 999"); await File.WriteAllTextAsync(path, json);
        await Assert.ThrowsAsync<UnsupportedProjectVersionException>(() => ProjectWorkspace.OpenAsync(Project, Validate, Probe)); Assert.Equal(json, await File.ReadAllTextAsync(path));
    }
    [Fact] public async Task DecoderFailureRejectsCheckpointEvenWhenMoovExists()
    {
        await using var workspace = ProjectWorkspace.Create(Project, "project1", Validate, (_, _) => throw new IOException("decoder rejected media"));
        await File.WriteAllBytesAsync(workspace.Resolve("media/screen.mp4"), FinalizedMp4Header);
        await Assert.ThrowsAsync<IOException>(() => workspace.SaveAsync(Snapshot(), workspace.SessionEpoch));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task MissingOrCorruptManifestRecoversPreviousSnapshot(bool corrupt)
    {
        var workspace = await CreateAsync();
        await workspace.SaveAsync(Snapshot(), workspace.SessionEpoch);
        await workspace.SaveAsync(Snapshot(2), workspace.SessionEpoch);
        await workspace.DisposeAsync();
        if (corrupt) await File.WriteAllTextAsync(Path.Combine(Project, "recovery", "manifest.json"), "{broken");
        await File.WriteAllTextAsync(Path.Combine(Project, "project.json"), "{broken");
        var recovery = await ProjectWorkspace.RecoverAsync(Project, Validate, Probe);
        await using (var recovered = recovery.Workspace) { Assert.Equal(1, recovered.Snapshot!.Revision); }
        await using var reopened = await ProjectWorkspace.OpenAsync(Project, Validate, Probe);
        Assert.Equal(1, reopened.Snapshot!.Revision);
        Assert.Contains(recovery.Warnings, w => w.Contains("manifest unavailable"));
    }
    [Fact] public async Task RecoveryNeverOverwritesNewerProjectWithOldCheckpoint()
    {
        var workspace = await CreateAsync(); var snapshot = Snapshot();
        await workspace.SaveAsync(snapshot, workspace.SessionEpoch);
        await workspace.WriteManifestAsync(new() { ProjectId = "project1", CheckpointJson = ProjectJson.Serialize(snapshot) });
        await workspace.DisposeAsync();
        var file = Path.Combine(Project, "project.json");
        var future = (await File.ReadAllTextAsync(file)).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 999");
        await File.WriteAllTextAsync(file, future);
        await Assert.ThrowsAsync<UnsupportedProjectVersionException>(() => ProjectWorkspace.RecoverAsync(Project, Validate, Probe));
        Assert.Equal(future, await File.ReadAllTextAsync(file));
    }
    [Fact] public async Task CompletedProjectWithCorruptJsonAppearsInRecoveryPicker()
    {
        var workspace = await CreateAsync(); var snapshot = Snapshot();
        await workspace.SaveAsync(snapshot, workspace.SessionEpoch);
        await workspace.WriteManifestAsync(new() { ProjectId = "project1", Status = "complete", CheckpointJson = ProjectJson.Serialize(snapshot) });
        await workspace.DisposeAsync();
        Assert.Empty(await ProjectWorkspace.ScanRecoveryAsync(temporary));
        await File.WriteAllTextAsync(Path.Combine(Project, "project.json"), "{corrupt");
        Assert.Equal("snapshot-unreadable", Assert.Single(await ProjectWorkspace.ScanRecoveryAsync(temporary)).Status);
        var recovery = await ProjectWorkspace.RecoverAsync(Project, Validate, Probe);
        await using var recovered = recovery.Workspace; Assert.Equal(1, recovered.Snapshot!.Revision);
    }
    [Fact] public async Task RecoveryDoesNotDropUnknownFieldsFromAWellFormedProject()
    {
        var workspace = await CreateAsync(); var snapshot = Snapshot();
        await workspace.SaveAsync(snapshot, workspace.SessionEpoch);
        await workspace.WriteManifestAsync(new() { ProjectId = "project1", CheckpointJson = ProjectJson.Serialize(snapshot) });
        await workspace.DisposeAsync();
        var file = Path.Combine(Project, "project.json");
        var root = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        root["newerFeature"] = "must survive"; var json = root.ToJsonString(); await File.WriteAllTextAsync(file, json);
        await Assert.ThrowsAsync<ProjectFormatException>(() => ProjectWorkspace.RecoverAsync(Project, Validate, Probe));
        Assert.Equal(json, await File.ReadAllTextAsync(file));
    }
    [Fact] public async Task CorruptLatestMediaFallsBackToPreviousCompleteRevision()
    {
        var workspace = await CreateAsync(); await workspace.SaveAsync(Snapshot(), workspace.SessionEpoch);
        var extra = new MediaAsset { Id = "camera1", Kind = MediaKind.Camera, Path = "media/camera.mp4", Duration = MediaTime.FromTicks(10_000_000), StartOffset = MediaTime.FromTicks(0), Width = 640, Height = 360 };
        await File.WriteAllBytesAsync(workspace.Resolve(extra.Path), FinalizedMp4Header);
        var latest = Snapshot(2) with { Media = Snapshot().Media.Append(extra).ToArray() };
        await workspace.SaveAsync(latest, workspace.SessionEpoch);
        await workspace.WriteManifestAsync(new() { ProjectId = "project1", CheckpointJson = ProjectJson.Serialize(latest) });
        await File.WriteAllTextAsync(workspace.Resolve(extra.Path), "unreadable camera tail"); await workspace.DisposeAsync();
        var recovery = await ProjectWorkspace.RecoverAsync(Project, Validate, Probe); await using var result = recovery.Workspace;
        Assert.Equal(1, result.Snapshot!.Revision); Assert.Single(result.Snapshot.Media); Assert.Contains(recovery.Warnings, w => w.Contains("camera.mp4"));
    }
}

public sealed class CoordinatorTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "FrameTrail-Coordinator-" + Guid.NewGuid().ToString("N"));
    public Task InitializeAsync() { Directory.CreateDirectory(directory); return Task.CompletedTask; }
    public Task DisposeAsync() { if (Directory.Exists(directory)) Directory.Delete(directory, true); return Task.CompletedTask; }
    private RecordingPlan Plan => new(Path.Combine(directory, "recording.frametrailproject"), new(CaptureSourceKind.Monitor, 1, "Fake display", 0, 0, 640, 360, 96, 0), CheckpointSeconds: 5);
    private static RecordingCoordinator Coordinator(IRecordingDriver driver) => new(driver, ProjectJson.ValidateProtocol, (_, _) => { });
    private sealed class FakeDriver : IRecordingDriver
    {
        internal int Started, Disposed; internal bool Fail, BackdateFirst, FailCheckpoint;
        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Events = new();
        internal readonly TaskCompletionSource CheckpointFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IRecordingSegment> StartAsync(RecordingPlan plan, SegmentPaths paths, CancellationToken token)
        { if (Fail || (FailCheckpoint && Started > 0)) throw new IOException("Fake acquisition failed"); Started++; Events.Enqueue($"start-{Started}"); return Task.FromResult<IRecordingSegment>(new Segment(this, paths, Started)); }
        private sealed class Segment(FakeDriver driver, SegmentPaths paths, int index) : IRecordingSegment
        {
            public SegmentStart Start { get; } = new(Stopwatch.GetTimestamp() - (driver.BackdateFirst && index == 1 ? Stopwatch.Frequency * 6 : 0), Stopwatch.Frequency, 640, 360);
            public RecordingHealth Poll() => new(false, null, Array.Empty<string>(), 0.5);
            public async Task<CompletedCapture> FinishAsync(long boundary)
            { driver.Events.Enqueue($"finish-{index}"); await File.WriteAllBytesAsync(paths.Screen, PersistenceTests.FinalizedMp4Header); if (index == 1) driver.CheckpointFinished.TrySetResult(); return new(10_000_000, new[] { new CompletedTrack(MediaKind.Screen, paths.Screen, 10_000_000, Width: 640, Height: 360) }, Array.Empty<string>()); }
            public ValueTask DisposeAsync() { driver.Disposed++; driver.Events.Enqueue($"dispose-{index}"); return ValueTask.CompletedTask; }
        }
    }
    [Fact] public async Task PauseResumeBuildsIndependentSegmentsAndMarkersIgnorePause()
    {
        var driver = new FakeDriver(); await using var coordinator = Coordinator(driver); await coordinator.StartAsync(Plan); await coordinator.AddMarkerAsync("first");
        await coordinator.PauseAsync(); Assert.Equal(RecordingWorkspaceState.Paused, coordinator.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.AddMarkerAsync("paused"));
        await coordinator.ResumeAsync(); await coordinator.AddMarkerAsync("second"); var result = await coordinator.StopAsync();
        Assert.Equal(2, driver.Started); Assert.Equal(2, driver.Disposed); Assert.Equal(2, result!.Segments.Count); Assert.Equal(20_000_000, result.SourceDuration.Ticks);
        Assert.Equal(2, result.Markers.Count); Assert.Equal(10_000_000, result.Media[1].StartOffset.Ticks);
        Assert.Equal(RecordingWorkspaceState.RecordingComplete, coordinator.State);
    }
    [Fact] public async Task StartupFailureReturnsToSetupAndRetainsRecoveryManifest()
    {
        var driver = new FakeDriver { Fail = true }; await using var coordinator = Coordinator(driver);
        await Assert.ThrowsAsync<IOException>(() => coordinator.StartAsync(Plan)); Assert.Equal(RecordingWorkspaceState.Setup, coordinator.State);
        Assert.True(File.Exists(Path.Combine(Plan.ProjectPath, "recovery", "manifest.json")));
    }
    [Fact] public async Task ExistingDestinationFailureDoesNotReuseDisposedWorkspace()
    {
        var driver = new FakeDriver(); await using var coordinator = Coordinator(driver); await coordinator.StartAsync(Plan); await coordinator.StopAsync();
        var error = await Assert.ThrowsAsync<IOException>(() => coordinator.StartAsync(Plan)); Assert.Contains("already exists", error.Message); Assert.Equal(RecordingWorkspaceState.Setup, coordinator.State);
    }
    [Fact] public async Task ResumeFailureKeepsPausedCheckpoint()
    {
        var driver = new FakeDriver(); await using var coordinator = Coordinator(driver); await coordinator.StartAsync(Plan); await coordinator.PauseAsync(); driver.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => coordinator.ResumeAsync()); Assert.Equal(RecordingWorkspaceState.Paused, coordinator.State);
        var result = await coordinator.StopAsync(); Assert.Single(result!.Segments);
    }
    [Fact] public async Task CameraCheckpointStartsNextBeforeFinalizingPrevious()
    {
        var driver = new FakeDriver { BackdateFirst = true }; await using var coordinator = Coordinator(driver);
        await coordinator.StartAsync(Plan with { Camera = new("fake", "camera"), CameraFormat = new(640, 360, 30, 1) });
        await driver.CheckpointFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await coordinator.StopAsync();
        Assert.Equal(new[] { "start-1", "start-2", "finish-1", "dispose-1", "finish-2", "dispose-2" }, driver.Events.ToArray());
        Assert.Equal(2, result!.Segments.Count);
    }
    [Fact] public async Task FailedCameraCheckpointKeepsActiveTakeRunning()
    {
        var driver = new FakeDriver { BackdateFirst = true, FailCheckpoint = true }; await using var coordinator = Coordinator(driver);
        var warned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StatusChanged += (_, status) => { if (status.Warnings.Any(w => w.Contains("active recording continues"))) warned.TrySetResult(); };
        await coordinator.StartAsync(Plan with { Camera = new("fake", "camera"), CameraFormat = new(640, 360, 30, 1) });
        await warned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecordingWorkspaceState.Recording, coordinator.State); Assert.Equal(0, driver.Disposed);
        var result = await coordinator.StopAsync(); Assert.Single(result!.Segments); Assert.Equal(1, driver.Disposed);
    }
}
