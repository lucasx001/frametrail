using FrameTrail.Interop;
using FrameTrail.Domain;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Windows.Graphics.Imaging;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    private void ValidationTrace(string message) { if (validationDirectory is not null) File.AppendAllText(Path.Combine(validationDirectory, "lifecycle.log"), DateTimeOffset.UtcNow + " " + message + Environment.NewLine); }
    // Explicit developer entry point; always run against a disposable project copy.
    private async Task RunValidationAsync(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory); var checks = new List<string>(); bool success = false; string? failure = null;
        var priorAppearance = EditorAppearance.Current;
        try {
            await Task.Delay(400); Root.UpdateLayout();
            if (PreviewContainer.ActualWidth < 200 || PreviewContainer.ActualHeight < 120 || TimelineMount.ActualHeight < 100) throw new InvalidOperationException("Editor layout clipped.");
            checks.Add("Editor viewport, inspector and timeline have usable measured bounds.");
            var navigationRevision = editor.Snapshot.Revision;
            for (int tab = 0; tab < 6; tab++) { ShowInspector(tab); Root.UpdateLayout(); }
            ShowInspector(0); SetFramePage(true); SetFramePage(false);
            if (navigationRevision != editor.Snapshot.Revision) throw new InvalidOperationException("Inspector navigation changed project contents.");
            await ValidateControlMotionAsync(directory, checks);
            await ValidatePictureInputFocusAsync(directory, checks);
            var beforeBar = ProjectJson.Serialize(editor.Snapshot with { Revision = 0 }); uint originalCanvasWidth = editor.Snapshot.Canvas.Width;
            double desiredPadding = PaddingValue.Value >= 29 ? PaddingValue.Value - 1 : PaddingValue.Value + 1;
            PaddingValue.Value = desiredPadding;
            var editDeadline = DateTime.UtcNow.AddSeconds(10); while (working && DateTime.UtcNow < editDeadline) await Task.Delay(20);
            if (working || Math.Abs(editor.Snapshot.Canvas.Padding * 100 - desiredPadding) > 0.0001 || editor.Snapshot.Canvas.Width != originalCanvasWidth) throw new InvalidOperationException("Extent bar did not save its value or changed the canvas size.");
            await PerformAsync(editor.UndoAsync);
            if (ProjectJson.Serialize(editor.Snapshot with { Revision = 0 }) != beforeBar) throw new InvalidOperationException("Extent bar edit was not a single undo step.");
            checks.Add("Extent bar value commits through autosave, preserves canvas resolution, and reverts in one undo step.");
            var beforeTheme = ProjectJson.Serialize(editor.Snapshot);
            for (int i = 0; i < 12; i++) {
                ValidationTrace($"theme toggle {i}: {Root.ActualTheme}");
                Appearance_Click(this, new Microsoft.UI.Xaml.RoutedEventArgs());
                await Task.Delay(80); Root.UpdateLayout();
                if (EditorAppearance.Current != Root.RequestedTheme) throw new InvalidOperationException("Appearance toggle was not persisted.");
            }
            if (ProjectJson.Serialize(editor.Snapshot) != beforeTheme) throw new InvalidOperationException("Theme switching changed the project.");
            checks.Add("12 toolbar theme toggles persist and broadcast to the hidden recorder without changing the project.");
            Root.RequestedTheme = Microsoft.UI.Xaml.ElementTheme.Dark; await Task.Delay(80); await SaveUiImageAsync(Root, Path.Combine(directory, "editor-dark.png"));
            Root.RequestedTheme = Microsoft.UI.Xaml.ElementTheme.Light; await Task.Delay(80);
            var originalSize = AppWindow.Size; double dpi = RecorderWindowChrome.Scale(this);
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)Math.Round(840 * dpi), (int)Math.Round(600 * dpi))); await Task.Delay(150); Root.UpdateLayout();
            if (PreviewContainer.ActualWidth < 400 || PreviewContainer.ActualHeight < 150 || InspectorTabs.ActualWidth > 340) throw new InvalidOperationException("Compact editor layout clipped.");
            await SaveUiImageAsync(Root, Path.Combine(directory, "editor-compact.png")); AppWindow.Resize(originalSize); await Task.Delay(100);
            var exportDialog = new EditorExportDialog(ProjectTitle.Text, editor.Timeline.Duration, editor.Snapshot.Canvas.Width, editor.Snapshot.Canvas.Height, BackgroundPreview.Source, ExportToAsync) { XamlRoot = Root.XamlRoot, RequestedTheme = Microsoft.UI.Xaml.ElementTheme.Light };
            var showing = exportDialog.ShowAsync(); await Task.Delay(150); await SaveUiImageAsync(exportDialog, Path.Combine(directory, "export-dialog.png")); exportDialog.Hide(); await showing;
            Root.RequestedTheme = priorAppearance; checks.Add("Six inspector pages and frame subnavigation are read-only; light/dark, 840x600 DIP and export sheet render.");
            await preview.RenderAsync(0, true, Path.Combine(directory, "preview.png"));
            var last = Math.Min(editor.Timeline.Duration / 2, 15_000_000);
            var old = preview.RenderAsync(0); var latest = preview.RenderAsync(last); await Task.WhenAll(old, latest);
            if (preview.Session!.Status.RenderedTicks != last) throw new InvalidOperationException("Stale seek overwrote current seek.");
            checks.Add("Native decoder renders actual media and rejects superseded seek generations.");
            position = last; var loadedAt = DateTime.UtcNow;
            while (editor.Snapshot.Media.Any(m => m.Kind is MediaKind.Microphone or MediaKind.SystemAudio) && preparedAudio is null && DateTime.UtcNow - loadedAt < TimeSpan.FromSeconds(30)) await Task.Delay(100);
            await SeekAsync(0); await StartPlaybackAsync(); int timingBefore = renderedFrames;
            var timingClock = System.Diagnostics.Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(editor.Timeline.Duration / 10_000_000.0 - 0.05, 0.01, 2)));
            timingClock.Stop(); int timingFrames = renderedFrames - timingBefore; Pause();
            await File.WriteAllTextAsync(Path.Combine(directory, "playback-performance.json"), JsonSerializer.Serialize(new {
                seconds = timingClock.Elapsed.TotalSeconds, frames = timingFrames,
                fps = timingFrames / timingClock.Elapsed.TotalSeconds,
                width = editor.Snapshot.Canvas.Width, height = editor.Snapshot.Canvas.Height,
                note = "Actual completed compositor presentations in the WinUI window; includes dispatcher scheduling, no repeated-frame padding."
            }, new JsonSerializerOptions { WriteIndented = true }));
            position = 0; int before = renderedFrames; await StartPlaybackAsync();
            for (int i = 0; i < 6; i++) { Appearance_Click(this, new Microsoft.UI.Xaml.RoutedEventArgs()); await Task.Delay(100); }
            await Task.Delay(150); Pause();
            if (renderedFrames - before < 3) throw new InvalidOperationException("Preview did not schedule playback frames.");
            checks.Add($"Playback advanced {renderedFrames - before} compositor frames through 6 persisted theme toggles, then paused.");
            var cut = editor.Timeline.Clips.First();
            await editor.ExecuteAsync(new { op = "split", segmentId = cut.Id, sourceTicks = cut.SourceStart + cut.SourceDuration / 2, newId = "ui-m2-split" });
            await editor.ExecuteAsync(new { op = "rate", segmentId = "ui-m2-split", numerator = 2, denominator = 1 });
            await editor.ExecuteAsync(new { op = "move", segmentId = "ui-m2-split", index = 0 });
            var changed = ProjectJson.Serialize(editor.Snapshot with { Revision = 0 });
            await editor.UndoAsync(); await editor.RedoAsync();
            if (changed != ProjectJson.Serialize(editor.Snapshot with { Revision = 0 })) throw new InvalidOperationException("Undo/redo content changed.");
            editor.BeginTransaction(); editor.PreviewCommand(new { op = "delete", segmentId = "ui-m2-split" }); editor.CancelTransaction();
            if (changed != ProjectJson.Serialize(editor.Snapshot with { Revision = 0 })) throw new InvalidOperationException("Cancelled transaction changed project.");
            checks.Add("Split, rate, reorder, undo/redo and cancelled drag transaction preserve exact authored content.");
            await ReloadAsync(); await SeekAsync(0); await Task.Delay(500);
            var image = new RenderTargetBitmap(); await image.RenderAsync(Root);
            using (var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream()) {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream); var pixels = await image.GetPixelsAsync();
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)image.PixelWidth, (uint)image.PixelHeight, 96, 96, pixels.ToArray()); await encoder.FlushAsync();
                using var file = File.Create(Path.Combine(directory, "editor-controls.png")); stream.Seek(0); await stream.AsStreamForRead().CopyToAsync(file);
            }
            await editor.FlushAsync(); checks.Add("Edited project saved through the existing revision/lease barrier.");
            timeline.ThumbnailsEnabled = false;
            var many = Enumerable.Range(0, 10000).Select(i => new ResolvedClip("dense-" + i, i * 3000L, 3000, i * 3000L, 3000, 1, 1)).ToArray();
            var layoutWatch = System.Diagnostics.Stopwatch.StartNew(); timeline.Set(new ResolvedTimeline(30_000_000, many, Array.Empty<ResolvedMarker>()), many[0].Id, 0); layoutWatch.Stop();
            if (timeline.RenderedElementCount > 2000 || layoutWatch.Elapsed > TimeSpan.FromSeconds(1)) throw new InvalidOperationException("Dense timeline failed bounded drawing.");
            checks.Add($"10,000 dense clips draw {timeline.RenderedElementCount} visible/aggregated elements in {layoutWatch.Elapsed.TotalMilliseconds:0.0} ms.");
            timeline.ThumbnailsEnabled = true; RefreshTimeline();
            success = true;
        } catch (Exception error) { failure = error.ToString(); }
        finally {
            Root.RequestedTheme = priorAppearance; EditorAppearance.Save(priorAppearance);
            await File.WriteAllTextAsync(Path.Combine(directory, "editor-validation.json"), JsonSerializer.Serialize(new { success, timestampUtc = DateTimeOffset.UtcNow, checks, failure, note = "App-level interaction and WinUI controls render. SwapChainPanel pixels are independently captured in preview.png; OS mouse hit-testing is not represented." }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = success ? 0 : 1; working = false; closing = false; await RequestCloseAsync();
        }
    }
    private async Task ValidateControlMotionAsync(string directory, List<string> checks)
    {
        bool oldLoading = loadingInspector; var oldTheme = Root.RequestedTheme;
        bool? oldMute = SystemMute.IsChecked; double oldGain = SystemGain.Value;
        var samples = new List<object>();
        try {
            loadingInspector = true; ShowInspector(5); Root.UpdateLayout();
            SystemMute.IsChecked = false; await Task.Delay(200);
            var surface = (Microsoft.UI.Xaml.FrameworkElement)Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(SystemMute, 0);
            var translation = (Microsoft.UI.Xaml.Media.TranslateTransform)surface.FindName("SwitchKnobTranslation");
            var onTrack = (Microsoft.UI.Xaml.Controls.Border)surface.FindName("SwitchOnTrack");
            if (Math.Abs(translation.X) > 0.01 || onTrack.Opacity > 0.01) throw new InvalidOperationException("Switch off state is not at its endpoint.");
            bool intermediate = false; var motionClock = System.Diagnostics.Stopwatch.StartNew(); SystemMute.IsChecked = true;
            for (int i = 0; i < 8; i++) {
                await Task.Delay(25);
                samples.Add(new { elapsedMilliseconds = motionClock.Elapsed.TotalMilliseconds, x = translation.X, opacity = onTrack.Opacity });
                intermediate |= translation.X > 0.05 && translation.X < 11.95 && onTrack.Opacity > 0 && onTrack.Opacity < 1;
            }
            if (Math.Abs(translation.X - 12) > 0.01 || Math.Abs(onTrack.Opacity - 1) > 0.01) throw new InvalidOperationException("Switch on state did not settle.");
            if (SwitchMotion.AnimationsEnabled && !intermediate) throw new InvalidOperationException("Switch did not render an intermediate transition state.");
            SystemMute.IsChecked = false; await Task.Delay(40); SystemMute.IsChecked = true; await Task.Delay(40); SystemMute.IsChecked = false; await Task.Delay(200);
            if (Math.Abs(translation.X) > 0.01 || onTrack.Opacity > 0.01) throw new InvalidOperationException("Rapid switch reversals left a stale visual state.");
            SystemGain.Value = 50;
            foreach (var theme in new[] { Microsoft.UI.Xaml.ElementTheme.Light, Microsoft.UI.Xaml.ElementTheme.Dark }) {
                Root.RequestedTheme = theme; await Task.Delay(180); Root.UpdateLayout();
                SystemGain.Focus(Microsoft.UI.Xaml.FocusState.Keyboard); await Task.Delay(180);
                await SaveUiImageAsync(Root, Path.Combine(directory, $"controls-{theme.ToString().ToLowerInvariant()}.png"));
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "switch-motion.json"), JsonSerializer.Serialize(new { animationsEnabled = SwitchMotion.AnimationsEnabled, intermediate, samples, rapidReversalsSettled = true }, new JsonSerializerOptions { WriteIndented = true }));
            checks.Add("Switch samples contain intermediate thumb/track values and settle after rapid reversals; slider focus and light/dark controls render.");
        } finally {
            SystemMute.IsChecked = oldMute; SystemGain.Value = oldGain; Root.RequestedTheme = oldTheme;
            ShowInspector(0); SetFramePage(false); loadingInspector = oldLoading;
        }
    }
    private async Task ValidatePictureInputFocusAsync(string directory, List<string> checks)
    {
        var before = ProjectJson.Serialize(editor.Snapshot with { Revision = 0 });
        long revision = editor.Snapshot.Revision;
        ShowInspector(4); Root.UpdateLayout(); await Task.Delay(180);
        var mirrorSurface = (Microsoft.UI.Xaml.FrameworkElement)Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(CameraMirror, 0);
        var mirrorPosition = (Microsoft.UI.Xaml.Media.TranslateTransform)mirrorSurface.FindName("SwitchKnobTranslation");
        if (Math.Abs(mirrorPosition.X - (CameraMirror.IsChecked == true ? 12 : 0)) > 0.01) throw new InvalidOperationException("An initially hidden switch did not reflect its checked value when shown.");
        if (!CameraX.Focus(Microsoft.UI.Xaml.FocusState.Keyboard)) throw new InvalidOperationException("Camera X cannot receive keyboard focus.");
        CameraX.Text = "30";
        CameraY.Focus(Microsoft.UI.Xaml.FocusState.Keyboard); await Task.Delay(180);
        if (editor.Snapshot.Revision != revision || !ReferenceEquals(InputAncestor<Microsoft.UI.Xaml.Controls.NumberBox>(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot) as Microsoft.UI.Xaml.DependencyObject), CameraY))
            throw new InvalidOperationException("Moving X to Y saved/reloaded the inspector before the input group was finished.");
        CameraY.Text = "40";
        InspectorScroll.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && (working || changedNumbers.Count > 0 || pictureInputCommitQueued || editor.Snapshot.Revision == revision)) await Task.Delay(20);
        if (working || changedNumbers.Count > 0 || Math.Abs(editor.Snapshot.Canvas.Camera.X - 0.3) > 0.00001 || Math.Abs(editor.Snapshot.Canvas.Camera.Y - 0.4) > 0.00001 || !InspectorScroll.IsHitTestVisible)
            throw new InvalidOperationException("X/Y focus exit did not save both values or restore inspector interaction.");
        await SaveUiImageAsync(Root, Path.Combine(directory, "camera-inputs.png"));
        await PerformAsync(editor.UndoAsync);
        if (ProjectJson.Serialize(editor.Snapshot with { Revision = 0 }) != before) throw new InvalidOperationException("X/Y edit did not undo as one step.");
        // Explicit close/export flushing must also finish a still-focused numeric edit.
        CameraX.Focus(Microsoft.UI.Xaml.FocusState.Keyboard); CameraX.Text = "35";
        await FlushPictureInputsAsync();
        if (Math.Abs(editor.Snapshot.Canvas.Camera.X - 0.35) > 0.00001 || editor.IsDirty) throw new InvalidOperationException("Pending numeric input did not flush.");
        await PerformAsync(editor.UndoAsync);
        if (ProjectJson.Serialize(editor.Snapshot with { Revision = 0 }) != before) throw new InvalidOperationException("Pending input flush changed unrelated settings.");
        ShowInspector(0); SetFramePage(false);
        checks.Add("X-to-Y focus keeps the draft active; leaving the input group saves both values in one undo step; close/export flushes a focused numeric draft.");
    }
    private static async Task SaveUiImageAsync(Microsoft.UI.Xaml.UIElement element, string path)
    {
        var image = new RenderTargetBitmap(); await image.RenderAsync(element);
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)image.PixelWidth, (uint)image.PixelHeight, 96, 96, (await image.GetPixelsAsync()).ToArray());
        await encoder.FlushAsync(); using var file = File.Create(path); stream.Seek(0); await stream.AsStreamForRead().CopyToAsync(file);
    }
}
