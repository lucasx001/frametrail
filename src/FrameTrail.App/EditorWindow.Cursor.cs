using FrameTrail.Domain;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    private readonly ComboBox cursorStyle = VisualChoice("指针样式", "录制时的指针", "箭头", "圆点", "十字");
    private readonly ComboBox cursorClick = VisualChoice("点击反馈", "关闭", "波纹", "高亮");
    private readonly ComboBox cursorColor = VisualChoice("反馈颜色", "蓝色", "黄色", "红色");
    private readonly ToggleSwitch cursorIdle = new() { Header = "静止时隐藏", OnContent = "开启", OffContent = "关闭" };
    private readonly EditorBarSlider cursorDelay = VisualBar(0.2, 8, 0.1, "s"), cursorOffset = VisualBar(-2, 2, 0.01, "s");
    private readonly EditorBarSlider cursorClickOpacity = VisualBar(0, 1, 0.05), cursorClickScale = VisualBar(0.4, 3, 0.1, "×");
    private static readonly string[] cursorStyles = ["recorded", "arrow", "dot", "crosshair"], clickStyles = ["none", "ripple", "highlight"];
    private static readonly SdrColor[] clickColors = [new() { R = 0.35, G = 0.68, B = 1 }, new() { R = 1, G = 0.8, B = 0.15 }, new() { R = 1, G = 0.25, B = 0.3 }];
    private bool loadingCursor;
    private void InitializeCursor()
    {
        AutomationProperties.SetAutomationId(cursorStyle, "editor.cursor.style"); AutomationProperties.SetAutomationId(cursorClick, "editor.cursor.click");
        AutomationProperties.SetAutomationId(cursorIdle, "editor.cursor.idle"); AutomationProperties.SetAutomationId(cursorOffset, "editor.cursor.offset");
        CursorSettings.Children.Add(VisualTile(cursorStyle, cursorClick, cursorColor, VisualLabel("反馈不透明度"), cursorClickOpacity, VisualLabel("反馈大小"), cursorClickScale));
        CursorSettings.Children.Add(VisualTile(cursorIdle, VisualLabel("静止隐藏延迟"), cursorDelay));
        CursorSettings.Children.Add(VisualTile(VisualLabel("光标时间校准"), cursorOffset, new TextBlock { Text = "正值提前光标，负值延后光标；不会移动原始事件。", TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, FontSize = 11 }));
        foreach (var box in new[] { cursorStyle, cursorClick, cursorColor }) box.SelectionChanged += async (_, _) => await CommitCursorAsync();
        cursorIdle.Toggled += async (_, _) => await CommitCursorAsync();
        foreach (var slider in new[] { cursorDelay, cursorOffset, cursorClickOpacity, cursorClickScale }) slider.Committed += async (_, _) => await CommitCursorAsync();
    }
    private async Task CommitCursorAsync()
    {
        if (loadingCursor || loadingVisuals || loadingInspector || !ready || working) return;
        var prior = editor.Snapshot.Editor ?? new();
        var value = prior with {
            CursorStyle = cursorStyles[Math.Max(0, cursorStyle.SelectedIndex)], CursorClickStyle = clickStyles[Math.Max(0, cursorClick.SelectedIndex)],
            CursorClickColor = cursorColor.SelectedIndex >= 0 ? clickColors[cursorColor.SelectedIndex] : prior.CursorClickColor,
            CursorHideWhenIdle = cursorIdle.IsOn, CursorIdleDelay = cursorDelay.Value, CursorTimeOffset = cursorOffset.Value,
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
            cursorIdle.IsOn = value.CursorHideWhenIdle; cursorDelay.Value = value.CursorIdleDelay; cursorOffset.Value = value.CursorTimeOffset;
            cursorClickOpacity.Value = value.CursorClickOpacity; cursorClickScale.Value = value.CursorClickScale;
        } finally { loadingCursor = false; }
    }
}
