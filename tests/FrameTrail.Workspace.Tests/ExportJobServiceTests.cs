using FrameTrail.Domain;
using FrameTrail.Interop;
using FrameTrail.Workspace;
using Xunit;

namespace FrameTrail.Workspace.Tests;

public sealed class ExportJobServiceTests
{
    private static ProjectSnapshot Snapshot() => new() {
        ProjectId = "export-job", Revision = 1, SourceDuration = MediaTime.FromTicks(30_000_000),
        Media = [new() { Id = "screen", Kind = MediaKind.Screen, Path = "media/screen.mp4", Width = 640, Height = 360, Duration = MediaTime.FromTicks(30_000_000) }],
        Segments = [new() { Id = "clip", SourceDuration = MediaTime.FromTicks(30_000_000) }]
    };
    private sealed class Job : ExportJobService.IMediaJob
    {
        internal readonly ManualResetEventSlim Started = new(), Finish = new();
        internal bool Disposed, Cancelled, Running;
        public MediaStatus Status => new() { Total = 1, LastProgressMilliseconds = (ulong)Environment.TickCount64 };
        public void Export(string path, bool audioOnly)
        {
            Running = true;
            Started.Set();
            Finish.Wait();
            Running = false;
            if (Cancelled) throw new OperationCanceledException();
        }
        public void Cancel() { Cancelled = true; Finish.Set(); }
        public void Dispose() { Assert.False(Running); Disposed = true; }
    }

    private sealed class Progress(Action<MediaStatus> report) : IProgress<MediaStatus>
    {
        public void Report(MediaStatus value) => report(value);
    }

    [Fact]
    public async Task CancellationWaitsForNativeWorkBeforeDisposal()
    {
        var job = new Job();
        using var cancel = new CancellationTokenSource();
        var service = new ExportJobService((_, _) => job);
        var run = service.RunAsync(Snapshot(), "root", "target", false, null, cancel.Token);
        Assert.True(await Task.Run(() => job.Started.Wait(5000)));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(job.Cancelled);
        Assert.True(job.Disposed);
    }

    [Fact]
    public async Task FailingProgressConsumerCancelsAndDrainsJob()
    {
        var job = new Job();
        var service = new ExportJobService((_, _) => job);
        var error = await Assert.ThrowsAsync<IOException>(() => service.RunAsync(Snapshot(), "root", "target", false,
            new Progress(_ => throw new IOException("progress closed")), CancellationToken.None));
        Assert.Equal("progress closed", error.Message);
        Assert.True(job.Cancelled);
        Assert.True(job.Disposed);
    }

    [Fact]
    public async Task PreCancelledExportDoesNotCreateNativeSession()
    {
        bool created = false;
        var service = new ExportJobService((_, _) => { created = true; return new Job(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(Snapshot(), "root", "target", false,
            null, new CancellationToken(true)));
        Assert.False(created);
    }
}
