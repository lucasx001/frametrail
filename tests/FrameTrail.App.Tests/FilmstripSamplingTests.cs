using FrameTrail.App;
using Xunit;
namespace FrameTrail.App.Tests;
public sealed class FilmstripSamplingTests
{
    [Fact] public void VisibleSamplesAdvanceAndRemainInsideClip()
    {
        var tiles = FilmstripSampling.Visible(20_000_000, 600_000_000, 80, 0, 1200);
        Assert.True(tiles.Count > 8); Assert.Equal(tiles.Count, tiles.Select(t => t.Ticks).Distinct().Count());
        Assert.All(tiles, t => Assert.InRange(t.Ticks, 20_000_000, 619_999_999));
        Assert.True(tiles.Last().Ticks > tiles.First().Ticks + 50_000_000);
    }
    [Fact] public void ScrollUsesLaterSamplesAndDrawsOnlyVisibleTiles()
    {
        var tiles = FilmstripSampling.Visible(0, 36_000_000_000, 100, 25000, 26000);
        Assert.InRange(tiles.Count, 12, 14); Assert.All(tiles, t => Assert.True(t.Ticks > 2_000_000_000));
        Assert.All(tiles, t => Assert.InRange(t.Left + t.Width, 0, 1000));
    }
    [Fact] public void SubframeClipHasValidSampleAndNoUnboundedWork()
    {
        Assert.Empty(FilmstripSampling.Visible(0, 100, 80, 1000, 1200));
        Assert.InRange(FilmstripSampling.Visible(0, 100_000_000_000, 1000, 0, 100000).Count, 1, 64);
    }
    [Fact] public void PrefetchFramesCoversContainerWindowInAscendingOrderWhenZoomedIn()
    {
        Assert.Empty(FilmstripSampling.PrefetchFrames(0, 600_000_000, 120, 0, 1200));
        var frames = FilmstripSampling.PrefetchFrames(0, 600_000_000, 600, 6000, 7200);
        Assert.InRange(frames.Count, 30, 120);
        for (int i = 1; i < frames.Count; i++) Assert.True(frames[i] >= frames[i - 1]);
        Assert.All(frames, t => Assert.InRange(t, 0, 599_999_999));
    }
}
