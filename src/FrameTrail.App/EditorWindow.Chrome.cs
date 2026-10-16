using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    private bool loadingInspector;
    private int inspectorTab;
    private bool layoutPage;
    private bool mockupPage;
    private EditorSettingsWindow? settingsWindow;
    private readonly HashSet<NumberBox> changedNumbers = new();
    private readonly HashSet<NumberBox> pictureNumbers = new();
    private bool pictureInputCommitQueued, flushingPictureInputs;
    private readonly string[] tabLabels = ["画面", "开场", "运镜", "光标", "摄像头", "声音"];
    private readonly string[] tabGlyphs = ["Scene", "Opening", "Zoom", "Cursor", "Camera", "Speaker"];
    private static SolidColorBrush Hex(uint n) => new(Windows.UI.Color.FromArgb(255, (byte)(n >> 16), (byte)(n >> 8), (byte)n));
    private void InitializeChrome()
    {
        AppIcon.Apply(this);
        ExtendsContentIntoTitleBar = true; SetTitleBar(TitleDragRegion); AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        Root.RequestedTheme = EditorAppearance.Current;
        UpdateCaptionTheme();
        Root.ActualThemeChanged += (_, _) => { UpdateCaptionTheme(); DrawInspectorTabs(); UpdateFrameNavigation(); if (ready) UpdateBackdropTabs(); };
        foreach (var number in new[] { CanvasWidth, CanvasHeight, CropX, CropY, CropWidth, CropHeight, CameraX, CameraY }) {
            pictureNumbers.Add(number);
            number.ValueChanged += (sender, _) => { if (!loadingInspector && ready) changedNumbers.Add(sender); };
            number.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(PictureInput_KeyDown), true);
        }
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(ReleasePictureInputFocus), true);
        Root.SizeChanged += (_, _) => {
            ScenePresetButton.Visibility = Root.ActualWidth < 1120 ? Visibility.Collapsed : Visibility.Visible;
            ProjectTitle.MaxWidth = Root.ActualWidth < 1050 ? 125 : 220;
            TimelineRow.Height = new GridLength(Math.Clamp(Root.ActualHeight * 0.25, 190, 260));
        };
        ShowInspector(0); SetFramePage(false);
    }
    private void UpdateCaptionTheme()
    {
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = dark ? Colors.Gray : Colors.DarkGray;
    }
    private void DrawInspectorTabs()
    {
        InspectorTabs.Children.Clear();
        for (int i = 0; i < tabLabels.Length; i++) {
            int index = i; bool active = inspectorTab == i;
            var ink = active ? (Brush)Application.Current.Resources["EditorBlueInk"] : RecorderTheme.Brush(Root, "EditorSecondary");
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            content.Children.Add(new EditorGlyph { Kind = tabGlyphs[i], Width = 17, Height = 17, Foreground = ink });
            if (active) content.Children.Add(new TextBlock { Text = tabLabels[i], FontFamily = (FontFamily)Application.Current.Resources["RecorderSemiboldFont"], FontSize = 13, Foreground = ink });
            var button = new Button { Style = (Style)Application.Current.Resources["EditorButton"], Content = content, MinWidth = 44, Height = 44, Padding = active ? new Thickness(14, 0, 16, 0) : new Thickness(0), CornerRadius = new CornerRadius(22), Background = active ? (Brush)Application.Current.Resources["EditorBlue"] : new SolidColorBrush(Colors.Transparent) };
            bool available = i != 4 || editor.Snapshot.Media.Any(m => m.Kind == FrameTrail.Domain.MediaKind.Camera);
            button.IsEnabled = available; ToolTipService.SetToolTip(button, available ? tabLabels[i] : "当前项目没有摄像头素材");
            AutomationProperties.SetName(button, tabLabels[i]); AutomationProperties.SetAutomationId(button, "editor.inspector." + tabGlyphs[i].ToLowerInvariant());
            button.Click += (_, _) => ShowInspector(index); InspectorTabs.Children.Add(button);
        }
    }
    private void ShowInspector(int tab)
    {
        inspectorTab = tab;
        PictureSettings.Visibility = tab == 0 ? Visibility.Visible : Visibility.Collapsed;
        FrameNavigation.Visibility = tab == 0 ? Visibility.Visible : Visibility.Collapsed;
        AudioSettings.Visibility = tab == 5 ? Visibility.Visible : Visibility.Collapsed;
        CameraSettings.Visibility = tab == 4 ? Visibility.Visible : Visibility.Collapsed;
        CursorSettings.Visibility = tab == 3 ? Visibility.Visible : Visibility.Collapsed;
        ClipSettings.Visibility = tab == 6 ? Visibility.Visible : Visibility.Collapsed;
        UnavailableSettings.Visibility = Visibility.Collapsed;
        openingSettings.Visibility = tab == 1 ? Visibility.Visible : Visibility.Collapsed;
        motionSettings.Visibility = tab == 2 ? Visibility.Visible : Visibility.Collapsed;
        InspectorHeading.Text = tab == 6 ? "片段" : tabLabels[tab];
        InspectorScope.Text = tab == 6 && SelectedClip is { } c ? $"第 {editor.Timeline.Clips.IndexOf(c) + 1} 段" : tab == 4 ? "全片基础布局" : "全片";
        InspectorScroll.ChangeView(null, 0, null, true); DrawInspectorTabs();
    }
    private void SetFramePage(bool layout)
    {
        layoutPage = layout; mockupPage = false; UpdateFrameNavigation();
        InspectorScroll.ChangeView(null, 0, null, true);
    }
    private void UpdateFrameNavigation()
    {
        BackgroundSettings.Visibility = !layoutPage && !mockupPage ? Visibility.Visible : Visibility.Collapsed;
        LayoutSettings.Visibility = layoutPage && !mockupPage ? Visibility.Visible : Visibility.Collapsed;
        mockupSettings.Visibility = mockupPage ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { BackgroundTab, LayoutTab, MockupTab }) {
            bool active = button == MockupTab ? mockupPage : !mockupPage && (button == LayoutTab ? layoutPage : !layoutPage);
            button.Background = active ? (Brush)Application.Current.Resources["EditorBlue"] : new SolidColorBrush(Colors.Transparent);
            var brush = active ? (Brush)Application.Current.Resources["EditorBlueInk"] : RecorderTheme.Brush(Root, "EditorSecondary");
            if (button.Content is StackPanel row) foreach (var child in row.Children) { if (child is TextBlock text) text.Foreground = brush; if (child is EditorGlyph icon) icon.Foreground = brush; }
        }
    }
    private void BackgroundTab_Click(object sender, RoutedEventArgs e) => SetFramePage(false);
    private void LayoutTab_Click(object sender, RoutedEventArgs e) => SetFramePage(true);
    private void RatioHud_Click(object sender, RoutedEventArgs e) { ShowInspector(0); SetFramePage(true); AspectRatio.Focus(FocusState.Programmatic); }
    private void CropHud_Click(object sender, RoutedEventArgs e) { ShowInspector(0); SetFramePage(true); CropEnabled.StartBringIntoView(); CropEnabled.Focus(FocusState.Programmatic); }
    private async Task CommitPictureAsync()
    {
        if (!ready || loadingInspector || working || closing || editor.IsExporting) return;
        changedNumbers.Clear();
        await ApplyPictureAsync();
    }
    private async void PictureBar_Committed(object? sender, EventArgs e) => await CommitPictureAsync();
    private async void PictureToggle_Click(object sender, RoutedEventArgs e) => await CommitPictureAsync();
    private static T? InputAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null) { if (element is T match) return match; element = VisualTreeHelper.GetParent(element); }
        return null;
    }
    private bool IsPictureInputFocused() => Root.XamlRoot is not null &&
        InputAncestor<NumberBox>(FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject) is { } number && pictureNumbers.Contains(number);
    private void PictureInput_LostFocus(object sender, RoutedEventArgs e) => QueuePictureInputCommit();
    private void QueuePictureInputCommit()
    {
        if (pictureInputCommitQueued || closing || flushingPictureInputs) return;
        pictureInputCommitQueued = true;
        // NumberBox validates its text and moves focus asynchronously. In particular,
        // moving X -> Y must not reload Y while the user is starting to type there.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () => {
            pictureInputCommitQueued = false;
            if (flushingPictureInputs || IsPictureInputFocused() || Root.XamlRoot is null) return;
            var focused = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
            if (InputAncestor<EditorBarSlider>(focused)?.IsDragging == true) return;
            if (changedNumbers.Count > 0) await CommitPictureAsync();
        });
    }
    private async Task FlushPictureInputsAsync()
    {
        flushingPictureInputs = true;
        try {
            if (IsPictureInputFocused()) InspectorScroll.Focus(FocusState.Programmatic);
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => settled.SetResult())) await settled.Task;
            if (changedNumbers.Count > 0) await CommitPictureAsync();
        } finally { flushingPictureInputs = false; }
    }
    private void PictureInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        // Run after NumberBox's own Enter validation, even when it handles the key.
        DispatcherQueue.TryEnqueue(() => { InspectorScroll.Focus(FocusState.Programmatic); QueuePictureInputCommit(); });
    }
    private void ReleasePictureInputFocus(object sender, PointerRoutedEventArgs e)
    {
        if (!IsPictureInputFocused()) return;
        var source = e.OriginalSource as DependencyObject;
        if (InputAncestor<NumberBox>(source) is not null || InputAncestor<TextBox>(source) is not null ||
            InputAncestor<Microsoft.UI.Xaml.Controls.Primitives.ButtonBase>(source) is not null ||
            InputAncestor<Slider>(source) is not null || InputAncestor<ComboBox>(source) is not null) return;
        InspectorScroll.Focus(FocusState.Pointer);
    }
    private async void PictureChoice_Changed(object sender, SelectionChangedEventArgs e) => await CommitPictureAsync();
    private void AudioBar_Committed(object? sender, EventArgs e) { if (ready && !loadingInspector && !working) ApplyAudio_Click(this, new RoutedEventArgs()); }
    private void AudioToggle_Click(object sender, RoutedEventArgs e) => AudioBar_Committed(sender, EventArgs.Empty);
    private void RateChoice_Changed(object sender, SelectionChangedEventArgs e) { if (ready && !loadingInspector && !working) ApplyRate_Click(sender, new RoutedEventArgs()); }
    private void UpdateBackgroundChoice()
    {
        uint rgb = (uint)Math.Round(background.R * 255) << 16 | (uint)Math.Round(background.G * 255) << 8 | (uint)Math.Round(background.B * 255);
        foreach (var item in BackgroundColors.Children.OfType<Button>()) {
            bool match = Convert.ToUInt32(((string)item.Tag)[1..], 16) == rgb;
            item.Content = match ? new EditorGlyph { Kind = "Check", Width = 22, Height = 22, Foreground = Hex(rgb > 0xAAAAAA ? 0x1A1A1Au : 0xFFFFFFu) } : null;
            item.BorderThickness = new Thickness(match ? 2 : 0); item.BorderBrush = Hex(0x5AAEFF);
        }
    }
    private void Appearance_Click(object sender, RoutedEventArgs e) { Root.RequestedTheme = Root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark; EditorAppearance.Save(Root.RequestedTheme); UpdateFrameNavigation(); timeline.RefreshAppearance(); }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (settingsWindow is null) { settingsWindow = new EditorSettingsWindow(editor.DirectoryPath, theme => { Root.RequestedTheme = theme; UpdateFrameNavigation(); timeline.RefreshAppearance(); }); settingsWindow.Closed += (_, _) => settingsWindow = null; }
        settingsWindow.Activate();
    }
    private void Snap_Click(object sender, RoutedEventArgs e) { timeline.SnappingEnabled = !timeline.SnappingEnabled; SnapLabel.Text = timeline.SnappingEnabled ? "吸附" : "吸附已关"; SnapButton.Background = timeline.SnappingEnabled ? new SolidColorBrush(Windows.UI.Color.FromArgb(32, 90, 174, 255)) : new SolidColorBrush(Colors.Transparent); }
    private void FilmMode_Click(object sender, RoutedEventArgs e) => SetTimelineMode(false);
    private void WaveMode_Click(object sender, RoutedEventArgs e) => SetTimelineMode(true);
    private void SetTimelineMode(bool waveform) { timeline.WaveformMode = waveform; FilmModeButton.Background = waveform ? new SolidColorBrush(Colors.Transparent) : RecorderTheme.Brush(Root, "EditorThumb"); WaveModeButton.Background = waveform ? RecorderTheme.Brush(Root, "EditorThumb") : new SolidColorBrush(Colors.Transparent); }
    private void FitTimeline_Click(object sender, RoutedEventArgs e) => TimelineZoom.Value = Math.Clamp((TimelineMount.ActualWidth - 32) / Math.Max(0.01, editor.Timeline.Duration / 10_000_000.0), TimelineZoom.Minimum, TimelineZoom.Maximum);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => TimelineZoom.Value /= 1.5;
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => TimelineZoom.Value *= 1.5;
    private void PreviewOptions_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout(); foreach (var (label, index) in new[] { ("适合窗口", 0), ("125%", 1), ("150%", 2), ("200%", 3) }) { int choice = index; var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = CanvasZoom.SelectedIndex == choice }; item.Click += (_, _) => CanvasZoom.SelectedIndex = choice; menu.Items.Add(item); }
        menu.Items.Add(new MenuFlyoutSeparator()); var reload = new MenuFlyoutItem { Text = "重新加载预览" }; reload.Click += ReloadPreview_Click; menu.Items.Add(reload); menu.ShowAt((FrameworkElement)sender);
    }
}

internal static class EditorAppearance
{
    internal static event Action<ElementTheme>? Changed;
    private static readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "appearance.txt");
    internal static ElementTheme Current { get { try { return Enum.TryParse<ElementTheme>(File.ReadAllText(path), out var value) ? value : ElementTheme.Light; } catch { return ElementTheme.Light; } } }
    internal static void Save(ElementTheme value) { try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value.ToString()); } catch (IOException) { } catch (UnauthorizedAccessException) { } Changed?.Invoke(value); }
}
