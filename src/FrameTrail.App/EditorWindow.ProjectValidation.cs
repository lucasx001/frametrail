using System.Runtime.InteropServices;
using FrameTrail.Domain;
using FrameTrail.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    internal bool ProjectSwitchAvailable => SwitchProjectMenuItem.IsEnabled;
    internal async Task ValidateExportCodecsAsync(string directory, List<string> checks)
    {
        var dialog = new EditorExportDialog(ProjectTitle.Text, editor.Timeline.Duration, editor.Snapshot.Canvas.Width, editor.Snapshot.Canvas.Height, BackgroundPreview.Source, ExportToAsync,
            await Task.Run(CaptureSession.GetVideoCodecs), editor.Snapshot.Export.VideoCodec) { XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme };
        var showing = dialog.ShowAsync();
        try { await Task.Delay(200); await dialog.ValidateCodecChoicesAsync(directory); }
        finally { dialog.Hide(); await showing; }
        checks.Add("Export dialog exposes H.264, H.265/HEVC and AV1; available selections update the MP4 summary and render without exporting or changing the project.");
    }
    internal async Task ValidateDeleteCancellationAsync(string directory, List<string> checks)
    {
        var originalSize = AppWindow.Size;
        var originalTheme = Root.RequestedTheme;
        double scale = RecorderWindowChrome.Scale(this);
        try {
            foreach (bool compact in new[] { false, true }) {
                Root.RequestedTheme = compact ? ElementTheme.Light : ElementTheme.Dark;
                if (compact) AppWindow.Resize(new Windows.Graphics.SizeInt32((int)Math.Round(840 * scale), (int)Math.Round(600 * scale)));
                await Task.Delay(180); Root.UpdateLayout();
                var button = DeleteProjectButton.TransformToVisual(Root).TransformBounds(new Windows.Foundation.Rect(0, 0, DeleteProjectButton.ActualWidth, DeleteProjectButton.ActualHeight));
                var export = ExportButton.TransformToVisual(Root).TransformBounds(new Windows.Foundation.Rect(0, 0, ExportButton.ActualWidth, ExportButton.ActualHeight));
                if (!DeleteProjectButton.IsEnabled || button.Width < 70 || button.X < Root.ActualWidth / 2 || button.Right > export.Left || export.Right > Root.ActualWidth - TitleBand.Padding.Right + 1 || button.Top < 0 || button.Bottom > TitleBand.ActualHeight)
                    throw new IOException("Top-right delete/export buttons overlap or clip.");
                string mode = compact ? "compact-light" : "dark";
                await SaveUiImageAsync(Root, Path.Combine(directory, "delete-toolbar-" + mode + ".png"));
                Task request = RequestProjectDeletionAsync();
                var dialog = await WaitForDeleteConfirmationAsync(request);
                if (dialog.DefaultButton != ContentDialogButton.Close || DeleteProjectButton.IsEnabled || ProjectMenuButton.IsEnabled)
                    throw new IOException("Delete confirmation does not default to cancel or allows another project action.");
                await SaveUiImageAsync(dialog, Path.Combine(directory, "delete-confirm-" + mode + ".png"));
                InvokeValidationButton(FindConfirmationButton(dialog, "取消"));
                await request.WaitAsync(TimeSpan.FromSeconds(10));
                if (HasClosed || !Directory.Exists(editor.DirectoryPath) || !DeleteProjectButton.IsEnabled || showingDeleteConfirmation)
                    throw new IOException("Cancel changed the project or left deletion locked.");
            }
            checks.Add("Independent top-right delete button and export fit normal/dark and 840x600 DIP/light layouts; confirmation defaults to cancel, blocks duplicate actions and cancellation keeps the editor/project.");
        } finally { AppWindow.Resize(originalSize); Root.RequestedTheme = originalTheme; await Task.Delay(100); }
    }
    internal async Task ValidateConfirmedDeletionAsync(List<string> checks)
    {
        Task request = RequestProjectDeletionAsync();
        var dialog = await WaitForDeleteConfirmationAsync(request);
        InvokeValidationButton(FindConfirmationButton(dialog, "确认删除"));
        await request.WaitAsync(TimeSpan.FromSeconds(15));
        if (!HasClosed || Directory.Exists(editor.DirectoryPath)) throw new IOException("Confirmed deletion did not close and recycle the active project.");
        checks.Add("Invoking the real confirmation button saves edits, closes the editor and recycles only the active disposable project.");
    }
    private async Task<ContentDialog> WaitForDeleteConfirmationAsync(Task request)
    {
        await Task.Delay(250);
        if (request.IsCompleted) { await request; throw new IOException("Delete confirmation did not stay open."); }
        return deleteConfirmation ?? throw new IOException("Delete confirmation was not shown.");
    }
    private static Button FindConfirmationButton(DependencyObject parent, string text)
    {
        Button? Find(DependencyObject item)
        {
            if (item is Button button && button.Content is string content && content == text) return button;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++)
                if (Find(VisualTreeHelper.GetChild(item, i)) is { } found) return found;
            return null;
        }
        return Find(parent) ?? throw new IOException("Confirmation button not found: " + text);
    }
    private static void InvokeValidationButton(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }
    internal uint StageProjectValidationEdit()
    {
        // Leave a NumberBox change pending, as when a user immediately opens the
        // project menu without first leaving the inspector's input group.
        uint width = editor.Snapshot.Canvas.Width + 2;
        loadingInspector = true; AspectRatio.SelectedIndex = 5; loadingInspector = false;
        CanvasWidth.Value = width;
        return width;
    }
    internal async Task SaveProjectValidationFrameAsync(string path)
    {
        await preview.RenderAsync(0); Root.UpdateLayout();
        await SaveUiImageAsync(Root, path);
    }
    internal async Task ValidateLiveProjectEditsAsync(string directory, List<string> checks)
    {
        var original = preview.Session!; nint swapchain = original.AcquireSwapChain();
        try {
            async Task Check(string label, Func<Task> change)
            {
                long revision = editor.Snapshot.Revision;
                await PerformAsync(change);
                if (editor.Snapshot.Revision <= revision || editor.IsDirty || !ReferenceEquals(original, preview.Session)) throw new IOException("Edit recreated preview or did not save: " + label);
                nint same = original.AcquireSwapChain();
                try { if (same != swapchain) throw new IOException("Edit replaced attached swapchain: " + label); } finally { Marshal.Release(same); }
                if (original.Status.Duration != editor.Timeline.Duration) throw new IOException("Hot timeline duration is stale: " + label);
                await preview.RenderAsync(position, png: Path.Combine(directory, label + "-live.png"));
                using var fresh = new MediaSession(editor.Snapshot, editor.DirectoryPath); fresh.Render(position, png: Path.Combine(directory, label + "-fresh.png"));
            }
            var clip = editor.Snapshot.Segments.First();
            await Check("split", () => editor.ExecuteAsync(new { op = "split", segmentId = clip.Id, sourceTicks = 10_000_000L, newId = "validation-right" }));
            await Check("trim", () => editor.ExecuteAsync(new { op = "trim", segmentId = clip.Id, sourceStart = 1_000_000L, sourceEnd = 9_000_000L }));
            await Check("crop", () => editor.ExecuteAsync(new { op = "settings", editor = (editor.Snapshot.Editor ?? new()) with { Crop = new() { X = 0.1, Y = 0.1, Width = 0.8, Height = 0.8 } } }));
            await Check("frame", () => editor.ExecuteAsync(new { op = "settings", editor = (editor.Snapshot.Editor ?? new()) with { FrameStyle = "browser" } }));
            await Check("canvas", () => editor.ExecuteAsync(new { op = "settings", canvas = editor.Snapshot.Canvas with { Width = 642, Height = 362 } }));
            await Check("zoom", () => editor.UpsertEffectAsync(new() { Id = "validation-zoom", Kind = "zoom", SourceStart = MediaTime.FromTicks(0), SourceDuration = MediaTime.FromTicks(20_000_000), Parameters = System.Text.Json.JsonSerializer.SerializeToElement(new { scale = 2.0, enter = 0, exit = 0, focusX = 0.7, focusY = 0.4 }) }));
            await Check("undo", editor.UndoAsync); await Check("redo", editor.RedoAsync);
            try { original.UpdateProject(editor.Snapshot, 0); throw new IOException("Stale full-project update was accepted."); } catch (OperationCanceledException) { }
            try { original.UpdateProject(editor.Snapshot with { ProjectId = "another-project" }, 1_000_000); throw new IOException("Foreign project hot update was accepted."); } catch (COMException) { }
            await preview.RenderAsync(position);
            await PerformAsync(() => editor.ExecuteAsync(new { op = "settings", export = new ExportSettings { Width = 640, Height = 360 } }));
            if (!ReferenceEquals(original, preview.Session)) throw new IOException("Export settings recreated the preview.");
            await Task.Run(() => original.Export(Path.Combine(directory, "live-edited.mp4")));
            checks.Add("Split, trim, crop, frame, canvas resize, zoom, undo and redo keep the same native media session and attached swapchain; duration updates; stale/foreign project drafts are rejected.");
            checks.Add("Export settings update in place and the reused native session exports the edited timeline to MP4.");
        } finally { Marshal.Release(swapchain); }
    }
}
