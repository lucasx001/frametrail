using Xunit;

namespace FrameTrail.App.Tests;
public sealed class PlaybackFrameGateTests
{
    [Fact] public void JitterAroundSixtyHertzDoesNotDropEveryOtherFrame()
    {
        var gate = new PlaybackFrameGate(); int admitted = 0;
        for (int i = 0; i < 600; i++)
            if (gate.TryAdvance((long)Math.Round(i * TimeSpan.TicksPerSecond / 60.0) + (i % 2 == 0 ? 2000 : -2000))) admitted++;
        Assert.Equal(600, admitted);
    }
    [Fact] public void HighRefreshCallbacksRemainCappedAtSixty()
    {
        var gate = new PlaybackFrameGate(); int admitted = 0;
        for (int i = 0; i < 1650; i++) if (gate.TryAdvance((long)(i * TimeSpan.TicksPerSecond / 165.0))) admitted++;
        Assert.InRange(admitted, 600, 601);
    }
    [Fact] public void SlowRenderingSkipsMissedSlotsAndResetAllowsReplay()
    {
        var gate = new PlaybackFrameGate(); Assert.True(gate.TryAdvance(0));
        Assert.True(gate.TryAdvance(TimeSpan.TicksPerSecond));
        for (int i = 0; i < 100; i++) Assert.False(gate.TryAdvance(TimeSpan.TicksPerSecond));
        gate.Reset(); Assert.True(gate.TryAdvance(0));
    }
}
