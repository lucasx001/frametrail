using Microsoft.Win32.SafeHandles;

namespace FrameTrail.Interop;

internal sealed class CameraPreviewHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private CameraPreviewHandle() : base(true) { }
    protected override bool ReleaseHandle() { RecordingNative.ft_camera_preview_destroy(handle); return true; }
}

public sealed class CameraPreviewSession : IDisposable
{
    private readonly CameraPreviewHandle handle;
    private long qpc;
    public ulong SourceIdentity { get; private set; }
    public CameraFormat Format { get; }
    private CameraPreviewSession(CameraPreviewHandle handle, CameraFormat format) { this.handle = handle; Format = format; }
    public static CameraPreviewSession Start(CameraDevice device, CameraFormat format)
    {
        var f = new RecordingNative.Format { Width = format.Width, Height = format.Height, Numerator = format.FpsNumerator, Denominator = format.FpsDenominator };
        int hr = RecordingNative.ft_camera_preview_start(device.Id, in f, out var handle);
        if (hr < 0) { handle?.Dispose(); RecordingNative.Check(hr); }
        if (handle is null || handle.IsInvalid) throw new InvalidOperationException("摄像头预览未能启动。");
        return new(handle, format);
    }
    public bool CopyFrame(byte[] pixels)
    {
        int hr = RecordingNative.ft_camera_preview_copy(handle, pixels, (uint)pixels.Length, ref qpc, out var identity);
        RecordingNative.Check(hr); SourceIdentity = identity; return hr == 0;
    }
    public void Dispose() => handle.Dispose();
}
