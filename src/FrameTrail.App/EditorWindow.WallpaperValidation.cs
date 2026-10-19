using System.Security.Cryptography;
using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    // Explicit developer entry point; the supplied project must be a disposable fixture.
    private async Task RunWallpaperValidationAsync(string directory)
    {
        Directory.CreateDirectory(directory); bool success = false; string? failure = null;
        var checks = new List<string>(); var appearance = Root.RequestedTheme;
        try {
            Pause(); ShowInspector(0); SetFramePage(false); await SeekAsync(0);
            long initialRevision = editor.Snapshot.Revision;
            Backdrop_Click(BackgroundSourceTabs.Children.OfType<Microsoft.UI.Xaml.Controls.Button>().First(b => b.Tag as string == "image"), new RoutedEventArgs());
            if (WallpaperPicker.Visibility != Visibility.Visible || editor.Snapshot.Revision != initialRevision) throw new IOException("Opening the gallery changed the project.");
            if (wallpaperButtons.Count != 18) throw new IOException("The bundled gallery is incomplete.");
            foreach (var wallpaper in BuiltInWallpapers.All) {
                await using var input = File.OpenRead(wallpaper.SourcePath);
                if (Convert.ToHexString(await SHA256.HashDataAsync(input)).ToLowerInvariant() != wallpaper.Sha256) throw new IOException("Wallpaper bytes differ from upstream: " + wallpaper.Number);
                await PerformAsync(() => ApplyWallpaperAsync(wallpaper));
                if (editor.Snapshot.Editor?.Backdrop?.Path != wallpaper.ProjectPath || wallpaperButtons.Count(b => b.Value.IsChecked == true) != 1 || wallpaperButtons[wallpaper].IsChecked != true) throw new IOException("Wallpaper selection failed: " + wallpaper.Number);
                await preview.RenderAsync(0, true, Path.Combine(directory, "wallpaper-" + wallpaper.Number + ".png"));
            }
            checks.Add("All 18 unchanged upstream JPEGs import, select and render through the native preview.");
            var selectedWallpaper = BuiltInWallpapers.All[2];
            await PerformAsync(() => ApplyWallpaperAsync(selectedWallpaper));
            int assets = Directory.GetFiles(Path.Combine(editor.DirectoryPath, "assets"), "wallpaper-*.jpg").Length;
            await PerformAsync(() => ApplyWallpaperAsync(selectedWallpaper));
            if (Directory.GetFiles(Path.Combine(editor.DirectoryPath, "assets"), "wallpaper-*.jpg").Length != assets || assets != 18) throw new IOException("Reselecting duplicated a wallpaper asset.");
            await ApplyBackgroundColorAsync("#14161B");
            if (WallpaperPicker.Visibility != Visibility.Collapsed || editor.Snapshot.Editor?.Backdrop?.Kind != "solid") throw new IOException("Solid color failed to replace wallpaper.");
            await PerformAsync(editor.UndoAsync);
            if (WallpaperPicker.Visibility != Visibility.Visible || wallpaperButtons[selectedWallpaper].IsChecked != true) throw new IOException("Undo lost gallery selection.");
            await PerformAsync(editor.RedoAsync); await PerformAsync(editor.UndoAsync);
            checks.Add("Repeated selection reuses the project asset; color replacement and undo/redo restore the selected thumbnail.");
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark }) {
                Root.RequestedTheme = theme; await Task.Delay(200); Root.UpdateLayout();
                await SaveUiImageAsync(Root, Path.Combine(directory, "gallery-" + theme.ToString().ToLowerInvariant() + ".png"));
            }
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(wallpaperButtons[selectedWallpaper]);
            if (peer.GetPattern(PatternInterface.Toggle) is not IToggleProvider toggle || toggle.ToggleState != ToggleState.On) throw new IOException("Wallpaper is not exposed as selected to accessibility.");
            var originalSize = AppWindow.Size; double scale = RecorderWindowChrome.Scale(this);
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(840 * scale), (int)(600 * scale))); await Task.Delay(200); Root.UpdateLayout();
            if (WallpaperGrid.ActualWidth < 200 || wallpaperButtons.Values.Any(b => b.ActualWidth < 50)) throw new IOException("Compact gallery clipped its thumbnails.");
            await SaveUiImageAsync(Root, Path.Combine(directory, "gallery-compact.png")); AppWindow.Resize(originalSize);
            checks.Add("Gallery renders in light/dark and compact windows, with accessible selected state.");
            await editor.FlushAsync();
            string export = Path.Combine(directory, "wallpaper-export.mp4");
            await editor.ExportAsync(export, false, null, CancellationToken.None); RecordingUtilities.ProbeMedia(export, true);
            checks.Add("Selected project wallpaper saves and exports to a decodable MP4.");
            success = true;
        } catch (Exception error) { failure = error.ToString(); }
        finally {
            Root.RequestedTheme = appearance;
            await File.WriteAllTextAsync(Path.Combine(directory, "wallpapers-ui.json"), JsonSerializer.Serialize(new { success, checks, failure }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = success ? 0 : 1; working = false; closing = false; await RequestCloseAsync();
        }
    }
}
