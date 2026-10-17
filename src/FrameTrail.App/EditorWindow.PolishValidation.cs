using FrameTrail.Domain;
using FrameTrail.Interop;
using FrameTrail.Workspace;
using Microsoft.UI.Xaml;
using System.Text.Json;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    private static async Task UntilAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(40);
        if (!condition()) throw new InvalidOperationException(message);
    }
    private async Task RunEditorPolishValidationAsync(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var checks = new List<string>(); bool success = false; string? failure = null;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try {
            if (Environment.GetCommandLineArgs().Contains("--preset-ui-only")) {
                presetLibrary = new VisualPresetLibrary(Path.Combine(directory, "preset-library"));
                await ValidatePresetDialogAsync(directory, checks); success = true; return;
            }
            await StopAssetsAsync(); audioPreparationBarrier = barrier.Task;
            await editor.ExecuteAsync(new { op = "settings", editor = (editor.Snapshot.Editor ?? new()) with { Shadow = 0.3721 } });
            int thumbnailsBefore = thumbnailRenderCount; await ReloadAsync();
            await UntilAsync(() => thumbnailRenderCount > thumbnailsBefore, "Thumbnails did not advance while audio preparation was blocked.");
            if (preparedAudio is not null) throw new InvalidOperationException("Audio barrier was not active.");
            checks.Add("Visible thumbnails render while audio preparation is deliberately held; separate sessions/cancellation paths.");
            barrier.SetResult(); audioPreparationBarrier = null;
            await UntilAsync(() => preparedAudio is not null, "Audio preparation did not complete.");
            string originalAudio = preparedAudio!; int prepared = audioPreparationCount;
            await PerformAsync(() => editor.ExecuteAsync(new { op = "settings", editor = (editor.Snapshot.Editor ?? new()) with { Shadow = 0.74 } }));
            if (preparedAudio != originalAudio || audioPreparationCount != prepared) throw new InvalidOperationException("Picture edit rebuilt audio.");
            await PerformAsync(() => editor.ExecuteAsync(new { op = "settings", editor = (editor.Snapshot.Editor ?? new()) with { SystemGain = 0.43 } }));
            await UntilAsync(() => preparedAudio is not null && preparedAudio != originalAudio, "Gain edit did not prepare new audio.");
            int afterGain = audioPreparationCount; await PerformAsync(editor.UndoAsync);
            await UntilAsync(() => preparedAudio == originalAudio, "Undo did not reuse prior audio content.");
            if (audioPreparationCount != afterGain) throw new InvalidOperationException("Undo remixed a reusable audio cache.");
            checks.Add("Picture-only edit preserves audio path; gain change invalidates it; undo reuses prior mix without recomputing.");
            await Task.Delay(700);
            string cacheDirectory = Path.Combine(editor.DirectoryPath, "cache");
            var pngs = Directory.GetFiles(cacheDirectory, "editor-v2-*.png");
            if (pngs.Length < 3) throw new InvalidOperationException("Filmstrip did not request multiple samples.");
            foreach (var file in pngs) { var bytes = await File.ReadAllBytesAsync(file); if (bytes.Length < 24 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16,4)) > 384) throw new InvalidOperationException("Thumbnail resolution is unbounded."); }
            checks.Add("Continuous filmstrip requests distinct time samples and limits thumbnail width to 384 pixels.");
            var identity = preview.Session; var baseSettings = editor.Snapshot.Editor ?? new();
            var cases = new (string Name, EditorSettings Settings, long Time)[] {
                ("recorded", baseSettings with { CursorSmoothing = 0, CursorStyle = "recorded" }, 6_000_000),
                ("dot", baseSettings with { CursorSmoothing = 0, CursorStyle = "dot" }, 6_000_000),
                ("crosshair", baseSettings with { CursorSmoothing = 0, CursorStyle = "crosshair" }, 6_000_000),
                ("ripple", baseSettings with { CursorSmoothing = 0, CursorClickStyle = "ripple" }, 6_000_000),
                ("highlight", baseSettings with { CursorSmoothing = 0, CursorClickStyle = "highlight" }, 6_000_000),
                ("idle", baseSettings with { CursorSmoothing = 0, CursorHideWhenIdle = true, CursorIdleDelay = 0.2 }, 18_000_000),
                ("hidden", baseSettings with { CursorScale = 0 }, 18_000_000),
                ("offset", baseSettings with { CursorSmoothing = 0, CursorTimeOffset = 0.5 }, 28_000_000),
                ("offset-base", baseSettings with { CursorSmoothing = 0 }, 28_000_000)
            };
            var renders = new List<object>();
            foreach (var item in cases) {
                await PerformAsync(() => editor.ExecuteAsync(new { op = "settings", editor = item.Settings }));
                if (!ReferenceEquals(identity, preview.Session)) throw new InvalidOperationException("Cursor edit recreated attached preview.");
                using var media = new MediaSession(editor.Snapshot, editor.DirectoryPath);
                string png = Path.Combine(directory, item.Name + ".png"), mp4 = Path.Combine(directory, item.Name + ".mp4");
                await Task.Run(() => { media.Render(item.Time, png: png); media.Export(mp4); });
                renders.Add(new { name = item.Name, ticks = item.Time });
            }
            checks.Add("All cursor controls hot-update the attached session and render/export on the shared compositor.");
            await File.WriteAllTextAsync(Path.Combine(directory,"cursor-cases.json"), JsonSerializer.Serialize(renders));
            presetLibrary = new VisualPresetLibrary(Path.Combine(directory, "preset-library"));
            var saved = await presetLibrary.SaveAsync("跨工程预设", editor.Snapshot, editor.DirectoryPath);
            await presetLibrary.RenameAsync(saved.Id, "课程演示"); await presetLibrary.SetDefaultAsync(saved.Id);
            var catalog = await presetLibrary.ListAsync(); if (catalog.DefaultId != saved.Id || catalog.Presets.Single().Name != "课程演示") throw new InvalidOperationException("Preset management failed.");
            await PerformAsync(() => editor.ApplyPresetAsync("clean"));
            await PerformAsync(() => editor.ApplyLibraryPresetAsync(presetLibrary, saved.Id));
            await PerformAsync(editor.UndoAsync); await PerformAsync(editor.RedoAsync);
            checks.Add("Named presets save/rename/default/apply through an undoable command; isolated test library leaves user presets untouched.");
            await ValidatePresetDialogAsync(directory, checks);
            ShowInspector(3); Root.UpdateLayout(); await SaveUiImageAsync(Root, Path.Combine(directory,"cursor-controls.png"));
            success = true;
        } catch (Exception error) { failure = error.ToString(); }
        finally {
            barrier.TrySetResult(); audioPreparationBarrier = null;
            await File.WriteAllTextAsync(Path.Combine(directory,"editor-polish-validation.json"), JsonSerializer.Serialize(new { success, checks, failure, audioPreparationCount, thumbnailRenderCount, timestampUtc = DateTimeOffset.UtcNow }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = success ? 0 : 1; working = false; closing = false; await RequestCloseAsync();
        }
    }
    private async Task ValidatePresetDialogAsync(string directory, List<string> checks)
    {
        var originalSize = AppWindow.Size; var theme = Root.RequestedTheme;
        var showing = ManagePresetsAsync();
        try {
            await UntilAsync(() => activePresetDialog?.IsLoaded == true, "Preset dialog did not load.");
            var dialog = activePresetDialog!;
            IEnumerable<DependencyObject> Walk(DependencyObject element) {
                yield return element;
                for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element); i++)
                    foreach (var child in Walk(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, i))) yield return child;
            }
            T Control<T>(string id) where T : DependencyObject => Walk(dialog).OfType<T>().Single(c => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(c) == id);
            void Invoke(string id) {
                var button = Control<Microsoft.UI.Xaml.Controls.Button>(id);
                var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(button);
                ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
            }
            var list = Control<Microsoft.UI.Xaml.Controls.ListView>("editor.presets.list");
            var name = Control<Microsoft.UI.Xaml.Controls.TextBox>("editor.presets.name");
            int count = list.Items.Count; name.Text = "应用内验证预设"; Invoke("editor.presets.save");
            await UntilAsync(() => list.Items.Count == count + 1 && dialog.IsPrimaryButtonEnabled, "Preset save button did not update list.");
            name.Text = "重命名后的预设"; Invoke("editor.presets.rename");
            await UntilAsync(() => list.SelectedItem is VisualPreset p && p.Name == "重命名后的预设" && dialog.IsPrimaryButtonEnabled, "Preset rename button did not update selection.");
            foreach (bool compact in new[] { false, true }) {
                Root.RequestedTheme = compact ? ElementTheme.Dark : ElementTheme.Light; dialog.RequestedTheme = Root.RequestedTheme;
                if (compact) { double dpi = RecorderWindowChrome.Scale(this); AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(840 * dpi), (int)(600 * dpi))); }
                await Task.Delay(180); Root.UpdateLayout();
                if (dialog.ActualWidth < 350 || name.ActualWidth < 200) throw new InvalidOperationException("Preset dialog clipped.");
                await SaveUiImageAsync(dialog, Path.Combine(directory, compact ? "presets-compact-dark.png" : "presets-light.png"));
            }
            Invoke("editor.presets.delete"); await UntilAsync(() => list.Items.Count == count, "Preset delete button did not update list.");
            checks.Add("Real ContentDialog save/rename/delete controls invoked through WinUI peers; light and compact dark layouts rendered.");
        } finally { activePresetDialog?.Hide(); await showing; AppWindow.Resize(originalSize); Root.RequestedTheme = theme; }
    }
}
