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
    private readonly Stopwatch playbackClock = new();
    private EngineSession mapper = new();
    private CancellationTokenSource exportCancellation = new();
    private Channel<long>? thumbnails;
    private readonly HashSet<long> pendingThumbnails = new();
    private long position, playbackStart;
    private string? selected, preparedAudio;
    private SdrColor background = new();
    private (CanvasSettings Canvas, EditorSettings Editor)? copiedPicture;
    private bool ready, working, playing, rendering, allowClose, closing, streamedPlayback;
    private string? validationDirectory;
    private int renderedFrames;
    private readonly PlaybackFrameGate playbackFrames = new();
    private readonly SemaphoreSlim audioTransport = new(1);
    private Task audioStopWork = Task.CompletedTask;
    private long playbackGeneration;
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
        editor = new EditorSession(workspace); this.validationDirectory = validationDirectory;
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
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Min(1500, area.Width), Math.Min(980, area.Height)));
        if (AppWindow.Presenter is OverlappedPresenter presenter) { double scale = RecorderWindowChrome.Scale(this); presenter.PreferredMinimumWidth = (int)Math.Round(840 * scale); presenter.PreferredMinimumHeight = (int)Math.Round(600 * scale); }
        editor.Changed += (_, _) => DispatcherQueue.TryEnqueue(UpdateState);
        timeline.Seek += async ticks => { if (!working && !editor.IsExporting) await SafeAsync(() => SeekAsync(ticks)); };
        timeline.SelectClip += async id => { await SafeAsync(async () => { await FlushVisualChangesAsync(); selected = id; LoadInspector(); RefreshTimeline(); }); };
        timeline.BeginEdit += () => { if (working || editor.IsExporting) return; Pause(); editor.BeginTransaction(); };
        timeline.PreviewEdit += command => {
            if (working || editor.IsExporting) return;
            try { var candidate = editor.PreviewCommand(command); timeline.Set(EngineSession.ResolveTimeline(candidate), selected, position); }
            catch (Exception error) { ShowNotice(L.T("S72DC2DB39CED"), error.Message); }
        };
        timeline.EndEdit += async commit => {
            if (working || editor.IsExporting) return;
            if (commit) await PerformAsync(editor.CommitTransactionAsync); else { editor.CancelTransaction(); RefreshTimeline(); }
        };
        timeline.ThumbnailsNeeded += RequestThumbnails;
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
        await FlushPictureInputsAsync();
        closing = true;
        try { Pause(); timeline.CancelDrag(); ValidationTrace("closing: flush"); await editor.FlushAsync(); ValidationTrace("closing: assets"); await StopAssetsAsync(); ValidationTrace("closing: preview"); await preview.DisposeAsync(); mapper.Dispose(); settingsWindow?.Close(); ValidationTrace("closing: complete"); allowClose = true; Close(); }
        catch (Exception error) { closing = false; ShowNotice(L.T("S8F52FE1C3C78"), error.Message + L.T("SBF47EE4B2133"), InfoBarSeverity.Error); }
    }
    internal async Task DiscardWindowAsync()
    {
        // Only used for an unopened destination; it has no user edits to save.
        Pause(); closing = true; await StopAssetsAsync();
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
        deleteConfirmation = new ContentDialog { Title = L.T("S761F0E313D82"), Content = details, PrimaryButtonText = L.T("SA3EA3C17B401"), CloseButtonText = L.T("S2CD0F3BE8738"), DefaultButton = ContentDialogButton.Close, XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme };
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
        var before = editor.Snapshot; var sourcePosition = mapper.MapOutputTime(position)?.Ticks;
        try { await action(); if (editor.Snapshot.Revision != before.Revision) await UpdateEditedProjectAsync(before, sourcePosition); }
        catch (Exception error) { ShowNotice(L.T("S6ACBD42B6557"), error.Message, InfoBarSeverity.Warning); RefreshTimeline(); }
        finally { working = false; timeline.EditingEnabled = true; UpdateState(); QueuePictureInputCommit(); }
    }
    private async Task EditAsync(object command) => await PerformAsync(() => editor.ExecuteAsync(command));
    private async Task ReloadAsync()
    {
        Pause(); await StopAssetsAsync(); audioCacheKey = null; mediaCache?.Unpin(preparedAudio);
        preparedAudio = null; thumbnails = Channel.CreateBounded<long>(128); pendingThumbnails.Clear(); timeline.ClearAssets();
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
    private void RefreshTimeline() { timeline.Set(editor.Timeline, selected, position); TimelineSummary.Text = L.T("S788BF0579E4C", editor.Timeline.Clips.Count, Format(editor.Timeline.Duration)); }
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
        SwitchProjectMenuItem.IsEnabled = openProject is not null && ProjectMenuButton.IsEnabled;
        DeleteProjectButton.IsEnabled = deleteProject is not null && ProjectMenuButton.IsEnabled && !projectTransition;
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
    }
    private void ShowNotice(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational) { if (previewFullscreen) SetPreviewFullscreen(false); Notice.Title = title; Notice.Message = message; Notice.Severity = severity; Notice.IsOpen = true; }
    private static string Format(long ticks) => TimeSpan.FromTicks(ticks).ToString(ticks >= TimeSpan.TicksPerHour ? @"h\:mm\:ss\.ff" : @"mm\:ss\.ff");
    private void UpdateClock() { QueueCanvasGeometry(); ClockLabel.Text = $"{Format(position)} / {Format(editor.Timeline.Duration)}"; timeline.Position(position); QueueMockupPoster(); }
    private async Task SeekAsync(long ticks)
    {
        await BrowseSeekAsync(ticks);
    }
    private async Task StartPlaybackAsync()
    {
        if (editor.IsExporting || preview.Session is null) return;
        CancelBrowsing();
        if (cropEditing) await ToggleCropEditingAsync();
        canvasOverlay.Finish(true);await canvasCommitWork;
        if (position >= editor.Timeline.Duration - 1) position = 0;
        var current=preview.Session;if(current is null)return;long request=++playbackGeneration;
        await audioTransport.WaitAsync();
        try {
            if(request!=playbackGeneration||closing||current!=preview.Session)return;
            streamedPlayback = !previewMuted && editor.Snapshot.Media.Any(m => m.Kind is MediaKind.Microphone or MediaKind.SystemAudio or MediaKind.AndroidAudio);
            if (streamedPlayback) await Task.Run(() => current.StreamAudio(position));
            if(request!=playbackGeneration||closing)return;
        } finally {audioTransport.Release();}
        playbackStart = position; playbackClock.Restart(); playbackFrames.Reset(); playing = true; canvasOverlay.SetItems([]); PlayGlyph.Kind = "Pause";
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= PlaybackRendering;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += PlaybackRendering;
    }
    private void Pause(bool cancelBrowsing = true)
    {
        if (cancelBrowsing) CancelBrowsing();
        playbackGeneration++;
        playing = false; Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= PlaybackRendering; playbackClock.Stop(); PlayGlyph.Kind = "Play";
        if (preview.Session is { } current) audioStopWork=StopPreviewAudioAsync(current);
        QueueCanvasGeometry();
    }
    private async Task StopPreviewAudioAsync(MediaSession current)
    {
        await audioTransport.WaitAsync();
        try {await Task.Run(current.StopAudio);}catch(ObjectDisposedException){}catch(Exception error){SafeDiagnostics.Record(error);}
        finally {audioTransport.Release();}
    }
    private async Task TickAsync()
    {
        if (!playing || rendering || preview.Session is null || closing) return;
        rendering = true;
        try {
            var status = preview.Session.Status;
            if (streamedPlayback && status.AudioRunning == 3) { Pause(); ShowNotice(L.T("SC73025417431"), L.T("SF83C898D4E60")); return; }
            var time = streamedPlayback && status.AudioTicks >= 0 ? status.AudioTicks : playbackStart + playbackClock.Elapsed.Ticks;
            if (time >= editor.Timeline.Duration - TimeSpan.TicksPerMillisecond) { Pause(); position = Math.Max(0, editor.Timeline.Duration - 1); }
            else position = Math.Max(0, time);
            await preview.RenderAsync(position, false); renderedFrames++; UpdateClock();
        } catch (Exception error) { Pause(); ShowNotice(L.T("S78D32B15407C"), error.Message, InfoBarSeverity.Error); }
        finally { rendering = false; }
    }
    private async void PlaybackRendering(object? sender, object args)
    {
        if (!playing || rendering || !playbackFrames.TryAdvance(playbackClock.Elapsed.Ticks)) return;
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
    private void InspectorMode_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (PictureSettings is null || InspectorTabs is null) return; ShowInspector(InspectorMode.SelectedIndex switch { 1 => 5, 2 => 6, _ => 0 }); }
    private void PictureTab_Click(object sender, RoutedEventArgs e) => ShowInspector(0);
    private void AudioTab_Click(object sender, RoutedEventArgs e) => ShowInspector(5);
    private void ClipTab_Click(object sender, RoutedEventArgs e) => ShowInspector(6);
    private void TimelineZoom_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) { if (timeline is not null) timeline.PixelsPerSecond = e.NewValue; }
    private void CanvasZoom_SelectionChanged(object sender, SelectionChangedEventArgs e) => SizePreview();
    private void PreviewContainer_SizeChanged(object sender, SizeChangedEventArgs e) => SizePreview();
    private void SizePreview() { if (PreviewMount is null || PreviewContainer is null || CanvasZoom is null) return; double zoom = previewFullscreen ? 1 : CanvasZoom.SelectedIndex switch { 1 => 1.25, 2 => 1.5, 3 => 2, _ => 1 }; double ratio = (double)editor.Snapshot.Canvas.Width / editor.Snapshot.Canvas.Height; double width = Math.Max(2, Math.Min(PreviewContainer.ActualWidth, PreviewContainer.ActualHeight * ratio)); CanvasScroll.Width = width; CanvasScroll.Height = width / ratio; PreviewMount.Width = width * zoom; PreviewMount.Height = width / ratio * zoom; }
    private async void RetrySave_Click(object sender, RoutedEventArgs e) => await SafeAsync(editor.FlushAsync);
    private void Sources_Click(object sender, RoutedEventArgs e) { try { Process.Start(new ProcessStartInfo("explorer.exe", editor.DirectoryPath) { UseShellExecute = true }); } catch (Exception error) { ShowNotice(L.T("SDC6AD02E12FB"), error.Message); } }
    internal void ReportProjectError(string title, string message) => ShowNotice(title, message, InfoBarSeverity.Error);
    private static uint Even(double n) => (uint)Math.Max(2, Math.Round(n / 2) * 2);
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (editor.IsExporting) { exportCancellation.Cancel(); return; }
        if (working || switchingProjects || showingExport || showingDeleteConfirmation || flushingPictureInputs) return;
        await FlushVisualChangesAsync(); await FlushPictureInputsAsync(); Pause(); showingExport = true;
        await SafeAsync(async () => {
            var coverPath = Path.Combine(editor.DirectoryPath, "cache", $"editor-{editor.Snapshot.Revision}-export-{position}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(coverPath)!); await preview.RenderAsync(position, true, coverPath);
            var cover = File.Exists(coverPath) ? new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(coverPath)) : BackgroundPreview.Source;
            var codecs = await Task.Run(CaptureSession.GetVideoCodecs);
            var dialog = new EditorExportDialog(ProjectTitle.Text, editor.Timeline.Duration, editor.Snapshot.Canvas.Width, editor.Snapshot.Canvas.Height, cover, ExportToAsync, codecs, editor.Snapshot.Export.VideoCodec) { XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme };
            try { await dialog.ShowAsync(); } finally { showingExport = false; }
        });
        showingExport = false;
    }
    private async Task<string?> ExportToAsync(int format, int resolution, int fps, string videoCodec, IProgress<MediaStatus> progress, CancellationToken cancellation)
    {
            string? path; bool sources = format == 2, audioOnly = format == 1;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (sources) { var picker = new Windows.Storage.Pickers.FolderPicker(); picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd); var folder = await picker.PickSingleFolderAsync(); if (folder is null) return null; path = Path.Combine(folder.Path, ProjectTitle.Text + L.T("SE72999A484D4") + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".frametrailproject"); }
            else {
                var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = ProjectTitle.Text }; picker.FileTypeChoices.Add(audioOnly ? L.T("S2446F46DF86E") : L.T("SAB5C34012DBC"), new[] { audioOnly ? ".m4a" : ".mp4" }); WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                var file = await picker.PickSaveFileAsync(); if (file is null) return null; path = file.Path;
                var size = new[] { 1080, 1440, 2160 }[resolution]; double ratio = (double)editor.Snapshot.Canvas.Width / editor.Snapshot.Canvas.Height;
                var output = editor.Snapshot.Export with { Width = Even(size * ratio), Height = (uint)size, Fps = new Rational { Numerator = fps == 1 ? 60 : 30 }, VideoCodec = audioOnly ? editor.Snapshot.Export.VideoCodec : videoCodec };
                var before = editor.Snapshot; var sourcePosition = SourcePosition;
                await editor.ExecuteAsync(new { op = "settings", export = output }); await UpdateEditedProjectAsync(before, sourcePosition);
            }
            exportCancellation.Dispose(); exportCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            try {
                if (sources) await editor.CopySourcesAsync(path, exportCancellation.Token);
                else await editor.ExportAsync(path, audioOnly, progress, exportCancellation.Token);
                return path;
            }
            finally { UpdateState(); }
    }
    private async void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && canvasOverlay.Dragging) { e.Handled=true;canvasOverlay.Finish(false);await canvasCommitWork;return; }
        if (e.Key == VirtualKey.Escape && cropEditing) { e.Handled=true;await SafeAsync(ToggleCropEditingAsync);return; }
        if (e.Key == VirtualKey.F11 || (previewFullscreen && e.Key == VirtualKey.Escape)) {
            e.Handled = true; await SafeAsync(TogglePreviewFullscreenAsync); return;
        }
        var source = e.OriginalSource as DependencyObject;
        if (InputAncestor<TextBox>(source) is not null || InputAncestor<NumberBox>(source) is not null || InputAncestor<Slider>(source) is not null || InputAncestor<ToggleButton>(source) is not null || InputAncestor<ComboBox>(source) is not null || working || switchingProjects || showingDeleteConfirmation || showingPresets || editor.IsExporting || showingExport) return;
        if (previewFullscreen && e.Key is not (VirtualKey.Space or VirtualKey.Left or VirtualKey.Right)) return;
        bool control = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        bool shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (control && e.Key == VirtualKey.Z) { e.Handled = true; await PerformAsync(editor.UndoAsync); }
        else if (control && e.Key == VirtualKey.Y) { e.Handled = true; await PerformAsync(editor.RedoAsync); }
        else if (inspectorTab == 2 && control && e.Key == VirtualKey.C) { copiedEffect = CurrentEffect; e.Handled = true; }
        else if (inspectorTab == 2 && control && e.Key == VirtualKey.V && copiedEffect is not null) { e.Handled = true; await PerformAsync(shift ? PasteAttributesAsync : PasteCopiedVisualAsync); }
        else if (e.Key == VirtualKey.Home || e.Key == VirtualKey.End) { e.Handled=true;await SafeAsync(()=>SeekAsync(e.Key==VirtualKey.Home?0:editor.Timeline.Duration-1)); }
        else if (inspectorTab == 2 && e.Key == VirtualKey.Delete && selectedEffect is not null) { e.Handled = true; await PerformAsync(() => editor.DeleteEffectAsync(selectedEffect)); }
        else if (e.Key == VirtualKey.Space) { e.Handled = true; await SafeAsync(async () => { if (playing) Pause(); else await StartPlaybackAsync(); }); }
        else if (e.Key == VirtualKey.Left || e.Key == VirtualKey.Right) { e.Handled = true; await SafeAsync(() => SeekAsync(position + (e.Key == VirtualKey.Left ? -1 : 1) * TimeSpan.TicksPerSecond / editor.Snapshot.Export.Fps.Numerator)); }
        else if (e.Key == VirtualKey.Escape) { e.Handled = true; timeline.CancelDrag(); editor.CancelTransaction(); RefreshTimeline(); }
        else if (e.Key == VirtualKey.S) { e.Handled = true; Split_Click(sender, e); }
        else if (e.Key == VirtualKey.Delete) { e.Handled = true; Delete_Click(sender, e); }
    }
}
