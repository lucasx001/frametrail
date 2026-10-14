using System.Runtime.InteropServices;

namespace FrameTrail.Interop;

internal static class EngineNative
{
    private const string Library = "FrameTrail.Engine.dll";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint ft_engine_abi_version();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ft_engine_version([Out] byte[]? utf8, uint capacity, out uint required);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ft_session_create(uint abiVersion, out ulong session);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ft_session_destroy(ulong session);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ft_session_cancel(EngineSafeHandle session);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ft_session_is_cancelled(EngineSafeHandle session, out uint cancelled);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ft_project_validate(byte[] utf8, uint length);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ft_session_load_project(EngineSafeHandle session, byte[] utf8, uint length);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ft_session_output_duration(EngineSafeHandle session, out long ticks);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ft_session_map_output(EngineSafeHandle session, long ticks, out NativeMappedTime mapped);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ft_session_map_source(EngineSafeHandle session, long ticks, out NativeMappedTime mapped);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ft_session_evaluate_scene(EngineSafeHandle session, long ticks, out NativeFrameScene scene);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ft_error_copy([Out] byte[]? utf8, uint capacity, out uint required);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct NativeMappedTime
{
    public long Ticks;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string SegmentId;
}
[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect { public double X, Y, Width, Height; }
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct NativeMediaFrame
{
    public long LocalTicks;
    public NativeRect OutputRect;
    public uint Mirror;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string MediaId;
}
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct NativeFrameScene
{
    public long Revision, OutputTicks, SourceTicks;
    public uint Width, Height;
    public double BackgroundR, BackgroundG, BackgroundB, BackgroundA;
    public NativeMediaFrame Screen, Camera;
    public uint HasCamera;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string SegmentId;
}
