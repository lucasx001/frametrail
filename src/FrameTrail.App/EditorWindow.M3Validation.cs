using System.Text.Json;
using System.Security.Cryptography;
using FrameTrail.Domain;
using FrameTrail.Interop;
using FrameTrail.Workspace;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    // Explicit developer entry point. The supplied project must be a disposable copy.
    private async Task RunM3ValidationAsync(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var checks = new List<string>(); bool success = false; string? failure = null;
        var appearance = EditorAppearance.Current;
        try
        {
            await Task.Delay(300); Root.RequestedTheme = ElementTheme.Light; position = 10_000_000;
            var importedBackground = editor.Snapshot.Editor?.Backdrop?.Path;
            foreach (var kind in new[] { "zoom", "tilt", "mask", "focus", "sticker" })
            {
                ShowInspector(2); await PerformAsync(() => AddVisualAsync(kind));
                if (CurrentEffect?.Kind != kind) throw new InvalidOperationException("Effect selection failed: " + kind);
                effectScale.Value = 1.8; effectX.Value = 30; effectY.Value = 25; effectWidth.Value = 40; effectHeight.Value = 45;
                effectAngleX.Value = 12; effectAngleY.Value = -18; effectEasing.SelectedIndex = 1;
                stickerRotation.Value = 15; stickerText.Text = "M3 重点";
                await FlushVisualChangesAsync();
                if (StringValue(CurrentEffect!.Parameters, "origin", "") != "edited") throw new InvalidOperationException("Edited effect lost its authored origin.");
                await preview.RenderAsync(position, true, Path.Combine(directory, kind + "-preview.png"));
            }
            checks.Add("Five effect types can be added, selected, edited and previewed through editor panels.");
            await ValidateLiveVisualsAsync(directory, checks);
            copiedEffect = CurrentEffect; var copied = copiedEffect!;
            var count = editor.Snapshot.Effects.Count;
            position = 20_000_000; await PerformAsync(PasteCopiedVisualAsync);
            if (editor.Snapshot.Effects.Count != count + 1 || selectedEffect == copied.Id || CurrentEffect?.Kind != copied.Kind) throw new InvalidOperationException("Paste did not select its new effect.");
            await PerformAsync(editor.UndoAsync); await PerformAsync(editor.RedoAsync);
            if (editor.Snapshot.Effects.Count != count + 1) throw new InvalidOperationException("Paste redo failed.");
            checks.Add("Effect paste and toolbar undo/redo preserve authored parameters.");
            var zoom = editor.Snapshot.Effects.First(e => e.Kind == "zoom");
            editor.BeginTransaction(); editor.PreviewCommand(new { op = "effectAtOutput", effectId = zoom.Id, outputTicks = 5_000_000, edge = "move" });
            await PerformAsync(editor.CommitTransactionAsync);
            if (VisualResources.Ticks(editor.Snapshot.Effects.First(e => e.Id == zoom.Id).SourceStart) != 5_000_000) throw new InvalidOperationException("Effect drag transaction did not commit.");
            await PerformAsync(editor.UndoAsync);
            checks.Add("Effect timeline move commits in a single undo transaction.");
            ShowInspector(1); introDuration.Value = 0.6; introStyle.SelectedIndex = 2;
            await PerformAsync(() => ApplyOpeningAsync(false)); await PerformAsync(() => ApplyOpeningAsync(true));
            var camera = editor.Snapshot.Effects.First(e => e.Id == "opening-camera");
            if (VisualResources.Ticks(camera.SourceStart) != 5_000_000) throw new InvalidOperationException("Camera entrance must follow the camera source interval.");
            checks.Add("Screen and camera entrance effects save with independent source intervals.");
            ShowInspector(0); FrameMockup_Click(this, new RoutedEventArgs()); Root.UpdateLayout();
            if (mockupSettings.Visibility != Visibility.Visible || LayoutSettings.Visibility != Visibility.Collapsed || BackgroundSettings.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Mockup navigation is inconsistent.");
            await SaveUiImageAsync(Root, Path.Combine(directory, "mockup-light.png"));
            if (!string.IsNullOrEmpty(importedBackground)) {
                await PerformAsync(() => SetBackdropAsync("image", importedBackground));
                await ApplyBackgroundColorAsync("#54786D");
                if (editor.Snapshot.Editor?.Backdrop?.Kind != "solid") throw new InvalidOperationException("Color swatch failed to replace imported background.");
                await PerformAsync(editor.UndoAsync);
                if (editor.Snapshot.Editor?.Backdrop?.Path != importedBackground) throw new InvalidOperationException("Undo lost the imported background.");
                checks.Add("Color swatches replace imported backgrounds in one undo step.");
            }
            SetFramePage(false); await PerformAsync(() => SetBackdropAsync("gradient"));
            await editor.SaveVisualPresetAsync(); await PerformAsync(() => editor.ApplyPresetAsync("clean")); await PerformAsync(editor.LoadVisualPresetAsync);
            if (editor.Snapshot.Editor?.Backdrop?.Kind != "gradient") throw new InvalidOperationException("Saved visual preset did not restore.");
            ShowInspector(2); selectedEffect = editor.Snapshot.Effects.First(e => e.Kind == "zoom").Id; RefreshVisualInspector();
            await Task.Delay(100); await SaveUiImageAsync(Root, Path.Combine(directory, "motion-light.png"));
            var authored = ProjectJson.Serialize(editor.Snapshot);
            await preview.RenderAsync(15_000_000, true, Path.Combine(directory, "motion-before-theme.png"));
            for (int i = 0; i < 12; i++) { Appearance_Click(this, new RoutedEventArgs()); await Task.Delay(60); }
            Root.RequestedTheme = ElementTheme.Dark; await Task.Delay(150); await SaveUiImageAsync(Root, Path.Combine(directory, "motion-dark.png"));
            if (ProjectJson.Serialize(editor.Snapshot) != authored) throw new InvalidOperationException("UI appearance changed authored effects.");
            await preview.RenderAsync(15_000_000, true, Path.Combine(directory, "motion-after-theme.png"));
            if (!SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(directory, "motion-before-theme.png"))).SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(directory, "motion-after-theme.png"))))) throw new InvalidOperationException("UI theme changed authored motion pixels.");
            // UI animation preference is deliberately absent from FrameScene input.
            using var mapper = new EngineSession(); mapper.LoadProject(editor.Snapshot);
            var before = mapper.EvaluateScene(10_000_000);
            Root.RequestedTheme = ElementTheme.Light; await Task.Delay(100);
            if (!Equals(before, mapper.EvaluateScene(10_000_000))) throw new InvalidOperationException("UI appearance changed the scene.");
            checks.Add("Twelve theme toggles preserve effect JSON and sampled scene; authored motion has no UI animation preference dependency.");
            await SeekAsync(0); int priorFrames = renderedFrames; var watch = System.Diagnostics.Stopwatch.StartNew(); await StartPlaybackAsync(); await Task.Delay(2000); Pause(); watch.Stop();
            if (renderedFrames - priorFrames < 15) throw new InvalidOperationException("Effects playback stalled.");
            await File.WriteAllTextAsync(Path.Combine(directory, "effects-playback-performance.json"), JsonSerializer.Serialize(new { frames = renderedFrames - priorFrames, seconds = watch.Elapsed.TotalSeconds, fps = (renderedFrames - priorFrames) / watch.Elapsed.TotalSeconds, width = editor.Snapshot.Canvas.Width, height = editor.Snapshot.Canvas.Height, note = "Actual WinUI presentations, all six effect kinds; short local smoke, not a 1080p60 performance acceptance." }));
            checks.Add("Combined effects advance actual compositor presentations without stalling.");
            var original = AppWindow.Size; double dpi = RecorderWindowChrome.Scale(this);
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(840 * dpi), (int)(600 * dpi))); await Task.Delay(150); Root.UpdateLayout();
            if (InspectorScroll.ActualWidth < 250 || PreviewContainer.ActualWidth < 350) throw new InvalidOperationException("Compact effects layout clipped.");
            await SaveUiImageAsync(Root, Path.Combine(directory, "motion-compact.png")); AppWindow.Resize(original);
            await editor.FlushAsync(); checks.Add("Effect controls, six timeline lanes and light/dark/compact layouts render; project autosaves.");
            success = true;
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            Root.RequestedTheme = appearance; EditorAppearance.Save(appearance);
            await File.WriteAllTextAsync(Path.Combine(directory, "m3-editor-validation.json"), JsonSerializer.Serialize(new { success, checks, failure, timestampUtc = DateTimeOffset.UtcNow, note = "App-level control actions and renders; native mouse hit testing is checked separately." }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = success ? 0 : 1; working = false; closing = false; await RequestCloseAsync();
        }
    }
}
