using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FrameTrail.Interop;

internal static class CaptureNative
{
    private const string Library = "FrameTrail.Capture";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct Source
    {
        internal uint StructSize, Kind;
        internal nuint Handle;
        internal int X, Y, Width, Height;
        internal uint Dpi, Hdr;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct Options
    {
        internal uint StructSize, Kind;
        internal nuint SourceHandle;
        internal int CropX, CropY, CropWidth, CropHeight;
        internal uint Fps, Bitrate, QueueCapacity, AllowSoftware;
        [MarshalAs(UnmanagedType.LPWStr)] internal string OutputPath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct Status
    {
        internal uint StructSize, State;
        internal int ErrorCode;
        internal uint Width, Height, HardwareAvailable, HardwareActive, QueueDepth, QueueHighWater;
        internal ulong FramesReceived, FramesWritten, FramesDropped, FramesRateLimited;
        internal long QpcFrequency, FirstFrameQpc, PausedQpcTicks, PauseStartedQpc, Duration100ns;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] internal string Message;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Capabilities
    {
        internal uint StructSize, WgcSupported, HardwareH264Count, SoftwareH264Count;
        internal long QpcFrequency;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ft_capture_get_capabilities")]
    internal static extern int GetCapabilities(ref Capabilities capabilities);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ft_capture_enumerate_sources")]
    internal static extern int EnumerateSources([Out] Source[]? sources, uint capacity, out uint count);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, EntryPoint = "ft_capture_start")]
    internal static extern int Start(in Options options, out CaptureSafeHandle handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ft_capture_pause")]
    internal static extern int Pause(CaptureSafeHandle handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ft_capture_resume")]
    internal static extern int Resume(CaptureSafeHandle handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ft_capture_pause_at")]
    internal static extern int PauseAt(CaptureSafeHandle handle, long boundaryQpc);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ft_capture_resume_at")]
    internal static extern int ResumeAt(CaptureSafeHandle handle, long boundaryQpc);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ft_capture_stop")]
    internal static extern int Stop(CaptureSafeHandle handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ft_capture_get_status")]
    internal static extern int GetStatus(CaptureSafeHandle handle, ref Status status);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ft_capture_destroy")]
    internal static extern void Destroy(nint handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ft_capture_last_error")]
    internal static extern nint LastError();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ft_capture_region_from_dip")]
    internal static extern int RegionFromDip(uint dpi, double x, double y, double width, double height,
        int monitorWidth, int monitorHeight, [Out] int[] physicalXywh);

    internal static void Check(int result)
    {
        if (result >= 0) return;
        string? detail = Marshal.PtrToStringUni(LastError());
        throw new CaptureException(string.IsNullOrWhiteSpace(detail)
            ? $"Windows capture failed (0x{result:X8})." : detail, result);
    }
}

internal sealed class CaptureSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public CaptureSafeHandle() : base(true) { }
    protected override bool ReleaseHandle() { CaptureNative.Destroy(handle); return true; }
}
