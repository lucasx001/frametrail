using FrameTrail.Interop;
using FrameTrail.Workspace;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Text.Json;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace FrameTrail.App;

public sealed partial class MainWindow
{
    private async Task RunChromeValidationAsync(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        bool success = false; string? error = null; var checks = new List<string>();
        try {
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark }) {
                RecorderRoot.RequestedTheme = theme; await Task.Delay(120); RecorderRoot.UpdateLayout();
                if (!RecorderWindowChrome.HasMatchingClientBounds(this)) throw new InvalidOperationException("Native frame still consumes client pixels.");
                var image = new RenderTargetBitmap(); await image.RenderAsync(RecorderRoot); var pixels = (await image.GetPixelsAsync()).ToArray();
                if (pixels[3] != 0 || pixels[(image.PixelWidth - 1) * 4 + 3] != 0) throw new InvalidOperationException("Capsule corners are opaque.");
                int clipped = RecorderWindowChrome.CountClippedPixels(this, image.PixelWidth, image.PixelHeight, pixels);
                if (clipped != 0) throw new InvalidOperationException($"Input contour cuts {clipped} visible anti-aliased pixels.");
                await SaveUiFrameAsync(RecorderRoot, Path.Combine(directory, $"capsule-{theme.ToString().ToLowerInvariant()}.png"));
                checks.Add($"{theme}: native/client bounds match, corner alpha is zero, no painted edge pixels are clipped.");
            }
            success = true;
        } catch (Exception exception) { error = exception.ToString(); }
        finally {
            await File.WriteAllTextAsync(Path.Combine(directory, "chrome-validation.json"), JsonSerializer.Serialize(new { success, checks, error, chrome = RecorderWindowChrome.Diagnostics(this), transparentBackdrop = SystemBackdrop is RecorderTransparentBackdrop }, new JsonSerializerOptions { WriteIndented = true }));
            allowClose = true; closing = true; UnregisterHotkeys(); await coordinator.DisposeAsync(); session?.Dispose(); Environment.ExitCode = success ? 0 : 1; Close();
        }
    }
    // Explicit local QA entry point, isolated from ordinary product startup.
    // PNGs are rendered by WinUI itself; they are not OS window screenshots.
    private async Task RunUiValidationAsync(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        bool success = false; string? error = null; var checks = new List<string>();
        try
        {
            RecorderRoot.RequestedTheme = ElementTheme.Light;
            await Task.Delay(250); RecorderRoot.UpdateLayout();
            await SaveUiFrameAsync(RecorderRoot, Path.Combine(directory, "setup-light.png"));
            if (Math.Abs(RecorderRoot.ActualHeight - 56) > 1 || SetupPanel.ActualWidth > RecorderRoot.ActualWidth + 1)
                throw new InvalidOperationException($"Setup layout clipped: {RecorderRoot.ActualWidth}x{RecorderRoot.ActualHeight}, content {SetupPanel.ActualWidth}.");
            checks.Add($"Setup fits {SetupWidth}x56 DIPs without content clipping.");
            if (CodecPicker.Items.Count != 3 || VideoCodecSelector.Selected(CodecPicker) != FrameTrail.Domain.VideoCodecs.H264) throw new InvalidOperationException("Recording codecs or compatible default are missing.");
            foreach (var item in CodecPicker.Items.OfType<ComboBoxItem>().Where(i => i.IsEnabled)) {
                CodecPicker.SelectedItem = item;
                if (VideoCodecSelector.Selected(CodecPicker) != (string)item.Tag) throw new InvalidOperationException("Recording codec selection was ignored.");
            }
            CodecPicker.SelectedIndex = 0;
            checks.Add("Recording settings expose H.264, H.265/HEVC and AV1, gate availability and retain the selected codec; H.264 is the default.");
            if (AutomaticZoomEnabled.IsChecked != true || !AutomaticZoomGlyph.IsOn) throw new InvalidOperationException("Automatic zoom must default on.");
            AutomaticZoomEnabled.IsChecked = false;
            if (AutomaticZoomGlyph.IsOn || PointerEnabled.IsChecked != true) throw new InvalidOperationException("Automatic zoom off must retain cursor recording.");
            await SaveUiFrameAsync(RecorderRoot, Path.Combine(directory, "setup-zoom-off.png"));
            AutomaticZoomEnabled.IsChecked = true; PointerEnabled.IsChecked = false;
            if (AutomaticZoomEnabled.IsEnabled || AutomaticZoomGlyph.IsOn) throw new InvalidOperationException("Automatic zoom needs pointer recording.");
            PointerEnabled.IsChecked = true;
            if (!AutomaticZoomEnabled.IsEnabled || !AutomaticZoomGlyph.IsOn) throw new InvalidOperationException("Automatic zoom was not restored with pointer recording.");
            checks.Add("Recorder capsule exposes automatic zoom on/off independently of cursor recording, with the pointer dependency enforced.");
            RecorderRoot.RequestedTheme = ElementTheme.Dark; await Task.Delay(150);
            await SaveUiFrameAsync(RecorderRoot, Path.Combine(directory, "setup-dark.png"));
            RecorderRoot.RequestedTheme = ElementTheme.Light;
            RollSource(1); if (SourceModeLabel.Text != "窗口") throw new InvalidOperationException("Source wheel did not advance to window.");
            RollSource(1); if (SourceModeLabel.Text != "区域") throw new InvalidOperationException("Source wheel did not advance to region.");
            RollSource(1); if (SourceModeLabel.Text != "手机") throw new InvalidOperationException("Source wheel did not advance to Android.");
            if (CodecPicker.IsEnabled) throw new InvalidOperationException("Phone original codec must not use the desktop codec selector.");
            if (AutomaticZoomEnabled.IsEnabled || AutomaticZoomGlyph.IsOn) throw new InvalidOperationException("Desktop click zoom must be disabled for Android.");
            RollSource(1); if (SourceModeLabel.Text != "显示器") throw new InvalidOperationException("Source wheel did not return to display.");
            checks.Add("Source wheel cycles display, window, region and Android.");
            MicrophoneFlyout.ShowAt(MicrophoneButton); await Task.Delay(200);
            if (microphones.Count > 0)
            {
                MicEnabled.IsChecked = true;
                if (!MicrophoneGlyph.IsOn) throw new InvalidOperationException("Microphone glyph did not reflect enabled state.");
                checks.Add("Microphone switch and device choice are connected.");
            }
            await SaveFlyoutAsync(MicrophoneFlyout, Path.Combine(directory, "microphone.png"));
            MicEnabled.IsChecked = false; MicrophoneFlyout.Hide();
            MoreFlyout.ShowAt(MoreButton); await Task.Delay(150);
            await SaveFlyoutAsync(MoreFlyout, Path.Combine(directory, "settings.png")); MoreFlyout.Hide();
            // A short real WGC take exercises the same product coordinator and
            // window state updates. It intentionally does not enable microphone/camera.
            projects = Path.Combine(directory, "projects"); RecordingName.Text = "ui-validation-" + DateTime.Now.ToString("HHmmss");
            SoftwareAllowed.IsChecked = true; SystemEnabled.IsChecked = false; CameraEnabled.IsChecked = false;
            var source = sources.First(s => s.Kind == CaptureSourceKind.Monitor && s.IsSdr);
            foreach (var mode in new[] { 0, 1 })
            {
                CaptureSource? picked = null;
                var front = source with { Kind = CaptureSourceKind.Window, Handle = 1, Name = "Front fixture window", X = source.X + 20, Y = source.Y + 20, Width = 320, Height = 240 };
                var back = front with { Handle = 2, Name = "Back fixture window" };
                using var picking = new CaptureSelectionController(this, mode, new[] { source, front, back }, (chosen, crop) => { if (crop is not null) throw new InvalidOperationException("Click selection unexpectedly cropped."); picked = chosen; }, () => { });
                if (picking.UsesCrosshair) throw new InvalidOperationException("Display/window selection registered a crosshair cursor.");
                picking.Pointer(0x201, source.X + 100, source.Y + 100);
                if (picked is not null) throw new InvalidOperationException("Selection must wait for click release.");
                picking.Pointer(0x202, source.X + 100, source.Y + 100);
                if (picked?.Handle != (mode == 0 ? source.Handle : front.Handle)) throw new InvalidOperationException("Click did not directly confirm the display/topmost window.");
            }
            checks.Add("Display/window selectors register the arrow cursor; one down/up click confirms the display or frontmost overlapping window without a second confirmation.");
            using (var framing = new CaptureSelectionController(this, 2, sources, (_, _) => { }, () => { }))
            {
                if (!framing.UsesCrosshair) throw new InvalidOperationException("Region selector must register the crosshair cursor.");
                framing.Show();
                framing.Pointer(0x201, source.X + 20, source.Y + 20);
                framing.Pointer(0x200, source.X + 660, source.Y + 380);
                framing.Pointer(0x202, source.X + 660, source.Y + 380);
                await Task.Delay(150);
                if (framing.SelectedRegion != new CaptureRegion(20, 20, 640, 360)) throw new InvalidOperationException("Region selection did not preserve physical coordinates.");
                await SaveUiFrameAsync(framing.Surface, Path.Combine(directory, "region-selector.png"));
                checks.Add("Region framing returns the selected 640x360 physical-pixel rectangle and disposes its native overlays.");
            }
            await ExecuteAsync(() => BeginRecordingAsync(source, new CaptureRegion(0, 0, Math.Min(640, source.Width) & ~1, Math.Min(360, source.Height) & ~1)));
            if (coordinator.State != RecordingWorkspaceState.Recording) throw new InvalidOperationException("Real capture did not enter Recording.");
            await Task.Delay(1500);
            await AddMarkerAsync("UI validation"); await Task.Delay(100);
            await SaveUiFrameAsync(RecorderRoot, Path.Combine(directory, "recording.png"));
            await ExecuteAsync(coordinator.PauseAsync); await Task.Delay(150);
            if (coordinator.State != RecordingWorkspaceState.Paused || PauseGlyph.Kind != "Play") throw new InvalidOperationException("Paused UI did not offer resume.");
            await SaveUiFrameAsync(RecorderRoot, Path.Combine(directory, "paused.png"));
            await ExecuteAsync(() => coordinator.ResumeAsync()); await Task.Delay(500);
            var snapshot = await coordinator.StopAsync() ?? throw new InvalidOperationException("No completed project.");
            if (snapshot.Segments.Count != 2 || snapshot.Markers.Count != 1) throw new InvalidOperationException("Pause/resume or marker was not persisted.");
            checks.Add("Real WGC capture, marker, pause, resume and stop persist two segments and one marker.");
            var projectDirectory = coordinator.Workspace!.DirectoryPath;
            await coordinator.DisposeAsync();
            await using var reopened = await ProjectWorkspace.OpenAsync(projectDirectory);
            checks.Add("Recorded project reopens through native media validation.");
            PresentCompletion(projectDirectory, reopened.Snapshot!); await Task.Delay(1500);
            if (completion?.Content is FrameworkElement card) await SaveUiFrameAsync(card, Path.Combine(directory, "completion.png"));
            success = true;
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally
        {
            try { await coordinator.DisposeAsync(); } catch (Exception exception) { success = false; error += "\nCleanup: " + exception.Message; }
            await File.WriteAllTextAsync(Path.Combine(directory, "ui-validation.json"), JsonSerializer.Serialize(new
            { success, timestampUtc = DateTimeOffset.UtcNow, checks, error, imageSource = "WinUI RenderTargetBitmap; OS input automation not represented by these images." }, new JsonSerializerOptions { WriteIndented = true }));
            allowClose = true; closing = true; completion?.Close(); UnregisterHotkeys(); session?.Dispose();
            Environment.ExitCode = success ? 0 : 1; Close();
        }
    }
    private async Task SaveFlyoutAsync(Flyout flyout, string path)
    {
        // RenderTargetBitmap cannot reliably read a separate HWND's popup
        // surface. Rehost the very same controls for the explicit visual QA
        // export, then restore them to their product flyout.
        flyout.Hide(); await Task.Delay(50);
        var content = (FrameworkElement)flyout.Content; flyout.Content = null;
        var border = new Border { Child = content, Padding = new Thickness(10), CornerRadius = new CornerRadius(18), RequestedTheme = ElementTheme.Light };
        border.Background = RecorderTheme.Brush(border, "RecorderBase");
        var host = new Window { Title = "FrameTrail · 配置面板验证", Content = border };
        try
        {
            RecorderWindowChrome.Configure(host, 300, 500, 18); host.Activate(); await Task.Delay(100);
            content.Measure(new Windows.Foundation.Size(278, double.PositiveInfinity));
            RecorderWindowChrome.Resize(host, 300, Math.Min(680, content.DesiredSize.Height + 20), 18);
            await Task.Delay(100); await SaveUiFrameAsync(border, path);
        }
        finally { border.Child = null; host.Close(); flyout.Content = content; Activate(); }
    }
    internal static async Task SaveUiFrameAsync(FrameworkElement element, string path)
    {
        element.UpdateLayout(); var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(element);
        if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0) throw new InvalidOperationException("WinUI returned an empty validation frame.");
        var pixels = await bitmap.GetPixelsAsync();
        using var stream = File.Open(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var random = stream.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, random);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();
    }
}
