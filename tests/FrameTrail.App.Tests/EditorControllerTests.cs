using FrameTrail.App;
using FrameTrail.Domain;
using FrameTrail.Interop;
using FrameTrail.Workspace;
using Xunit;

namespace FrameTrail.App.Tests;

public sealed class EditorControllerTests
{
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task PauseRejectsInFlightAudioStartAndWaitsForItsStop()
    {
        var controller = new EditorTransportController();
        var entered = Signal();
        var release = Signal();
        int stopped = 0;
        var start = controller.StartAsync(20, true, async () => { entered.SetResult(true); await release.Task; }, () => true);
        await entered.Task;
        controller.Pause(() => { ++stopped; return Task.CompletedTask; });
        Assert.False(controller.Playing);
        Assert.False(controller.StopWork.IsCompleted);
        release.SetResult(true);
        Assert.False(await start);
        await controller.StopWork;
        Assert.Equal(1, stopped);
    }

    [Fact]
    public async Task NewPlaybackWaitsForOldAudioToStopAndUsesItsOwnClock()
    {
        var controller = new EditorTransportController();
        Assert.True(await controller.StartAsync(100, false, () => throw new Exception(), () => true));
        Assert.True(controller.Position(-1) >= 100);
        var entered = Signal();
        var release = Signal();
        controller.Pause(async () => { entered.SetResult(true); await release.Task; });
        await entered.Task;
        bool started = false;
        var start = controller.StartAsync(900, true, () => { started = true; return Task.CompletedTask; }, () => true);
        Assert.False(started);
        release.SetResult(true);
        Assert.True(await start);
        Assert.Equal(1234, controller.Position(1234));
        Assert.True(controller.ShouldRender());
        controller.Pause(null);
        Assert.False(controller.ShouldRender());
    }

    [Fact]
    public async Task ChangedPreviewSessionRejectsStartAndFailedStopDoesNotPoisonTransport()
    {
        var controller = new EditorTransportController();
        Assert.False(await controller.StartAsync(0, false, () => Task.CompletedTask, () => false));
        Exception? failure = null;
        controller.Pause(() => throw new IOException("device removed"), error => failure = error);
        await controller.StopWork;
        Assert.IsType<IOException>(failure);
        Assert.True(await controller.StartAsync(0, false, () => Task.CompletedTask, () => true));
    }

    [Fact]
    public async Task AssetReplacementCancelsAndDrainsOldWorkerBeforeStartingNewOne()
    {
        var assets = new EditorAssetCoordinator();
        var entered = Signal();
        var cancelled = Signal();
        var release = Signal();
        await assets.ReplaceAudioAsync(() => { }, async token => {
            using var registration = token.Register(() => cancelled.TrySetResult(true));
            entered.SetResult(true);
            await release.Task;
        });
        await entered.Task;
        bool reset = false;
        var replace = assets.ReplaceAudioAsync(() => reset = true, _ => Task.CompletedTask);
        await cancelled.Task;
        Assert.False(reset);
        release.SetResult(true);
        await replace;
        Assert.True(reset);
        await assets.StopAsync(Task.CompletedTask);
    }

    [Fact]
    public async Task AssetStopIncludesPlaybackBarrierAndBothMediaWorkers()
    {
        var assets = new EditorAssetCoordinator();
        var audio = Signal();
        var thumbnail = Signal();
        var barrier = Signal();
        await assets.ReplaceAudioAsync(() => { }, async token => {
            using var registration = token.Register(() => audio.TrySetResult(true));
            await audio.Task;
        });
        await assets.ReplaceThumbnailsAsync(async (_, token) => {
            using var registration = token.Register(() => thumbnail.TrySetResult(true));
            await thumbnail.Task;
        });
        var stop = assets.StopAsync(barrier.Task);
        await Task.WhenAll(audio.Task, thumbnail.Task);
        Assert.False(stop.IsCompleted);
        barrier.SetResult(true);
        await stop;
        Assert.False(assets.HasPendingWork);
    }

    [Fact]
    public async Task ConcurrentAssetReplacementsNeverOverlapWorkersAndStopDrainsTheLatest()
    {
        var assets = new EditorAssetCoordinator();
        var entered = Signal();
        var release = Signal();
        int active = 0, maximum = 0;
        Task Build(CancellationToken token) => Task.Run(async () => {
            int count = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, count);
            var stopped = Signal();
            using var registration = token.Register(() => stopped.TrySetResult(true));
            await stopped.Task;
            Interlocked.Decrement(ref active);
        });
        await assets.ReplaceAudioAsync(() => { }, async _ => {
            Interlocked.Increment(ref active);
            entered.SetResult(true);
            await release.Task;
            Interlocked.Decrement(ref active);
        });
        await entered.Task;
        var first = assets.ReplaceAudioAsync(() => { }, Build);
        var last = assets.ReplaceAudioAsync(() => { }, Build);
        Assert.False(first.IsCompleted);
        Assert.False(last.IsCompleted);
        release.SetResult(true);
        await Task.WhenAll(first, last);
        await assets.StopAsync(Task.CompletedTask);
        Assert.Equal(0, active);
        Assert.Equal(1, maximum);
    }

    [Fact]
    public async Task ThumbnailRequestsKeepOnlyCurrentDemandAndRemainBounded()
    {
        var assets = new EditorAssetCoordinator();
        var entered = Signal();
        var release = Signal();
        long[] observed = [];
        await assets.ReplaceThumbnailsAsync(async (queue, _) => {
            entered.SetResult(true);
            await release.Task;
            var items = new List<long>();
            while (queue.Reader.TryRead(out var value)) items.Add(value);
            observed = items.ToArray();
        });
        await entered.Task;
        assets.RequestThumbnails(Enumerable.Range(0, 1000).Select(i => (long)i).ToArray());
        assets.BeginThumbnail(1200);
        assets.RequestThumbnails([1200, 1400, 1400, 1600]);
        Assert.False(assets.WantsThumbnail(1));
        Assert.True(assets.WantsThumbnail(1400));
        release.SetResult(true);
        await assets.StopAsync(Task.CompletedTask);
        Assert.Equal(new long[] { 1400, 1600 }, observed);
    }

    [Fact]
    public async Task OwnedGesturesPreventCrossControlCommitAndKeepOneUndo()
    {
        string root = Path.Combine(Path.GetTempPath(), "FrameTrail-OwnedEdit-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            await using var workspace = ProjectWorkspace.Create(Path.Combine(root, "test.frametrailproject"), "owned", EngineSession.ValidateProject, (_, _) => { });
            await File.WriteAllBytesAsync(workspace.Resolve("media/screen.mp4"), FinalizedHeader());
            var snapshot = new ProjectSnapshot {
                ProjectId = "owned", Revision = 1, SourceDuration = MediaTime.FromTicks(30_000_000),
                Media = [new() { Id = "screen", Kind = MediaKind.Screen, Path = "media/screen.mp4", Width = 640, Height = 360, Duration = MediaTime.FromTicks(30_000_000) }],
                Segments = [new() { Id = "clip", SourceDuration = MediaTime.FromTicks(30_000_000) }]
            };
            await workspace.SaveAsync(snapshot, workspace.SessionEpoch);
            var session = new EditorSession(workspace);
            var edit = new EditorEditController(session);
            edit.Begin(EditorEditOwner.Canvas);
            edit.Preview(EditorEditOwner.Canvas, new { op = "trim", segmentId = "clip", sourceStart = 1_000_000L, sourceEnd = 30_000_000L });
            edit.Preview(EditorEditOwner.Canvas, new { op = "trim", segmentId = "clip", sourceStart = 2_000_000L, sourceEnd = 30_000_000L });
            Assert.Throws<InvalidOperationException>(() => edit.Cancel(EditorEditOwner.Visual));
            await Assert.ThrowsAsync<InvalidOperationException>(() => edit.CommitAsync(EditorEditOwner.Timeline));
            await edit.CommitAsync(EditorEditOwner.Canvas);
            Assert.Equal(EditorEditOwner.None, edit.Owner);
            Assert.Equal(2_000_000, session.Snapshot.Segments[0].SourceStart.Ticks);
            await session.UndoAsync();
            Assert.Equal(0, session.Snapshot.Segments[0].SourceStart.Ticks);
            Assert.False(session.CanUndo);
            edit.Begin(EditorEditOwner.Visual);
            edit.Preview(EditorEditOwner.Visual, new { op = "trim", segmentId = "clip", sourceStart = 3_000_000L, sourceEnd = 30_000_000L });
            edit.CancelCurrent();
            Assert.Equal(0, session.Snapshot.Segments[0].SourceStart.Ticks);
            Assert.Equal(EditorEditOwner.None, edit.Owner);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static byte[] FinalizedHeader() => [0, 0, 0, 12, (byte)'m', (byte)'o', (byte)'o', (byte)'v', 0, 0, 0, 0];
}
