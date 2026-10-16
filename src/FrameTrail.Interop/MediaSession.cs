using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using FrameTrail.Domain;

namespace FrameTrail.Interop;

internal sealed class MediaHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal MediaHandle(ulong value) : base(true) => SetHandle(checked((nint)value));
    protected override bool ReleaseHandle() { MediaNative.ft_media_destroy((ulong)handle); return true; }
}

[StructLayout(LayoutKind.Sequential)]
public struct MediaStatus
{
    public uint Size, Stage;
    public ulong Completed, Total, LastProgressMilliseconds;
    public long Duration, RenderedTicks, AudioTicks;
    public uint SoftwareDevice, CpuDecodeFallbacks, AudioRunning;
}

internal static class MediaNative
{
    private const string Dll = "FrameTrail.Media.dll";
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int ft_media_probe_visual(string path, uint video, out uint width, out uint height, out long duration);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int ft_media_create(byte[] json, uint length, string root, out ulong handle);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void ft_media_destroy(ulong handle);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_media_swapchain(MediaHandle handle, out nint swapchain);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_media_resize(MediaHandle handle, uint width, uint height, double sx, double sy);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_media_generation(MediaHandle handle, ulong generation);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_media_update_effects(MediaHandle handle, byte[] json, uint length, ulong generation);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_media_update_project(MediaHandle handle, byte[] json, uint length, ulong generation);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int ft_media_render(MediaHandle handle, long ticks, ulong generation, string? png);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_media_status_get(MediaHandle handle, ref MediaStatus status);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_media_cancel(MediaHandle handle);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int ft_media_export(MediaHandle handle, string target, uint audioOnly);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int ft_media_audio_prepare(MediaHandle handle, string wav);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int ft_media_audio_play(MediaHandle handle, string wav, long ticks);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_media_audio_stop(MediaHandle handle);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int ft_media_error(StringBuilder text, uint capacity);
    internal static void Check(int result)
    {
        if (result >= 0) return;
        if (result == unchecked((int)0x800704C7)) throw new OperationCanceledException();
        var text = new StringBuilder(2048); ft_media_error(text, (uint)text.Capacity);
        throw new COMException(string.IsNullOrWhiteSpace(text.ToString()) ? $"Media engine error 0x{result:X8}" : text.ToString(), result);
    }
}

/// <summary>Native media resources; callers schedule blocking work off the UI thread.</summary>
public sealed class MediaSession : IDisposable
{
    private readonly MediaHandle handle;
    private static readonly JsonSerializerOptions EffectJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static (uint Width, uint Height, long Duration) ProbeVisual(string path, bool video)
    { MediaNative.Check(MediaNative.ft_media_probe_visual(Path.GetFullPath(path), video ? 1u : 0u, out var width, out var height, out var duration)); return (width, height, duration); }
    public MediaSession(ProjectSnapshot snapshot, string directory)
    {
        var json = Encoding.UTF8.GetBytes(ProjectJson.Serialize(snapshot));
        MediaNative.Check(MediaNative.ft_media_create(json, (uint)json.Length, Path.GetFullPath(directory), out var value)); handle = new(value);
    }
    public nint AcquireSwapChain() { MediaNative.Check(MediaNative.ft_media_swapchain(handle, out var pointer)); return pointer; }
    public void Resize(uint width, uint height, double sx = 1, double sy = 1) => MediaNative.Check(MediaNative.ft_media_resize(handle, width, height, sx, sy));
    public void SetGeneration(ulong value) => MediaNative.Check(MediaNative.ft_media_generation(handle, value));
    /// <summary>Replace effects at an edit boundary without recreating native media resources.</summary>
    public void UpdateEffects(ProjectSnapshot snapshot, ulong generation)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { revision = snapshot.Revision, effects = snapshot.Effects }, EffectJson);
        MediaNative.Check(MediaNative.ft_media_update_effects(handle, bytes, (uint)bytes.Length, generation));
    }
    public void UpdateProject(ProjectSnapshot snapshot, ulong generation)
    {
        var bytes = Encoding.UTF8.GetBytes(ProjectJson.Serialize(snapshot));
        MediaNative.Check(MediaNative.ft_media_update_project(handle, bytes, (uint)bytes.Length, generation));
    }
    public void Render(long ticks, ulong generation = 0, string? png = null) => MediaNative.Check(MediaNative.ft_media_render(handle, ticks, generation, png));
    public MediaStatus Status { get { var s = new MediaStatus { Size = (uint)Marshal.SizeOf<MediaStatus>() }; MediaNative.Check(MediaNative.ft_media_status_get(handle, ref s)); return s; } }
    public void PrepareAudio(string wav) => MediaNative.Check(MediaNative.ft_media_audio_prepare(handle, Path.GetFullPath(wav)));
    public void PlayAudio(string wav, long ticks) => MediaNative.Check(MediaNative.ft_media_audio_play(handle, wav, ticks));
    public void StopAudio() => MediaNative.Check(MediaNative.ft_media_audio_stop(handle));
    public void Export(string target, bool audioOnly = false) => MediaNative.Check(MediaNative.ft_media_export(handle, Path.GetFullPath(target), audioOnly ? 1u : 0u));
    public void Cancel() { if (!handle.IsClosed) MediaNative.Check(MediaNative.ft_media_cancel(handle)); }
    public void Dispose() => handle.Dispose();
}
