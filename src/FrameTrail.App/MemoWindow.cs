using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FrameTrail.App;

internal sealed partial class MemoWindow : Window
{
    private readonly string path;
    private readonly Grid root;
    private readonly MoveSurface grip;
    private readonly TextBox editor;
    private readonly Button mode;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool saved, saving;
    internal MemoWindow(string? storagePath = null, Action<string>? dragTrace = null)
    {
        path = storagePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "memo.txt");
        Title = L.T("SD510F63CB8AF");
        root = new Grid { CornerRadius = new CornerRadius(22), RequestedTheme = EditorAppearance.Current }; root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) }); root.RowDefinitions.Add(new RowDefinition());
        void SetAppearance(ElementTheme theme) => root.RequestedTheme = theme;
        EditorAppearance.Changed += SetAppearance; Closed += (_, _) => EditorAppearance.Changed -= SetAppearance;
        var bar = new Grid { Padding = new Thickness(6, 0, 6, 0), ColumnSpacing = 2 };
        for (int i = 0; i < 5; i++) bar.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(30) });
        grip = new MoveSurface { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Padding = new Thickness(10, 0, 0, 0) };
        grip.Children.Add(new TextBlock { Text = L.T("S890E0D58781D"), FontSize = 12, Opacity = .6, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
        AutomationProperties.SetName(grip, L.T("SD2853C1F8032")); AutomationProperties.SetAutomationId(grip, "memo.drag");
        ToolTipService.SetToolTip(grip, L.T("SEC9CBDCEAF88"));
        RecorderWindowChrome.EnableDragging(this, grip, () => { }, dragTrace); bar.Children.Add(grip);
        editor = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 20, FontFamily = (FontFamily)Application.Current.Resources["RecorderFont"],
            PlaceholderText = L.T("S891F1023D8AF"), BorderThickness = new Thickness(0), Padding = new Thickness(16, 14, 16, 14), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), MaxLength = 200000 };
        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(editor, L.T("SC959FA1BB9C9")); AutomationProperties.SetAutomationId(editor, "memo.editor");
        mode = AddButton(bar, 1, "Memo", L.T("S80DB4E5F5DD1"), () => editor.IsReadOnly = !editor.IsReadOnly);
        AddTextButton(bar, 2, "A−", L.T("SA6E47D2E82DE"), () => editor.FontSize = Math.Max(16, editor.FontSize - 2));
        AddTextButton(bar, 3, "A+", L.T("SA4A6EBEABE26"), () => editor.FontSize = Math.Min(40, editor.FontSize + 2));
        AddButton(bar, 4, "Close", L.T("S01927B273B65"), async () => await RequestCloseAsync());
        root.Children.Add(bar); Grid.SetRow(editor, 1); root.Children.Add(editor); Content = root;
        void Theme() { root.Background = RecorderTheme.Brush(root, "RecorderBase"); editor.Foreground = RecorderTheme.Brush(root, "RecorderInk"); ((TextBlock)grip.Children[0]).Foreground = RecorderTheme.Brush(root, "RecorderInk"); }
        Theme(); root.ActualThemeChanged += (_, _) => Theme();
        root.Loaded += async (_, _) => { try { if (File.Exists(path) && new FileInfo(path).Length <= 2_000_000) editor.Text = await File.ReadAllTextAsync(path); } catch { } finally { ready.TrySetResult(); } };
        AppWindow.Closing += async (_, args) =>
        {
            if (saved) return; args.Cancel = true;
            await RequestCloseAsync();
        };
        RecorderWindowChrome.Configure(this, 360, 360, 22);
    }
    internal async Task<bool> RequestCloseAsync()
    {
        if (saved) { Close(); return true; }
        if (saving) return false;
        saving = true;
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, editor.Text);
            saved = true; Close(); return true;
        } catch (Exception error) {
            editor.Header = L.T("S3702D4000E7F");
            ToolTipService.SetToolTip(mode, L.T("S17785044D94F") + error.Message);
            return false;
        } finally { saving = false; }
    }
    // Limit the move cursor to the drag surface; text and toolbar buttons keep
    // their normal pointer behavior and never participate in window movement.
    private sealed class MoveSurface : Grid
    {
        internal InputSystemCursor MoveCursor { get; } = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
        internal MoveSurface() => ProtectedCursor = MoveCursor;
    }
    private static Button AddButton(Grid bar, int column, string kind, string label, Action action)
    {
        var button = new Button { Content = new RecorderGlyph { Kind = kind, Width = 12, Height = 12 }, Width = 30, Height = 30, Style = (Style)Application.Current.Resources["RecorderIconButton"] };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label); ToolTipService.SetToolTip(button, label); button.Click += (_, _) => action(); Grid.SetColumn(button, column); bar.Children.Add(button); return button;
    }
    private static void AddTextButton(Grid bar, int column, string text, string label, Action action)
    { var button = AddButton(bar, column, "Memo", label, action); button.Content = new TextBlock { Text = text, FontSize = 12 }; }
}
