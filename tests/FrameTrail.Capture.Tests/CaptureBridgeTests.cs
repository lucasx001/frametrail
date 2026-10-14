using FrameTrail.Interop;
using Xunit;

namespace FrameTrail.Capture.Tests;

public sealed class CaptureBridgeTests
{
    [Theory]
    [InlineData(120U, 13, 25, 998, 750)]
    [InlineData(144U, 15, 30, 1200, 900)]
    [InlineData(192U, 20, 40, 1600, 1200)]
    public void MonitorLocalDipSelectionUsesPhysicalPixels(uint dpi, int x, int y, int width, int height)
    {
        // A monitor left of the primary display still uses a nonnegative local selection origin.
        var monitor = new CaptureSource(CaptureSourceKind.Monitor, 1, "Left display", -2560, -200, 2560, 1440, dpi, 0);
        var region = CaptureSession.RegionFromDip(monitor, 10, 20, 800, 600);
        Assert.Equal(new CaptureRegion(x, y, width, height), region);
    }

    [Fact]
    public void RegionCrossingTheDisplayBoundaryIsRejected()
    {
        var monitor = new CaptureSource(CaptureSourceKind.Monitor, 1, "Display", 0, 0, 1920, 1080, 96, 0);
        Assert.Throws<CaptureException>(() => CaptureSession.RegionFromDip(monitor, 1800, 0, 200, 100));
    }

    [Fact]
    public async Task HdrSourceIsRejectedBeforeStartingCapture()
    {
        var hdr = new CaptureSource(CaptureSourceKind.Monitor, 1, "HDR", 0, 0, 1920, 1080, 96, 1);
        await Assert.ThrowsAsync<NotSupportedException>(() => CaptureSession.StartAsync(new(hdr, "unused.mp4")));
    }

    [Fact]
    public void CapabilitiesCanBeProbedRepeatedlyAcrossNativeApartments()
    {
        for (int i = 0; i < 3; i++) Assert.True(CaptureSession.GetCapabilities().QpcFrequency > 0);
    }
}
