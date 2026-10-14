using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FrameTrail.Interop;

internal static class RecordingNative
{
    private const string Dll = "FrameTrail.Recording.dll";
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct Device
    { [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)] public string Id; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name; }
    [StructLayout(LayoutKind.Sequential)] internal struct Format { public uint Width, Height, Numerator, Denominator; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct CameraOptions
    { public uint Size; public string DeviceId, OutputPath; public uint Width, Height, FpsNumerator, FpsDenominator; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct CameraStatus
    {
        public uint Size, State; public int ErrorCode; public uint Width, Height;
        public ulong Written, Dropped; public long FirstOffset, Duration;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Message;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct PointerOptions
    { public uint Size, Kind; public nuint SourceHandle; public int X, Y, Width, Height; public long Epoch, Frequency; public string OutputPath, ShapesDirectory; }
    [StructLayout(LayoutKind.Sequential)] internal struct PointerStatus { public uint Size; public int ErrorCode; public ulong Written, Dropped; }
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_camera_enumerate([Out] Device[]? values, uint capacity, out uint count);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int ft_camera_formats(string id, [Out] Format[]? values, uint capacity, out uint count);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int ft_camera_start(in CameraOptions options, out CameraHandle handle);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_camera_set_epoch(CameraHandle handle, long qpc, long frequency);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_camera_stop_at(CameraHandle handle, long qpc);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_camera_get_status(CameraHandle handle, ref CameraStatus status);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void ft_camera_destroy(nint handle);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int ft_recording_probe(string path, uint video, out long duration);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int ft_pointer_start(in PointerOptions options, out PointerHandle handle);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_pointer_stop_at(PointerHandle handle, long qpc);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_pointer_get_status(PointerHandle handle, ref PointerStatus status);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void ft_pointer_destroy(nint handle);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_recording_exclude_window(nuint hwnd, uint exclude);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_recording_hotkey(nuint hwnd, int id, uint modifiers, uint key, uint enable);
    internal static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
}
internal sealed class CameraHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public CameraHandle() : base(true) { }
    protected override bool ReleaseHandle() { RecordingNative.ft_camera_destroy(handle); return true; }
}
internal sealed class PointerHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public PointerHandle() : base(true) { }
    protected override bool ReleaseHandle() { RecordingNative.ft_pointer_destroy(handle); return true; }
}

public sealed record CameraDevice(string Id, string Name);
public sealed record CameraFormat(uint Width, uint Height, uint FpsNumerator, uint FpsDenominator)
{ public override string ToString() => $"{Width} × {Height} · {(double)FpsNumerator / Math.Max(1, FpsDenominator):0.###} FPS"; }
public sealed record CameraCaptureStatus(uint State, int ErrorCode, uint Width, uint Height, ulong FramesWritten,
    ulong FramesDropped, long FirstOffsetTicks, long DurationTicks, string Message);
public sealed record PointerCaptureStatus(int ErrorCode, ulong EventsWritten, ulong EventsDropped);

public sealed class CameraCaptureSession : IDisposable
{
    private readonly CameraHandle handle;
    private CameraCaptureSession(CameraHandle handle) => this.handle = handle;
    public static IReadOnlyList<CameraDevice> EnumerateDevices()
    {
        RecordingNative.Check(RecordingNative.ft_camera_enumerate(null, 0, out uint count));
        for (int i = 0; i < 3; i++) { var values = new RecordingNative.Device[count]; int hr = RecordingNative.ft_camera_enumerate(values, count, out var required);
            if (hr == unchecked((int)0x8007007A)) { count = required; continue; } RecordingNative.Check(hr); return values.Take((int)required).Select(v => new CameraDevice(v.Id, v.Name)).ToArray(); }
        throw new InvalidOperationException("Camera devices changed repeatedly. Refresh devices.");
    }
    public static IReadOnlyList<CameraFormat> EnumerateFormats(string id)
    {
        RecordingNative.Check(RecordingNative.ft_camera_formats(id, null, 0, out var count)); var values = new RecordingNative.Format[count];
        RecordingNative.Check(RecordingNative.ft_camera_formats(id, values, count, out var required));
        return values.Take((int)required).Select(v => new CameraFormat(v.Width, v.Height, v.Numerator, v.Denominator)).ToArray();
    }
    public static CameraCaptureSession Start(CameraDevice device, CameraFormat format, string outputPath)
    {
        var options = new RecordingNative.CameraOptions { Size = (uint)Marshal.SizeOf<RecordingNative.CameraOptions>(), DeviceId = device.Id,
            OutputPath = Path.GetFullPath(outputPath), Width = format.Width, Height = format.Height, FpsNumerator = format.FpsNumerator, FpsDenominator = format.FpsDenominator };
        int result = RecordingNative.ft_camera_start(in options, out var handle);
        if (result < 0) { handle?.Dispose(); RecordingNative.Check(result); }
        if (handle is null || handle.IsInvalid) throw new InvalidOperationException("Camera did not return a valid native handle."); return new(handle);
    }
    public void SetEpoch(long qpc, long frequency) => RecordingNative.Check(RecordingNative.ft_camera_set_epoch(handle, qpc, frequency));
    public void StopAt(long qpc) => RecordingNative.Check(RecordingNative.ft_camera_stop_at(handle, qpc));
    public CameraCaptureStatus GetStatus()
    {
        var value = new RecordingNative.CameraStatus { Size = (uint)Marshal.SizeOf<RecordingNative.CameraStatus>() };
        RecordingNative.Check(RecordingNative.ft_camera_get_status(handle, ref value));
        return new(value.State, value.ErrorCode, value.Width, value.Height, value.Written, value.Dropped, value.FirstOffset, value.Duration, value.Message);
    }
    public void Dispose() => handle.Dispose();
}
public sealed class PointerCaptureSession : IDisposable
{
    private readonly PointerHandle handle;
    private PointerCaptureSession(PointerHandle handle) => this.handle = handle;
    public static PointerCaptureSession Start(CaptureSource source, CaptureRegion? region, string outputPath, string shapesDirectory, long epoch, long frequency)
    {
        var crop = region.GetValueOrDefault(); var options = new RecordingNative.PointerOptions { Size = (uint)Marshal.SizeOf<RecordingNative.PointerOptions>(),
            Kind = (uint)(region.HasValue ? CaptureSourceKind.Region : source.Kind), SourceHandle = source.Handle, X = crop.X, Y = crop.Y, Width = crop.Width, Height = crop.Height,
            Epoch = epoch, Frequency = frequency, OutputPath = Path.GetFullPath(outputPath), ShapesDirectory = Path.GetFullPath(shapesDirectory) };
        int result = RecordingNative.ft_pointer_start(in options, out var handle); if (result < 0) { handle?.Dispose(); RecordingNative.Check(result); }
        if (handle is null || handle.IsInvalid) throw new InvalidOperationException("Pointer capture did not return a valid native handle."); return new(handle);
    }
    public void StopAt(long qpc) => RecordingNative.Check(RecordingNative.ft_pointer_stop_at(handle, qpc));
    public PointerCaptureStatus GetStatus()
    {
        var value = new RecordingNative.PointerStatus { Size = (uint)Marshal.SizeOf<RecordingNative.PointerStatus>() };
        RecordingNative.Check(RecordingNative.ft_pointer_get_status(handle, ref value)); return new(value.ErrorCode, value.Written, value.Dropped);
    }
    public void Dispose() => handle.Dispose();
}
public static class RecordingUtilities
{
    public static long ProbeMedia(string path, bool video) { RecordingNative.Check(RecordingNative.ft_recording_probe(path, video ? 1u : 0u, out long duration)); return duration; }
    public static void ExcludeWindow(nuint hwnd, bool exclude) => RecordingNative.Check(RecordingNative.ft_recording_exclude_window(hwnd, exclude ? 1u : 0u));
    public static void RegisterHotkey(nuint hwnd, int id, uint modifiers, uint virtualKey) => RecordingNative.Check(RecordingNative.ft_recording_hotkey(hwnd, id, modifiers, virtualKey, 1));
    public static void UnregisterHotkey(nuint hwnd, int id) => RecordingNative.Check(RecordingNative.ft_recording_hotkey(hwnd, id, 0, 0, 0));
}
