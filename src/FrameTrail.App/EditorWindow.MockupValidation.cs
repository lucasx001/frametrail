using System.Runtime.InteropServices;
using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    // Explicit developer entry point; use only a disposable fixture project.
    private async Task RunMockupValidationAsync(string directory)
    {
        Directory.CreateDirectory(directory); var checks = new List<string>(); var renders = new List<object>(); bool success = false; string? failure = null;
        var originalSize = AppWindow.Size; var originalTheme = Root.RequestedTheme;
        try {
            ShowInspector(0); FrameMockup_Click(this, new RoutedEventArgs()); await Task.Delay(350); await mockupPosterWork;
            if (mockupSettings.Visibility != Visibility.Visible || mockupPicker.ActualWidth < 250) throw new IOException("Mockup inspector did not arrange.");
            foreach (string style in FrameStyles.All) {
                await SelectMockupAsync(style, true); await Task.Delay(1100); Root.UpdateLayout();
                if (mockupPicker.SelectedStyle != style || (frameKnobs.Visibility == Visibility.Visible) != (style != "none") ||
                    (frameTitleRow!.Visibility == Visibility.Visible) != FrameStyles.IsWindow(style) || (frameAddressRow!.Visibility == Visibility.Visible) != FrameStyles.IsBrowser(style))
                    throw new IOException("Mockup selection and conditional controls disagree: " + style);
                foreach (var knob in MockupKnobs.Where(k => k.Visibility == Visibility.Visible && frameKnobs.Visibility == Visibility.Visible)) {
                    var bounds = knob.TransformToVisual(mockupSettings).TransformBounds(new Windows.Foundation.Rect(0, 0, knob.ActualWidth, knob.ActualHeight));
                    if (bounds.Left < -0.5 || bounds.Right > mockupSettings.ActualWidth + 0.5) throw new IOException("Mockup knob clipped: " + knob.Label);
                }
                await SaveUiImageAsync(Root, Path.Combine(directory, "mockup-" + style + "-light.png"));
                var snapshot = editor.Snapshot with { Export = editor.Snapshot.Export with { Width = 640, Height = 360, Fps = new() { Numerator = 30 } } };
                string png = Path.Combine(directory, style + "-preview.png"), video = Path.Combine(directory, style + ".mp4");
                await Task.Run(() => { using var media = new MediaSession(snapshot, editor.DirectoryPath); media.Resize(640, 360); media.Render(0, png: png); media.Export(video); });
                renders.Add(new { style, preview = png, video });
            }
            checks.Add("Six styles select, conditionally arrange metadata and knobs, persist and render/export through the native pipeline.");
            await SelectMockupAsync("windowLight");
            var before = ProjectJson.Serialize(editor.Snapshot with { Revision = 0 }); long revision = editor.Snapshot.Revision; var chain = preview.Session!.AcquireSwapChain();
            try {
                int rendered = visualPreviewCount;
                for (int i = 0; i < 5; i++) { frameScale.Value = 1.1 + i * 0.025; await Task.Delay(40); }
                await FlushVisualChangesAsync();
                if (Math.Abs(editor.Snapshot.Editor!.FrameScale - 1.2) > 0.001 || editor.Snapshot.Revision != revision + 1 || visualPreviewCount <= rendered) throw new IOException("Knob preview did not commit once.");
                var current = preview.Session.AcquireSwapChain(); try { if (current != chain) throw new IOException("Mockup knob replaced the swap chain."); } finally { Marshal.Release(current); }
                await PerformAsync(editor.UndoAsync);
                if (ProjectJson.Serialize(editor.Snapshot with { Revision = 0 }) != before) throw new IOException("Mockup knob undo changed unrelated settings.");
            } finally { Marshal.Release(chain); }
            checks.Add("Knob updates hot-preview on the existing swap chain, commit one revision, and undo exactly.");
            frameTitle.Focus(FocusState.Programmatic); frameTitle.Text = "FrameTrail 演示窗口"; await Task.Delay(520);
            if (!visualTransaction || editor.Preview?.Editor?.FrameTitle != frameTitle.Text) throw new IOException("Title did not preview during its focus session.");
            await FlushVisualChangesAsync(); frameScale.Focus(FocusState.Programmatic);
            await SelectMockupAsync("browserDark"); frameAddress.Focus(FocusState.Programmatic); frameAddress.Text = "example.com/frametrail"; await Task.Delay(120); await FlushVisualChangesAsync(); frameScale.Focus(FocusState.Programmatic);
            frameScale.Value = 1.3; frameToolbar.Value = 1.4; frameRadius.Value = 90; await FlushVisualChangesAsync();
            var custom = editor.Snapshot with { Export = editor.Snapshot.Export with { Width = 640, Height = 360, Fps = new() { Numerator = 30 } } };
            string customPng = Path.Combine(directory, "browser-custom-preview.png"), customVideo = Path.Combine(directory, "browser-custom.mp4");
            await Task.Run(() => { using var media = new MediaSession(custom, editor.DirectoryPath); media.Resize(640, 360); media.Render(0, png: customPng); media.Export(customVideo); });
            renders.Add(new { style = "browser-custom", preview = customPng, video = customVideo });
            await SelectMockupAsync("browserDark", true);
            if (editor.Snapshot.Editor is not { FrameTitle: "FrameTrail 演示窗口", FrameBrowserAddress: "example.com/frametrail", FrameScale: 1, FrameToolbarScale: 1.08, FrameOuterCornerRadius: 22 }) throw new IOException("Reset did not restore geometry while preserving authored metadata.");
            await preview.RenderAsync(0, true, Path.Combine(directory, "browser-metadata-preview.png"));
            await SelectMockupAsync("windowLight"); await preview.RenderAsync(0, true, Path.Combine(directory, "window-title-preview.png"));
            checks.Add("Focused title/address preview before saving; reset restores native defaults while retaining both metadata fields.");
            await SelectMockupAsync("devicePhone"); mockupPicker.Step(1);
            var deadline = DateTime.UtcNow.AddSeconds(10); while (DateTime.UtcNow < deadline && (working || editor.Snapshot.Editor?.FrameStyle != "none")) await Task.Delay(20);
            if (editor.Snapshot.Editor?.FrameStyle != "none") throw new IOException("Turntable did not wrap from device to none.");
            await PerformAsync(editor.UndoAsync); if (mockupPicker.SelectedStyle != "devicePhone") throw new IOException("Turntable did not follow undo.");
            checks.Add("Arrow selection wraps around the six-card ring and undo synchronizes the selected card.");
            await SelectMockupAsync("browserLight"); double dpi = RecorderWindowChrome.Scale(this);
            foreach (bool compact in new[] { false, true }) {
                AppWindow.Resize(compact ? new Windows.Graphics.SizeInt32((int)Math.Round(840 * dpi), (int)Math.Round(600 * dpi)) : originalSize);
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark }) {
                    Root.RequestedTheme = theme; await Task.Delay(1100); Root.UpdateLayout();
                    if (mockupPicker.ActualWidth < 250 || frameKnobs.ActualWidth < 250) throw new IOException("Compact mockup controls clipped.");
                    await SaveUiImageAsync(Root, Path.Combine(directory, $"mockup-{(compact ? "compact" : "normal")}-{theme}.png"));
                }
            }
            await editor.FlushAsync(); var saved = ProjectJson.Deserialize(await File.ReadAllTextAsync(Path.Combine(editor.DirectoryPath, "project.json")));
            if (JsonSerializer.Serialize(saved.Editor) != JsonSerializer.Serialize(editor.Snapshot.Editor)) throw new IOException("Mockup settings did not reach project.json.");
            checks.Add("Light/dark and 840x600 DIP layouts render; style, dimensions and Unicode metadata persist.");
            success = true;
        } catch (Exception error) { failure = error.ToString(); }
        finally {
            Root.RequestedTheme = originalTheme; AppWindow.Resize(originalSize);
            await File.WriteAllTextAsync(Path.Combine(directory, "mockup-validation.json"), JsonSerializer.Serialize(new { success, checks, renders, failure }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = success ? 0 : 1; working = false; closing = false; await RequestCloseAsync();
        }
    }
}
