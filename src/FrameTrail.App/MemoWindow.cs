using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FrameTrail.App;

internal sealed class MemoWindow : Window
{
    private readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "memo.txt");
    private readonly TextBox editor;
    private bool saved;
    internal MemoWindow()
    {
        Title = "FrameTrail · 备忘录";
        var root = new Grid { CornerRadius = new CornerRadius(22), RequestedTheme = EditorAppearance.Current }; root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) }); root.RowDefinitions.Add(new RowDefinition());
        void SetAppearance(ElementTheme theme) => root.RequestedTheme = theme;
        EditorAppearance.Changed += SetAppearance; Closed += (_, _) => EditorAppearance.Changed -= SetAppearance;
        var bar = new Grid { Padding = new Thickness(6, 0, 6, 0), ColumnSpacing = 2 };
        for (int i = 0; i < 5; i++) bar.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(30) });
        var grip = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) }; grip.PointerPressed += (_, args) => RecorderWindowChrome.Drag(this, args); bar.Children.Add(grip);
        editor = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 20, FontFamily = (FontFamily)Application.Current.Resources["RecorderFont"],
            PlaceholderText = "写下或粘贴要讲的内容…", BorderThickness = new Thickness(0), Padding = new Thickness(16, 14, 16, 14), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), MaxLength = 200000 };
        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        var mode = AddButton(bar, 1, "Memo", "切换阅读与编辑", () => editor.IsReadOnly = !editor.IsReadOnly);
        AddTextButton(bar, 2, "A−", "缩小字号", () => editor.FontSize = Math.Max(16, editor.FontSize - 2));
        AddTextButton(bar, 3, "A+", "放大字号", () => editor.FontSize = Math.Min(40, editor.FontSize + 2));
        AddButton(bar, 4, "Close", "收起备忘录", Close);
        root.Children.Add(bar); Grid.SetRow(editor, 1); root.Children.Add(editor); Content = root;
        void Theme() { root.Background = RecorderTheme.Brush(root, "RecorderBase"); editor.Foreground = RecorderTheme.Brush(root, "RecorderInk"); }
        Theme(); root.ActualThemeChanged += (_, _) => Theme();
        root.Loaded += async (_, _) => { try { if (File.Exists(path) && new FileInfo(path).Length <= 2_000_000) editor.Text = await File.ReadAllTextAsync(path); } catch { } };
        AppWindow.Closing += async (_, args) =>
        {
            if (saved) return; args.Cancel = true;
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, editor.Text); saved = true; Close(); }
            catch { mode.Content = "保存失败"; mode.Width = 70; }
        };
        RecorderWindowChrome.Configure(this, 360, 360, 22);
    }
    private static Button AddButton(Grid bar, int column, string kind, string label, Action action)
    {
        var button = new Button { Content = new RecorderGlyph { Kind = kind, Width = 12, Height = 12 }, Width = 30, Height = 30, Style = (Style)Application.Current.Resources["RecorderIconButton"] };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label); ToolTipService.SetToolTip(button, label); button.Click += (_, _) => action(); Grid.SetColumn(button, column); bar.Children.Add(button); return button;
    }
    private static void AddTextButton(Grid bar, int column, string text, string label, Action action)
    { var button = AddButton(bar, column, "Memo", label, action); button.Content = new TextBlock { Text = text, FontSize = 12 }; }
}
