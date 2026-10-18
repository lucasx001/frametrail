using System.Security.Cryptography;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace FrameTrail.App;

internal sealed partial class ProjectLibraryWindow : Window
{
    private readonly ProjectCatalog catalog;
    private readonly string[] folders;
    private readonly string? currentProject;
    private readonly Func<string, Task> open, delete;
    private readonly Grid root = new() { Padding = new Thickness(28), RowSpacing = 18 };
    private readonly TextBox search = new() { PlaceholderText = "搜索项目名称或位置", MaxLength = 256 };
    private readonly ListView list = new() { SelectionMode = ListViewSelectionMode.None, IsTabStop = false };
    private readonly TextBlock count = new(), empty = new() { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly InfoBar notice = new() { IsClosable = true };
    private readonly Button browse = new() { Content = "其他位置…" }, refresh = new() { Content = "刷新" };
    private readonly CancellationTokenSource cancellation = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IReadOnlyList<ProjectCatalogEntry> entries = [];
    private bool working, confirming, closed, allowClose;
    private ContentDialog? confirmation;
    internal Task Ready => ready.Task;

    internal ProjectLibraryWindow(ProjectCatalog catalog, IEnumerable<string> folders, string? currentProject, Func<string, Task> open, Func<string, Task> delete)
    {
        this.catalog = catalog; this.folders = folders.ToArray(); this.currentProject = currentProject; this.open = open; this.delete = delete;
        Title = "FrameTrail · 项目"; AppIcon.Apply(this); Content = root;
        root.RequestedTheme = EditorAppearance.Current;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel { Spacing = 6 }; heading.Children.Add(Text("打开项目", 26));
        heading.Children.Add(Text("从上次停下的地方继续。", 13, true)); root.Children.Add(heading);
        Grid.SetRow(search, 1); root.Children.Add(search);
        var body = new Grid(); body.Children.Add(list); body.Children.Add(empty); Grid.SetRow(body, 2); root.Children.Add(body);
        Grid.SetRow(notice, 3); root.Children.Add(notice);
        var footer = new Grid { ColumnSpacing = 10 }; footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        count.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(count);
        foreach (var button in new[] { browse, refresh }) { button.Style = (Style)Application.Current.Resources["EditorButton"]; button.Padding = new Thickness(14, 0, 14, 0); }
        Grid.SetColumn(browse, 1); Grid.SetColumn(refresh, 2); footer.Children.Add(browse); footer.Children.Add(refresh); Grid.SetRow(footer, 4); root.Children.Add(footer);
        AutomationProperties.SetAutomationId(search, "projects.search"); AutomationProperties.SetName(search, "搜索项目");
        search.Resources["TextControlBorderBrushFocused"] = Application.Current.Resources["EditorBlue"];
        AutomationProperties.SetAutomationId(list, "projects.list"); AutomationProperties.SetName(list, "本地项目");
        AutomationProperties.SetAutomationId(browse, "projects.browse"); AutomationProperties.SetAutomationId(refresh, "projects.refresh");
        search.TextChanged += (_, _) => RenderRows();
        search.KeyDown += async (_, e) => {
            if (e.Key != VirtualKey.Enter || working || confirming) return;
            var matches = Filtered().Where(entry => entry.CanOpen).ToArray();
            if (matches.Length == 1) { e.Handled = true; await OpenAsync(matches[0]); }
        };
        browse.Click += async (_, _) => await BrowseAsync(); refresh.Click += async (_, _) => await RefreshAsync();
        root.KeyDown += (_, e) => { if (e.Key == VirtualKey.Escape && !working && !confirming) { e.Handled = true; Close(); } };
        void Theme(ElementTheme theme) {
            root.RequestedTheme = theme; root.Background = Brush("EditorStage"); count.Foreground = empty.Foreground = Brush("EditorSecondary");
            ((TextBlock)heading.Children[0]).Foreground = Brush("EditorInk"); ((TextBlock)heading.Children[1]).Foreground = Brush("EditorSecondary"); RenderRows();
        }
        EditorAppearance.Changed += Theme;
        root.ActualThemeChanged += (_, _) => Theme(root.RequestedTheme);
        root.Loaded += async (_, _) => { await RefreshAsync(); if (!closed) search.Focus(FocusState.Programmatic); ready.TrySetResult(); };
        AppWindow.Closing += (_, args) => { if (!allowClose && (working || confirming)) args.Cancel = true; };
        Closed += (_, _) => { closed = true; cancellation.Cancel(); EditorAppearance.Changed -= Theme; };
        Theme(EditorAppearance.Current);
        double scale = RecorderWindowChrome.Scale(this); var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new((int)Math.Min(760 * scale, area.Width), (int)Math.Min(660 * scale, area.Height)));
        AppWindow.Move(new(area.X + (area.Width - AppWindow.Size.Width) / 2, area.Y + (area.Height - AppWindow.Size.Height) / 2));
        if (AppWindow.Presenter is OverlappedPresenter presenter) { presenter.PreferredMinimumWidth = (int)(480 * scale); presenter.PreferredMinimumHeight = (int)(420 * scale); }
    }

    private Brush Brush(string key) => RecorderTheme.Brush(root, key);
    private TextBlock Text(string value, double size = 12, bool secondary = false) => new() { Text = value, FontSize = size, Foreground = Brush(secondary ? "EditorSecondary" : "EditorInk"), TextTrimming = TextTrimming.CharacterEllipsis };
    private IEnumerable<ProjectCatalogEntry> Filtered() => entries.Where(entry => string.IsNullOrWhiteSpace(search.Text) ||
        entry.Name.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) || entry.Directory.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase));
    private static string EntryId(ProjectCatalogEntry entry) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Directory.ToUpperInvariant())))[..16];
    private void SetBusy(bool value) { working = value; search.IsEnabled = list.IsEnabled = browse.IsEnabled = refresh.IsEnabled = !value; }
    private void ShowError(Exception error) { notice.Title = "操作未完成"; notice.Message = error.Message; notice.Severity = InfoBarSeverity.Error; notice.IsOpen = true; }

    private async Task RefreshAsync()
    {
        if (closed || working) return;
        SetBusy(true);
        try { entries = await Task.Run(() => catalog.List(folders, cancellation.Token)); if (!closed) RenderRows(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closed) ShowError(error); }
        finally { if (!closed) SetBusy(false); }
    }
    private void RenderRows()
    {
        if (closed) return;
        list.Items.Clear(); var visible = Filtered().ToArray();
        count.Text = $"{visible.Length} 个项目";
        empty.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        empty.Text = entries.Count == 0 ? "还没有项目\n录制完成后，项目会出现在这里。\n已有其他位置的项目？点击下方“其他位置”。" : "没有找到匹配的项目";
        foreach (var entry in visible) {
            var row = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var content = new Grid { ColumnSpacing = 14 }; content.ColumnDefinitions.Add(new() { Width = new GridLength(42) }); content.ColumnDefinitions.Add(new());
            content.Children.Add(new Border { Width = 42, Height = 44, CornerRadius = new CornerRadius(12), Background = Brush("EditorWell"), Child = new EditorGlyph { Kind = "Film", Width = 22, Height = 22, Foreground = Brush("EditorBlue") } });
            var details = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            var title = Text(entry.Name, 14); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; details.Children.Add(title);
            string duration = entry.RecordingSeconds is double seconds ? $"录制 {(int)(seconds / 60):00}:{(int)seconds % 60:00} · {entry.Width} × {entry.Height} · " : "";
            details.Children.Add(Text(entry.Error ?? duration + entry.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), 11, true));
            details.Children.Add(Text(entry.Directory, 10, true)); Grid.SetColumn(details, 1); content.Children.Add(details);
            var select = new Button { Content = content, Style = (Style)Application.Current.Resources["EditorButton"], HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(14, 12, 14, 12), Height = 92, Background = Brush("EditorPanel"), CornerRadius = new CornerRadius(12) };
            AutomationProperties.SetName(select, "打开项目 " + entry.Name); AutomationProperties.SetAutomationId(select, "projects.open." + EntryId(entry)); ToolTipService.SetToolTip(select, entry.Directory);
            select.Click += async (_, _) => {
                if (entry.CanOpen) await OpenAsync(entry);
                else { notice.Title = "项目暂时无法打开"; notice.Message = entry.Error; notice.Severity = InfoBarSeverity.Warning; notice.IsOpen = true; }
            }; row.Children.Add(select);
            if (string.Equals(entry.Directory, currentProject, StringComparison.OrdinalIgnoreCase)) {
                title.Text += " · 当前项目";
            }
            var remove = new Button { Content = new EditorGlyph { Kind = "Trash", Foreground = new SolidColorBrush(Colors.IndianRed) }, Style = (Style)Application.Current.Resources["EditorButton"], Width = 40, Height = 40, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(remove, "删除项目 " + entry.Name); AutomationProperties.SetAutomationId(remove, "projects.delete." + EntryId(entry)); ToolTipService.SetToolTip(remove, "删除项目");
            remove.Click += async (_, _) => await DeleteAsync(entry); Grid.SetColumn(remove, 1); row.Children.Add(remove);
            list.Items.Add(new ListViewItem { Content = row, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, IsTabStop = false });
        }
    }
    private async Task OpenAsync(ProjectCatalogEntry entry)
    {
        if (working || confirming || !entry.CanOpen) return;
        SetBusy(true); notice.IsOpen = false;
        try { await open(entry.Directory); allowClose = true; Close(); }
        catch (Exception error) { if (!closed) { Activate(); ShowError(error); } }
        finally { if (!closed) SetBusy(false); }
    }
    private async Task DeleteAsync(ProjectCatalogEntry entry)
    {
        if (working || confirming) return;
        var details = new StackPanel { Spacing = 12 };
        details.Children.Add(new TextBlock { Text = "项目和其中的全部录制素材将移到回收站。若此位置不支持回收站，Windows 会另行确认。", TextWrapping = TextWrapping.Wrap });
        details.Children.Add(new TextBlock { Text = entry.Directory, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .65 });
        confirmation = new ContentDialog { Title = $"删除“{entry.Name}”？", Content = details, PrimaryButtonText = "移到回收站", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = root.XamlRoot, RequestedTheme = root.RequestedTheme };
        confirming = true; ContentDialogResult result;
        try { result = await confirmation.ShowAsync(); }
        finally { confirming = false; confirmation = null; }
        if (result != ContentDialogResult.Primary || closed) return;
        SetBusy(true); notice.IsOpen = false;
        try { await delete(entry.Directory); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closed) ShowError(error); }
        finally { if (!closed) { SetBusy(false); await RefreshAsync(); Activate(); } }
    }
    private async Task BrowseAsync()
    {
        if (working || confirming) return;
        try {
            using var picker = new ProjectFolderPicker(folders.First(), "选择项目所在的位置", "显示这里的项目");
            string? selected = picker.Pick(WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (selected is null) return;
            catalog.RememberFolder(selected); await RefreshAsync();
        } catch (Exception error) { ShowError(error); }
    }
}
