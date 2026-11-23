using FrameTrail.Workspace;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace FrameTrail.App;

/// <summary>
/// Dedicated blank project workspace for creating a new FrameTrail project directly from
/// local MP4/MOV/M4V video files (via drag-and-drop or file picker) without screen recording.
/// </summary>
internal sealed class NewProjectWindow : Window
{
    private readonly Func<IReadOnlyList<string>, string?, string, Task> createProject;
    private readonly Action<string>? folderChanged;
    private readonly Grid root = new() { Padding = new Thickness(28, 24, 28, 24), RowSpacing = 16 };
    private readonly TextBox nameBox = new() { PlaceholderText = L.T("NewProjectNamePlaceholder"), MaxLength = 80 };
    private readonly TextBlock folderPathText = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button changeFolderButton = new() { Content = L.T("NewProjectChangeFolder") };
    private readonly Border metadataCard = new() { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(16, 14, 16, 14) };
    private readonly Border dropZone = new() { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(2), AllowDrop = true };
    private readonly Border iconCircle = new() { Width = 48, Height = 48, CornerRadius = new CornerRadius(24), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly EditorGlyph dropGlyph = new() { Kind = "Film", Width = 22, Height = 22 };
    private readonly TextBlock dropTitle = new() { Text = L.T("NewProjectDropTitle"), FontSize = 16, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock dropSubtitle = new() { Text = L.T("NewProjectDropSubtitle"), FontSize = 12, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 };
    private readonly Button chooseButton = new() { Content = L.T("NewProjectChooseVideos"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
    private readonly StackPanel idleDropContent = new() { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel busyDropContent = new() { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly ProgressRing busyRing = new() { Width = 28, Height = 28, IsActive = false };
    private readonly TextBlock busyLabel = new() { FontSize = 13, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly InfoBar notice = new() { IsClosable = true, IsOpen = false, Visibility = Visibility.Collapsed };
    private readonly Button cancelButton = new() { Content = L.T("NewProjectCancel") };
    private readonly TextBlock headingTitle;
    private readonly TextBlock headingSubtitle;
    private readonly TextBlock nameLabel;
    private readonly TextBlock folderLabel;

    private string currentFolder;
    private bool working, dragActive, closed, allowClose;

    internal NewProjectWindow(
        string initialFolder,
        Func<IReadOnlyList<string>, string?, string, Task> createProject,
        Action<string>? folderChanged = null)
    {
        this.currentFolder = initialFolder;
        this.createProject = createProject;
        this.folderChanged = folderChanged;

        Title = L.T("NewProjectWindowTitle");
        AppIcon.Apply(this);
        Content = root;
        root.RequestedTheme = EditorAppearance.Current;

        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });

        headingTitle = new TextBlock { Text = L.T("NewProjectHeading"), FontSize = 24, FontFamily = (FontFamily)Application.Current.Resources["RecorderSemiboldFont"] };
        headingSubtitle = new TextBlock { Text = L.T("NewProjectSubtitle"), FontSize = 13, TextWrapping = TextWrapping.Wrap };
        var heading = new StackPanel { Spacing = 4 };
        heading.Children.Add(headingTitle);
        heading.Children.Add(headingSubtitle);
        root.Children.Add(heading);

        // Project name + save folder metadata card
        nameLabel = new TextBlock { Text = L.T("NewProjectNameLabel"), FontSize = 12 };
        folderLabel = new TextBlock { Text = L.T("NewProjectFolderLabel"), FontSize = 12 };
        folderPathText.Text = currentFolder;
        ToolTipService.SetToolTip(folderPathText, currentFolder);

        changeFolderButton.Style = (Style)Application.Current.Resources["EditorButton"];
        changeFolderButton.Padding = new Thickness(12, 0, 12, 0);
        changeFolderButton.Click += (_, _) => PickFolder();

        var folderRow = new Grid { ColumnSpacing = 10 };
        folderRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        folderRow.Children.Add(folderPathText);
        Grid.SetColumn(changeFolderButton, 1);
        folderRow.Children.Add(changeFolderButton);

        var metaGrid = new Grid { ColumnSpacing = 16, RowSpacing = 6 };
        metaGrid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        metaGrid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        metaGrid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        metaGrid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        metaGrid.Children.Add(nameLabel);
        Grid.SetColumn(folderLabel, 1); metaGrid.Children.Add(folderLabel);
        Grid.SetRow(nameBox, 1); metaGrid.Children.Add(nameBox);
        Grid.SetRow(folderRow, 1); Grid.SetColumn(folderRow, 1); metaGrid.Children.Add(folderRow);
        metadataCard.Child = metaGrid;
        Grid.SetRow(metadataCard, 1);
        root.Children.Add(metadataCard);

        // Spacious center drag-and-drop zone
        dropTitle.FontFamily = (FontFamily)Application.Current.Resources["RecorderSemiboldFont"];
        chooseButton.Style = (Style)Application.Current.Resources["EditorPrimaryButton"];
        chooseButton.Padding = new Thickness(20, 0, 20, 0);
        chooseButton.Height = 38;
        chooseButton.Click += async (_, _) => await PickVideosAsync();

        iconCircle.Child = dropGlyph;
        idleDropContent.Children.Add(iconCircle);
        idleDropContent.Children.Add(dropTitle);
        idleDropContent.Children.Add(dropSubtitle);
        idleDropContent.Children.Add(chooseButton);

        busyDropContent.Children.Add(busyRing);
        busyDropContent.Children.Add(busyLabel);

        var dropInner = new Grid { Padding = new Thickness(24, 20, 24, 20) };
        dropInner.Children.Add(idleDropContent);
        dropInner.Children.Add(busyDropContent);
        var dropScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = dropInner
        };
        dropScroll.SizeChanged += (_, _) => dropInner.MinHeight = Math.Max(0, dropScroll.ViewportHeight);
        dropZone.Child = dropScroll;
        dropZone.DragOver += DropZone_DragOver;
        dropZone.DragLeave += DropZone_DragLeave;
        dropZone.Drop += DropZone_Drop;
        Grid.SetRow(dropZone, 2);
        root.Children.Add(dropZone);

        // Also accept drops anywhere on the root surface for effortless targeting.
        root.AllowDrop = true;
        root.DragOver += DropZone_DragOver;
        root.DragLeave += DropZone_DragLeave;
        root.Drop += DropZone_Drop;

        notice.Closed += (_, _) => notice.Visibility = Visibility.Collapsed;

        cancelButton.Style = (Style)Application.Current.Resources["EditorButton"];
        cancelButton.Padding = new Thickness(16, 0, 16, 0);
        cancelButton.HorizontalAlignment = HorizontalAlignment.Right;
        cancelButton.Click += (_, _) => { if (!working) Close(); };

        var footerStack = new StackPanel { Spacing = 12 };
        footerStack.Children.Add(notice);
        footerStack.Children.Add(cancelButton);
        Grid.SetRow(footerStack, 3);
        root.Children.Add(footerStack);

        AutomationProperties.SetAutomationId(nameBox, "newProject.name");
        AutomationProperties.SetName(nameBox, L.T("NewProjectNameLabel"));
        AutomationProperties.SetAutomationId(dropZone, "newProject.dropzone");
        AutomationProperties.SetName(dropZone, L.T("NewProjectDropTitle"));
        AutomationProperties.SetAutomationId(chooseButton, "newProject.pickFiles");
        AutomationProperties.SetAutomationId(changeFolderButton, "newProject.changeFolder");

        root.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape && !working)
            {
                e.Handled = true;
                Close();
            }
        };

        void ApplyTheme(ElementTheme theme)
        {
            root.RequestedTheme = theme;
            root.Background = Brush("EditorStage");
            headingTitle.Foreground = Brush("EditorInk");
            headingSubtitle.Foreground = Brush("EditorSecondary");
            nameLabel.Foreground = folderLabel.Foreground = Brush("EditorSecondary");
            folderPathText.Foreground = Brush("EditorInk");
            metadataCard.Background = Brush("EditorPanel");
            metadataCard.BorderBrush = Brush("EditorLine");
            nameBox.Resources["TextControlBorderBrushFocused"] = Brush("EditorAccent");
            iconCircle.Background = Brush("EditorAccentSoft");
            dropGlyph.Foreground = Brush("EditorAccent");
            dropTitle.Foreground = Brush("EditorInk");
            dropSubtitle.Foreground = Brush("EditorSecondary");
            busyLabel.Foreground = Brush("EditorInk");
            UpdateDropZoneStyle();
        }

        EditorAppearance.Changed += ApplyTheme;
        root.ActualThemeChanged += (_, _) => ApplyTheme(root.RequestedTheme);
        AppWindow.Closing += (_, args) => { if (!allowClose && working) args.Cancel = true; };
        Closed += (_, _) => { closed = true; EditorAppearance.Changed -= ApplyTheme; };
        ApplyTheme(EditorAppearance.Current);

        double scale = RecorderWindowChrome.Scale(this);
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new((int)Math.Min(740 * scale, area.Width), (int)Math.Min(640 * scale, area.Height)));
        AppWindow.Move(new(area.X + (area.Width - AppWindow.Size.Width) / 2, area.Y + (area.Height - AppWindow.Size.Height) / 2));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)Math.Min(540 * scale, area.Width);
            presenter.PreferredMinimumHeight = (int)Math.Min(560 * scale, area.Height);
        }
    }

    private Brush Brush(string key) => RecorderTheme.Brush(root, key);

    private void UpdateDropZoneStyle()
    {
        dropZone.Background = dragActive ? Brush("EditorAccentSoft") : Brush("EditorPanel");
        dropZone.BorderBrush = dragActive ? Brush("EditorAccent") : Brush("EditorLine");
        dropTitle.Text = dragActive ? L.T("NewProjectDropActive") : L.T("NewProjectDropTitle");
    }

    private void SetBusy(bool value, string message = "")
    {
        working = value;
        nameBox.IsEnabled = changeFolderButton.IsEnabled = chooseButton.IsEnabled = cancelButton.IsEnabled = !value;
        idleDropContent.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        busyDropContent.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        busyRing.IsActive = value;
        busyLabel.Text = message;
    }

    private void ShowError(string title, string message)
    {
        notice.Title = title;
        notice.Message = message;
        notice.Severity = InfoBarSeverity.Error;
        notice.Visibility = Visibility.Visible;
        notice.IsOpen = true;
    }

    private void PickFolder()
    {
        if (working) return;
        try
        {
            nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            using var picker = new ProjectFolderPicker(currentFolder, L.T("S53EC764876D9"), L.T("S314C49AF454C"));
            string? picked = picker.Pick(handle);
            if (string.IsNullOrWhiteSpace(picked)) return;
            currentFolder = picked;
            folderPathText.Text = picked;
            ToolTipService.SetToolTip(folderPathText, picked);
            folderChanged?.Invoke(picked);
        }
        catch (Exception error)
        {
            ShowError(L.T("S79BE13BDF0C5"), error.Message);
        }
    }

    private async Task PickVideosAsync()
    {
        if (working) return;
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".mov");
            picker.FileTypeFilter.Add(".m4v");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0) return;
            await CreateFromPathsAsync(files.Select(f => f.Path).ToArray());
        }
        catch (Exception error)
        {
            ShowError(L.T("NewProjectCreateFailed"), error.Message);
        }
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        if (working || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            if (dragActive) { dragActive = false; UpdateDropZoneStyle(); }
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Copy;
        if (e.DragUIOverride is not null)
            e.DragUIOverride.Caption = L.T("DropVideoCreateProjectPrompt");
        if (!dragActive) { dragActive = true; UpdateDropZoneStyle(); }
        e.Handled = true;
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        if (dragActive) { dragActive = false; UpdateDropZoneStyle(); }
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (dragActive) { dragActive = false; UpdateDropZoneStyle(); }
        if (working || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
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
            ShowError(L.T("NewProjectCreateFailed"), error.Message);
            return;
        }
        finally
        {
            deferral.Complete();
        }

        if (paths.Length == 0)
        {
            ShowError(L.T("NewProjectCreateFailed"), L.T("NewProjectUnsupportedFiles"));
            return;
        }

        await CreateFromPathsAsync(paths);
    }

    private async Task CreateFromPathsAsync(IReadOnlyList<string> paths)
    {
        if (working || closed || paths.Count == 0) return;
        notice.IsOpen = false;
        SetBusy(true, L.T("NewProjectCreating", paths.Count));
        try
        {
            string? requestedName = string.IsNullOrWhiteSpace(nameBox.Text) ? null : nameBox.Text.Trim();
            await createProject(paths, requestedName, currentFolder);
            allowClose = true;
            Close();
        }
        catch (Exception error)
        {
            if (!closed)
            {
                SetBusy(false);
                ShowError(L.T("NewProjectCreateFailed"), error.Message);
            }
        }
    }
}
