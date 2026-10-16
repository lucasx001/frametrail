using FrameTrail.Interop;
using Xunit;

namespace FrameTrail.App.Tests;

public sealed class EngineInteropTests
{
    [Fact]
    public void EngineReportsRealVersion() => Assert.Equal(typeof(EngineSession).Assembly.GetName().Version!.ToString(3), EngineSession.Version);

    [Fact]
    public void SessionsCanBeCancelledAndDisposedRepeatedly()
    {
        for (var i = 0; i < 1000; i++)
        {
            var session = new EngineSession();
            Assert.False(session.IsCancelled);
            session.Cancel();
            session.Cancel();
            Assert.True(session.IsCancelled);
            session.Dispose();
            session.Dispose();
            Assert.Throws<ObjectDisposedException>(session.Cancel);
        }
    }

    [Fact]
    public void SessionsAreIndependent()
    {
        using var first = new EngineSession();
        using var second = new EngineSession();
        first.Cancel();
        Assert.True(first.IsCancelled);
        Assert.False(second.IsCancelled);
    }
}
