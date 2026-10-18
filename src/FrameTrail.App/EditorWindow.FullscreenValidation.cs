using System.Runtime.InteropServices;
using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    // Explicit developer entry point; always use a disposable project copy.
    private async Task RunFullscreenValidationAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>(); bool success = false; string? failure = null;
        try {
            await Task.Delay(300); Root.UpdateLayout();
            var original = ProjectJson.Serialize(editor.Snapshot);
            await ValidateWindowedPreviewAsync(directory, checks);
            // Also retain coverage of the optional viewport-matched mode.
            await preview.SetQualityAsync(PreviewQuality.Automatic, position);
            var session = preview.Session!; var chain = session.AcquireSwapChain();
            try {
                position = Math.Min(5_000_000, editor.Timeline.Duration / 4);
                CanvasZoom.SelectedIndex = 3;
                Root.UpdateLayout(); await preview.ResizeAsync();
                await preview.RenderAsync(position, true, Path.Combine(directory, "before.png"));
                var bounds = (AppWindow.Position, AppWindow.Size);
                long start = position;
                await TogglePreviewFullscreenAsync(); await Task.Delay(200); Root.UpdateLayout();
                await preview.ResizeAsync();
                if (!previewFullscreen || AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen || TitleBand.Visibility != Visibility.Collapsed || EditorBody.Visibility != Visibility.Collapsed || TimelinePanel.Visibility != Visibility.Collapsed)
                    throw new IOException("Fullscreen did not hide editor chrome or use the native fullscreen presenter.");
                if (PreviewContainer.Parent != FullscreenHost || PreviewTransport.Parent != FullscreenHost || PreviewEditTools.Visibility != Visibility.Collapsed || position != start || playing)
                    throw new IOException("Fullscreen lost preview, controls or paused position.");
                double ratio = (double)editor.Snapshot.Canvas.Width / editor.Snapshot.Canvas.Height;
                double expectedWidth = Math.Min(Root.ActualWidth, Root.ActualHeight * ratio);
                if (Math.Abs(CanvasScroll.Width - expectedWidth) > 1 || Math.Abs(PreviewMount.Width - CanvasScroll.Width) > 1 || Math.Abs(PreviewMount.Width / PreviewMount.Height - ratio) > .0001)
                    throw new IOException("Fullscreen did not fit the complete canvas without cropping.");
                await preview.RenderAsync(position, true, Path.Combine(directory, "fullscreen.png"));
                var fullSize = MediaSession.ProbeVisual(Path.Combine(directory, "fullscreen.png"), false);
                var expectedSize = PreviewSize(editor.Snapshot, preview.ActualWidth * preview.CompositionScaleX, preview.ActualHeight * preview.CompositionScaleY);
                if ((fullSize.Width, fullSize.Height) != expectedSize) throw new IOException($"Fullscreen raster {fullSize.Width}x{fullSize.Height} did not match physical viewport {expectedSize}.");
                await Task.Run(() => {
                    using var reference = new MediaSession(editor.Snapshot, editor.DirectoryPath);
                    reference.Resize(fullSize.Width, fullSize.Height); reference.Render(position, png: Path.Combine(directory, "fullscreen-reference.png"));
                });
                if (!(await File.ReadAllBytesAsync(Path.Combine(directory, "fullscreen-reference.png"))).SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(directory, "fullscreen.png"))))
                    throw new IOException("Fullscreen pixels did not match a fresh render at physical viewport resolution.");
                await preview.SetQualityAsync(PreviewQuality.UltraHd, position);
                await preview.RenderAsync(position, true, Path.Combine(directory, "4k.png"));
                var uhdSize = MediaSession.ProbeVisual(Path.Combine(directory, "4k.png"), false);
                if ((uhdSize.Width, uhdSize.Height) != PreviewSize(editor.Snapshot, 8192, 8192)) throw new IOException("Explicit 4K preview did not render at UHD resolution.");
                await preview.SetQualityAsync(PreviewQuality.Automatic, position);
                await SaveUiImageAsync(Root, Path.Combine(directory, "fullscreen-controls.png"));
                await TogglePreviewFullscreenAsync(); await Task.Delay(200); Root.UpdateLayout();
                await preview.ResizeAsync();
                if (previewFullscreen || PreviewContainer.Parent != PreviewStage || PreviewTransport.Parent != PreviewStage || TitleBand.Visibility != Visibility.Visible || EditorBody.Visibility != Visibility.Visible || TimelinePanel.Visibility != Visibility.Visible || CanvasZoom.SelectedIndex != 3 || Math.Abs(PreviewMount.Width / CanvasScroll.Width - 2) > .001)
                    throw new IOException("Leaving fullscreen did not restore the editor and authored preview zoom.");
                if (!AppWindow.Position.Equals(bounds.Item1) || !AppWindow.Size.Equals(bounds.Item2)) throw new IOException("Window bounds changed after fullscreen.");
                await preview.RenderAsync(position, true, Path.Combine(directory, "restored.png"));
                if (!(await File.ReadAllBytesAsync(Path.Combine(directory, "before.png"))).SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(directory, "restored.png")))) throw new IOException("Returning to the same viewport changed composed pixels.");
                checks.Add($"Fullscreen fits the canvas, rasterizes at {fullSize.Width}x{fullSize.Height} physical pixels, matches a fresh full-resolution render, and supports explicit {uhdSize.Width}x{uhdSize.Height} 4K preview.");
                checks.Add("Exiting restores identical preview pixels, paused position, 200% editor zoom and window bounds.");

                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark }) {
                    Root.RequestedTheme = theme;
                    for (int i = 0; i < 2; i++) { await TogglePreviewFullscreenAsync(); await Task.Delay(80); await TogglePreviewFullscreenAsync(); }
                }
                if (AppWindow.Presenter is OverlappedPresenter overlapped) {
                    overlapped.Maximize(); await Task.Delay(150);
                    await TogglePreviewFullscreenAsync(); await Task.Delay(80); await TogglePreviewFullscreenAsync(); await Task.Delay(150);
                    if (AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Maximized }) throw new IOException("Maximized state did not survive fullscreen.");
                    ((OverlappedPresenter)AppWindow.Presenter).Restore(); AppWindow.MoveAndResize(new(bounds.Item1.X, bounds.Item1.Y, bounds.Item2.Width, bounds.Item2.Height));
                }
                checks.Add("Repeated light/dark transitions and maximized-window restoration succeed.");

                await ValidatePreviewResolutionAsync(directory, checks);

                var deadline = DateTime.UtcNow.AddSeconds(10);
                bool hasAudio = editor.Snapshot.Media.Any(m => m.Kind is MediaKind.SystemAudio or MediaKind.Microphone);
                while (hasAudio && preparedAudio is null && DateTime.UtcNow < deadline) await Task.Delay(40);
                await SeekAsync(0); await StartPlaybackAsync();
                if (!playing) throw new IOException("Playback could not start before fullscreen.");
                async Task WaitForPlaybackFrameAsync(int before, string phase)
                {
                    // Swap-chain resize is asynchronous. Observe a completed
                    // presentation, allowing its pending render to finish.
                    var deadline = DateTime.UtcNow.AddMilliseconds(1500);
                    while (playing && renderedFrames <= before && DateTime.UtcNow < deadline) await Task.Delay(20);
                    if (!playing || renderedFrames <= before) throw new IOException($"{phase}: playing={playing}, frames={before}->{renderedFrames}, position={position}/{editor.Timeline.Duration}, notice={Notice.Title}: {Notice.Message}.");
                }
                int frames = renderedFrames;
                await preview.SetQualityAsync(PreviewQuality.UltraHd, position);
                await WaitForPlaybackFrameAsync(frames, "Switching to 4K interrupted playback");
                frames = renderedFrames;
                await TogglePreviewFullscreenAsync(); await WaitForPlaybackFrameAsync(frames, "Entering fullscreen interrupted playback");
                frames = renderedFrames; await TogglePreviewFullscreenAsync(); await WaitForPlaybackFrameAsync(frames, "Leaving fullscreen interrupted playback");
                Pause();
                if (!ReferenceEquals(session, preview.Session)) throw new IOException("Fullscreen recreated the media session.");
                var current = session.AcquireSwapChain(); try { if (current != chain) throw new IOException("Fullscreen replaced the swap chain."); } finally { Marshal.Release(current); }
                if (ProjectJson.Serialize(editor.Snapshot) != original) throw new IOException("Fullscreen changed project settings or revision.");
                checks.Add("Switching to 4K and playing across fullscreen transitions advances real compositor frames, preserves session/swap chain, and leaves project JSON unchanged.");
                success = true;
            } finally { Marshal.Release(chain); }
        } catch (Exception error) { failure = error.ToString(); }
        finally {
            Pause(); if (previewFullscreen) SetPreviewFullscreen(false);
            await File.WriteAllTextAsync(Path.Combine(directory, "fullscreen-validation.json"), JsonSerializer.Serialize(new { success, checks, failure }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = success ? 0 : 1; working = false; closing = false; await RequestCloseAsync();
        }
    }

    private async Task ValidateWindowedPreviewAsync(string directory, List<string> checks)
    {
        if (previewFullscreen || preview.Quality != PreviewQuality.UltraHd)
            throw new IOException("The editor did not open with 4K preview enabled by default.");
        var expected = PreviewSize(editor.Snapshot, 8192, 8192);
        async Task CaptureAsync(string name)
        {
            Root.UpdateLayout(); await preview.ResizeAsync();
            string png = Path.Combine(directory, name + ".png");
            await preview.RenderAsync(position, true, png);
            var size = MediaSession.ProbeVisual(png, false);
            if ((size.Width, size.Height) != expected)
                throw new IOException($"Windowed {name} rendered {size.Width}x{size.Height} instead of {expected}.");
        }
        var bounds = (AppWindow.Position, AppWindow.Size);
        int zoom = CanvasZoom.SelectedIndex;
        try {
            CanvasZoom.SelectedIndex = 0;
            await CaptureAsync("windowed-default-4k");
            AppWindow.Resize(new(Math.Min(bounds.Item2.Width, 1100), Math.Min(bounds.Item2.Height, 760)));
            await Task.Delay(150);
            await CaptureAsync("windowed-small-4k");
            CanvasZoom.SelectedIndex = 3;
            await CaptureAsync("windowed-zoom-4k");
            await TogglePreviewFullscreenAsync();
            await CaptureAsync("windowed-to-fullscreen-4k");
            await TogglePreviewFullscreenAsync();
            await CaptureAsync("windowed-restored-4k");
            await preview.LoadAsync(editor.Snapshot, editor.DirectoryPath);
            await CaptureAsync("windowed-reloaded-4k");
            if (preview.Quality != PreviewQuality.UltraHd) throw new IOException("Reload lost 4K preview quality.");
            if (!(await File.ReadAllBytesAsync(Path.Combine(directory, "windowed-default-4k.png"))).SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(directory, "windowed-reloaded-4k.png"))))
                throw new IOException("Window resize, zoom or reload changed the UHD composition.");
        } finally {
            if (previewFullscreen) SetPreviewFullscreen(false);
            CanvasZoom.SelectedIndex = zoom;
            AppWindow.MoveAndResize(new(bounds.Item1.X, bounds.Item1.Y, bounds.Item2.Width, bounds.Item2.Height));
            Root.UpdateLayout(); await preview.ResizeAsync();
        }
        checks.Add($"The editor defaults to {expected.Width}x{expected.Height} 4K before fullscreen; resizing, 200% zoom, fullscreen round-trip and reload preserve UHD pixels.");
    }

    private static (uint Width, uint Height) PreviewSize(ProjectSnapshot snapshot, double width, double height)
    {
        double w = snapshot.Canvas.Width, h = snapshot.Canvas.Height;
        double scale = Math.Min(Math.Min(Math.Round(width) / w, Math.Round(height) / h), Math.Min((w >= h ? 3840 : 2160) / w, (w >= h ? 2160 : 3840) / h));
        return ((uint)Math.Max(2, Math.Round(w * scale, MidpointRounding.AwayFromZero)), (uint)Math.Max(2, Math.Round(h * scale, MidpointRounding.AwayFromZero)));
    }

    private async Task ValidatePreviewResolutionAsync(string directory, List<string> checks)
    {
        var snapshot = editor.Snapshot with { Export = editor.Snapshot.Export with { Width = 640, Height = 360, Fps = new() { Numerator = 30 } } };
        await Task.Run(() => {
            foreach (var portrait in new[] { false, true }) {
                var project = snapshot with { Canvas = snapshot.Canvas with { Width = portrait ? 360u : 640u, Height = portrait ? 640u : 360u } };
                using var media = new MediaSession(project, editor.DirectoryPath);
                foreach (var (w, h, dpi) in new[] { (961u, 541u, 1.5), (2560u, 1440u, 1.0), (3840u, 2160u, 1.0), (2160u, 3840u, 1.5), (5000u, 3000u, 1.0) }) {
                    string png = Path.Combine(directory, $"viewport-{portrait}-{w}x{h}.png");
                    media.Resize(w, h, dpi, dpi); media.Render(0, png: png);
                    var size = MediaSession.ProbeVisual(png, false);
                    if ((size.Width, size.Height) != PreviewSize(project, w, h)) throw new IOException($"Incorrect preview resolution for {w}x{h}, portrait={portrait}.");
                }
                media.Resize(640, 360); media.SetPreviewQuality(PreviewQuality.UltraHd);
                string uhd = Path.Combine(directory, $"uhd-{portrait}.png"); media.Render(0, png: uhd);
                var uhdSize = MediaSession.ProbeVisual(uhd, false);
                if ((uhdSize.Width, uhdSize.Height) != (portrait ? (2160u, 3840u) : (3840u, 2160u))) throw new IOException("4K quality ignored canvas orientation.");
                try { media.SetPreviewQuality((PreviewQuality)2); throw new IOException("Invalid quality was accepted."); }
                catch (COMException error) when (error.HResult == unchecked((int)0x80070057)) { }
                media.SetPreviewQuality(PreviewQuality.Automatic); media.Render(0, png: Path.Combine(directory, $"automatic-{portrait}.png"));
                var automatic = MediaSession.ProbeVisual(Path.Combine(directory, $"automatic-{portrait}.png"), false);
                if ((automatic.Width, automatic.Height) != PreviewSize(project, 640, 360)) throw new IOException("Automatic quality did not restore viewport resolution.");
                if (!portrait) {
                    media.SetPreviewQuality(PreviewQuality.UltraHd);
                    string export = Path.Combine(directory, "export-preview-independent.mp4"); media.Export(export);
                    var exported = MediaSession.ProbeVisual(export, true);
                    if ((exported.Width, exported.Height) != (640u, 360u)) throw new IOException("4K preview changed export dimensions.");
                }
            }
        });
        checks.Add("Native portrait/landscape rendering matches 961px/150% DPI, 1440p, UHD and oversized viewports, caps at 4K, restores Auto, rejects invalid quality, and leaves 640x360 export unchanged.");
    }
}
