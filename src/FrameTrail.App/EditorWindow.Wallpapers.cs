using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    private readonly Dictionary<BuiltInWallpaper, ToggleButton> wallpaperButtons = new();

    private void InitializeWallpapers()
    {
        foreach (var wallpaper in BuiltInWallpapers.All) {
            int index = wallpaperButtons.Count;
            if (index % 3 == 0) WallpaperGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // Decode thumbnails at display size; the renderer uses the untouched high-resolution JPEG.
            var image = new Image { Source = new BitmapImage(new Uri(wallpaper.SourcePath)) { DecodePixelWidth = 240 }, Stretch = Stretch.UniformToFill };
            var button = new ToggleButton {
                Style = (Style)Application.Current.Resources["EditorWallpaperButton"],
                Content = new Border { Child = image, CornerRadius = new CornerRadius(8), Height = 58 },
                Padding = new Thickness(2), Height = 66, MinWidth = 0, BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                CornerRadius = new CornerRadius(12), Tag = wallpaper
            };
            AutomationProperties.SetName(button, wallpaper.Label);
            AutomationProperties.SetAutomationId(button, "editor.background.wallpaper." + wallpaper.Number);
            ToolTipService.SetToolTip(button, wallpaper.Label);
            button.Click += async (_, _) => {
                RefreshWallpaperSelection();
                await PerformAsync(() => ApplyWallpaperAsync(wallpaper));
                RefreshWallpaperSelection();
            };
            Grid.SetColumn(button, index % 3); Grid.SetRow(button, index / 3);
            WallpaperGrid.Children.Add(button); wallpaperButtons.Add(wallpaper, button);
        }
        ImportWallpaperButton.Click += async (_, _) => await PerformAsync(async () => {
            var path = await PickVisualAsync(false); if (path is not null) await SetBackdropAsync("image", path);
        });
        ImportBackgroundVideoButton.Click += async (_, _) => await PerformAsync(async () => {
            var path = await PickVisualAsync(true); if (path is not null) await SetBackdropAsync("video", path);
        });
    }

    private async Task ApplyWallpaperAsync(BuiltInWallpaper wallpaper)
    {
        if (editor.Snapshot.Editor?.Backdrop is { Kind: "image" } current && current.Path == wallpaper.ProjectPath) return;
        string path = await editor.ImportWallpaperAsync(wallpaper.SourcePath);
        await SetBackdropAsync("image", path);
    }

    private void RefreshWallpaperSelection()
    {
        var backdrop = editor.Snapshot.Editor?.Backdrop;
        foreach (var (wallpaper, button) in wallpaperButtons) {
            bool selected = backdrop is { Kind: "image" } && backdrop.Path == wallpaper.ProjectPath;
            button.IsChecked = selected;
            button.BorderBrush = selected ? (Brush)Application.Current.Resources["EditorBlue"] : new SolidColorBrush(Colors.Transparent);
            button.Background = RecorderTheme.Brush(Root, "EditorWell");
        }
    }
}
