using FrameTrail.Domain;
using FrameTrail.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using WinRT;

namespace FrameTrail.App;

[ComImport, Guid("63AAD0B8-7C24-40FF-85A8-640D944CC325"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISwapChainPanelNative { void SetSwapChain(nint swapChain); }

internal sealed class PreviewSurface : SwapChainPanel, IAsyncDisposable
{
    private MediaSession? media;
    private readonly SemaphoreSlim renderGate = new(1);
    private ulong generation;
    private bool disposed;
    internal MediaSession? Session => media;
    internal PreviewQuality Quality { get; private set; }
    internal event EventHandler<Exception>? Failed;
    internal PreviewSurface()
    {
        SizeChanged += async (_, _) => await ResizeAsync();
        CompositionScaleChanged += async (_, _) => await ResizeAsync();
    }
    internal async Task LoadAsync(ProjectSnapshot snapshot, string directory)
    {
        var previous = media; media = null; generation++;
        if (previous is not null) { previous.Cancel(); await renderGate.WaitAsync(); try { this.As<ISwapChainPanelNative>().SetSwapChain(0); await Task.Run(previous.Dispose); } finally { renderGate.Release(); } }
        var created = await Task.Run(() => new MediaSession(snapshot, directory));
        if (disposed) { created.Dispose(); return; }
        nint pointer = 0;
        try { pointer = await Task.Run(created.AcquireSwapChain); this.As<ISwapChainPanelNative>().SetSwapChain(pointer); media = created; created.SetGeneration(generation); created.SetPreviewQuality(Quality); await ResizeAsync(); }
        catch { created.Dispose(); throw; }
        finally { if (pointer != 0) Marshal.Release(pointer); }
    }
    internal void InvalidateRequests() { generation++; media?.SetGeneration(generation); }
    internal async Task SetQualityAsync(PreviewQuality quality, long ticks)
    {
        Quality = quality;
        var current = media; if (current is null || disposed) return;
        InvalidateRequests(); var request = generation;
        await renderGate.WaitAsync();
        try {
            if (ReferenceEquals(media, current)) {
                var selected = Quality;
                await Task.Run(() => current.SetPreviewQuality(selected));
                if (request == generation) await Task.Run(() => current.Render(ticks, request));
            }
        }
        catch (OperationCanceledException) { }
        finally { renderGate.Release(); }
    }
    internal async Task UpdateEffectsAsync(ProjectSnapshot snapshot, long ticks)
    {
        var current = media; if (current is null || disposed) return;
        InvalidateRequests(); var request = generation;
        await renderGate.WaitAsync();
        try {
            if (ReferenceEquals(media, current) && request == generation)
                await Task.Run(() => { current.UpdateEffects(snapshot, request); current.Render(ticks, request); });
        }
        catch (OperationCanceledException) { }
        finally { renderGate.Release(); }
    }
    internal async Task RenderAsync(long ticks, bool supersede = true, string? png = null)
    {
        var current = media; if (current is null || disposed) return;
        if (supersede) InvalidateRequests(); var request = generation;
        await renderGate.WaitAsync();
        try { if (ReferenceEquals(media, current) && request == generation) await Task.Run(() => current.Render(ticks, request, png)); }
        catch (OperationCanceledException) { }
        finally { renderGate.Release(); }
    }
    internal async Task UpdateProjectAsync(ProjectSnapshot snapshot, long ticks)
    {
        var current = media; if (current is null || disposed) throw new InvalidOperationException("Preview is unavailable.");
        InvalidateRequests(); var request = generation;
        await renderGate.WaitAsync();
        try {
            if (ReferenceEquals(media, current) && request == generation)
                await Task.Run(() => { current.UpdateProject(snapshot, request); current.Render(ticks, request); });
        } finally { renderGate.Release(); }
    }
    internal async Task ResizeAsync()
    {
        var current = media; if (current is null || disposed || ActualWidth < 1 || ActualHeight < 1) return;
        var sx = CompositionScaleX; var sy = CompositionScaleY;
        var w = (uint)Math.Clamp(Math.Round(ActualWidth * sx), 2, 8192); var h = (uint)Math.Clamp(Math.Round(ActualHeight * sy), 2, 8192);
        await renderGate.WaitAsync();
        try { if (ReferenceEquals(current, media)) { await Task.Run(() => current.Resize(w, h, sx, sy)); var ticks = current.Status.RenderedTicks; if (ticks >= 0) await Task.Run(() => current.Render(ticks, generation)); } }
        catch (OperationCanceledException) { }
        catch (Exception error) { Failed?.Invoke(this, error); }
        finally { renderGate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        disposed = true; var current = media; media = null; current?.Cancel(); await renderGate.WaitAsync();
        try { this.As<ISwapChainPanelNative>().SetSwapChain(0); if (current is not null) await Task.Run(current.Dispose); }
        finally { renderGate.Release(); }
    }
}
