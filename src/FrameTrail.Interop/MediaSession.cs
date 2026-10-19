using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using FrameTrail.Domain;

namespace FrameTrail.Interop;

public enum PreviewQuality : uint { Automatic, UltraHd }

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
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_media_export_cpu(MediaHandle handle,uint forceCpu);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_media_export_metrics(MediaHandle handle,[Out] byte[]? output,uint capacity,out uint required);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_media_edit_geometry(MediaHandle handle, long ticks, uint crop, [Out] byte[]? output, uint capacity, out uint required);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int ft_media_probe_visual(string path, uint video, out uint width, out uint height, out long duration);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int ft_media_create(byte[] json, uint length, string root, out ulong handle);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void ft_media_destroy(ulong handle);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_media_swapchain(MediaHandle handle, out nint swapchain);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_media_resize(MediaHandle handle, uint width, uint height, double sx, double sy);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_media_preview_quality(MediaHandle handle, PreviewQuality quality);
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
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ft_media_audio_stream(MediaHandle handle, long ticks);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int ft_media_audio_peaks(MediaHandle handle, string json);
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
    public void SetPreviewQuality(PreviewQuality quality) => MediaNative.Check(MediaNative.ft_media_preview_quality(handle, quality));
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
    public void StreamAudio(long ticks) => MediaNative.Check(MediaNative.ft_media_audio_stream(handle, ticks));
    public IReadOnlyList<CanvasEditItem> EditGeometry(long ticks, bool cropMode = false)
    {
        MediaNative.Check(MediaNative.ft_media_edit_geometry(handle, ticks, cropMode ? 1u : 0u, null, 0, out var required));
        var bytes = new byte[required];
        MediaNative.Check(MediaNative.ft_media_edit_geometry(handle, ticks, cropMode ? 1u : 0u, bytes, required, out _));
        return JsonSerializer.Deserialize<CanvasEditItem[]>(bytes.AsSpan(0, bytes.Length - 1), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    public void PreparePeaks(string path) => MediaNative.Check(MediaNative.ft_media_audio_peaks(handle, Path.GetFullPath(path)));
    public void StopAudio() => MediaNative.Check(MediaNative.ft_media_audio_stop(handle));
    public void Export(string target, bool audioOnly = false) => MediaNative.Check(MediaNative.ft_media_export(handle, Path.GetFullPath(target), audioOnly ? 1u : 0u));
    public void ForceCpuExport(bool value) => MediaNative.Check(MediaNative.ft_media_export_cpu(handle,value?1u:0u));
    public JsonElement ExportMetrics {
        get {
            MediaNative.Check(MediaNative.ft_media_export_metrics(handle,null,0,out var size));var bytes=new byte[size];
            MediaNative.Check(MediaNative.ft_media_export_metrics(handle,bytes,size,out _));
            using var json=JsonDocument.Parse(bytes.AsMemory(0,bytes.Length-1));return json.RootElement.Clone();
        }
    }
    public void Cancel() { if (!handle.IsClosed) MediaNative.Check(MediaNative.ft_media_cancel(handle)); }
    public void Dispose() => handle.Dispose();
}

public sealed record CanvasEditItem(string Id, string Kind, NormalizedRect Rect, double[][] Quad, double[][] Base, double Rotation = 0, double Aspect = 1);
