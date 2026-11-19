using FrameTrail.Domain;
using FrameTrail.Interop;
using FrameTrail.Workspace;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using System.Diagnostics;
using System.Threading.Channels;
using Windows.System;

namespace FrameTrail.App;

public sealed partial class EditorWindow : Window
{
    private readonly EditorSession editor;
    private readonly PreviewSurface preview = new();
    private readonly EditorTimeline timeline = new();
    private readonly EditorTransportController transport = new();
    private readonly EditorEditController editController;
    private readonly EditorAssetCoordinator assetCoordinator = new();
    private bool playing => transport.Playing;
    private Task audioStopWork => transport.StopWork;
    private EngineSession mapper = new();
    private CancellationTokenSource exportCancellation = new();
    private long position;
    private string? selected, preparedAudio;
    private SdrColor background = new();
    private (CanvasSettings Canvas, EditorSettings Editor)? copiedPicture;
    private bool ready, working, rendering, allowClose, closing;
    private string? validationDirectory;
    private int renderedFrames;
    private bool fittedTimeline, showingExport;
    private readonly TaskCompletionSource<bool> startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<string, Task>? openProject;
    private readonly Func<string, Task>? deleteProject;
    private readonly Action? browseProjects;
    private bool switchingProjects;
    private bool projectTransition;
    private bool showingDeleteConfirmation;
    private ContentDialog? deleteConfirmation;
    internal Task<bool> Startup => startup.Task;
    internal bool HasClosed => allowClose;

    internal EditorWindow(ProjectWorkspace workspace, string? validationDirectory = null, Func<string, Task>? openProject = null, Func<string, Task>? deleteProject = null, Action? browseProjects = null)
    {
        editor = new EditorSession(workspace);
        editController = new EditorEditController(editor);
        this.validationDirectory = validationDirectory;
        this.openProject = openProject;
        this.deleteProject = deleteProject;
        this.browseProjects = browseProjects;
        var launchArguments = Environment.GetCommandLineArgs();
        int presetTestIndex = Array.IndexOf(launchArguments, "--preset-library");
        if (launchArguments.Contains("--inspect-ui") && presetTestIndex >= 0 && launchArguments.Length > presetTestIndex + 1)
            presetLibrary = new VisualPresetLibrary(launchArguments[presetTestIndex + 1]);
        InitializeComponent(); Title = L.T("S5A421B10D046");
        ProjectTitle.Text = Path.GetFileNameWithoutExtension(workspace.DirectoryPath);
        PreviewMount.Children.Add(preview); TimelineMount.Children.Add(timeline);
        InitializeChrome();
        InitializeCanvasEditing();
        InitializeBrowsing();
        InitializeVisuals();
        InitializeSubtitles();
        InitializeAiCopilot();
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Min(1500, area.Width), Math.Min(980, area.Height)));
        if (AppWindow.Presenter is OverlappedPresenter presenter) { double scale = RecorderWindowChrome.Scale(this); presenter.PreferredMinimumWidth = (int)Math.Round(840 * scale); presenter.PreferredMinimumHeight = (int)Math.Round(600 * scale); }
        editor.Changed += (_, _) => DispatcherQueue.TryEnqueue(UpdateState);
        timeline.Seek += async ticks => { if (!working && !editor.IsExporting) await SafeAsync(() => SeekAsync(ticks)); };
        timeline.SelectClip += async id => { await SafeAsync(async () => { await FlushVisualChangesAsync(); selected = id; LoadInspector(); if (editController.Owner != EditorEditOwner.Timeline) RefreshTimeline(); }); };
        timeline.ClipDoubleClicked += async id => { await SafeAsync(async () => { await FlushVisualChangesAsync(); selected = id; LoadInspector(); ShowInspector(7); RefreshTimeline(); }); };
        timeline.ZoomChanged += value => { if (Math.Abs(TimelineZoom.Value - value) > 0.0001) TimelineZoom.Value = Math.Clamp(value, TimelineZoom.Minimum, TimelineZoom.Maximum); };
        timeline.BeginEdit += () => { if (working || editor.IsExporting) return; Pause(); editController.Begin(EditorEditOwner.Timeline); };
        timeline.PreviewEdit += command => {
            if (working || editor.IsExporting) return;
            try {
                var candidate = editController.Preview(EditorEditOwner.Timeline, command);
                var resolved = EngineSession.ResolveTimeline(candidate);
                long clampedPos = Math.Clamp(position, 0, Math.Max(0, resolved.Duration - 1));
                timeline.Set(resolved, selected, clampedPos);
            }
            catch (Exception error) { ShowNotice(L.T("S72DC2DB39CED"), error.Message); }
        };
        timeline.EndEdit += async commit => {
            if (working || editor.IsExporting) return;
            if (commit) await PerformAsync(() => editController.CommitAsync(EditorEditOwner.Timeline)); else { editController.CancelCurrent(); RefreshTimeline(); }
        };
        timeline.ThumbnailsNeeded += RequestThumbnails;
        timeline.EffectLanesChanged += UpdateTimelineEffectGutter;
        timeline.VerticalScrollChanged += SyncTimelineGutterScroll;
        preview.Failed += (_, error) => ShowNotice(L.T("SC759601593C2"), error.Message + L.T("SFC665652D1F0"), InfoBarSeverity.Warning);
        AppWindow.Changed += async (_, args) => {
            if (AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized) Pause();
            else if (ready && args.DidPresenterChange && !working && !closing) await SafeAsync(() => preview.RenderAsync(position));
        };
        Root.Loaded += async (_, _) => {
            if (ready) return; ready = true;
            bool initialized = false; working = true;
            await SafeAsync(async () => { await ReloadAsync(); initialized = true; });
            working = false; UpdateState();
            PlayButton.Focus(FocusState.Programmatic);
            UiAccessibility.NameControls(Root);
            startup.TrySetResult(initialized);
            if (this.validationDirectory is not null) {
                if (Environment.GetCommandLineArgs().Contains("--wallpapers-self-test")) await RunWallpaperValidationAsync(this.validationDirectory);
                else if (Environment.GetCommandLineArgs().Contains("--p2-self-test")) await RunP2ValidationAsync(this.validationDirectory);
                else if (Environment.GetCommandLineArgs().Contains("--p1-self-test")) await RunP1ValidationAsync(this.validationDirectory);
                else if (Environment.GetCommandLineArgs().Contains("--editor-polish-self-test")) await RunEditorPolishValidationAsync(this.validationDirectory);
                else if (Environment.GetCommandLineArgs().Contains("--fullscreen-self-test")) await RunFullscreenValidationAsync(this.validationDirectory);
                else if (Environment.GetCommandLineArgs().Contains("--mockup-self-test")) await RunMockupValidationAsync(this.validationDirectory);
                else if (Environment.GetCommandLineArgs().Contains("--m4-self-test")) await RunM4ValidationAsync(this.validationDirectory);
                else if (Environment.GetCommandLineArgs().Contains("--m3-self-test")) await RunM3ValidationAsync(this.validationDirectory);
                else await RunValidationAsync(this.validationDirectory);
            }
        };
        AppWindow.Closing += async (_, args) => {
            if (allowClose) return; args.Cancel = true;
            if (switchingProjects) return;
            if (projectTransition) return;
            await RequestCloseAsync();
        };
    }
    internal async Task RequestCloseAsync()
    {
        if (allowClose || closing || flushingPictureInputs) return;
        if (showingExport || showingDeleteConfirmation || showingPresets) return;
        if (working || editor.IsExporting) { ShowNotice(L.T("SCB3734A5E3A1"), L.T("SBA00FAC001D6")); return; }
        await FlushVisualChangesAsync();
        await FlushSubtitleStyleChangesAsync();
        await FlushPictureInputsAsync();
        closing = true;
        try { DisposeSubtitles(); DisposeAiCopilot(); Pause(); timeline.CancelDrag(); ValidationTrace("closing: flush"); await editor.FlushAsync(); ValidationTrace("closing: assets"); await StopAssetsAsync(); ValidationTrace("closing: preview"); await preview.DisposeAsync(); mapper.Dispose(); settingsWindow?.Close(); ValidationTrace("closing: complete"); allowClose = true; Close(); }
        catch (Exception error) { closing = false; ShowNotice(L.T("S8F52FE1C3C78"), error.Message + L.T("SBF47EE4B2133"), InfoBarSeverity.Error); }
    }
    internal async Task DiscardWindowAsync()
    {
        // Only used for an unopened destination; it has no user edits to save.
        DisposeSubtitles(); DisposeAiCopilot(); Pause(); closing = true; await StopAssetsAsync();
        await preview.DisposeAsync(); mapper.Dispose(); settingsWindow?.Close(); allowClose = true; Close();
    }
    internal void SetProjectTransition(bool value) { projectTransition = value; UpdateState(); }
    private async void OpenProject_Click(object sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        if (switchingProjects || closing || working || showingExport || showingDeleteConfirmation || editor.IsExporting || flushingPictureInputs || openProject is null) return;
        Pause(); await FlushVisualChangesAsync(); await FlushPictureInputsAsync();
        browseProjects?.Invoke();
    });
    internal async Task SwitchSelectedProjectAsync(string path)
    {
        if (switchingProjects || closing || working || showingExport || showingDeleteConfirmation || editor.IsExporting || flushingPictureInputs || openProject is null) throw new InvalidOperationException(L.T("SE8B814821605"));
        switchingProjects = true; UpdateState();
        try {
            Pause(); await FlushVisualChangesAsync(); await FlushPictureInputsAsync(); await editor.FlushAsync();
            await openProject(path);
        }
        finally { switchingProjects = false; if (!allowClose) UpdateState(); }
    }
    private async void DeleteProject_Click(object sender, RoutedEventArgs e) => await SafeAsync(RequestProjectDeletionAsync);
    internal async Task RequestProjectDeletionAsync()
    {
        if (switchingProjects || projectTransition || closing || working || showingExport || showingDeleteConfirmation || editor.IsExporting || deleteProject is null) return;
        Pause();
        var details = new StackPanel { Spacing = 12 };
        details.Children.Add(new TextBlock { Text = L.T("S565118DEFB85", Path.GetFileNameWithoutExtension(editor.DirectoryPath)), TextWrapping = TextWrapping.Wrap });
        details.Children.Add(new TextBlock { Text = editor.DirectoryPath, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.65 });
        details.Children.Add(new TextBlock { Text = L.T("S15BB8FA512EA"), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var cancelStyle = CancelButtonStyle();
        deleteConfirmation = new ContentDialog { Title = L.T("S761F0E313D82"), Content = details, PrimaryButtonText = L.T("SA3EA3C17B401"), CloseButtonText = L.T("S2CD0F3BE8738"), DefaultButton = ContentDialogButton.Close, PrimaryButtonStyle = DangerButtonStyle(), CloseButtonStyle = cancelStyle, XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme };
        ProjectLibraryWindow.ConfigureDialogButtons(deleteConfirmation, cancelStyle);
        ContentDialogResult result;
        showingDeleteConfirmation = true; UpdateState();
        try { result = await deleteConfirmation.ShowAsync(); }
        finally { showingDeleteConfirmation = false; deleteConfirmation = null; UpdateState(); }
        if (result == ContentDialogResult.Primary) await DeleteCurrentProjectAsync();
    }
    internal async Task DeleteCurrentProjectAsync()
    {
        if (switchingProjects || projectTransition || closing || working || showingExport || showingDeleteConfirmation || editor.IsExporting || deleteProject is null) throw new InvalidOperationException(L.T("S07B878043412"));
        switchingProjects = true; UpdateState();
        try { Pause(); await FlushVisualChangesAsync(); await FlushPictureInputsAsync(); await editor.FlushAsync(); await deleteProject(editor.DirectoryPath); }
        finally { switchingProjects = false; if (!allowClose) UpdateState(); }
    }

    private static Style DangerButtonStyle()
    {
        if (Application.Current.Resources.TryGetValue("EditorDangerButton", out var existing) && existing is Style style) return style;
        var danger = new Style(typeof(Button)) { BasedOn = (Style)Application.Current.Resources["EditorButton"] };
        danger.Setters.Add(new Setter(Control.BackgroundProperty, new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 227, 93, 98))));
        danger.Setters.Add(new Setter(Control.ForegroundProperty, new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)));
        danger.Setters.Add(new Setter(Control.FontFamilyProperty, (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["RecorderSemiboldFont"]));
        danger.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14, 0, 14, 0)));
        return danger;
    }
    private static Style CancelButtonStyle()
    {
        if (Application.Current.Resources.TryGetValue("EditorDialogCancelButton", out var existing) && existing is Style style) return style;
        return (Style)Application.Current.Resources["EditorButton"];
    }
    private ResolvedClip? SelectedClip => editor.Timeline.Clips.FirstOrDefault(c => c.Id == selected);
    private async Task SafeAsync(Func<Task> action)
    {
        try { await action(); } catch (OperationCanceledException) { } catch (Exception error) { ShowNotice(L.T("S79BE13BDF0C5"), error.Message, InfoBarSeverity.Error); }
    }
    private async Task PerformAsync(Func<Task> action)
    {
        if (working || editor.IsExporting) return;
        await FlushVisualChangesAsync();
        if (working || editor.IsExporting) return; working = true; Pause(); timeline.EditingEnabled = false;
        await transport.StopWork;
        var before = editor.Snapshot; var sourcePosition = mapper.MapOutputTime(position)?.Ticks;
        try { await action(); if (editor.Snapshot.Revision != before.Revision) await UpdateEditedProjectAsync(before, sourcePosition); }
        catch (Exception error) { ShowNotice(L.T("S6ACBD42B6557"), error.Message, InfoBarSeverity.Warning); RefreshTimeline(); }
        finally { working = false; timeline.EditingEnabled = true; UpdateState(); QueuePictureInputCommit(); }
    }
    private async Task EditAsync(object command) => await PerformAsync(() => editor.ExecuteAsync(command));
    private async Task ReloadAsync()
    {
        Pause(); await StopAssetsAsync(); audioCacheKey = null; mediaCache?.Unpin(preparedAudio);
        preparedAudio = null; timeline.ClearAssets();
        mapper.Dispose(); mapper = new EngineSession(); mapper.LoadProject(editor.Snapshot);
        if (!editor.Timeline.Clips.Any(c => c.Id == selected)) selected = editor.Timeline.Clips.First().Id;
        position = Math.Clamp(position, 0, Math.Max(0, editor.Timeline.Duration - 1));
        await preview.LoadAsync(editor.Snapshot, editor.DirectoryPath);
        if (closing) return;
        SizePreview(); await preview.RenderAsync(position);
        LoadInspector(); RefreshTimeline(); UpdateClock(); UpdateState();
        if (!fittedTimeline && TimelineMount.ActualWidth > 0) { FitTimeline_Click(this, new RoutedEventArgs()); fittedTimeline = true; }
        await StartAssetsAsync(true, true);
    }
    private static async Task<float[]> ReadWaveformAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        int buckets = (int)Math.Clamp((file.Length - 44) / (48000 * 4 / 20), 1, 24000); var result = new float[buckets];
        var buffer = new byte[8192]; long bytes = Math.Max(1, file.Length - 44); file.Position = 44; long read = 0;
        while (true) {
            token.ThrowIfCancellationRequested(); int count = await file.ReadAsync(buffer, token); if (count == 0) break;
            for (int i = 0; i + 1 < count; i += 2) { int bucket = (int)Math.Clamp((read + i) * buckets / bytes, 0, buckets - 1); result[bucket] = Math.Max(result[bucket], Math.Abs(BitConverter.ToInt16(buffer, i) / 32768f)); } read += count;
        }
        return result;
    }
    private void PreviewStage_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTransportLayout();
    private void UpdateTransportLayout()
    {
        if (PreviewStage is null || PreviewProgressScrubber is null || PreviewTransport is null || TransportLeft is null || FullscreenButton is null) return;
        bool stacked = !previewFullscreen && PreviewStage.ActualWidth > 0 && PreviewStage.ActualWidth < 680;
        Grid.SetRow(PreviewProgressScrubber, stacked ? 0 : 1);
        Grid.SetColumn(PreviewProgressScrubber, stacked ? 0 : 1);
        Grid.SetColumnSpan(PreviewProgressScrubber, stacked ? 2 : 1);
        Grid.SetRow(FullscreenButton, stacked ? 0 : 1);
        Grid.SetColumn(FullscreenButton, 2);
        Grid.SetColumnSpan(TransportLeft, stacked ? 3 : 1);
        TransportLeft.HorizontalAlignment = stacked ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        PreviewProgressScrubber.Margin = stacked ? new Thickness(10, 2, 4, 0) : new Thickness(4, 0, 6, 0);
        PreviewTransport.Margin = previewFullscreen ? new Thickness(24, 0, 24, 20) : (stacked ? new Thickness(12, 8, 12, 10) : new Thickness(20, 12, 20, 14));
        PreviewTransport.CornerRadius = new CornerRadius(stacked ? 18 : 22);
    }
    private void RefreshTimeline() { timeline.FrameTicks = TimeSpan.TicksPerSecond / Math.Max(1, editor.Snapshot.Export.Fps.Numerator); timeline.Set(editor.Timeline, selected, position); PreviewProgressScrubber.SetProgress(position, editor.Timeline.Duration); TimelineSummary.Text = L.T("S788BF0579E4C", editor.Timeline.Clips.Count, Format(editor.Timeline.Duration)); UpdateAiContextPills(); }
    private void UpdateState()
    {
        if (!ready) return;
        QueueCanvasGeometry();
        Root.IsHitTestVisible = !switchingProjects && !projectTransition;
        SaveStatus.Text = editor.IsExporting ? L.T("SC8921F15167F") : editor.IsDirty ? L.T("S6A1BE012C99C") : L.T("S1BD91A7D0C53");
        RetrySave.Visibility = editor.IsDirty ? Visibility.Visible : Visibility.Collapsed;
        UndoButton.IsEnabled = !working && !switchingProjects && !editor.IsExporting && editor.CanUndo; RedoButton.IsEnabled = !working && !switchingProjects && !editor.IsExporting && editor.CanRedo;
        PlayButton.IsEnabled = !working && !switchingProjects && !editor.IsExporting;
        InspectorMode.IsEnabled = InspectorScroll.IsHitTestVisible = !working && !switchingProjects && !editor.IsExporting;
        timeline.EditingEnabled = !working && !switchingProjects && !editor.IsExporting && !visualTransaction && !visualPending && !committingVisuals;
        ExportLabel.Text = editor.IsExporting ? L.T("S7EEFAAFF791F") : L.T("S76420433F22D");
        ProjectMenuButton.IsEnabled = !working && !closing && !switchingProjects && !showingExport && !showingDeleteConfirmation && !editor.IsExporting;
        ImportClipButton.IsEnabled = ImportClipsMenuItem.IsEnabled = CanImportClips;
        SwitchProjectMenuItem.IsEnabled = openProject is not null && ProjectMenuButton.IsEnabled;
        DeleteProjectButton.IsEnabled = DeleteProjectMenuItem.IsEnabled = deleteProject is not null && ProjectMenuButton.IsEnabled && !projectTransition;
        if (editor.SaveError is not null) ShowNotice(L.T("S555A663BC512"), editor.SaveError + L.T("SF697646FD2A3"), InfoBarSeverity.Error);
    }
    private void LoadInspector()
    {
        LoadCursor();
        loadingInspector = true;
        try {
        var p = editor.Snapshot; var settings = p.Editor ?? new(); background = p.Canvas.Background;
        CanvasWidth.Value = p.Canvas.Width; CanvasHeight.Value = p.Canvas.Height;
        var ratio = (double)p.Canvas.Width / p.Canvas.Height; var ratios = new[] { 0, 16.0 / 9, 9.0 / 16, 1.0, 4.0 / 3 };
        AspectRatio.SelectedIndex = Enumerable.Range(1, 4).FirstOrDefault(i => Math.Abs(ratios[i] - ratio) < 0.003, 5);
        PaddingValue.Value = p.Canvas.Padding * 100; CornerValue.Value = settings.CornerRadius; ShadowValue.Value = settings.Shadow; CursorValue.Value = settings.CursorScale;
        CameraVisible.IsChecked = p.Canvas.Camera.Enabled; CameraVisible.IsEnabled = p.Media.Any(m => m.Kind == MediaKind.Camera); CameraMirror.IsChecked = p.Canvas.Camera.Mirror; CameraSize.Value = p.Canvas.Camera.Width * 100;
        CameraX.Value = p.Canvas.Camera.X * 100; CameraY.Value = p.Canvas.Camera.Y * 100;
        CropEnabled.IsChecked = settings.Crop is not null; CropX.Value = (settings.Crop?.X ?? 0) * 100; CropY.Value = (settings.Crop?.Y ?? 0) * 100; CropWidth.Value = (settings.Crop?.Width ?? 1) * 100; CropHeight.Value = (settings.Crop?.Height ?? 1) * 100;
        PrimaryAudioLabel.Text = ClipPrimaryAudioLabel.Text = p.PrimarySource?.Kind == "androidUsb" ? L.T("S060AB6DF2ED4") : L.T("SAAA9A8F072BD");
        SystemGain.Value = settings.SystemGain * 100; MicGain.Value = settings.MicrophoneGain * 100; SystemMute.IsChecked = settings.SystemMuted; MicMute.IsChecked = settings.MicrophoneMuted;
        var level = selected is not null && settings.ClipAudio.TryGetValue(selected, out var value) ? value : new AudioLevel();
        ClipSystemGain.Value = level.SystemGain * 100; ClipMicGain.Value = level.MicrophoneGain * 100; ClipSystemMute.IsChecked = level.SystemMuted; ClipMicMute.IsChecked = level.MicrophoneMuted;
        if (SelectedClip is ResolvedClip c) {
            ClipLabel.Text = L.T("S9D4973468893", editor.Timeline.Clips.IndexOf(c) + 1); TrimStart.Value = c.SourceStart / 10_000_000.0; TrimEnd.Value = (c.SourceStart + c.SourceDuration) / 10_000_000.0;
            var rates = new[] { 0.25, 0.5, 1, 1.5, 2, 4 }; RateValue.SelectedIndex = Array.IndexOf(rates, (double)c.Numerator / c.Denominator);
        }
        RatioHudLabel.Text = (AspectRatio.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? L.T("S82A59F3D1C16");
        UpdateBackgroundChoice();
        } finally { loadingInspector = false; }
        RefreshVisualInspector();
        RefreshSubtitleInspector();
    }
    private void ShowNotice(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational) { if (previewFullscreen) SetPreviewFullscreen(false); Notice.Title = title; Notice.Message = message; Notice.Severity = severity; Notice.IsOpen = true; }
    private static string Format(long ticks) => TimeSpan.FromTicks(ticks).ToString(ticks >= TimeSpan.TicksPerHour ? @"h\:mm\:ss\.ff" : @"mm\:ss\.ff");
    private void UpdateClock() { QueueCanvasGeometry(); ClockLabel.Text = $"{Format(position)} / {Format(editor.Timeline.Duration)}"; PreviewProgressScrubber.SetProgress(position, editor.Timeline.Duration); timeline.Position(position); if (playing) timeline.FollowPlayhead(position); QueueMockupPoster(); NotifyAiContextThrottled(); }
    private async Task SeekAsync(long ticks)
    {
        DismissSubtitleInlineEditor(commit: true);
        await BrowseSeekAsync(ticks);
    }
    private async Task StartPlaybackAsync()
    {
        if (editor.IsExporting || preview.Session is null) return;
        DismissSubtitleInlineEditor(commit: true);
        CancelBrowsing();
        if (cropEditing) await ToggleCropEditingAsync();
        canvasOverlay.Finish(true);await canvasCommitWork;
        if (position >= editor.Timeline.Duration - 1) position = 0;
        var current = preview.Session;
        if (current is null) return;
        bool hasAudio = !previewMuted && editor.Snapshot.Media.Any(m => m.Kind is MediaKind.Microphone or MediaKind.SystemAudio or MediaKind.AndroidAudio);
        if (!await transport.StartAsync(position, hasAudio, () => Task.Run(() => current.StreamAudio(position)),
            () => !closing && current == preview.Session)) return;
        canvasOverlay.SetItems([]);
        timeline.FollowPlayhead(position);
        PlayGlyph.Kind = "Pause";
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= PlaybackRendering;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += PlaybackRendering;
    }
    private void Pause(bool cancelBrowsing = true)
    {
        if (cancelBrowsing) CancelBrowsing();
        transport.Pause(preview.Session is { } current ? () => Task.Run(current.StopAudio) : null, SafeDiagnostics.Record);
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= PlaybackRendering;
        PlayGlyph.Kind = "Play";
        QueueCanvasGeometry();
    }
    private async Task TickAsync()
    {
        if (!playing || rendering || preview.Session is null || closing) return;
        rendering = true;
        try {
            var status = preview.Session.Status;
            if (transport.UsesAudio && status.AudioRunning == 3) { Pause(); ShowNotice(L.T("SC73025417431"), L.T("SF83C898D4E60")); return; }
            var time = transport.Position(status.AudioTicks);
            if (time >= editor.Timeline.Duration - TimeSpan.TicksPerMillisecond) { Pause(); position = Math.Max(0, editor.Timeline.Duration - 1); }
            else position = Math.Max(0, time);
            await preview.RenderAsync(position, false); renderedFrames++; UpdateClock();
        } catch (Exception error) { Pause(); ShowNotice(L.T("S78D32B15407C"), error.Message, InfoBarSeverity.Error); }
        finally { rendering = false; }
    }
    private async void PlaybackRendering(object? sender, object args)
    {
        if (!playing || rendering || !transport.ShouldRender()) return;
        await TickAsync();
    }
    private async void Play_Click(object sender, RoutedEventArgs e) => await SafeAsync(async () => { if (playing) Pause(); else await StartPlaybackAsync(); });
    private async void PreviousFrame_Click(object sender, RoutedEventArgs e) => await SafeAsync(() => SeekAsync(position - TimeSpan.TicksPerSecond / editor.Snapshot.Export.Fps.Numerator));
    private async void NextFrame_Click(object sender, RoutedEventArgs e) => await SafeAsync(() => SeekAsync(position + TimeSpan.TicksPerSecond / editor.Snapshot.Export.Fps.Numerator));
    private async void Undo_Click(object sender, RoutedEventArgs e) => await PerformAsync(editor.UndoAsync);
    private async void Redo_Click(object sender, RoutedEventArgs e) => await PerformAsync(editor.RedoAsync);
    private async void ReloadPreview_Click(object sender, RoutedEventArgs e) => await PerformAsync(ReloadAsync);
    private async void Split_Click(object sender, RoutedEventArgs e)
    {
        var mapped = mapper.MapOutputTime(position); if (mapped is null) return;
        await EditAsync(new { op = "split", segmentId = mapped.SegmentId, sourceTicks = mapped.Ticks, newId = "clip-" + Guid.NewGuid().ToString("N") });
    }
    private async void Delete_Click(object sender, RoutedEventArgs e) { if (selected is not null) await EditAsync(new { op = "delete", segmentId = selected }); }
    private async void Restore_Click(object sender, RoutedEventArgs e) => await EditAsync(new { op = "restore", idPrefix = "original-" + Guid.NewGuid().ToString("N") + "-" });
    private async void MoveEarlier_Click(object sender, RoutedEventArgs e) { if (SelectedClip is ResolvedClip c) await EditAsync(new { op = "move", segmentId = c.Id, index = Math.Max(0, editor.Timeline.Clips.IndexOf(c) - 1) }); }
    private async void MoveLater_Click(object sender, RoutedEventArgs e) { if (SelectedClip is ResolvedClip c) await EditAsync(new { op = "move", segmentId = c.Id, index = Math.Min(editor.Timeline.Clips.Count - 1, editor.Timeline.Clips.IndexOf(c) + 1) }); }
    private bool trimInputCommitQueued;
    private bool IsTrimInputFocused() => Root.XamlRoot is not null &&
        InputAncestor<NumberBox>(FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject) is { } number &&
        (ReferenceEquals(number, TrimStart) || ReferenceEquals(number, TrimEnd));
    private void TrimInput_LostFocus(object sender, RoutedEventArgs e)
    {
        if (trimInputCommitQueued || loadingInspector || !ready || closing) return;
        trimInputCommitQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () => {
            trimInputCommitQueued = false;
            if (!ready || loadingInspector || working || closing || editor.IsExporting || IsTrimInputFocused()) return;
            if (SelectedClip is not ResolvedClip clip) return;
            if (!double.IsFinite(TrimStart.Value) || !double.IsFinite(TrimEnd.Value)) return;
            long startTicks = Math.Max(0, (long)Math.Round(TrimStart.Value * 10_000_000));
            long endTicks = Math.Max(0, (long)Math.Round(TrimEnd.Value * 10_000_000));
            if (endTicks <= startTicks) return;
            if (startTicks == clip.SourceStart && endTicks == clip.SourceStart + clip.SourceDuration) return;
            await SafeAsync(() => EditAsync(new { op = "trim", segmentId = clip.Id, sourceStart = startTicks, sourceEnd = endTicks }));
        });
    }
    private async void ApplyTrim_Click(object sender, RoutedEventArgs e) => await SafeAsync(async () => { if (selected is not null) await EditAsync(new { op = "trim", segmentId = selected, sourceStart = checked((long)(TrimStart.Value * 10_000_000)), sourceEnd = checked((long)(TrimEnd.Value * 10_000_000)) }); });
    private async void ApplyRate_Click(object sender, RoutedEventArgs e)
    {
        if (selected is null || RateValue.SelectedIndex < 0) return; var rates = new[] { (1, 4), (1, 2), (1, 1), (3, 2), (2, 1), (4, 1) }; var rate = rates[RateValue.SelectedIndex];
        await EditAsync(new { op = "rate", segmentId = selected, numerator = rate.Item1, denominator = rate.Item2 });
    }
    private async void ApplyPicture_Click(object sender, RoutedEventArgs e) => await ApplyPictureAsync();
    private async Task ApplyPictureAsync() => await SafeAsync(async () => {
        var p = editor.Snapshot; var screen = p.Media.First(m => m.Kind == MediaKind.Screen); double ratio = AspectRatio.SelectedIndex switch { 1 => 16.0 / 9, 2 => 9.0 / 16, 3 => 1, 4 => 4.0 / 3, _ => (double)screen.Width / screen.Height };
        uint width = AspectRatio.SelectedIndex == 5 ? Even(CanvasWidth.Value) : p.Canvas.Width, height = AspectRatio.SelectedIndex == 5 ? Even(CanvasHeight.Value) : Even(p.Canvas.Width / ratio);
        if (AspectRatio.SelectedIndex != 5 && height > 3840) { height = 3840; width = Even(height * ratio); } ratio = (double)width / height;
        var cameraMedia = p.Media.FirstOrDefault(m => m.Kind == MediaKind.Camera); var cameraAspect = cameraMedia is null ? 0.75 : (double)cameraMedia.Height / cameraMedia.Width;
        bool resizeCamera = Math.Abs(CameraSize.Value / 100 - p.Canvas.Camera.Width) > 0.000001 || width != p.Canvas.Width || height != p.Canvas.Height;
        var camera = p.Canvas.Camera with { Enabled = CameraVisible.IsChecked == true, Mirror = CameraMirror.IsChecked == true, X = CameraX.Value / 100, Y = CameraY.Value / 100, Width = CameraSize.Value / 100, Height = resizeCamera ? Math.Min(0.6, CameraSize.Value / 100 * ratio * cameraAspect) : p.Canvas.Camera.Height };
        camera = camera with { X = Math.Min(camera.X, 1 - camera.Width), Y = Math.Min(camera.Y, 1 - camera.Height) };
        var canvas = p.Canvas with { Width = width, Height = height, Padding = PaddingValue.Value / 100, Background = background, Camera = camera };
        var settings = (p.Editor ?? new()) with { CornerRadius = CornerValue.Value, Shadow = ShadowValue.Value, CursorScale = CursorValue.Value,
            Crop = CropEnabled.IsChecked == true ? new NormalizedRect { X = CropX.Value / 100, Y = CropY.Value / 100, Width = CropWidth.Value / 100, Height = CropHeight.Value / 100 } : null };
        if (ProjectJson.Serialize(p with { Canvas = canvas, Editor = settings }) == ProjectJson.Serialize(p)) return;
        await EditAsync(new { op = "settings", canvas, editor = settings });
    });
    private async void ApplyAudio_Click(object sender, RoutedEventArgs e)
    {
        var p = editor.Snapshot; var settings = p.Editor ?? new(); var levels = settings.ClipAudio.ToDictionary(k => k.Key, k => k.Value);
        if (selected is not null) levels[selected] = new AudioLevel { SystemGain = ClipSystemGain.Value / 100, MicrophoneGain = ClipMicGain.Value / 100, SystemMuted = ClipSystemMute.IsChecked == true, MicrophoneMuted = ClipMicMute.IsChecked == true };
        settings = settings with { SystemGain = SystemGain.Value / 100, MicrophoneGain = MicGain.Value / 100, SystemMuted = SystemMute.IsChecked == true, MicrophoneMuted = MicMute.IsChecked == true, ClipAudio = levels };
        await EditAsync(new { op = "settings", editor = settings });
    }
    private async void Background_Click(object sender, RoutedEventArgs e) {
        if ((sender as Button)?.Tag is not string hex) return;
        await ApplyBackgroundColorAsync(hex);
    }
    private async Task ApplyBackgroundColorAsync(string hex) {
        background = new SdrColor { R = Convert.ToInt32(hex.Substring(1, 2), 16) / 255.0, G = Convert.ToInt32(hex.Substring(3, 2), 16) / 255.0, B = Convert.ToInt32(hex.Substring(5, 2), 16) / 255.0, A = 1 };
        var prior = editor.Snapshot.Editor ?? new();
        await EditAsync(new { op = "settings", canvas = editor.Snapshot.Canvas with { Background = background }, editor = prior with { Backdrop = prior.Backdrop?.Kind == "gradient" ? prior.Backdrop : new BackdropSettings() } });
    }
    private void CopyPicture_Click(object sender, RoutedEventArgs e) { copiedPicture = (editor.Snapshot.Canvas, editor.Snapshot.Editor ?? new()); ShowNotice(L.T("SFBFEC8DBFCDF"), L.T("S67016195BB87")); }
    private async void PastePicture_Click(object sender, RoutedEventArgs e) { if (copiedPicture is not { } copied) return; var current = editor.Snapshot.Editor ?? new(); var settings = current with { CornerRadius = copied.Editor.CornerRadius, Shadow = copied.Editor.Shadow, CursorScale = copied.Editor.CursorScale, CursorSmoothing = copied.Editor.CursorSmoothing, Crop = copied.Editor.Crop, Backdrop = copied.Editor.Backdrop, FrameStyle = copied.Editor.FrameStyle, CameraShape = copied.Editor.CameraShape }; await EditAsync(new { op = "settings", canvas = copied.Canvas, editor = settings }); }
    private void InspectorMode_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (PictureSettings is null || InspectorTabs is null) return; ShowInspector(InspectorMode.SelectedIndex switch { 1 => 5, 2 => 7, _ => 0 }); }
    private void PictureTab_Click(object sender, RoutedEventArgs e) => ShowInspector(0);
    private void AudioTab_Click(object sender, RoutedEventArgs e) => ShowInspector(5);
    private void ClipTab_Click(object sender, RoutedEventArgs e) => ShowInspector(7);
    private void TimelineZoom_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) { if (timeline is not null) timeline.PixelsPerSecond = e.NewValue; }
    private void CanvasZoom_SelectionChanged(object sender, SelectionChangedEventArgs e) => SizePreview();
    private void PreviewContainer_SizeChanged(object sender, SizeChangedEventArgs e) => SizePreview();
    private void SizePreview() { if (PreviewMount is null || PreviewContainer is null || CanvasZoom is null) return; double zoom = previewFullscreen ? 1 : CanvasZoom.SelectedIndex switch { 1 => 1.25, 2 => 1.5, 3 => 2, _ => 1 }; double ratio = (double)editor.Snapshot.Canvas.Width / editor.Snapshot.Canvas.Height; double width = Math.Max(2, Math.Min(PreviewContainer.ActualWidth, PreviewContainer.ActualHeight * ratio)); CanvasScroll.Width = width; CanvasScroll.Height = width / ratio; PreviewMount.Width = width * zoom; PreviewMount.Height = width / ratio * zoom; canvasOverlay.CanvasAspect = ratio; }
    private async void RetrySave_Click(object sender, RoutedEventArgs e) => await SafeAsync(editor.FlushAsync);
    private void Sources_Click(object sender, RoutedEventArgs e) { try { Process.Start(new ProcessStartInfo("explorer.exe", editor.DirectoryPath) { UseShellExecute = true }); } catch (Exception error) { ShowNotice(L.T("SDC6AD02E12FB"), error.Message); } }
    internal void ReportProjectError(string title, string message) => ShowNotice(title, message, InfoBarSeverity.Error);
    private static uint Even(double n) => (uint)Math.Max(2, Math.Round(n / 2) * 2);
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (editor.IsExporting) { exportCancellation.Cancel(); return; }
        if (working || switchingProjects || showingExport || showingDeleteConfirmation || flushingPictureInputs) return;
        await FlushVisualChangesAsync(); await FlushSubtitleStyleChangesAsync(); await FlushPictureInputsAsync(); Pause(); showingExport = true;
        await SafeAsync(async () => {
            var coverPath = Path.Combine(editor.DirectoryPath, "cache", $"editor-{editor.Snapshot.Revision}-export-{position}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(coverPath)!); await preview.RenderAsync(position, true, coverPath);
            var cover = File.Exists(coverPath) ? new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(coverPath)) : BackgroundPreview.Source;
            var codecs = await Task.Run(CaptureSession.GetVideoCodecs);
            var dialog = new EditorExportDialog(ProjectTitle.Text, editor.Timeline.Duration, editor.Snapshot.Canvas.Width, editor.Snapshot.Canvas.Height, cover, ExportWithOptionsAsync, codecs, editor.Snapshot.Export.VideoCodec, hasSubtitles: HasSubtitles, initialBurnSubtitles: editor.Snapshot.Export.BurnSubtitles) { XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme };
            try { await dialog.ShowAsync(); } finally { showingExport = false; }
        });
        showingExport = false;
    }
    private Task<string?> ExportToAsync(int format, int resolution, int fps, string videoCodec, IProgress<MediaStatus> progress, CancellationToken cancellation) =>
        ExportWithOptionsAsync(new EditorExportOptions(format, resolution, fps, videoCodec, editor.Snapshot.Export.BurnSubtitles, false, SubtitleExportFormat.Srt), progress, cancellation);

    private async Task<string?> ExportWithOptionsAsync(EditorExportOptions options, IProgress<MediaStatus> progress, CancellationToken cancellation)
    {
        string? path;
        bool sources = options.Kind == 2, audioOnly = options.Kind == 1, subtitlesOnly = options.Kind == 3;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        if (subtitlesOnly)
        {
            string ext = options.SubtitleFormat switch { SubtitleExportFormat.Vtt => ".vtt", SubtitleExportFormat.Txt => ".txt", _ => ".srt" };
            string label = options.SubtitleFormat switch { SubtitleExportFormat.Vtt => "WebVTT Subtitle", SubtitleExportFormat.Txt => "Plain Text", _ => "SubRip Subtitle" };
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = ProjectTitle.Text };
            picker.FileTypeChoices.Add(label, new[] { ext });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return null;
            string content = SrtVttCodec.Export(editor.Snapshot, editor.Timeline, options.SubtitleFormat);
            await File.WriteAllTextAsync(file.Path, content, new System.Text.UTF8Encoding(false), cancellation);
            progress.Report(new MediaStatus { Stage = 4, Completed = 1, Total = 1 });
            return file.Path;
        }

        if (sources)
        {
            var picker = new Windows.Storage.Pickers.FolderPicker(); picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync(); if (folder is null) return null;
            path = Path.Combine(folder.Path, ProjectTitle.Text + L.T("SE72999A484D4") + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".frametrailproject");
        }
        else
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = ProjectTitle.Text };
            picker.FileTypeChoices.Add(audioOnly ? L.T("S2446F46DF86E") : L.T("SAB5C34012DBC"), new[] { audioOnly ? ".m4a" : ".mp4" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSaveFileAsync(); if (file is null) return null; path = file.Path;
            var size = new[] { 1080, 1440, 2160 }[options.ResolutionIndex]; double ratio = (double)editor.Snapshot.Canvas.Width / editor.Snapshot.Canvas.Height;
            var output = editor.Snapshot.Export with
            {
                Width = Even(size * ratio),
                Height = (uint)size,
                Fps = new Rational { Numerator = options.FpsIndex == 1 ? 60 : 30 },
                VideoCodec = audioOnly ? editor.Snapshot.Export.VideoCodec : options.VideoCodec,
                BurnSubtitles = audioOnly ? editor.Snapshot.Export.BurnSubtitles : options.BurnSubtitles
            };
            var before = editor.Snapshot; var sourcePosition = SourcePosition;
            await editor.ExecuteAsync(new { op = "settings", export = output }); await UpdateEditedProjectAsync(before, sourcePosition);
        }
        exportCancellation.Dispose(); exportCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        try
        {
            if (sources) await editor.CopySourcesAsync(path, exportCancellation.Token);
            else
            {
                await editor.ExportAsync(path, audioOnly, progress, exportCancellation.Token);
                if (!audioOnly && options.ExportSidecarSubtitles && HasSubtitles)
                {
                    string sidecarExt = options.SubtitleFormat switch { SubtitleExportFormat.Vtt => ".vtt", SubtitleExportFormat.Txt => ".txt", _ => ".srt" };
                    string sidecarPath = Path.ChangeExtension(path, sidecarExt);
                    string sidecarContent = SrtVttCodec.Export(editor.Snapshot, editor.Timeline, options.SubtitleFormat);
                    await File.WriteAllTextAsync(sidecarPath, sidecarContent, new System.Text.UTF8Encoding(false), exportCancellation.Token);
                }
            }
            return path;
        }
        finally { UpdateState(); }
    }
    private async void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && canvasOverlay.Dragging) { e.Handled = true; canvasOverlay.Finish(false); await canvasCommitWork; return; }
        if (e.Key == VirtualKey.Escape && cropEditing) { e.Handled = true; await SafeAsync(ToggleCropEditingAsync); return; }
        if (e.Key == VirtualKey.F11 || (previewFullscreen && e.Key == VirtualKey.Escape)) {
            e.Handled = true; await SafeAsync(TogglePreviewFullscreenAsync); return;
        }
        var source = e.OriginalSource as DependencyObject;
        if (InputAncestor<TextBox>(source) is not null || InputAncestor<NumberBox>(source) is not null || InputAncestor<Slider>(source) is not null || InputAncestor<ToggleButton>(source) is not null || InputAncestor<ComboBox>(source) is not null) return;
        bool control = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        bool shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (await HandleEditorShortcutAsync(e.Key, control, shift)) e.Handled = true;
    }

    internal async Task<bool> HandleEditorShortcutAsync(VirtualKey key, bool control, bool shift)
    {
        if (working || switchingProjects || showingDeleteConfirmation || showingPresets || editor.IsExporting || showingExport) return false;
        if (key == VirtualKey.F11 || (previewFullscreen && key == VirtualKey.Escape)) {
            await SafeAsync(TogglePreviewFullscreenAsync);
            return true;
        }
        if (previewFullscreen && key is not (VirtualKey.Space or VirtualKey.Left or VirtualKey.Right)) return false;

        if (control && key == VirtualKey.Z) { await PerformAsync(editor.UndoAsync); return true; }
        if (control && key == VirtualKey.Y) { await PerformAsync(editor.RedoAsync); return true; }
        if (control && key == VirtualKey.T) { await SafeAsync(AddSubtitleAtPlayheadAsync); return true; }
        if (inspectorTab == 2 && control && key == VirtualKey.C) { copiedEffect = CurrentEffect; return true; }
        if (inspectorTab == 2 && control && key == VirtualKey.V && copiedEffect is not null) { await PerformAsync(shift ? PasteAttributesAsync : PasteCopiedVisualAsync); return true; }
        if (key == VirtualKey.Home || key == VirtualKey.End) { await SafeAsync(() => SeekAsync(key == VirtualKey.Home ? 0 : editor.Timeline.Duration - 1)); return true; }
        if (inspectorTab == 6 && key == VirtualKey.Delete && (selectedSubtitleIds.Count > 0 || selectedSubtitleId is not null)) { await SafeAsync(DeleteSelectedSubtitlesAsync); return true; }
        if (inspectorTab == 2 && key == VirtualKey.Delete && selectedEffect is not null) { await PerformAsync(() => editor.DeleteEffectAsync(selectedEffect)); return true; }
        if (key == VirtualKey.Space) { await SafeAsync(async () => { if (playing) Pause(); else await StartPlaybackAsync(); }); return true; }
        if (key == VirtualKey.Left || key == VirtualKey.Right) { await SafeAsync(() => SeekAsync(position + (key == VirtualKey.Left ? -1 : 1) * TimeSpan.TicksPerSecond / editor.Snapshot.Export.Fps.Numerator)); return true; }
        if (key == VirtualKey.Escape) { DismissSubtitleInlineEditor(commit: false); timeline.CancelDrag(); editController.CancelCurrent(); RefreshTimeline(); return true; }
        if (key == VirtualKey.S && !control) {
            if ((inspectorTab == 6 || selectedSubtitleIds.Count > 0) && await SplitSubtitleAtPlayheadAsync(selectedSubtitleId)) return true;
            Split_Click(this, new RoutedEventArgs());
            return true;
        }
        if (key == VirtualKey.Delete) { Delete_Click(this, new RoutedEventArgs()); return true; }
        return false;
    }
}
