using FrameTrail.Domain;
using FrameTrail.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    private readonly EditorMockupTurntable mockupPicker = new();
    private readonly EditorMockupKnob frameScale = new() { Label = L.T("S2C25F46A33FC"), Minimum = 0.6, Maximum = 1.6, StepFrequency = 0.01 };
    private readonly EditorMockupKnob frameToolbar = new() { Label = L.T("S053FEAE05283"), Minimum = 0.65, Maximum = 1.6, StepFrequency = 0.01 };
    private readonly EditorMockupKnob frameRadius = new() { Label = L.T("S904F14496A39"), Minimum = 0, Maximum = 160, StepFrequency = 1, Suffix = "pt" };
    private readonly TextBox frameTitle = new() { PlaceholderText = L.T("S293B74110F45"), MaxLength = 120 };
    private readonly TextBox frameAddress = new() { PlaceholderText = "example.com", MaxLength = 240 };
    private readonly Grid frameKnobs = new() { Margin = new Thickness(0, 6, 0, 0) };
    private Grid? frameTitleRow, frameAddressRow;
    private bool mockupEditing;
    private readonly DispatcherTimer mockupPosterTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private Task mockupPosterWork = Task.CompletedTask;
    private long mockupPosterPosition = -1;
    private int mockupPosterGeneration;
    private EditorMockupKnob[] MockupKnobs => [frameScale, frameToolbar, frameRadius];

    private void InitializeMockups()
    {
        mockupSettings.Spacing = 10; mockupSettings.Children.Add(mockupPicker);
        mockupPicker.SelectionRequested += async style => await SafeAsync(() => SelectMockupAsync(style));
        var reset = new Button { Style = (Style)Application.Current.Resources["EditorButton"], FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
        var resetLabel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; resetLabel.Children.Add(new EditorGlyph { Kind = "Undo", Width = 13, Height = 13 }); resetLabel.Children.Add(new TextBlock { Text = L.T("S0D9B78A156EB"), FontSize = 12 }); reset.Content = resetLabel;
        AutomationProperties.SetName(reset, L.T("S0D9B78A156EB")); AutomationProperties.SetAutomationId(reset, "editor.frame.reset");
        ToolTipService.SetToolTip(reset, L.T("S08E9AFDE5C0D")); reset.Click += async (_, _) => await SafeAsync(() => SelectMockupAsync((editor.Snapshot.Editor ?? new()).FrameStyle, true));
        mockupSettings.Children.Add(reset);
        Grid Metadata(string label, TextBox field, string id)
        {
            var row = new Grid { ColumnSpacing = 10 }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            var caption = new TextBlock { Text = label, FontSize = 11, VerticalAlignment = VerticalAlignment.Center }; caption.SetValue(AutomationProperties.AccessibilityViewProperty, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw); row.Children.Add(caption);
            field.Height = field.MinHeight = 30; field.FontSize = 11; field.Padding = new Thickness(10, 6, 10, 6); field.CornerRadius = new CornerRadius(8); field.BorderThickness = new Thickness(0.75);
            AutomationProperties.SetName(field, label); AutomationProperties.SetAutomationId(field, id); Grid.SetColumn(field, 1); row.Children.Add(field);
            field.TextChanged += (_, _) => QueueMockupChange();
            field.LostFocus += async (_, _) => { if (!loadingVisuals) { field.Text = field.Text.Trim(); await SafeAsync(FlushVisualChangesAsync); } };
            field.KeyDown += async (_, e) => {
                if (e.Key == Windows.System.VirtualKey.Enter) { await SafeAsync(FlushVisualChangesAsync); e.Handled = true; }
                else if (e.Key == Windows.System.VirtualKey.Escape) { visualPreviewTimer?.Stop(); visualCommitTimer?.Stop(); await CancelVisualChangesAsync(); e.Handled = true; }
            };
            return row;
        }
        frameTitleRow = Metadata(L.T("S2BFA4E3EE28A"), frameTitle, "editor.frame.title"); frameAddressRow = Metadata(L.T("S663B2C666C4C"), frameAddress, "editor.frame.address");
        mockupSettings.Children.Add(frameTitleRow); mockupSettings.Children.Add(frameAddressRow);
        for (int i = 0; i < 3; i++) {
            var knob = MockupKnobs[i]; frameKnobs.ColumnDefinitions.Add(new ColumnDefinition()); Grid.SetColumn(knob, i); frameKnobs.Children.Add(knob);
            AutomationProperties.SetName(knob, i == 2 ? L.T("S5BC3D7600A78") : knob.Label); AutomationProperties.SetAutomationId(knob, new[] { "editor.frame.scale", "editor.frame.toolbar", "editor.frame.radius" }[i]);
            ToolTipService.SetToolTip(knob, L.T("S35465274F28A"));
            knob.ValueChanged += (_, _) => QueueMockupChange();
            knob.InteractionEnded += commit => {
                if (!commit) { visualPreviewTimer?.Stop(); visualCommitTimer?.Stop(); visualCommitWork = CancelVisualChangesAsync(); }
                else if (visualPending || visualTransaction) { visualCommitTimer!.Interval = TimeSpan.FromMilliseconds(1); visualCommitTimer.Start(); }
            };
        }
        mockupSettings.Children.Add(frameKnobs);
        mockupPosterTimer.Tick += (_, _) => { mockupPosterTimer.Stop(); if (mockupPosterWork.IsCompleted) mockupPosterWork = UpdateMockupPosterAsync(); };
        Root.ActualThemeChanged += (_, _) => RefreshMockupTheme();
        Closed += (_, _) => { mockupPosterGeneration++; mockupPosterTimer.Stop(); };
        RefreshMockupTheme();
    }
    private void RefreshMockupTheme()
    {
        foreach (var row in new[] { frameTitleRow, frameAddressRow }) if (row?.Children[0] is TextBlock caption) caption.Foreground = RecorderTheme.Brush(Root, "EditorSecondary");
        foreach (var field in new[] { frameTitle, frameAddress }) { field.Background = RecorderTheme.Brush(Root, "EditorWell"); field.Foreground = RecorderTheme.Brush(Root, "EditorInk"); field.BorderBrush = RecorderTheme.Brush(Root, "EditorLine"); }
    }
    private bool MockupMetadataFocused => Root.XamlRoot is not null && InputAncestor<TextBox>(FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject) is TextBox field && (ReferenceEquals(field, frameTitle) || ReferenceEquals(field, frameAddress));
    private void RefreshMockupInspector(bool forceText = false)
    {
        var value = editor.Snapshot.Editor ?? new(); var style = FrameStyles.Normalize(value.FrameStyle);
        mockupPicker.SetSelected(style); frameScale.Value = value.FrameScale; frameToolbar.Value = value.FrameToolbarScale; frameRadius.Value = value.FrameOuterCornerRadius ?? FrameStyles.OuterRadius(style);
        if (forceText || Root.XamlRoot is null || !ReferenceEquals(InputAncestor<TextBox>(FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject), frameTitle)) frameTitle.Text = value.FrameTitle;
        if (forceText || Root.XamlRoot is null || !ReferenceEquals(InputAncestor<TextBox>(FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject), frameAddress)) frameAddress.Text = value.FrameBrowserAddress;
        frameTitleRow!.Visibility = FrameStyles.IsWindow(style) ? Visibility.Visible : Visibility.Collapsed;
        frameAddressRow!.Visibility = FrameStyles.IsBrowser(style) ? Visibility.Visible : Visibility.Collapsed;
        bool toolbar = FrameStyles.IsWindow(style) || FrameStyles.IsBrowser(style);
        frameKnobs.Visibility = style != "none" ? Visibility.Visible : Visibility.Collapsed;
        frameToolbar.Visibility = toolbar ? Visibility.Visible : Visibility.Collapsed; frameKnobs.ColumnDefinitions[1].Width = toolbar ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }
    private async Task SelectMockupAsync(string style, bool reset = false)
    {
        if (!ready || closing || editor.IsExporting) return;
        if (reset || FrameStyles.Normalize((editor.Snapshot.Editor ?? new()).FrameStyle) != FrameStyles.Normalize(style))
            await PerformAsync(() => editor.ExecuteAsync(new { op = "settings", editor = FrameStyles.Apply(editor.Snapshot.Editor ?? new(), style) }));
        loadingVisuals = true; try { RefreshMockupInspector(); } finally { loadingVisuals = false; }
    }
    private EditorSettings MockupDraft() => (editor.Snapshot.Editor ?? new()) with { FrameScale = frameScale.Value, FrameToolbarScale = frameToolbar.Value, FrameOuterCornerRadius = frameRadius.Value, FrameTitle = frameTitle.Text, FrameBrowserAddress = frameAddress.Text };
    private void QueueMockupChange()
    {
        if (loadingVisuals || !ready || working || closing || editor.IsExporting) return;
        if (!mockupEditing && (visualPending || visualTransaction)) { _ = SafeAsync(async () => { await FlushVisualChangesAsync(); QueueMockupChange(); }); return; }
        mockupEditing = true; Pause(); visualPending = true; timeline.EditingEnabled = false; preview.InvalidateRequests();
        if (!committingVisuals && visualPreviewWork.IsCompleted && !visualPreviewTimer!.IsRunning) visualPreviewTimer.Start();
        visualCommitTimer!.Stop(); if (!MockupKnobs.Any(k => k.IsDragging) && !MockupMetadataFocused) { visualCommitTimer.Interval = TimeSpan.FromMilliseconds(400); visualCommitTimer.Start(); }
    }
    private void QueueMockupPoster()
    {
        if (!mockupPage || playing || closing || position == mockupPosterPosition) return;
        mockupPosterTimer.Stop(); mockupPosterTimer.Start();
    }
    private async Task UpdateMockupPosterAsync()
    {
        if (closing || !ready) return;
        var generation = ++mockupPosterGeneration; long ticks = position; var snapshot = editor.Snapshot;
        var source = snapshot.Media.First(m => m.Kind == MediaKind.Screen); var crop = snapshot.Editor?.Crop;
        uint posterHeight = (uint)Math.Clamp(Math.Round(480d * source.Height * (crop?.Height ?? 1) / (source.Width * (crop?.Width ?? 1))), 64, 4096);
        var picture = snapshot with { Effects = [], Canvas = snapshot.Canvas with { Width = 480, Height = posterHeight, Padding = 0, Screen = new() { X = 0, Y = 0, Width = 1, Height = 1 }, Camera = snapshot.Canvas.Camera with { Enabled = false } },
            Editor = (snapshot.Editor ?? new()) with { FrameStyle = "none", CornerRadius = 0, Shadow = 0, CursorScale = 0, Backdrop = null } };
        var path = Path.Combine(editor.DirectoryPath, "cache", $"mockup-poster-{ticks}.png"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try {
            await Task.Run(() => { using var media = new MediaSession(picture, editor.DirectoryPath); media.Render(ticks, 0, path); });
            if (closing || generation != mockupPosterGeneration) return;
            mockupPosterPosition = ticks; mockupPicker.SetPoster(new BitmapImage(new Uri(path)));
        } catch (OperationCanceledException) { }
        catch (Exception error) { if (!closing) ShowNotice(L.T("S5CC320E8CC52"), error.Message); }
        if (ticks != position) QueueMockupPoster();
    }
}
