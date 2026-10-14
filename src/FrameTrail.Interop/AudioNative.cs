using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FrameTrail.Interop;

internal sealed class AudioSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal AudioSafeHandle(nint value) : base(true) => SetHandle(value);
    protected override bool ReleaseHandle() { AudioNative.ft_audio_destroy(handle); return true; }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct AudioNativeDevice
{
    public uint Size, Kind, IsDefault;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)] public string Id;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct AudioNativeOptions
{
    public uint Size, Kind, ProcessId;
    [MarshalAs(UnmanagedType.LPWStr)] public string? DeviceId;
    [MarshalAs(UnmanagedType.LPWStr)] public string OutputPath;
    [MarshalAs(UnmanagedType.LPWStr)] public string? DiagnosticsPath;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct AudioNativeStatus
{
    public uint Size, State;
    public int ErrorCode;
    public uint SampleRate, Channels, NativeSampleRate, NativeChannels;
    public uint DefaultDeviceChanged, Discontinuities, TimestampErrors, DriftAvailable;
    public ulong Packets, FramesWritten, SilentFrames, TrimmedFrames, CorrectionFrames;
    public long EpochQpc, QpcFrequency, Duration100ns;
    public double Peak, Rms, DriftPpm, MaxTimestampErrorMs;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string Message;
}

internal static class AudioNative
{
    private const string Library = "FrameTrail.Audio.dll";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_audio_enumerate_devices(uint kind, [Out] AudioNativeDevice[]? devices, uint capacity, out uint count);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_audio_process_from_window(nuint hwnd, out uint processId);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_audio_process_loopback_supported(out uint supported);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int ft_audio_start(in AudioNativeOptions options, out nint handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_audio_set_epoch(AudioSafeHandle handle, long qpc, long frequency);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_audio_pause_at(AudioSafeHandle handle, long qpc);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_audio_resume_at(AudioSafeHandle handle, long qpc);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_audio_stop_at(AudioSafeHandle handle, long qpc);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_audio_get_status(AudioSafeHandle handle, ref AudioNativeStatus status);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void ft_audio_destroy(nint handle);
}
