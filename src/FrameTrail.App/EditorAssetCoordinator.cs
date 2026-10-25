using System.Threading.Channels;

namespace FrameTrail.App;

/// <summary>Owns replaceable media jobs and the bounded, latest-demand thumbnail queue.</summary>
internal sealed class EditorAssetCoordinator
{
    private readonly Job thumbnail = new(), audio = new();
    private readonly HashSet<long> pending = new();
    private HashSet<long> demand = new();
    private Channel<long>? requests;
    private long inFlight = -1;
    internal bool AudioCompleted => audio.Work.IsCompleted;
    internal bool HasPendingWork => !thumbnail.Work.IsCompleted || !audio.Work.IsCompleted;

    internal Task ReplaceThumbnailsAsync(Func<Channel<long>, CancellationToken, Task> build) =>
        thumbnail.ReplaceAsync(() => {
            requests = Channel.CreateBounded<long>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait });
            pending.Clear();
        }, token => build(requests!, token));

    internal Task ReplaceAudioAsync(Action reset, Func<CancellationToken, Task> build) => audio.ReplaceAsync(reset, build);

    internal void RequestThumbnails(IReadOnlyList<long> ticks)
    {
        Volatile.Write(ref demand, ticks.ToHashSet());
        if (requests is null) return;
        while (requests.Reader.TryRead(out _)) { }
        pending.Clear();
        foreach (long ticksValue in ticks)
            if (ticksValue != Interlocked.Read(ref inFlight) && pending.Add(ticksValue) && !requests.Writer.TryWrite(ticksValue))
                pending.Remove(ticksValue);
    }

    internal bool WantsThumbnail(long ticks) => Volatile.Read(ref demand).Contains(ticks);
    internal void BeginThumbnail(long ticks) => Interlocked.Exchange(ref inFlight, ticks);
    internal void EndThumbnail() => Interlocked.Exchange(ref inFlight, -1);
    internal void PublishedThumbnail(long ticks) => pending.Remove(ticks);

    internal async Task StopAsync(Task transportBarrier)
    {
        thumbnail.Cancel();
        audio.Cancel();
        await Task.WhenAll(thumbnail.StopAsync(), audio.StopAsync(), transportBarrier);
    }

    private sealed class Job
    {
        private readonly SemaphoreSlim changes = new(1);
        private readonly object cancellationGate = new();
        private CancellationTokenSource cancellation = new();
        internal Task Work { get; private set; } = Task.CompletedTask;
        internal void Cancel() { lock (cancellationGate) cancellation.Cancel(); }
        internal async Task StopAsync()
        {
            Cancel();
            await changes.WaitAsync();
            try { Cancel(); await DrainAsync(); }
            finally { changes.Release(); }
        }
        internal async Task ReplaceAsync(Action prepare, Func<CancellationToken, Task> build)
        {
            await changes.WaitAsync();
            try
            {
                Cancel();
                await DrainAsync();
                lock (cancellationGate)
                {
                    cancellation.Dispose();
                    cancellation = new();
                }
                prepare();
                var token = cancellation.Token;
                Work = Task.Run(() => build(token), CancellationToken.None);
            }
            finally { changes.Release(); }
        }
        private async Task DrainAsync()
        {
            try { await Work; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }
}
