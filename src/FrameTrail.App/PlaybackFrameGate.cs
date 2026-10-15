namespace FrameTrail.App;

// Phase-locked slots tolerate vsync jitter without accumulating a fresh delay
// after each frame. Late work skips slots; it never queues a catch-up burst.
internal sealed class PlaybackFrameGate
{
    private long last = -1;
    internal void Reset() => last = -1;
    internal bool TryAdvance(long elapsedTicks)
    {
        if (elapsedTicks < 0) return false;
        long frame = (elapsedTicks * 60 + TimeSpan.TicksPerSecond / 2) / TimeSpan.TicksPerSecond;
        if (frame <= last) return false;
        last = frame; return true;
    }
}
