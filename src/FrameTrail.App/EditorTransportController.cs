using System.Diagnostics;

namespace FrameTrail.App;

/// <summary>Serializes audio transport and rejects playback starts superseded by pause/seek/close.</summary>
internal sealed class EditorTransportController
{
    private readonly SemaphoreSlim audioGate = new(1);
    private readonly Stopwatch clock = new();
    private readonly PlaybackFrameGate frames = new();
    private long generation, start;
    internal bool Playing { get; private set; }
    internal bool UsesAudio { get; private set; }
    internal Task StopWork { get; private set; } = Task.CompletedTask;

    internal async Task<bool> StartAsync(long ticks, bool usesAudio, Func<Task> startAudio, Func<bool> isCurrent)
    {
        long request = ++generation;
        await audioGate.WaitAsync();
        try
        {
            if (request != generation || !isCurrent()) return false;
            if (usesAudio) await startAudio();
            if (request != generation || !isCurrent()) return false;
            start = ticks;
            UsesAudio = usesAudio;
            clock.Restart();
            frames.Reset();
            Playing = true;
            return true;
        }
        finally { audioGate.Release(); }
    }

    internal void Pause(Func<Task>? stopAudio, Action<Exception>? onError = null)
    {
        ++generation;
        Playing = false;
        clock.Stop();
        if (stopAudio is not null) StopWork = StopAudioAsync(stopAudio, onError);
    }

    private async Task StopAudioAsync(Func<Task> stopAudio, Action<Exception>? onError)
    {
        await audioGate.WaitAsync();
        try { await stopAudio(); }
        catch (ObjectDisposedException) { }
        catch (Exception error) { onError?.Invoke(error); }
        finally { audioGate.Release(); }
    }

    internal long Position(long audioTicks) => UsesAudio && audioTicks >= 0 ? audioTicks : start + clock.Elapsed.Ticks;
    internal bool ShouldRender() => Playing && frames.TryAdvance(clock.Elapsed.Ticks);
}
