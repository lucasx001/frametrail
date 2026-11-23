using System.Security.Cryptography;
using System.Text;
using System.Collections.ObjectModel;
using FrameTrail.Workspace;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace FrameTrail.App;

internal sealed partial class ProjectLibraryWindow : Window
{
    private readonly ProjectCatalog catalog;
    private readonly string[] folders;
    private readonly string? currentProject;
    private readonly Func<string, Task> open, delete;
    private readonly Action? createNew;
    private readonly Func<IReadOnlyList<string>, Task>? createFromVideos;
    private readonly Grid root = new() { Padding = new Thickness(28), RowSpacing = 18 };
    private readonly TextBox search = new() { PlaceholderText = L.T("S50EE445EF0FB"), MaxLength = 256 };
    private readonly ListView list = new() { SelectionMode = ListViewSelectionMode.Single, IsTabStop = true };
    private readonly TextBlock count = new(), empty = new() { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly InfoBar notice = new() { IsClosable = true };
    private readonly Button newProjectButton = new() { Content = L.T("ProjectLibraryNewButton") };
    private readonly Button browse = new() { Content = L.T("SFD2C929E7801") }, refresh = new() { Content = L.T("SAEE887434131") };
    private readonly Grid dropOverlay = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(148, 0, 0, 0)), CornerRadius = new CornerRadius(14) };
    private readonly Border dropOverlayCard = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(16), Padding = new Thickness(24, 18, 24, 18) };
    private readonly TextBlock dropOverlayText = new() { Text = L.T("DropVideoCreateProjectPrompt"), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly CancellationTokenSource cancellation = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer searchDelay;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IReadOnlyList<ProjectCatalogEntry> entries = [];
    private ObservableCollection<ProjectCatalogEntry> visibleEntries = [];
    private bool working, confirming, closed, allowClose;
    private ContentDialog? confirmation;
    internal Task Ready => ready.Task;

    internal ProjectLibraryWindow(
        ProjectCatalog catalog,
        IEnumerable<string> folders,
        string? currentProject,
        Func<string, Task> open,
        Func<string, Task> delete,
        Action? createNew = null,
        Func<IReadOnlyList<string>, Task>? createFromVideos = null)
    {
        this.catalog = catalog; this.folders = folders.ToArray(); this.currentProject = currentProject; this.open = open; this.delete = delete;
        this.createNew = createNew; this.createFromVideos = createFromVideos;
        Title = L.T("S9B33FBA75B36"); AppIcon.Apply(this); Content = root;
        root.RequestedTheme = EditorAppearance.Current;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel { Spacing = 6 }; heading.Children.Add(Text(L.T("SC565A6AA55BD"), 26));
        heading.Children.Add(Text(L.T("S6724B5CA42E1"), 13, true)); root.Children.Add(heading);
        Grid.SetRow(search, 1); root.Children.Add(search);
        var body = new Grid(); body.Children.Add(list); body.Children.Add(empty);
        var overlayContent = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
        overlayContent.Children.Add(new EditorGlyph { Kind = "Film", Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Center });
        overlayContent.Children.Add(dropOverlayText);
        dropOverlayCard.Child = overlayContent;
        dropOverlay.Children.Add(dropOverlayCard);
        body.Children.Add(dropOverlay);
        Grid.SetRow(body, 2); root.Children.Add(body);
        Grid.SetRow(notice, 3); root.Children.Add(notice);
        var footer = new Grid { ColumnSpacing = 10 };
        footer.ColumnDefinitions.Add(new());
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        count.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(count);
        newProjectButton.Style = (Style)Application.Current.Resources["EditorPrimaryButton"];
        newProjectButton.Padding = new Thickness(14, 0, 14, 0);
        newProjectButton.Visibility = createNew is not null ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { browse, refresh }) { button.Style = (Style)Application.Current.Resources["EditorButton"]; button.Padding = new Thickness(14, 0, 14, 0); }
        Grid.SetColumn(newProjectButton, 1); Grid.SetColumn(browse, 2); Grid.SetColumn(refresh, 3);
        footer.Children.Add(newProjectButton); footer.Children.Add(browse); footer.Children.Add(refresh); Grid.SetRow(footer, 4); root.Children.Add(footer);
        AutomationProperties.SetAutomationId(search, "projects.search"); AutomationProperties.SetName(search, L.T("SDFB59EA2B7A2"));
        search.Resources["TextControlBorderBrushFocused"] = Brush("EditorAccent");
        AutomationProperties.SetAutomationId(list, "projects.list"); AutomationProperties.SetName(list, L.T("S909A4F288125"));
        list.ItemContainerTransitions = new TransitionCollection { new AddDeleteThemeTransition(), new RepositionThemeTransition() };
        // The card owns its hover surface; the recycled ListViewItem must not draw a second one.
        foreach (string key in new[] { "ListViewItemBackgroundPointerOver", "ListViewItemBackgroundPressed", "ListViewItemBackgroundSelected", "ListViewItemBackgroundSelectedPointerOver", "ListViewItemBackgroundSelectedPressed" })
            list.Resources[key] = new SolidColorBrush(Colors.Transparent);
        AutomationProperties.SetAutomationId(newProjectButton, "projects.new");
        AutomationProperties.SetAutomationId(browse, "projects.browse"); AutomationProperties.SetAutomationId(refresh, "projects.refresh");
        if (createFromVideos is not null)
        {
            root.AllowDrop = true;
            root.DragOver += Root_DragOver;
            root.DragLeave += Root_DragLeave;
            root.Drop += Root_Drop;
        }
        newProjectButton.Click += (_, _) => { if (working || confirming || createNew is null) return; allowClose = true; Close(); createNew(); };
        searchDelay=DispatcherQueue.CreateTimer();searchDelay.Interval=TimeSpan.FromMilliseconds(150);searchDelay.IsRepeating=false;
        searchDelay.Tick+=(_,_)=>RenderRows();
        search.TextChanged += (_, _) => {searchDelay.Stop();searchDelay.Start();};
        list.ItemTemplate=(Microsoft.UI.Xaml.DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid HorizontalAlignment='Stretch'/></DataTemplate>");
        void BindRow(ListViewBase sender,ContainerContentChangingEventArgs args){
            if(args.InRecycleQueue){if(args.ItemContainer.ContentTemplateRoot is Grid old)old.Children.Clear();return;}
            args.ItemContainer.Padding=new Thickness(0);args.ItemContainer.HorizontalContentAlignment=HorizontalAlignment.Stretch;
            args.ItemContainer.Margin=new Thickness(0,0,0,6);args.ItemContainer.CornerRadius=new CornerRadius(12);
            args.ItemContainer.IsTabStop=true;
            if(args.ItemContainer.ContentTemplateRoot is not Grid host){if(args.Phase==0)args.RegisterUpdateCallback(1,BindRow);return;}
            if(args.Item is ProjectCatalogEntry entry){host.Children.Clear();host.Children.Add(CreateRow(entry));args.Handled=true;}
        }
        list.ContainerContentChanging+=BindRow;
        search.KeyDown += async (_, e) => {
            if (e.Key != VirtualKey.Enter || working || confirming) return;
            var matches = Filtered().Where(entry => entry.CanOpen).ToArray();
            if (matches.Length == 1) { e.Handled = true; await OpenAsync(matches[0]); }
        };
        list.KeyDown+=async(_,e)=>{if(e.Key==VirtualKey.Enter&&!working&&!confirming&&list.SelectedItem is ProjectCatalogEntry entry&&entry.CanOpen){e.Handled=true;await OpenAsync(entry);}};
        browse.Click += async (_, _) => await BrowseAsync(); refresh.Click += async (_, _) => await RefreshAsync();
        root.KeyDown += (_, e) => { if (e.Key == VirtualKey.Escape && !working && !confirming) { e.Handled = true; Close(); } };
        void Theme(ElementTheme theme) {
            root.RequestedTheme = theme; root.Background = Brush("EditorStage"); count.Foreground = empty.Foreground = Brush("EditorSecondary");
            search.Resources["TextControlBorderBrushFocused"] = Brush("EditorAccent");
            dropOverlayCard.Background = Brush("EditorPanel");
            dropOverlayCard.BorderBrush = Brush("EditorAccent");
            dropOverlayText.Foreground = Brush("EditorInk");
            ((TextBlock)heading.Children[0]).Foreground = Brush("EditorInk"); ((TextBlock)heading.Children[1]).Foreground = Brush("EditorSecondary"); RenderRows();
        }
        EditorAppearance.Changed += Theme;
        root.ActualThemeChanged += (_, _) => Theme(root.RequestedTheme);
        root.Loaded += async (_, _) => { await RefreshAsync(); if (!closed) search.Focus(FocusState.Programmatic); ready.TrySetResult(); };
        AppWindow.Closing += (_, args) => { if (!allowClose && (working || confirming)) args.Cancel = true; };
        Closed += (_, _) => { closed = true; searchDelay.Stop(); cancellation.Cancel(); EditorAppearance.Changed -= Theme; };
        Theme(EditorAppearance.Current);
        double scale = RecorderWindowChrome.Scale(this); var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new((int)Math.Min(760 * scale, area.Width), (int)Math.Min(660 * scale, area.Height)));
        AppWindow.Move(new(area.X + (area.Width - AppWindow.Size.Width) / 2, area.Y + (area.Height - AppWindow.Size.Height) / 2));
        if (AppWindow.Presenter is OverlappedPresenter presenter) { presenter.PreferredMinimumWidth = (int)(480 * scale); presenter.PreferredMinimumHeight = (int)(420 * scale); }
    }

    private Brush Brush(string key) => RecorderTheme.Brush(root, key);
    private static Grid Row(ListViewItem item)=>((Grid)item.ContentTemplateRoot).Children.OfType<Grid>().Single();
    private TextBlock Text(string value, double size = 12, bool secondary = false) => new() { Text = value, FontSize = size, Foreground = Brush(secondary ? "EditorSecondary" : "EditorInk"), TextTrimming = TextTrimming.CharacterEllipsis };
    private IEnumerable<ProjectCatalogEntry> Filtered() => entries.Where(entry => string.IsNullOrWhiteSpace(search.Text) ||
        entry.Name.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) || entry.Directory.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase));
    private static string EntryId(ProjectCatalogEntry entry) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Directory.ToUpperInvariant())))[..16];
    private void SetBusy(bool value)
    {
        working = value;
        search.IsEnabled = newProjectButton.IsEnabled = browse.IsEnabled = refresh.IsEnabled = !value;
        // Block actions without switching every card into its dimmed disabled visual state.
        list.IsHitTestVisible = list.IsTabStop = !value;
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (working || confirming || createFromVideos is null || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            dropOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Copy;
        if (e.DragUIOverride is not null)
            e.DragUIOverride.Caption = L.T("DropVideoCreateProjectPrompt");
        dropOverlay.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void Root_DragLeave(object sender, DragEventArgs e)
    {
        dropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        dropOverlay.Visibility = Visibility.Collapsed;
        if (working || confirming || createFromVideos is null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.Handled = true;

        var deferral = e.GetDeferral();
        string[] paths;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            paths = items
                .OfType<StorageFile>()
                .Select(f => f.Path)
                .Where(p => !string.IsNullOrWhiteSpace(p) && ProjectWorkspace.IsSupportedImportVideoExtension(Path.GetExtension(p)))
                .ToArray();
        }
        catch (Exception error)
        {
            ShowError(error);
            return;
        }
        finally
        {
            deferral.Complete();
        }

        if (paths.Length == 0)
        {
            notice.Title = L.T("NewProjectCreateFailed");
            notice.Message = L.T("NewProjectUnsupportedFiles");
            notice.Severity = InfoBarSeverity.Warning;
            notice.IsOpen = true;
            return;
        }

        SetBusy(true);
        try
        {
            await createFromVideos(paths);
            allowClose = true;
            Close();
        }
        catch (Exception error)
        {
            if (!closed) { SetBusy(false); ShowError(error); }
        }
    }
    private void ShowError(Exception error) { notice.Title = L.T("S79BE13BDF0C5"); notice.Message = error.Message; notice.Severity = InfoBarSeverity.Error; notice.IsOpen = true; }

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
        searchDelay.Stop(); visibleEntries = new(Filtered());
        list.ItemsSource = visibleEntries;
        UpdateSummary();
    }
    private void UpdateSummary()
    {
        count.Text = L.T("S90B234CEB9F7", visibleEntries.Count);
        empty.Visibility = visibleEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        empty.Text = entries.Count == 0 ? L.T("S2BF4A30A3122") : L.T("S4306AF5EF8E0");
    }
    private void RemoveDeletedEntry(string path)
    {
        entries = entries.Where(entry => !string.Equals(entry.Directory, path, StringComparison.OrdinalIgnoreCase)).ToArray();
        var visible = visibleEntries.FirstOrDefault(entry => string.Equals(entry.Directory, path, StringComparison.OrdinalIgnoreCase));
        if (visible is not null) visibleEntries.Remove(visible);
        UpdateSummary();
    }
    private Grid CreateRow(ProjectCatalogEntry entry)
    {
            var row = new Grid();
            var content = new Grid { ColumnSpacing = 14, Margin = new Thickness(0, 0, 32, 0) }; content.ColumnDefinitions.Add(new() { Width = new GridLength(42) }); content.ColumnDefinitions.Add(new());
            UIElement thumbnail = new EditorGlyph { Kind = "Film", Width = 22, Height = 22, Foreground = Brush("EditorAccent") };
            try {
                string cacheDir = Path.Combine(entry.Directory, "cache");
                if (Directory.Exists(cacheDir)) {
                    string? png = Directory.EnumerateFiles(cacheDir, "*.png").FirstOrDefault();
                    if (png is not null) {
                        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = 96, UriSource = new Uri(png) };
                        var preview = new Image { Source = bitmap, Stretch = Stretch.UniformToFill };
                        preview.ImageFailed += (_, _) => {
                            if (preview.Parent is Border host) host.Child = new EditorGlyph { Kind = "Film", Width = 22, Height = 22, Foreground = Brush("EditorAccent") };
                        };
                        thumbnail = preview;
                    }
                }
            } catch { }
            content.Children.Add(new Border { Width = 42, Height = 44, CornerRadius = new CornerRadius(12), Background = Brush("EditorWell"), Child = thumbnail });
            var details = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            var title = Text(entry.Name, 14); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; details.Children.Add(title);
            string duration = entry.RecordingSeconds is double seconds ? L.T("S63D1E10A091A", (int)(seconds / 60), (int)seconds % 60, entry.Width, entry.Height) : "";
            details.Children.Add(Text(entry.Error ?? duration + entry.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), 11, true));
            details.Children.Add(Text(entry.Directory, 10, true)); Grid.SetColumn(details, 1); content.Children.Add(details);
            var select = new Button { Content = content, Style = (Style)Application.Current.Resources["EditorButton"], HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(14, 12, 14, 12), Height = 92, Background = Brush("EditorPanel"), CornerRadius = new CornerRadius(12) };
            AutomationProperties.SetName(select, L.T("S83D632804077") + entry.Name); AutomationProperties.SetAutomationId(select, "projects.open." + EntryId(entry)); ToolTipService.SetToolTip(select, entry.Directory);
            select.Click += async (_, _) => {
                if (entry.CanOpen) await OpenAsync(entry);
                else { notice.Title = L.T("S9C50E8825F41"); notice.Message = entry.Error; notice.Severity = InfoBarSeverity.Warning; notice.IsOpen = true; }
            }; row.Children.Add(select);
            if (string.Equals(entry.Directory, currentProject, StringComparison.OrdinalIgnoreCase)) {
                title.Text += L.T("S94FB17438C8C");
            }
            var remove = new Button { Content = new EditorGlyph { Kind = "Close", Width = 14, Height = 14, Foreground = new SolidColorBrush(Colors.IndianRed) }, Style = (Style)Application.Current.Resources["EditorIconButton"], Width = 28, Height = 28, MinWidth = 0, MinHeight = 0, CornerRadius = new CornerRadius(8), Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            AutomationProperties.SetName(remove, L.T("SA19F315BE5D3") + entry.Name); AutomationProperties.SetAutomationId(remove, "projects.delete." + EntryId(entry)); ToolTipService.SetToolTip(remove, L.T("SF6DC6A110D09"));
            remove.Click += async (_, _) => await DeleteAsync(entry); row.Children.Add(remove);
            return row;
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
        details.Children.Add(new TextBlock { Text = L.T("S858392592478"), TextWrapping = TextWrapping.Wrap });
        details.Children.Add(new TextBlock { Text = entry.Directory, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .65 });
        var cancelStyle = CancelButtonStyle();
        confirmation = new ContentDialog {
            Title = L.T("S591F8EBAE3D1", entry.Name),
            Content = details,
            PrimaryButtonText = L.T("S6A5B1CB18FB6"),
            CloseButtonText = L.T("S2CD0F3BE8738"),
            DefaultButton = ContentDialogButton.Close,
            PrimaryButtonStyle = DeleteButtonStyle(),
            CloseButtonStyle = cancelStyle,
            XamlRoot = root.XamlRoot,
            RequestedTheme = root.RequestedTheme
        };
        ConfigureDialogButtons(confirmation, cancelStyle);
        confirming = true; ContentDialogResult result;
        try { result = await confirmation.ShowAsync(); }
        finally { confirming = false; confirmation = null; }
        if (result != ContentDialogResult.Primary || closed) return;
        SetBusy(true); notice.IsOpen = false;
        try { await delete(entry.Directory); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closed) ShowError(error); }
        finally {
            if (!closed) {
                // Only remove the confirmed missing entry; keep the source and surviving containers attached.
                if (!Directory.Exists(entry.Directory)) RemoveDeletedEntry(entry.Directory);
                SetBusy(false); Activate();
            }
        }
    }
    private static Style DeleteButtonStyle()
    {
        if (Application.Current.Resources.TryGetValue("EditorDangerButton", out var existing) && existing is Style danger)
            return danger;
        var style = new Style(typeof(Button)) { BasedOn = (Style)Application.Current.Resources["EditorButton"] };
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(ColorHelper.FromArgb(255, 0xE3, 0x5D, 0x62))));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Colors.White)));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, (FontFamily)Application.Current.Resources["RecorderSemiboldFont"]));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14, 0, 14, 0)));
        return style;
    }
    private static Style CancelButtonStyle()
    {
        if (Application.Current.Resources.TryGetValue("EditorDialogCancelButton", out var existing) && existing is Style cancel)
            return cancel;
        return (Style)Application.Current.Resources["EditorButton"];
    }
    internal static void ConfigureDialogButtons(ContentDialog dialog, Style cancelStyle)
    {
        dialog.Resources["DefaultButtonStyle"] = cancelStyle;
        dialog.Opened += (sender, _) => { if (sender is ContentDialog d) ApplyDialogButtonChrome(d); };
        dialog.LayoutUpdated += (_, _) => ApplyDialogButtonChrome(dialog);
    }
    internal static void ApplyDialogButtonChrome(ContentDialog dialog)
    {
        var editorButtonStyle = (Style)Application.Current.Resources["EditorButton"];
        var template = editorButtonStyle.Setters.OfType<Setter>().FirstOrDefault(s => s.Property == Control.TemplateProperty)?.Value as ControlTemplate;
        var font = (FontFamily)Application.Current.Resources["RecorderSemiboldFont"];
        void Walk(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Button button && button.Name is "PrimaryButton" or "SecondaryButton" or "CloseButton")
                {
                    if (template is not null && !ReferenceEquals(button.Template, template)) button.Template = template;
                    if (button.CornerRadius != new CornerRadius(15)) button.CornerRadius = new CornerRadius(15);
                    if (Math.Abs(button.Height - 30) > 0.01) button.Height = 30;
                    if (Math.Abs(button.MinHeight - 30) > 0.01) button.MinHeight = 30;
                    button.Padding = new Thickness(14, 0, 14, 0);
                    button.BorderThickness = new Thickness(0);
                    button.FontFamily = font;
                    button.FontSize = 12;
                    if (button.Name == "CloseButton")
                    {
                        button.Background = RecorderTheme.Brush(dialog, "EditorControlTrack");
                        button.Foreground = RecorderTheme.Brush(dialog, "EditorInk");
                    }
                }
                Walk(child);
            }
        }
        Walk(dialog);
    }
    private async Task BrowseAsync()
    {
        if (working || confirming) return;
        try {
            using var picker = new ProjectFolderPicker(folders.First(), L.T("S2BF6FE205DD8"), L.T("S754DD87ADE59"));
            string? selected = picker.Pick(WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (selected is null) return;
            catalog.RememberFolder(selected); await RefreshAsync();
        } catch (Exception error) { ShowError(error); }
    }
}
