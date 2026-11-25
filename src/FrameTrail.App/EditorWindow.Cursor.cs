using FrameTrail.Domain;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    private readonly ComboBox cursorStyle = VisualChoice(L.T("S913CD56DA217"), L.T("S6D5D42F9C747"), L.T("S83FA01A2D780"), L.T("SEE694FC591A5"), L.T("S593C0A51FCED"));
    private readonly ComboBox cursorClick = VisualChoice(L.T("SAE165ABB741E"), L.T("StateOff"), L.T("S23A7127B85D6"), L.T("S05F954565F29"));
    private readonly ComboBox cursorColor = VisualChoice(L.T("S43CC1FCAFD4E"), L.T("S67035E6B7F82"), L.T("SD451C07D739B"), L.T("S789F61DBFCC7"));
    private readonly ToggleSwitch cursorIdle = new() { Header = L.T("S143DDCCA177C"), OnContent = L.T("S8DA97DDDA990"), OffContent = L.T("StateOff") };
    private readonly ToggleSwitch keystrokeHudToggle = new() { Header = L.T("KeystrokeHudLabel"), OnContent = L.T("S8DA97DDDA990"), OffContent = L.T("StateOff"), IsOn = true };
    private readonly EditorBarSlider cursorDelay = VisualBar(0.2, 8, 0.1, "s"), cursorOffset = VisualBar(-2, 2, 0.01, "s");
    private readonly EditorBarSlider cursorClickOpacity = VisualBar(0, 1, 0.05), cursorClickScale = VisualBar(0.4, 3, 0.1, "×");
    private static readonly string[] cursorStyles = ["recorded", "arrow", "dot", "crosshair"], clickStyles = ["none", "ripple", "highlight"];
    private static readonly SdrColor[] clickColors = [new() { R = 0.35, G = 0.68, B = 1 }, new() { R = 1, G = 0.8, B = 0.15 }, new() { R = 1, G = 0.25, B = 0.3 }];
    private readonly StackPanel cursorClickDetails = new() { Spacing = 8 };
    private readonly StackPanel cursorIdleDelayRow = new() { Spacing = 4 };
    private bool loadingCursor;
    private void InitializeCursor()
    {
        AutomationProperties.SetAutomationId(cursorStyle, "editor.cursor.style"); AutomationProperties.SetAutomationId(cursorClick, "editor.cursor.click");
        AutomationProperties.SetAutomationId(cursorIdle, "editor.cursor.idle"); AutomationProperties.SetAutomationId(cursorOffset, "editor.cursor.offset");
        AutomationProperties.SetAutomationId(keystrokeHudToggle, "editor.cursor.keystrokeHud");
        TextBlock FieldLabel(string text) => new() { Text = text, Style = (Microsoft.UI.Xaml.Style)Root.Resources["SmallLabel"] };

        cursorClickDetails.Children.Add(cursorColor);
        cursorClickDetails.Children.Add(FieldLabel(L.T("SE97F164E8917")));
        cursorClickDetails.Children.Add(cursorClickOpacity);
        cursorClickDetails.Children.Add(FieldLabel(L.T("S713F392B0F60")));
        cursorClickDetails.Children.Add(cursorClickScale);
        CursorSettings.Children.Add(VisualTile(cursorStyle, cursorClick, cursorClickDetails));

        cursorIdleDelayRow.Children.Add(FieldLabel(L.T("S2A149D8ADB7F")));
        cursorIdleDelayRow.Children.Add(cursorDelay);
        CursorSettings.Children.Add(VisualTile(cursorIdle, cursorIdleDelayRow, keystrokeHudToggle));

        CursorSettings.Children.Add(VisualTile(FieldLabel(L.T("SAF2F03C18F32")), cursorOffset, new TextBlock { Text = L.T("S3B71B905D70B"), Style = (Microsoft.UI.Xaml.Style)Root.Resources["SmallLabel"], TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap }));
        foreach (var box in new[] { cursorStyle, cursorClick, cursorColor }) box.SelectionChanged += async (_, _) => { UpdateCursorFieldVisibility(); await CommitCursorAsync(); };
        cursorIdle.Toggled += async (_, _) => { UpdateCursorFieldVisibility(); await CommitCursorAsync(); };
        keystrokeHudToggle.Toggled += async (_, _) => await CommitCursorAsync();
        foreach (var slider in new[] { cursorDelay, cursorOffset, cursorClickOpacity, cursorClickScale }) slider.Committed += async (_, _) => await CommitCursorAsync();
        UpdateCursorFieldVisibility();
    }
    private void UpdateCursorFieldVisibility()
    {
        cursorClickDetails.Visibility = cursorClick.SelectedIndex > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        cursorIdleDelayRow.Visibility = cursorIdle.IsOn ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    }
    private async Task CommitCursorAsync()
    {
        if (loadingCursor || loadingVisuals || loadingInspector || !ready || working) return;
        var prior = editor.Snapshot.Editor ?? new();
        var value = prior with {
            CursorStyle = cursorStyles[Math.Max(0, cursorStyle.SelectedIndex)], CursorClickStyle = clickStyles[Math.Max(0, cursorClick.SelectedIndex)],
            CursorClickColor = cursorColor.SelectedIndex >= 0 ? clickColors[cursorColor.SelectedIndex] : prior.CursorClickColor,
            CursorHideWhenIdle = cursorIdle.IsOn, KeystrokeHud = keystrokeHudToggle.IsOn, CursorIdleDelay = cursorDelay.Value, CursorTimeOffset = cursorOffset.Value,
            CursorClickOpacity = cursorClickOpacity.Value, CursorClickScale = cursorClickScale.Value
        };
        await PerformAsync(() => editor.ExecuteAsync(new { op = "settings", editor = value }));
    }
    private void LoadCursor()
    {
        loadingCursor = true;
        try {
            var value = editor.Snapshot.Editor ?? new();
            cursorStyle.SelectedIndex = Array.IndexOf(cursorStyles, value.CursorStyle); cursorClick.SelectedIndex = Array.IndexOf(clickStyles, value.CursorClickStyle);
            cursorColor.SelectedIndex = Array.IndexOf(clickColors, value.CursorClickColor);
            cursorIdle.IsOn = value.CursorHideWhenIdle; keystrokeHudToggle.IsOn = value.KeystrokeHud; cursorDelay.Value = value.CursorIdleDelay; cursorOffset.Value = value.CursorTimeOffset;
            cursorClickOpacity.Value = value.CursorClickOpacity; cursorClickScale.Value = value.CursorClickScale;
        } finally { loadingCursor = false; }
        UpdateCursorFieldVisibility();
    }
}
