using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FrameTrail.Interop;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct AndroidCaptureStatus
{
    public uint Size, State, Width, Height, AudioState, FpsLimit;
    public long FirstQpc, QpcFrequency, FirstPtsUs, LastPtsUs;
    public ulong Frames, Bytes;
    public long MinimumOffsetQpc, LatestOffsetQpc;
    public int Error;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Message;
}
[StructLayout(LayoutKind.Sequential)]
public struct AndroidSegmentStatus
{
    public uint Size, Ready;
    public long EpochQpc, OriginPtsUs, DurationTicks;
    public ulong Frames, AudioFrames;
    public int Error;
}
internal sealed class AndroidHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal AndroidHandle(ulong value) : base(true) => SetHandle((nint)value);
    protected override bool ReleaseHandle() { AndroidNative.ft_android_destroy((ulong)handle); return true; }
}
internal static class AndroidNative
{
    private const string Dll = "FrameTrail.Android.dll";
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_android_create(uint port, uint audio, uint fps, out ulong handle);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void ft_android_destroy(ulong handle);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_android_status(AndroidHandle handle, ref AndroidCaptureStatus status);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int ft_android_begin(AndroidHandle handle, string video, string? audio, out ulong segment);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_android_segment_status(AndroidHandle handle, ulong segment, ref AndroidSegmentStatus status);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_android_finish(AndroidHandle handle, ulong segment, long boundary, ref AndroidSegmentStatus status);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_android_swapchain(AndroidHandle handle, out nint swapchain);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int ft_android_snapshot(AndroidHandle handle, string png);
    internal static void Check(int hr) { if (hr < 0) throw new COMException($"Android media operation failed (0x{hr:X8}).", hr); }
}

/// <summary>scrcpy 3.3.4 media receiver. Only the app's loopback ADB forward is accepted.</summary>
public sealed class AndroidCaptureSession : IDisposable
{
    private readonly AndroidHandle handle;
    public AndroidCaptureSession(uint port, bool audio, uint fps = 30)
    { AndroidNative.Check(AndroidNative.ft_android_create(port, audio ? 1u : 0u, fps, out var value)); handle = new(value); }
    public AndroidCaptureStatus Status { get { var value = new AndroidCaptureStatus { Size = (uint)Marshal.SizeOf<AndroidCaptureStatus>() }; AndroidNative.Check(AndroidNative.ft_android_status(handle, ref value)); return value; } }
    public ulong Begin(string video, string? audio) { AndroidNative.Check(AndroidNative.ft_android_begin(handle, Path.GetFullPath(video), audio is null ? null : Path.GetFullPath(audio), out var segment)); return segment; }
    public AndroidSegmentStatus SegmentStatus(ulong segment) { var value = new AndroidSegmentStatus { Size = (uint)Marshal.SizeOf<AndroidSegmentStatus>() }; AndroidNative.Check(AndroidNative.ft_android_segment_status(handle, segment, ref value)); return value; }
    public AndroidSegmentStatus Finish(ulong segment, long boundary) { var value = new AndroidSegmentStatus { Size = (uint)Marshal.SizeOf<AndroidSegmentStatus>() }; AndroidNative.Check(AndroidNative.ft_android_finish(handle, segment, boundary, ref value)); return value; }
    public nint AcquireSwapChain() { AndroidNative.Check(AndroidNative.ft_android_swapchain(handle, out var value)); return value; }
    public void SavePreview(string png) => AndroidNative.Check(AndroidNative.ft_android_snapshot(handle, Path.GetFullPath(png)));
    public void Dispose() => handle.Dispose();
}
