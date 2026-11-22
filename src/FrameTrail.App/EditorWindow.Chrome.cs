using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using FrameTrail.Interop;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    private bool previewMuted;
    private bool loadingInspector;
    private int inspectorTab;
    private bool layoutPage;
    private bool mockupPage;
    private EditorSettingsWindow? settingsWindow;
    private readonly HashSet<NumberBox> changedNumbers = new();
    private readonly HashSet<NumberBox> pictureNumbers = new();
    private bool pictureInputCommitQueued, flushingPictureInputs;
    private readonly string[] tabLabels = [L.T("SD051DF30A636"), L.T("SFD0D4B7258C2"), L.T("SCFCC288E1D3D"), L.T("S47A75CA0B8FE"), L.T("SFAC308FBABB8"), L.T("S473400815C0F"), L.T("SubTabTitle")];
    private readonly string[] tabGlyphs = ["Scene", "Opening", "Zoom", "Cursor", "Camera", "Speaker", "Subtitles"];
    private static SolidColorBrush Hex(uint n) => new(Windows.UI.Color.FromArgb(255, (byte)(n >> 16), (byte)(n >> 8), (byte)n));
    private void InitializeChrome()
    {
        AppIcon.Apply(this);
        ExtendsContentIntoTitleBar = true; SetTitleBar(TitleDragRegion); AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        Root.RequestedTheme = EditorAppearance.Current;
        UpdateCaptionTheme();
        Root.ActualThemeChanged += (_, _) => { UpdateCaptionTheme(); DrawInspectorTabs(); UpdateFrameNavigation(); UpdateSnapButton(); SetTimelineMode(timeline.WaveformMode); UpdateAiCopilotTheme(); UpdateSubtitleTheme(); UpdateTimelineEffectGutter(); if (ready) { UpdateBackdropTabs(); UpdateBackgroundChoice(); } };
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
        ShowInspector(0); SetFramePage(false); UpdateSnapButton(); SetTimelineMode(timeline.WaveformMode);
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
            var ink = active ? RecorderTheme.Brush(Root, "EditorAccentInk") : RecorderTheme.Brush(Root, "EditorSecondary");
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            content.Children.Add(new EditorGlyph { Kind = tabGlyphs[i], Width = 16, Height = 16, Foreground = ink });
            if (active) content.Children.Add(new TextBlock { Text = tabLabels[i], FontFamily = (FontFamily)Application.Current.Resources["RecorderSemiboldFont"], FontSize = 12, Foreground = ink });
            var button = new Button { Style = (Style)Application.Current.Resources["EditorButton"], Content = content, MinWidth = 34, Height = 38, Padding = active ? new Thickness(10, 0, 12, 0) : new Thickness(0), CornerRadius = new CornerRadius(19), Background = active ? RecorderTheme.Brush(Root, "EditorAccent") : new SolidColorBrush(Colors.Transparent) };
            bool available = i != 4 || editor.Snapshot.Media.Any(m => m.Kind == FrameTrail.Domain.MediaKind.Camera);
            button.IsEnabled = available; ToolTipService.SetToolTip(button, available ? tabLabels[i] : L.T("S00C39FE87C1F"));
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
        subtitleSettings.Visibility = tab == 6 ? Visibility.Visible : Visibility.Collapsed;
        ClipSettings.Visibility = tab == 7 ? Visibility.Visible : Visibility.Collapsed;
        UnavailableSettings.Visibility = Visibility.Collapsed;
        openingSettings.Visibility = tab == 1 ? Visibility.Visible : Visibility.Collapsed;
        motionSettings.Visibility = tab == 2 ? Visibility.Visible : Visibility.Collapsed;
        InspectorHeading.Text = tab == 7 ? L.T("S908E56FE5ED8") : tabLabels[tab];
        InspectorScope.Text = tab == 7 && SelectedClip is { } c ? L.T("S85F334543581", editor.Timeline.Clips.IndexOf(c) + 1) : tab == 4 ? L.T("S9B436B8DF494") : L.T("SC78DFFE1E8AB");
        InspectorScroll.ChangeView(null, 0, null, true); DrawInspectorTabs();
        if (tab == 6) RefreshSubtitleInspector();
        timeline.SelectVisual(tab == 2 ? selectedEffect : tab == 6 ? selectedSubtitleId : null);
        QueueCanvasGeometry();
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
            button.Background = active ? RecorderTheme.Brush(Root, "EditorAccent") : new SolidColorBrush(Colors.Transparent);
            var brush = active ? RecorderTheme.Brush(Root, "EditorAccentInk") : RecorderTheme.Brush(Root, "EditorSecondary");
            if (button.Content is StackPanel row) foreach (var child in row.Children) { if (child is TextBlock text) text.Foreground = brush; if (child is EditorGlyph icon) icon.Foreground = brush; }
        }
    }
    private void BackgroundTab_Click(object sender, RoutedEventArgs e) => SetFramePage(false);
    private void LayoutTab_Click(object sender, RoutedEventArgs e) => SetFramePage(true);
    private void RatioHud_Click(object sender, RoutedEventArgs e) { ShowInspector(0); SetFramePage(true); AspectRatio.Focus(FocusState.Programmatic); }
    private async void CropHud_Click(object sender, RoutedEventArgs e) => await SafeAsync(ToggleCropEditingAsync);
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
            item.BorderThickness = new Thickness(match ? 2 : 0); item.BorderBrush = RecorderTheme.Brush(Root, "EditorAccent");
        }
    }
    private void Appearance_Click(object sender, RoutedEventArgs e) { Root.RequestedTheme = Root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark; EditorAppearance.Save(Root.RequestedTheme); UpdateFrameNavigation(); timeline.RefreshAppearance(); }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (settingsWindow is null) { settingsWindow = new EditorSettingsWindow(editor.DirectoryPath, theme => { Root.RequestedTheme = theme; UpdateFrameNavigation(); timeline.RefreshAppearance(); }, mediaCache); settingsWindow.Closed += (_, _) => settingsWindow = null; }
        settingsWindow.Activate();
    }
    private void Snap_Click(object sender, RoutedEventArgs e) { timeline.SnappingEnabled = !timeline.SnappingEnabled; UpdateSnapButton(); }
    private void UpdateSnapButton()
    {
        bool on = timeline.SnappingEnabled;
        SnapLabel.Text = on ? L.T("S98ABD5C8F638") : L.T("S33751F2542A2");
        SnapButton.Background = on ? RecorderTheme.Brush(Root, "EditorAccent") : new SolidColorBrush(Colors.Transparent);
        var ink = on ? RecorderTheme.Brush(Root, "EditorAccentInk") : RecorderTheme.Brush(Root, "EditorSecondary");
        SnapButton.Foreground = SnapGlyph.Foreground = SnapLabel.Foreground = ink;
    }
    private void FilmMode_Click(object sender, RoutedEventArgs e) => SetTimelineMode(false);
    private void WaveMode_Click(object sender, RoutedEventArgs e) => SetTimelineMode(true);
    private void SetTimelineMode(bool waveform)
    {
        timeline.WaveformMode = waveform;
        FilmModeButton.Background = waveform ? new SolidColorBrush(Colors.Transparent) : RecorderTheme.Brush(Root, "EditorThumb");
        WaveModeButton.Background = waveform ? RecorderTheme.Brush(Root, "EditorThumb") : new SolidColorBrush(Colors.Transparent);
        var activeInk = RecorderTheme.Brush(Root, "EditorInk");
        var idleInk = RecorderTheme.Brush(Root, "EditorSecondary");
        void PaintMode(Button button, bool active)
        {
            var ink = active ? activeInk : idleInk;
            button.Foreground = ink;
            if (button.Content is StackPanel row) foreach (var child in row.Children) { if (child is TextBlock text) text.Foreground = ink; else if (child is EditorGlyph glyph) glyph.Foreground = ink; }
        }
        PaintMode(FilmModeButton, !waveform);
        PaintMode(WaveModeButton, waveform);
    }
    private void TimelineGutterHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        TimelineGutterHost.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, Math.Max(0, TimelineGutterHost.ActualWidth), Math.Max(0, TimelineGutterHost.ActualHeight))
        };
        SyncTimelineGutterScroll(timeline.VerticalScrollOffset);
    }
    private void SyncTimelineGutterScroll(double verticalOffset)
    {
        if (TimelineGutterTransform is not null)
            TimelineGutterTransform.Y = -verticalOffset;
    }
    private void UpdateTimelineEffectGutter()
    {
        if (TimelineEffectGutter is null) return;
        SyncTimelineGutterScroll(timeline.VerticalScrollOffset);
        TimelineEffectGutter.Children.Clear();
        if (timeline.EffectLaneRows.Count == 0) return;

        bool dark = Root.ActualTheme == ElementTheme.Dark;
        double maxBottom = 148;
        foreach (var (kind, rowY) in timeline.EffectLaneRows.OrderBy(pair => pair.Value))
        {
            maxBottom = Math.Max(maxBottom, rowY + 24);
            double top = rowY - 148;

            var (glyphKind, bgBrush, fgBrush, tipText, targetTab) = kind switch
            {
                "subtitle" => ("Subtitles", new SolidColorBrush(Windows.UI.Color.FromArgb(36, 14, 165, 233)), Hex(0x0EA5E9u), L.T("SubTabTitle"), 6),
                "transition" => ("Transition", new SolidColorBrush(Windows.UI.Color.FromArgb(40, 168, 85, 247)), Hex(0xA855F7u), L.T("TransitionName"), 2),
                "zoom" => ("Zoom", RecorderTheme.Brush(Root, "EditorAccentSoft"), RecorderTheme.Brush(Root, "EditorAccent"), L.T("SB762AEC769A3"), 2),
                "focus" => ("Sparkle", new SolidColorBrush(Windows.UI.Color.FromArgb(36, 255, 184, 48)), Hex(dark ? 0xFFB830u : 0xD97706u), L.T("SED19BD5E2620"), 2),
                "mask" => ("Eye", new SolidColorBrush(Windows.UI.Color.FromArgb(36, 255, 107, 146)), Hex(0xFF6B92u), L.T("SAC7CE836857B"), 2),
                "tilt" => ("Layout", new SolidColorBrush(Windows.UI.Color.FromArgb(36, 255, 133, 80)), Hex(0xFF8550u), L.T("S17E14283A497"), 2),
                "intro" => ("Opening", new SolidColorBrush(Windows.UI.Color.FromArgb(32, 61, 219, 176)), Hex(0x008F68u), L.T("SFD0D4B7258C2"), 1),
                "sticker" => ("Scene", new SolidColorBrush(Windows.UI.Color.FromArgb(36, 181, 230, 85)), Hex(dark ? 0xB5E655u : 0x65A30Du), L.T("S622DA0460277"), 2),
                _ => ("Layers", RecorderTheme.Brush(Root, "EditorWell"), RecorderTheme.Brush(Root, "EditorSecondary"), kind, 2)
            };

            var badge = new Button
            {
                Width = 24,
                Height = 20,
                MinWidth = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Background = bgBrush,
                Content = new EditorGlyph { Kind = glyphKind, Width = 12, Height = 12, Foreground = fgBrush }
            };
            ToolTipService.SetToolTip(badge, tipText);
            AutomationProperties.SetName(badge, tipText);
            AutomationProperties.SetAutomationId(badge, $"editor.timeline.lane.{kind}");
            badge.Click += (_, _) => ShowInspector(targetTab);

            Canvas.SetLeft(badge, 10);
            Canvas.SetTop(badge, top);
            TimelineEffectGutter.Children.Add(badge);
        }
        TimelineEffectGutter.Height = Math.Max(24, maxBottom - 148);
        TimelineGutterContent.Height = maxBottom;
    }
    private void FitTimeline_Click(object sender, RoutedEventArgs e) => TimelineZoom.Value = Math.Clamp((TimelineMount.ActualWidth - 32) / Math.Max(0.01, editor.Timeline.Duration / 10_000_000.0), TimelineZoom.Minimum, TimelineZoom.Maximum);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => TimelineZoom.Value /= 1.5;
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => TimelineZoom.Value *= 1.5;
    private void PreviewOptions_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        var mute = new ToggleMenuFlyoutItem { Text = L.T("S86CEDAE64496"), IsChecked = previewMuted };
        mute.Click += (_, _) => { Pause(); previewMuted = mute.IsChecked; };
        menu.Items.Add(mute);
 foreach (var (label, index) in new[] { (L.T("SFF6C8048B322"), 0), ("125%", 1), ("150%", 2), ("200%", 3) }) { int choice = index; var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = CanvasZoom.SelectedIndex == choice }; item.Click += (_, _) => CanvasZoom.SelectedIndex = choice; menu.Items.Add(item); }
        menu.Items.Add(new MenuFlyoutSeparator());
        var qualityMenu = new MenuFlyoutSubItem { Text = L.T("S4B350ABAABEA") };
        foreach (var (label, quality) in new[] { (L.T("S3339E6CC541D"), PreviewQuality.UltraHd), (L.T("S916B0AF69EE3"), PreviewQuality.Automatic) }) {
            var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = preview.Quality == quality };
            item.Click += async (_, _) => {
                try { await preview.SetQualityAsync(quality, position); }
                catch (Exception error) { ShowNotice(L.T("S363E855FD08B"), error.Message, InfoBarSeverity.Error); }
            };
            qualityMenu.Items.Add(item);
        }
        menu.Items.Add(qualityMenu);
        menu.Items.Add(new MenuFlyoutSeparator()); var reload = new MenuFlyoutItem { Text = L.T("S2B4D78B18E58") }; reload.Click += ReloadPreview_Click; menu.Items.Add(reload); menu.ShowAt((FrameworkElement)sender);
    }
}

internal static class EditorAppearance
{
    internal static event Action<ElementTheme>? Changed;
    private static readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "appearance.txt");
    internal static ElementTheme Current { get { try { return Enum.TryParse<ElementTheme>(File.ReadAllText(path), out var value) ? value : ElementTheme.Light; } catch { return ElementTheme.Light; } } }
    internal static void Save(ElementTheme value) { try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value.ToString()); } catch (IOException) { } catch (UnauthorizedAccessException) { } Changed?.Invoke(value); }
}
