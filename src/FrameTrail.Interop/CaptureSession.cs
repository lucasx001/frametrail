using System.Runtime.InteropServices;

namespace FrameTrail.Interop;

public enum CaptureSourceKind : uint { Monitor = 1, Window = 2, Region = 3 }
public enum CaptureState : uint { Starting, Recording, Paused, Finalizing, Stopped, Failed }
public sealed record CaptureSource(CaptureSourceKind Kind, nuint Handle, string Name,
    int X, int Y, int Width, int Height, uint Dpi, uint Hdr)
{
    public bool IsSdr => Hdr == 0;
}
public readonly record struct CaptureRegion(int X, int Y, int Width, int Height);
public sealed record CaptureCapabilities(bool WgcSupported, uint HardwareH264Count, uint SoftwareH264Count, long QpcFrequency);
public sealed record CapturePerformance(bool GpuSamples, ulong Converted, ulong ConversionQpc, ulong ConversionMaxQpc, ulong WriterQpc, ulong WriterMaxQpc);
public sealed record CaptureSettings(CaptureSource Source, string OutputPath,
    CaptureRegion? Region = null, uint Fps = 30, uint Bitrate = 10_000_000, uint QueueCapacity = 4, bool AllowSoftware = false);
public sealed record CaptureStatus(CaptureState State, int ErrorCode, uint Width, uint Height,
    uint HardwareAvailable, bool HardwareActive, uint QueueDepth, uint QueueHighWater,
    ulong FramesReceived, ulong FramesWritten, ulong FramesDropped, ulong FramesRateLimited,
    long QpcFrequency, long FirstFrameQpc, long PausedQpcTicks, long PauseStartedQpc, long Duration100ns, string Message)
{
    public TimeSpan Duration => TimeSpan.FromTicks(Duration100ns);
}
public sealed class CaptureException(string message, int nativeError) : Exception(message)
{
    public int NativeError { get; } = nativeError;
}

/// <summary>One native WGC recording. Start/stop/destroy run off the UI dispatcher; no pixels cross this bridge.</summary>
public sealed class CaptureSession : IDisposable, IAsyncDisposable
{
    private readonly CaptureSafeHandle _handle;
    private readonly object _gate = new();
    private bool _disposed;
    private CaptureSession(CaptureSafeHandle handle) => _handle = handle;

    public static CaptureCapabilities GetCapabilities()
    {
        var value = new CaptureNative.Capabilities { StructSize = (uint)Marshal.SizeOf<CaptureNative.Capabilities>() };
        CaptureNative.Check(CaptureNative.GetCapabilities(ref value));
        return new(value.WgcSupported != 0, value.HardwareH264Count, value.SoftwareH264Count, value.QpcFrequency);
    }

    public static IReadOnlyList<CaptureSource> EnumerateSources()
    {
        CaptureNative.Check(CaptureNative.EnumerateSources(null, 0, out uint count));
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var values = new CaptureNative.Source[count];
            int hr = CaptureNative.EnumerateSources(values, count, out uint required);
            if (hr == unchecked((int)0x8007007A)) { count = required; continue; }
            CaptureNative.Check(hr);
            return values.Take((int)required).Select(s => new CaptureSource((CaptureSourceKind)s.Kind,
                s.Handle, s.Name, s.X, s.Y, s.Width, s.Height, s.Dpi, s.Hdr)).ToArray();
        }
        throw new InvalidOperationException("Capture sources changed repeatedly. Refresh the source list.");
    }

    public static CaptureRegion RegionFromDip(CaptureSource monitor, double x, double y, double width, double height)
    {
        if (monitor.Kind != CaptureSourceKind.Monitor) throw new ArgumentException("Region selection requires one display.", nameof(monitor));
        int[] region = new int[4];
        CaptureNative.Check(CaptureNative.RegionFromDip(monitor.Dpi, x, y, width, height, monitor.Width, monitor.Height, region));
        return new(region[0], region[1], region[2], region[3]);
    }

    public static Task<CaptureSession> StartAsync(CaptureSettings settings, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = settings.Source;
        if (!source.IsSdr) throw new NotSupportedException("Capture requires an SDR display; HDR or unknown color mode is unsupported.");
        if (settings.Region.HasValue && source.Kind != CaptureSourceKind.Monitor)
            throw new ArgumentException("A region must belong to one display.", nameof(settings));
        var region = settings.Region.GetValueOrDefault();
        var options = new CaptureNative.Options
        {
            StructSize = (uint)Marshal.SizeOf<CaptureNative.Options>(), Kind = (uint)(settings.Region.HasValue ? CaptureSourceKind.Region : source.Kind),
            SourceHandle = source.Handle, CropX = region.X, CropY = region.Y, CropWidth = region.Width, CropHeight = region.Height,
            Fps = settings.Fps, Bitrate = settings.Bitrate, QueueCapacity = settings.QueueCapacity,
            AllowSoftware = settings.AllowSoftware ? 1U : 0U, OutputPath = Path.GetFullPath(settings.OutputPath)
        };
        int result = CaptureNative.Start(in options, out var handle);
        if (result < 0) { handle?.Dispose(); CaptureNative.Check(result); }
        if (handle is null || handle.IsInvalid) throw new InvalidOperationException("Capture did not return a valid native session.");
        if (cancellationToken.IsCancellationRequested) { handle.Dispose(); cancellationToken.ThrowIfCancellationRequested(); }
        return new CaptureSession(handle);
    }, cancellationToken);

    public CaptureStatus GetStatus()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var value = new CaptureNative.Status { StructSize = (uint)Marshal.SizeOf<CaptureNative.Status>() };
            CaptureNative.Check(CaptureNative.GetStatus(_handle, ref value));
            return new((CaptureState)value.State, value.ErrorCode, value.Width, value.Height,
                value.HardwareAvailable, value.HardwareActive != 0, value.QueueDepth, value.QueueHighWater,
                value.FramesReceived, value.FramesWritten, value.FramesDropped, value.FramesRateLimited,
                value.QpcFrequency, value.FirstFrameQpc, value.PausedQpcTicks, value.PauseStartedQpc, value.Duration100ns, value.Message);
        }
    }
    public void Pause(long? boundaryQpc = null)
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); CaptureNative.Check(boundaryQpc.HasValue
            ? CaptureNative.PauseAt(_handle, boundaryQpc.Value) : CaptureNative.Pause(_handle)); }
    }
    public CapturePerformance GetPerformance()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var value = new CaptureNative.Performance { StructSize = (uint)Marshal.SizeOf<CaptureNative.Performance>() };
            CaptureNative.Check(CaptureNative.GetPerformance(_handle, ref value));
            return new(value.GpuSamples != 0, value.Converted, value.ConversionQpc, value.ConversionMaxQpc, value.WriterQpc, value.WriterMaxQpc);
        }
    }
    public void Resume(long? boundaryQpc = null)
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); CaptureNative.Check(boundaryQpc.HasValue
            ? CaptureNative.ResumeAt(_handle, boundaryQpc.Value) : CaptureNative.Resume(_handle)); }
    }
    public Task<CaptureStatus> StopAsync() => Task.Run(() =>
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Preserve status/partial-file diagnostics on source loss; callers inspect ErrorCode and Message.
            _ = CaptureNative.Stop(_handle);
            return GetStatus();
        }
    });
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _handle.Dispose(); }
    }
    public ValueTask DisposeAsync() => new(Task.Run(Dispose));
}
