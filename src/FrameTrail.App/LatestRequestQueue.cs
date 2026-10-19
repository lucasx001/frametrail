namespace FrameTrail.App;

// UI-thread owned. There is one active operation and at most one replacement.
// A request arriving during I/O supersedes its result without growing a task queue.
internal sealed class LatestRequestQueue<T>(Func<T, long, Task> apply)
{
    private (T Value, long Version)? pending;
    private Task? running;
    internal long Version { get; private set; }
    internal Task Completion => running ?? Task.CompletedTask;
    internal bool IsCurrent(long version) => version == Version;
    internal void Cancel() { Version++; pending = null; }
    internal Task Request(T value)
    {
        pending = (value, ++Version);
        if (running is null || running.IsCompleted) running = RunAsync();
        return running;
    }
    private async Task RunAsync()
    {
        // Let the caller publish the task before a synchronous callback reenters.
        await Task.Yield();
        while (pending is { } request) {
            pending = null;
            try { await apply(request.Value, request.Version); }
            catch when (pending is not null && !IsCurrent(request.Version)) { /* Superseded I/O cannot discard the replacement. */ }
        }
    }
}
