using FrameTrail.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;

namespace FrameTrail.App;

internal sealed class RecordingCompletionWindow : Window
{
    private readonly Grid root;
    private readonly Image preview = new() { Stretch = Stretch.UniformToFill };
    private readonly TextBlock notice = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly TextBlock fallback = new() { Text = L.T("SB8B39D354A52"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Action finished;
    private bool closed;

    internal RecordingCompletionWindow(string directory, ProjectSnapshot snapshot, Action finished, Action? edit = null)
    {
        this.finished = finished; Title = L.T("SAFEC21C58586");
        var column = new StackPanel { Padding = new Thickness(8), Width = 336 };
        root = new Grid { CornerRadius = new CornerRadius(28), RequestedTheme = EditorAppearance.Current }; root.Children.Add(column); Content = root;
        void SetAppearance(ElementTheme theme) => root.RequestedTheme = theme;
        EditorAppearance.Changed += SetAppearance; Closed += (_, _) => EditorAppearance.Changed -= SetAppearance;
        var media = new Grid { Width = 320, Height = 180, CornerRadius = new CornerRadius(20) };
        media.Children.Add(preview); media.Children.Add(fallback);
        var duration = TimeSpan.FromTicks(snapshot.SourceDuration.Ticks);
        var screen = snapshot.Media.FirstOrDefault(m => m.Kind == MediaKind.Screen);
        var metadata = new TextBlock { Text = $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}" + (screen is null ? "" : $" · {screen.Width} × {screen.Height}"), FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var badge = new Border { Child = metadata, CornerRadius = new CornerRadius(12), Padding = new Thickness(8, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10) };
        media.Children.Add(badge);
        var close = new Button { Content = new RecorderGlyph { Kind = "Close", Width = 10, Height = 10 }, Width = 26, Height = 26, Margin = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Style = (Style)Application.Current.Resources["RecorderIconButton"] };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, L.T("SDE7B10E3CDEF")); close.Click += (_, _) => Finish(); media.Children.Add(close); column.Children.Add(media);
        var title = new TextBlock { Text = Path.GetFileNameWithoutExtension(directory), FontFamily = (FontFamily)Application.Current.Resources["RecorderSemiboldFont"], FontSize = 14, MaxWidth = 270, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(10, 14, 10, 0) };
        var check = new RecorderGlyph { Kind = "Check", Width = 14, Height = 14 }; titleRow.Children.Add(check); titleRow.Children.Add(title); column.Children.Add(titleRow);
        var saved = new TextBlock { Text = L.T("S09AC85DD9C11", snapshot.Segments.Count, snapshot.Markers.Count), FontSize = 11, Margin = new Thickness(10, 6, 10, 0), Opacity = 0.56 }; column.Children.Add(saved);
        notice.Margin = new Thickness(10, 8, 10, 0); column.Children.Add(notice);
        var buttons = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 14, 0, 0), Height = 44 };
        buttons.ColumnDefinitions.Add(new ColumnDefinition()); buttons.ColumnDefinitions.Add(new ColumnDefinition());
        var again = new Button { Content = L.T("S377F85C59DC4"), Style = (Style)Application.Current.Resources["RecorderPillButton"], HorizontalAlignment = HorizontalAlignment.Stretch };
        again.Click += (_, _) => Finish(); buttons.Children.Add(again);
        var open = new Button { Content = edit is null ? L.T("S13B1B6FE3F5C") : L.T("SE0B7815E2DB5"), Style = (Style)Application.Current.Resources["RecorderPillButton"], HorizontalAlignment = HorizontalAlignment.Stretch };
        open.Click += (_, _) => { try { if (edit is not null) edit(); else Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true }); } catch (Exception error) { SetNotice(error.Message); } };
        Grid.SetColumn(open, 1); buttons.Children.Add(open); column.Children.Add(buttons);
        void Theme()
        {
            root.Background = RecorderTheme.Brush(root, "RecorderBase"); media.Background = RecorderTheme.Brush(root, "RecorderSilver");
            title.Foreground = metadata.Foreground = RecorderTheme.Brush(root, "RecorderInk"); badge.Background = RecorderTheme.Brush(root, "RecorderBase");
            fallback.Foreground = saved.Foreground = notice.Foreground = RecorderTheme.Brush(root, "RecorderMuted");
            open.Background = RecorderTheme.Brush(root, "RecorderPrimary"); open.Foreground = RecorderTheme.Brush(root, "RecorderOnPrimary");
        }
        Theme(); root.ActualThemeChanged += (_, _) => Theme();
        root.Loaded += async (_, _) =>
        {
            if (screen is null) { fallback.Text = L.T("S899E00EE93FA"); return; }
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.Combine(directory, screen.Path.Replace('/', Path.DirectorySeparatorChar)));
                var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);
                var composition = new Windows.Media.Editing.MediaComposition(); composition.Clips.Add(clip);
                using var thumbnail = await composition.GetThumbnailAsync(TimeSpan.Zero, 320, 180, Windows.Media.Editing.VideoFramePrecision.NearestFrame);
                if (closed) return;
                var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(thumbnail); preview.Source = bitmap; fallback.Visibility = Visibility.Collapsed;
            }
            catch { if (!closed) fallback.Text = L.T("S17FB8B89658E"); }
        };
        Closed += (_, _) => closed = true;
        RecorderWindowChrome.Configure(this, 336, 316, 28);
        var work = RecorderWindowChrome.WorkArea(this); var scale = RecorderWindowChrome.Scale(this);
        AppWindow.Move(new Windows.Graphics.PointInt32(work.Right - AppWindow.Size.Width - (int)(28 * scale), work.Bottom - AppWindow.Size.Height - (int)(28 * scale)));
        try { FrameTrail.Interop.RecordingUtilities.ExcludeWindow((nuint)WinRT.Interop.WindowNative.GetWindowHandle(this), true); } catch { }
    }
    internal void SetNotice(string text)
    {
        notice.Text = text; notice.Visibility = Visibility.Visible;
        notice.MaxHeight = 72;
        RecorderWindowChrome.Resize(this, 336, 384, 28);
    }
    private void Finish() { Close(); finished(); }
}
