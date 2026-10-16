using FrameTrail.Interop;
using FrameTrail.Domain;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FrameTrail.App;

// DogSC ExportSheet: 820 DIP, specification column and a separate media summary, flat 18 DIP tiles.
internal sealed class EditorExportDialog : ContentDialog
{
    private readonly Func<int, int, int, string, IProgress<MediaStatus>, CancellationToken, Task<string?>> export;
    private readonly TextBlock status = Text("选择保存位置后开始导出。", true);
    private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed, Height = 4 };
    private readonly StackPanel specifications = new() { Spacing = 10 };
    private readonly Grid choices = new() { ColumnSpacing = 4 };
    private readonly TextBlock formatSummary = Text("MP4"), sizeSummary = Text("1920 × 1080"), fpsSummary = Text("30 fps");
    private readonly ComboBox resolution = new() { Style = LookupStyle("EditorCombo"), SelectedIndex = 0 }, fps = new() { Style = LookupStyle("EditorCombo"), SelectedIndex = 0 };
    private readonly ComboBox codec = new() { Style = LookupStyle("EditorCombo"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly uint canvasWidth, canvasHeight;
    private int kind;
    private bool busy, complete;
    private CancellationTokenSource cancellation = new();
    private readonly Button exportButton = new() { Content = "选择位置并导出", Style = LookupStyle("EditorPrimaryButton") };
    private readonly Button cancelButton = new() { Content = "取消", Style = LookupStyle("EditorButton") };
    internal EditorExportDialog(string name, long duration, uint width, uint height, ImageSource? thumbnail, Func<int, int, int, string, IProgress<MediaStatus>, CancellationToken, Task<string?>> export,
        IReadOnlyList<VideoCodecCapabilities>? codecs = null, string videoCodec = VideoCodecs.H264)
    {
        this.export = export; canvasWidth = width; canvasHeight = height;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, "导出作品");
        CornerRadius = new CornerRadius(20);
        SizeChanged += (_, _) => FitDialogWidth();
        Resources["ContentDialogMaxWidth"] = 820d; Resources["ContentDialogMinWidth"] = 600d;
        Resources["ContentDialogCornerRadius"] = new CornerRadius(20);
        // Let the dialog template constrain its content after padding and borders.
        // An explicit Width can extend beyond the ContentScrollViewer's clip.
        var body = new StackPanel { Spacing = 16, MaxWidth = 772, HorizontalAlignment = HorizontalAlignment.Stretch };
        var heading = new Grid { ColumnSpacing = 10 }; heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Text("导出作品"); title.FontSize = 17; title.FontFamily = (FontFamily)Application.Current.Resources["RecorderSemiboldFont"]; title.VerticalAlignment = VerticalAlignment.Center; heading.Children.Add(title);
        var projectName = Text(name, true); projectName.VerticalAlignment = VerticalAlignment.Center; projectName.TextTrimming = TextTrimming.CharacterEllipsis; Grid.SetColumn(projectName, 1); heading.Children.Add(projectName);
        var dismiss = new Button { Style = LookupStyle("EditorIconButton"), Content = new EditorGlyph { Kind = "Close", Width = 12, Height = 12 } }; Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dismiss, "关闭导出面板"); dismiss.Click += (_, _) => { if (!busy) Hide(); }; Grid.SetColumn(dismiss, 2); heading.Children.Add(dismiss); body.Children.Add(heading);
        var columns = new Grid { ColumnSpacing = 16 }; columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.51, GridUnitType.Star) }); columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.49, GridUnitType.Star) });
        var left = new StackPanel { Spacing = 16 };
        left.Children.Add(Caption("文件"));
        var file = new StackPanel { Spacing = 8 }; file.Children.Add(Text(name)); file.Children.Add(Text("在下一步选择文件名与保存位置。", true)); left.Children.Add(Tile(file));
        left.Children.Add(Caption("导出内容"));
        var labels = new[] { "视频成片", "仅音频", "工程副本" };
        for (int i = 0; i < labels.Length; i++) { int index = i; choices.ColumnDefinitions.Add(new ColumnDefinition()); var button = new Button { Content = labels[i], Style = LookupStyle("EditorButton"), HorizontalAlignment = HorizontalAlignment.Stretch, CornerRadius = new CornerRadius(15) }; button.Click += (_, _) => SelectKind(index); Grid.SetColumn(button, i); choices.Children.Add(button); }
        left.Children.Add(new Border { Style = LookupStyle("EditorTile"), Padding = new Thickness(3), Child = choices });
        foreach (string value in new[] { "1080p", "1440p", "4K" }) resolution.Items.Add(value);
        fps.Items.Add("30 fps"); fps.Items.Add("60 fps");
        VideoCodecSelector.Populate(codec, codecs ?? CaptureSession.GetVideoCodecs(), false, videoCodec);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(codec, "导出视频编码");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(codec, "export.videoCodec");
        specifications.Children.Add(Text("视频编码", true)); specifications.Children.Add(codec);
        specifications.Children.Add(Text("分辨率", true)); specifications.Children.Add(resolution); specifications.Children.Add(Text("帧率", true)); specifications.Children.Add(fps);
        left.Children.Add(Tile(specifications)); columns.Children.Add(left);
        var summary = new StackPanel { Spacing = 12 };
        summary.Children.Add(new Border { CornerRadius = new CornerRadius(10), Height = 164, Background = new SolidColorBrush(Colors.Black), Child = new Image { Source = thumbnail, Stretch = Stretch.Uniform } });
        summary.Children.Add(Row("格式", formatSummary)); summary.Children.Add(Row("分辨率", sizeSummary)); summary.Children.Add(Row("帧率", fpsSummary)); summary.Children.Add(Row("时长", Text(TimeSpan.FromTicks(duration).ToString(@"mm\:ss\.ff"))));
        var card = Tile(summary); card.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(card, 1); columns.Children.Add(card);
        var scroll = new ScrollViewer { Content = columns, MaxHeight = 430, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; body.Children.Add(scroll); body.Children.Add(progress); status.TextWrapping = TextWrapping.Wrap; body.Children.Add(status);
        var footer = new Grid { ColumnSpacing = 12 };
        footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(cancelButton, 1); Grid.SetColumn(exportButton, 2); footer.Children.Add(cancelButton); footer.Children.Add(exportButton); body.Children.Add(footer); Content = body;
        resolution.SelectionChanged += (_, _) => UpdateSummary(); fps.SelectionChanged += (_, _) => UpdateSummary(); codec.SelectionChanged += (_, _) => UpdateSummary(); SelectKind(0);
        Opened += (_, _) => { scroll.MaxHeight = Math.Max(180, Math.Min(430, XamlRoot.Size.Height - 280)); SelectKind(kind); };
        exportButton.Click += Start;
        cancelButton.Click += (_, _) => { if (busy) { cancellation.Cancel(); status.Text = "正在取消导出…"; } else Hide(); };
        Closing += (_, args) => { if (busy) args.Cancel = true; };
        Closed += (_, _) => cancellation.Dispose();
    }
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate(); FitDialogWidth();
    }
    private void FitDialogWidth()
    {
        // Keep the intended 820 DIP sheet while allowing the content to measure
        // inside the template's actual padding and shrink with the host window.
        if (XamlRoot is not null && GetTemplateChild("BackgroundElement") is FrameworkElement surface)
            surface.MinWidth = Math.Min(820, Math.Max(320, XamlRoot.Size.Width - 96));
    }
    private void SelectKind(int value)
    {
        kind = value;
        foreach (var button in choices.Children.OfType<Button>()) { bool selected = Grid.GetColumn(button) == value; button.Background = selected ? (Brush)Application.Current.Resources["EditorBlue"] : new SolidColorBrush(Colors.Transparent); button.Foreground = selected ? (Brush)Application.Current.Resources["EditorBlueInk"] : RecorderTheme.Brush(this, "EditorSecondary"); }
        specifications.Visibility = value == 0 ? Visibility.Visible : Visibility.Collapsed; UpdateSummary();
    }
    private void UpdateSummary()
    {
        formatSummary.Text = kind switch { 1 => "M4A · AAC", 2 => "工程与原始素材", _ => "MP4 · " + VideoCodecs.DisplayName(VideoCodecSelector.Selected(codec)) + " · AAC" };
        exportButton.IsEnabled = kind != 0 || codec.SelectedItem is not null;
        uint h = (uint)new[] { 1080, 1440, 2160 }[Math.Max(0, resolution.SelectedIndex)];
        sizeSummary.Text = kind == 0 ? $"{Math.Round(h * (double)canvasWidth / canvasHeight / 2) * 2:0} × {h}" : "—";
        fpsSummary.Text = kind == 0 ? fps.SelectedItem?.ToString() ?? "30 fps" : "—";
    }
    private async void Start(object sender, RoutedEventArgs args)
    {
        if (complete) { Hide(); return; }
        if (busy || kind == 0 && codec.SelectedItem is null) return; busy = true;
        exportButton.IsEnabled = false; cancelButton.Content = "取消导出"; choices.IsHitTestVisible = specifications.IsHitTestVisible = false;
        cancellation.Dispose(); cancellation = new(); progress.Visibility = Visibility.Visible; progress.IsIndeterminate = true; status.Text = "请选择保存位置…";
        try {
            var path = await export(kind, resolution.SelectedIndex, fps.SelectedIndex, VideoCodecSelector.Selected(codec), new Progress<MediaStatus>(s => {
                progress.IsIndeterminate = s.Total == 0; if (s.Total > 0) progress.Value = s.Completed * 100.0 / s.Total;
                status.Text = (s.Stage switch { 1 => "准备声音", 2 => "编码", 3 => "校验文件", 4 => "已完成", _ => "准备媒体" }) + (s.Total > 0 ? $" · {s.Completed * 100.0 / s.Total:0}%" : "…");
            }), cancellation.Token);
            if (path is not null) { complete = true; status.Text = "导出完成 · " + path; exportButton.Content = "完成"; cancelButton.Content = "关闭"; progress.IsIndeterminate = false; progress.Value = 100; }
            else { status.Text = "尚未选择保存位置。"; progress.Visibility = Visibility.Collapsed; }
        } catch (OperationCanceledException) { status.Text = "导出已取消。"; progress.Visibility = Visibility.Collapsed; }
        catch (Exception error) { status.Text = "导出未完成 · " + error.Message; progress.Visibility = Visibility.Collapsed; }
        finally { busy = false; choices.IsHitTestVisible = specifications.IsHitTestVisible = !complete; exportButton.IsEnabled = complete || kind != 0 || codec.SelectedItem is not null; if (!complete) cancelButton.Content = "取消"; }
    }
    internal async Task ValidateCodecChoicesAsync(string directory)
    {
        if (codec.Items.Count != 3) throw new IOException("Export does not expose all three codecs.");
        foreach (var item in codec.Items.OfType<ComboBoxItem>().Where(i => i.IsEnabled)) {
            codec.SelectedItem = item;
            if (!formatSummary.Text.Contains(VideoCodecs.DisplayName((string)item.Tag), StringComparison.Ordinal)) throw new IOException("Export summary ignored codec choice.");
            await Task.Delay(80); await MainWindow.SaveUiFrameAsync(this, Path.Combine(directory, "export-codec-" + item.Tag + ".png"));
        }
    }
    internal async Task ValidateActionLayoutAsync(string path)
    {
        void CheckBounds()
        {
            UpdateLayout();
            foreach (var button in new[] { cancelButton, exportButton }) {
                if (button.ActualWidth <= 0 || button.ActualHeight <= 0) throw new IOException("Export action has no visible bounds.");
                for (var ancestor = VisualTreeHelper.GetParent(button); ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor)) {
                    if (ancestor is FrameworkElement frame) {
                        var bounds = button.TransformToVisual(frame).TransformBounds(new Windows.Foundation.Rect(0, 0, button.ActualWidth, button.ActualHeight));
                        if (bounds.Left < -0.5 || bounds.Right > frame.ActualWidth + 0.5 || bounds.Top < -0.5 || bounds.Bottom > frame.ActualHeight + 0.5)
                            throw new IOException($"Export action '{button.Content}' clips inside {frame.GetType().Name}/{frame.Name}: {bounds} versus {frame.ActualWidth}x{frame.ActualHeight}.");
                        if (frame.Name == "ContentScrollViewer") break;
                    }
                }
            }
        }
        CheckBounds(); await MainWindow.SaveUiFrameAsync(this, path + "-ready.png");
        var originalContent = cancelButton.Content; bool enabled = exportButton.IsEnabled;
        try {
            cancelButton.Content = "取消导出"; exportButton.IsEnabled = false;
            CheckBounds(); await MainWindow.SaveUiFrameAsync(this, path + "-busy.png");
        } finally { cancelButton.Content = originalContent; exportButton.IsEnabled = enabled; }
    }
    private static Style LookupStyle(string name) => (Style)Application.Current.Resources[name];
    private static TextBlock Text(string text, bool secondary = false) => new() { Text = text, Style = LookupStyle(secondary ? "EditorCaption" : "EditorText") };
    private static TextBlock Caption(string text) { var value = Text(text, true); value.Margin = new Thickness(4, 0, 0, -8); return value; }
    private static Border Tile(UIElement child) => new() { Style = LookupStyle("EditorTile"), Child = child };
    private static Grid Row(string name, TextBlock value) { var row = new Grid { Height = 30 }; row.Children.Add(Text(name, true)); value.HorizontalAlignment = HorizontalAlignment.Right; row.Children.Add(value); return row; }
}
