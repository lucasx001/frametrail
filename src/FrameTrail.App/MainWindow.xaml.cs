using FrameTrail.Domain;
using FrameTrail.Interop;
using FrameTrail.Workspace;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.System;

namespace FrameTrail.App;

public sealed partial class MainWindow : Window
{
    private EngineSession? session;
    private RecordingCoordinator coordinator = new();
    private ProjectWorkspace? opened;
    private MemoWindow? memo;
    private RecordingCompletionWindow? completion;
    private EditorWindow? editorWindow;
    private EditorSettingsWindow? settingsWindow;
    private CaptureSelectionController? selection;
    private IReadOnlyList<CaptureSource> sources = Array.Empty<CaptureSource>();
    private IReadOnlyList<AudioDevice> microphones = Array.Empty<AudioDevice>(), outputs = Array.Empty<AudioDevice>();
    private IReadOnlyList<CameraDevice> cameras = Array.Empty<CameraDevice>();
    private AudioDevice? microphone, output;
    private CameraDevice? camera;
    private CaptureSource? chosenSource;
    private CaptureRegion? chosenRegion;
    private int sourceMode, markerCount;
    private bool loaded, viewReady, busy, allowClose, closing;
    private nint hwnd, previousProcedure;
    private readonly WndProc procedure;
    private bool stopRegistered, markerRegistered;
    private string projects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FrameTrail");
    private string engineStatus = "";
    private string surface = "";
    private string lastWarning = "";
    private readonly List<Flyout> flyouts = new();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WndProc(nint window, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")] private static extern nint CallWindowProc(nint procedure, nint window, uint message, nuint wparam, nint lparam);

    public MainWindow()
    {
        procedure = WindowProcedure;
        InitializeComponent();
        RecorderRoot.RequestedTheme = EditorAppearance.Current;
        EditorAppearance.Changed += SetAppearance;
        Closed += (_, _) => { EditorAppearance.Changed -= SetAppearance; settingsWindow?.Close(); };
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        flyouts.AddRange(new[] { MicrophoneFlyout, CameraFlyout, SystemFlyout, MoreFlyout, RecordingMoreFlyout, MarkerFlyout, FeedbackFlyout, RecoveryFlyout });
        foreach (var flyout in flyouts) flyout.ShouldConstrainToRootBounds = false;
        viewReady = true;
        try { session = new(); engineStatus = $"FrameTrail Engine {EngineSession.Version} · ABI {EngineSession.ExpectedAbiVersion}"; }
        catch (Exception error) { engineStatus = error.Message; ShowFeedback("引擎初始化失败", error.Message, false); }
        coordinator.StatusChanged += UpdateStatus;
        RecorderWindowChrome.Configure(this, 374, 56, 28, center: true);
        RecorderRoot.ActualThemeChanged += (_, _) => ApplyIcons();
        AppWindow.Changed += (_, args) => { if (args.DidSizeChange) RecorderWindowChrome.Round(this, SetupPanel.Visibility == Visibility.Visible ? 28 : 24); };
        AppWindow.Closing += async (_, args) =>
        {
            if (allowClose) return;
            args.Cancel = true;
            if (editorWindow is not null) { editorWindow.Activate(); return; }
            if (busy) { ShowFeedback("正在完成操作", "录制与保存完成后可关闭窗口。"); return; }
            busy = true; closing = true;
            try
            {
                selection?.Dispose(); selection = null;
                HideFlyouts();
                await coordinator.DisposeAsync();
                if (opened is not null) await opened.DisposeAsync();
                completion?.Close(); editorWindow?.Close(); memo?.Close(); UnregisterHotkeys();
                if (previousProcedure != 0) SetWindowLongPtr(hwnd, -4, previousProcedure);
                session?.Dispose(); allowClose = true; Close();
            }
            catch (Exception error) { closing = false; busy = false; AppWindow.Show(); ShowFeedback("关闭前保存失败", error.Message); }
        };
        RecorderRoot.Loaded += async (_, _) =>
        {
            if (loaded) return;
            loaded = true;
            RecorderWindowChrome.Resize(this, 374, 56, 28);
            var args = Environment.GetCommandLineArgs();
            // Local visual QA must be able to capture the recorder itself. This
            // switch changes only this process; ordinary launches remain excluded.
            if (args.Contains("--inspect-ui")) ExcludeControl.IsChecked = false;
            var smokeIndex = Array.IndexOf(args, "--smoke-test");
            if (smokeIndex >= 0) { await RunSmokeTestAsync(args.Length > smokeIndex + 1 ? args[smokeIndex + 1] : Path.Combine(AppContext.BaseDirectory, "smoke-result.json")); return; }
            var editorIndex = Array.IndexOf(args, "--editor");
            if (editorIndex >= 0 && args.Length > editorIndex + 1) {
                await ExecuteAsync(async () => {
                    opened = await ProjectWorkspace.OpenAsync(Path.GetFullPath(args[editorIndex + 1]));
                    var testIndex = Array.IndexOf(args, "--m2-self-test");
                    PresentEditor(opened, testIndex >= 0 && args.Length > testIndex + 1 ? args[testIndex + 1] : null);
                }); return;
            }
            await InitializeRecorderAsync();
            var chromeIndex = Array.IndexOf(args, "--chrome-self-test");
            if (chromeIndex >= 0 && args.Length > chromeIndex + 1) { await RunChromeValidationAsync(args[chromeIndex + 1]); return; }
            AppWindow.Show();
            SourceWheel.Focus(FocusState.Programmatic);
            var validationIndex = Array.IndexOf(args, "--ui-self-test");
            if (validationIndex >= 0 && args.Length > validationIndex + 1)
            { await RunUiValidationAsync(args[validationIndex + 1]); return; }
            if (args.Contains("--inspect-ui"))
            {
                Activate();
            }
        };
    }
    private async Task InitializeRecorderAsync()
    {
        if (previousProcedure != 0) return;
        previousProcedure = SetWindowLongPtr(hwnd, -4, Marshal.GetFunctionPointerForDelegate(procedure));
        ApplyHotkeys(); ApplyExclusion(); ApplyIcons();
        await ExecuteAsync(RefreshDevicesAsync);
    }

    private void ToggleFlyout(Flyout flyout, FrameworkElement anchor)
    {
        if (flyout.IsOpen) { flyout.Hide(); return; }
        HideFlyouts();
        flyout.ShowAt(anchor);
    }
    private void HideFlyouts() { foreach (var flyout in flyouts) flyout.Hide(); }
    private void Microphone_Click(object sender, RoutedEventArgs e) => ToggleFlyout(MicrophoneFlyout, MicrophoneButton);
    private void Camera_Click(object sender, RoutedEventArgs e) => ToggleFlyout(CameraFlyout, CameraButton);
    private void System_Click(object sender, RoutedEventArgs e) => ToggleFlyout(SystemFlyout, SystemButton);
    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (NoticeBadge.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(Feedback.Text))
        { NoticeBadge.Visibility = Visibility.Collapsed; ToggleFlyout(FeedbackFlyout, MoreButton); return; }
        ToggleFlyout(MoreFlyout, MoreButton);
    }
    private void RecordingMore_Click(object sender, RoutedEventArgs e) => ToggleFlyout(RecordingMoreFlyout, RecordingMoreButton);
    private void SetAppearance(ElementTheme theme) => RecorderRoot.RequestedTheme = theme;
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        HideFlyouts();
        if (settingsWindow is null) { settingsWindow = new EditorSettingsWindow(opened?.DirectoryPath ?? projects, SetAppearance); settingsWindow.Closed += (_, _) => settingsWindow = null; }
        settingsWindow.Activate();
    }
    private void Recovery_Click(object sender, RoutedEventArgs e) => ToggleFlyout(RecoveryFlyout, MoreButton);
    private void ShowFeedback(string title, string message, bool show = true)
    {
        FeedbackTitle.Text = title; Feedback.Text = message; NoticeBadge.Visibility = Visibility.Visible;
        if (!show || !loaded || closing) return;
        HideFlyouts();
        FeedbackFlyout.ShowAt(SetupPanel.Visibility == Visibility.Visible ? MoreButton : RecordingMoreButton);
    }

    private void SourceMode_Click(object sender, RoutedEventArgs e) => RollSource(1);
    private void SourceMode_Wheel(object sender, PointerRoutedEventArgs e)
    { RollSource(e.GetCurrentPoint(SourceWheel).Properties.MouseWheelDelta > 0 ? -1 : 1); e.Handled = true; }
    private void SourceMode_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Left or VirtualKey.Up) { RollSource(-1); e.Handled = true; }
        else if (e.Key is VirtualKey.Right or VirtualKey.Down) { RollSource(1); e.Handled = true; }
    }
    private void RollSource(int step)
    {
        if (busy || coordinator.State is not (RecordingWorkspaceState.Setup or RecordingWorkspaceState.RecordingComplete)) return;
        HideFlyouts(); sourceMode = (sourceMode + step + 3) % 3;
        SourceGlyph.Kind = new[] { "Display", "Window", "Region" }[sourceMode];
        SourceModeLabel.Text = new[] { "显示器", "窗口", "区域" }[sourceMode];
        AutomationProperties.SetName(SourceWheel, "录制来源：" + SourceModeLabel.Text);
        ProcessAudio.IsEnabled = sourceMode == 1;
        if (sourceMode != 1) AllSystemAudio.IsChecked = true;
    }
    private async Task RefreshDevicesAsync()
    {
        var issues = new List<string>();
        IReadOnlyList<T> Optional<T>(string name, Func<IReadOnlyList<T>> enumerate)
        { try { return enumerate(); } catch (Exception error) { issues.Add(name + "不可用：" + error.Message); return Array.Empty<T>(); } }
        var data = await Task.Run(() => (
            Sources: CaptureSession.EnumerateSources(),
            Outputs: Optional("系统声音", () => AudioCaptureSession.EnumerateDevices(AudioCaptureKind.System)),
            Microphones: Optional("麦克风", () => AudioCaptureSession.EnumerateDevices(AudioCaptureKind.Microphone)),
            Cameras: Optional("摄像头", CameraCaptureSession.EnumerateDevices)));
        sources = data.Sources.Where(s => s.Handle != (nuint)hwnd).ToArray();
        microphones = data.Microphones; outputs = data.Outputs; cameras = data.Cameras;
        microphone = microphones.FirstOrDefault(d => d.Id == microphone?.Id) ?? microphones.FirstOrDefault(d => d.IsDefault) ?? microphones.FirstOrDefault();
        output = outputs.FirstOrDefault(d => d.Id == output?.Id) ?? outputs.FirstOrDefault(d => d.IsDefault) ?? outputs.FirstOrDefault();
        camera = cameras.FirstOrDefault(d => d.Id == camera?.Id);
        BuildChoices(MicrophoneDevices, microphones, d => d.Name, d => d.Id == microphone?.Id, d => { microphone = d; MicEnabled.IsChecked = true; }, "Microphones");
        BuildChoices(SystemDevices, outputs, d => d.Name, d => d.Id == output?.Id, d => { output = d; SystemEnabled.IsChecked = true; }, "Outputs");
        BuildChoices(CameraDevices, cameras, d => d.Name, d => d.Id == camera?.Id, d => { CameraEnabled.IsChecked = true; _ = SelectCameraAsync(d); }, "Cameras");
        MicrophoneEmpty.Visibility = microphones.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SystemEmpty.Visibility = outputs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CameraEmpty.Visibility = cameras.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MicEnabled.IsEnabled = microphones.Count > 0; SystemEnabled.IsEnabled = outputs.Count > 0; CameraEnabled.IsEnabled = cameras.Count > 0;
        if (microphones.Count == 0) MicEnabled.IsChecked = false;
        if (outputs.Count == 0) SystemEnabled.IsChecked = false;
        if (cameras.Count == 0) CameraEnabled.IsChecked = false;
        ProcessAudio.IsEnabled = sourceMode == 1;
        var recoverable = await ProjectWorkspace.ScanRecoveryAsync(projects);
        RecoveryPicker.ItemsSource = recoverable; RecoveryPicker.SelectedIndex = recoverable.Count > 0 ? 0 : -1; RecoverButton.IsEnabled = recoverable.Count > 0;
        DestinationName.Text = Path.GetFileName(projects.TrimEnd(Path.DirectorySeparatorChar));
        ToolTipService.SetToolTip(DestinationButton, projects);
        ApplyIcons();
        if (issues.Count > 0) ShowFeedback("部分设备不可用", string.Join(Environment.NewLine, issues), false);
    }
    private static void BuildChoices<T>(StackPanel panel, IReadOnlyList<T> devices, Func<T, string> label, Func<T, bool> selected, Action<T> choose, string group)
    {
        panel.Children.Clear();
        foreach (var device in devices)
        {
            var row = new RadioButton { GroupName = group, Style = (Style)Application.Current.Resources["RecorderChoice"],
                Content = new TextBlock { Text = label(device), TextWrapping = TextWrapping.Wrap, MaxWidth = 214 }, IsChecked = selected(device) };
            AutomationProperties.SetName(row, label(device));
            row.Checked += (_, _) => choose(device); panel.Children.Add(row);
        }
    }
    private async Task SelectCameraAsync(CameraDevice device)
    {
        camera = device;
        try
        {
            var formats = await Task.Run(() => CameraCaptureSession.EnumerateFormats(device.Id));
            if (camera?.Id != device.Id || closing) return;
            CameraFormatPicker.ItemsSource = formats; CameraFormatPicker.SelectedIndex = formats.Count > 0 ? 0 : -1;
            ApplyIcons();
        }
        catch (Exception error) { CameraEnabled.IsChecked = false; ShowFeedback("摄像头格式读取失败", error.Message); }
    }
    private async void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (!viewReady) return;
        ApplyIcons();
        if (ReferenceEquals(sender, CameraEnabled) && CameraEnabled.IsChecked == true && camera is null && cameras.Count > 0)
            await SelectCameraAsync(cameras[0]);
    }
    private void AudioScope_Changed(object sender, RoutedEventArgs e) { if (viewReady) ApplyIcons(); }
    private void ApplyIcons()
    {
        if (!viewReady) return;
        MicrophoneGlyph.IsOn = MicEnabled.IsChecked == true; CameraGlyph.IsOn = CameraEnabled.IsChecked == true; SystemGlyph.IsOn = SystemEnabled.IsChecked == true;
        AutomationProperties.SetName(MicrophoneButton, "麦克风：" + (MicrophoneGlyph.IsOn ? "开启" : "关闭"));
        AutomationProperties.SetName(CameraButton, "摄像头：" + (CameraGlyph.IsOn ? "开启" : "关闭"));
        AutomationProperties.SetName(SystemButton, "系统声音：" + (SystemGlyph.IsOn ? "开启" : "关闭"));
        MicrophoneMeterPanel.Visibility = MicrophoneGlyph.IsOn ? Visibility.Visible : Visibility.Collapsed;
        CameraOptions.Visibility = CameraGlyph.IsOn ? Visibility.Visible : Visibility.Collapsed;
        SetPanelEnabled(SystemDevices, SystemGlyph.IsOn && ProcessAudio.IsChecked != true);
        AllSystemAudio.IsEnabled = SystemGlyph.IsOn;
        ProcessAudio.IsEnabled = SystemGlyph.IsOn && sourceMode == 1;
        ProcessAudio.Opacity = SystemGlyph.IsOn ? 1 : 0.4; AllSystemAudio.Opacity = ProcessAudio.Opacity;
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) { HideFlyouts(); await ExecuteAsync(RefreshDevicesAsync); }
    private async void Destination_Click(object sender, RoutedEventArgs e)
    {
        HideFlyouts();
        await ExecuteAsync(async () =>
        {
            var picker = new Windows.Storage.Pickers.FolderPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync(); if (folder is null) return;
            projects = folder.Path; DestinationName.Text = folder.Name; ToolTipService.SetToolTip(DestinationButton, projects);
            var candidates = await ProjectWorkspace.ScanRecoveryAsync(projects);
            RecoveryPicker.ItemsSource = candidates; RecoveryPicker.SelectedIndex = candidates.Count > 0 ? 0 : -1; RecoverButton.IsEnabled = candidates.Count > 0;
        });
    }
    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (busy || session is null || selection is not null) return;
        HideFlyouts();
        try
        {
            selection = new CaptureSelectionController(this, sourceMode, sources, async (source, region) =>
            {
                selection = null; AppWindow.Show(); Activate();
                await ExecuteAsync(() => BeginRecordingAsync(source, region));
            }, () => { selection = null; if (!closing) { AppWindow.Show(); Activate(); } });
            selection.Show(); AppWindow.Hide();
        }
        catch (Exception error) { selection?.Dispose(); selection = null; AppWindow.Show(); ShowFeedback("无法选择录制来源", error.Message); }
    }
    private async Task BeginRecordingAsync(CaptureSource source, CaptureRegion? region)
    {
        if (opened is not null) { await opened.DisposeAsync(); opened = null; }
        var audio = new List<AudioSelection>();
        if (SystemEnabled.IsChecked == true)
        {
            if (ProcessAudio.IsChecked == true)
            { if (source.Kind != CaptureSourceKind.Window) throw new InvalidOperationException("App 声音需要选择窗口。"); audio.Add(new(AudioCaptureKind.ProcessTree, ProcessId: AudioCaptureSession.ProcessIdFromWindow(source.Handle))); }
            else if (output is not null) audio.Add(new(AudioCaptureKind.System, output.Id));
            else throw new InvalidOperationException("请选择声音输出设备。");
        }
        if (MicEnabled.IsChecked == true)
        { if (microphone is null) throw new InvalidOperationException("请选择麦克风。"); audio.Add(new(AudioCaptureKind.Microphone, microphone.Id)); }
        var title = RecordingName.Text.Trim();
        if (title.Length == 0) title = DateTime.Now.ToString("yyyy-MM-dd-HHmmss");
        if (title.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || title is "." or ".." || title.EndsWith('.') || title.EndsWith(' '))
            throw new InvalidOperationException("录制名称包含 Windows 不支持的字符。");
        var path = Path.Combine(projects, title + ".frametrailproject");
        markerCount = 0; MarkerCount.Text = ""; lastWarning = ""; NoticeBadge.Visibility = Visibility.Collapsed;
        chosenSource = source; chosenRegion = region;
        await coordinator.StartAsync(new(path, source, region, FpsPicker.SelectedIndex == 1 ? 60u : 30u,
            SoftwareAllowed.IsChecked == true, audio, CameraEnabled.IsChecked == true ? camera : null,
            CameraEnabled.IsChecked == true ? CameraFormatPicker.SelectedItem as CameraFormat : null, PointerEnabled.IsChecked == true));
    }
    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        HideFlyouts();
        await ExecuteAsync(async () => { if (coordinator.State == RecordingWorkspaceState.Paused) await coordinator.ResumeAsync(); else await coordinator.PauseAsync(); });
    }
    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        HideFlyouts();
        await ExecuteAsync(async () =>
        {
            var value = await coordinator.StopAsync();
            if (value is not null) PresentCompletion(coordinator.Workspace!.DirectoryPath, value);
        });
    }
    private async void Marker_Click(object sender, RoutedEventArgs e) => await AddMarkerAsync("");
    private void Marker_RightTapped(object sender, RightTappedRoutedEventArgs e) { ToggleFlyout(MarkerFlyout, MarkerButton); e.Handled = true; }
    private async void AddLabeledMarker_Click(object sender, RoutedEventArgs e) { HideFlyouts(); await AddMarkerAsync(MarkerLabel.Text); }
    private async Task AddMarkerAsync(string label) => await ExecuteAsync(async () =>
    { await coordinator.AddMarkerAsync(label); MarkerCount.Text = (++markerCount).ToString(); MarkerLabel.Text = ""; });

    private async Task ExecuteAsync(Func<Task> action)
    {
        if (busy) return; busy = true; ApplyInteraction();
        try { await action(); } catch (Exception error) {
            var arguments = Environment.GetCommandLineArgs(); var validationIndex = Array.IndexOf(arguments, "--m2-self-test");
            if (validationIndex >= 0 && arguments.Length > validationIndex + 1) { var directory = Path.GetFullPath(arguments[validationIndex + 1]); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "editor-startup-error.txt"), error.ToString()); }
            AppWindow.Show(); ShowFeedback("操作未完成", error.Message);
        }
        finally { busy = false; ApplyInteraction(); }
    }
    private void ApplyInteraction()
    {
        bool idle = coordinator.State is RecordingWorkspaceState.Setup or RecordingWorkspaceState.RecordingComplete;
        SetPanelEnabled(SetupPanel, idle && !busy);
        StartButton.IsEnabled = session is not null && sources.Count > 0 && !busy;
        PauseButton.IsEnabled = !busy && coordinator.State is RecordingWorkspaceState.Recording or RecordingWorkspaceState.Paused;
        StopButton.IsEnabled = PauseButton.IsEnabled;
        MarkerButton.IsEnabled = !busy && coordinator.State == RecordingWorkspaceState.Recording;
        RefreshButton.IsEnabled = idle && !busy;
    }
    private static void SetPanelEnabled(Panel panel, bool enabled)
    {
        panel.IsHitTestVisible = enabled;
        foreach (var control in panel.Children.OfType<Control>()) control.IsEnabled = enabled;
    }
    private void UpdateStatus(object? sender, RecordingWorkspaceStatus value) => DispatcherQueue.TryEnqueue(() =>
    {
        if (closing || !ReferenceEquals(sender, coordinator) || value.RunId != coordinator.RunId) return;
        var idle = value.State is RecordingWorkspaceState.Setup or RecordingWorkspaceState.RecordingComplete;
        var recording = value.State is RecordingWorkspaceState.Recording or RecordingWorkspaceState.Paused;
        var paused = value.State == RecordingWorkspaceState.Paused;
        SetupPanel.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
        RecordingPanel.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        ProgressPanel.Visibility = !idle && !recording ? Visibility.Visible : Visibility.Collapsed;
        TransitionProgress.IsActive = !idle && !recording;
        RecordingStatus.Text = value.State switch { RecordingWorkspaceState.Preparing => "正在准备…", RecordingWorkspaceState.Pausing => "正在暂停并保存…", RecordingWorkspaceState.Resuming => "正在继续…", _ => "正在结束并保存…" };
        DurationText.Text = value.Duration.TotalHours >= 1 ? value.Duration.ToString(@"hh\:mm\:ss") : $"{(int)value.Duration.TotalMinutes:00}:{value.Duration.Seconds:00}";
        DurationText.Foreground = RecorderTheme.Brush(RecorderRoot, paused ? "RecorderMuted" : "RecorderInk");
        PauseGlyph.Kind = paused ? "Play" : "Pause";
        AutomationProperties.SetName(PauseButton, paused ? "继续录制" : "暂停录制"); ToolTipService.SetToolTip(PauseButton, paused ? "继续录制" : "暂停录制");
        LevelBars.Visibility = MicEnabled.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        double level = paused ? 0 : Math.Clamp(value.MicrophonePeak, 0, 1);
        MicrophoneLevel.Value = level;
        Level1.Height = 3 + 11 * Math.Min(1, level * 0.99); Level2.Height = 3 + 11 * Math.Min(1, level * 1.6);
        Level3.Height = 3 + 11 * Math.Min(1, level * 1.28); Level4.Height = 3 + 11 * Math.Min(1, level * 0.8);
        var nextSurface = idle ? "setup" : recording ? $"record-{paused}-{LevelBars.Visibility}-{value.Duration.TotalHours >= 1}" : "progress";
        if (surface != nextSurface)
        {
            surface = nextSurface;
            HideFlyouts();
            RecorderRoot.CornerRadius = new CornerRadius(idle ? 28 : 24);
            RecordingPanel.Measure(new Windows.Foundation.Size(double.PositiveInfinity, 48));
            RecorderWindowChrome.Resize(this, idle ? 374 : recording ? RecordingPanel.DesiredSize.Width + 2 : 240, idle ? 56 : 48, idle ? 28 : 24);
            var visual = ElementCompositionPreview.GetElementVisual(RecordingDot); visual.StopAnimation("Opacity"); visual.Opacity = 1;
            RecordingDot.Fill = (Brush)Application.Current.Resources[paused ? "RecorderAmber" : "RecorderRed"];
            if (recording && !paused && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            {
                var pulse = visual.Compositor.CreateScalarKeyFrameAnimation(); pulse.InsertKeyFrame(0, 1); pulse.InsertKeyFrame(0.5f, 0.32f); pulse.InsertKeyFrame(1, 1);
                pulse.Duration = TimeSpan.FromSeconds(1.8); pulse.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
                visual.StartAnimation("Opacity", pulse);
            }
        }
        if (value.Warnings.LastOrDefault() is string warning && warning != lastWarning)
        { lastWarning = warning; ShowFeedback("录制提示", string.Join(Environment.NewLine, value.Warnings.TakeLast(6)), false); }
        ApplyInteraction();
    });

    private void PresentCompletion(string path, ProjectSnapshot snapshot)
    {
        completion?.Close();
        completion = new RecordingCompletionWindow(path, snapshot, () =>
        {
            completion = null;
            if (!closing) { AppWindow.Show(); Activate(); }
        }, () => {
            var workspace = opened is not null && string.Equals(opened.DirectoryPath, path, StringComparison.OrdinalIgnoreCase) ? opened : coordinator.Workspace;
            if (workspace is null) throw new InvalidOperationException("工程会话已关闭，请重新打开工程。");
            PresentEditor(workspace);
        });
        completion.Closed += (_, _) => { if (!closing) { AppWindow.Show(); } };
        completion.Activate(); AppWindow.Hide();
    }
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        HideFlyouts();
        await ExecuteAsync(async () =>
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker(); picker.FileTypeFilter.Add(".json"); WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSingleFileAsync(); if (file is null) return;
            if (!string.Equals(Path.GetFileName(file.Path), "project.json", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请选择工程目录中的 project.json。");
            await ResetWorkspaceAsync(); opened = await ProjectWorkspace.OpenAsync(Path.GetDirectoryName(file.Path)!); PresentEditor(opened);
        });
    }
    private async void Recover_Click(object sender, RoutedEventArgs e)
    {
        HideFlyouts();
        await ExecuteAsync(async () =>
        {
            if (RecoveryPicker.SelectedItem is not RecoveryCandidate candidate) throw new InvalidOperationException("请选择待恢复工程。");
            await ResetWorkspaceAsync(); var result = await ProjectWorkspace.RecoverAsync(candidate.Directory); opened = result.Workspace;
            PresentCompletion(opened.DirectoryPath, opened.Snapshot!);
            if (result.Warnings.Count > 0) completion?.SetNotice(string.Join(Environment.NewLine, result.Warnings));
        });
    }
    private async Task ResetWorkspaceAsync()
    {
        await coordinator.DisposeAsync(); coordinator.StatusChanged -= UpdateStatus;
        coordinator = new(); coordinator.StatusChanged += UpdateStatus;
        if (opened is not null) { await opened.DisposeAsync(); opened = null; }
        surface = ""; SetupPanel.Visibility = Visibility.Visible; RecordingPanel.Visibility = ProgressPanel.Visibility = Visibility.Collapsed;
        RecorderRoot.CornerRadius = new CornerRadius(28); RecorderWindowChrome.Resize(this, 374, 56, 28);
    }
    private void PresentEditor(ProjectWorkspace workspace, string? validation = null)
    {
        if (editorWindow is not null) { editorWindow.Activate(); return; }
        completion?.Close(); completion = null;
        editorWindow = new EditorWindow(workspace, validation);
        editorWindow.Closed += async (_, _) => { editorWindow = null; if (validation is not null) { if (opened is not null) await opened.DisposeAsync(); allowClose = true; closing = true; Close(); Application.Current.Exit(); } else if (!closing) { await InitializeRecorderAsync(); AppWindow.Show(); Activate(); } };
        editorWindow.Activate(); AppWindow.Hide();
    }
    private void Memo_Click(object sender, RoutedEventArgs e)
    {
        HideFlyouts();
        if (memo is not null) { memo.Close(); memo = null; MemoMenuButton.Content = "打开备忘录"; return; }
        memo = new MemoWindow(); memo.Closed += (_, _) => { memo = null; MemoMenuButton.Content = "打开备忘录"; };
        memo.Activate(); MemoMenuButton.Content = "收起备忘录"; ApplyExclusion();
    }
    private void ShowSource_Click(object sender, RoutedEventArgs e)
    {
        HideFlyouts();
        ShowFeedback("录制来源", chosenSource is null ? "尚未选择来源。" : chosenSource.Name + (chosenRegion is CaptureRegion r ? $"\n{r.Width} × {r.Height} · 区域 ({r.X}, {r.Y})" : $"\n{chosenSource.Width} × {chosenSource.Height}"));
    }
    private void Drag_Pressed(object sender, PointerRoutedEventArgs e) { HideFlyouts(); RecorderWindowChrome.Drag(this, e); }
    private void Root_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Escape) { HideFlyouts(); e.Handled = true; } }
    private void Quit_Click(object sender, RoutedEventArgs e) { HideFlyouts(); Close(); }
    private void Exclusion_Changed(object sender, RoutedEventArgs e) { if (loaded) ApplyExclusion(); }
    private void ApplyExclusion()
    {
        try { RecordingUtilities.ExcludeWindow((nuint)hwnd, ExcludeControl.IsChecked == true); if (memo is not null) RecordingUtilities.ExcludeWindow((nuint)WinRT.Interop.WindowNative.GetWindowHandle(memo), ExcludeControl.IsChecked == true); }
        catch (Exception error) { ShowFeedback("窗口排除失败", error.Message, false); }
    }
    private void Hotkey_Click(object sender, RoutedEventArgs e) => ApplyHotkeys();
    private void UnregisterHotkeys()
    {
        if (stopRegistered) { RecordingUtilities.UnregisterHotkey((nuint)hwnd, 0x4654); stopRegistered = false; }
        if (markerRegistered) { RecordingUtilities.UnregisterHotkey((nuint)hwnd, 0x4655); markerRegistered = false; }
    }
    private void ApplyHotkeys()
    {
        try
        {
            UnregisterHotkeys(); RecordingUtilities.RegisterHotkey((nuint)hwnd, 0x4654, 6, (uint)(0x75 + StopKey.SelectedIndex)); stopRegistered = true;
            RecordingUtilities.RegisterHotkey((nuint)hwnd, 0x4655, 6, (uint)(0x75 + MarkerKey.SelectedIndex)); markerRegistered = true;
            HotkeyStatus.Text = "全局快捷键已注册。";
            RecordingShortcutHint.Text = $"Ctrl + Shift + F{6 + StopKey.SelectedIndex} 结束录制";
        }
        catch (Exception error) { HotkeyStatus.Text = "快捷键冲突，请更换后重试：" + error.Message; }
    }
    private nint WindowProcedure(nint window, uint message, nuint wparam, nint lparam)
    {
        if (message == 0x0312)
        {
            if ((int)wparam == 0x4654 && StopButton.IsEnabled) DispatcherQueue.TryEnqueue(() => Stop_Click(this, new RoutedEventArgs()));
            if ((int)wparam == 0x4655 && MarkerButton.IsEnabled) DispatcherQueue.TryEnqueue(() => Marker_Click(this, new RoutedEventArgs()));
            return 0;
        }
        if (message == 0x02E0) DispatcherQueue.TryEnqueue(() => { surface = ""; RecorderWindowChrome.Round(this, SetupPanel.Visibility == Visibility.Visible ? 28 : 24); });
        return CallWindowProc(previousProcedure, window, message, wparam, lparam);
    }
    private async Task RunSmokeTestAsync(string outputPath)
    {
        bool success = false; string? errorMessage = null, version = null;
        try
        {
            if (session is null) throw new InvalidOperationException(engineStatus); version = EngineSession.Version;
            for (int i = 0; i < 100; i++) { using var probe = new EngineSession(); probe.Cancel(); if (!probe.IsCancelled) throw new InvalidOperationException("Cancel did not reach native session."); }
            RecorderRoot.UpdateLayout();
            if (SetupPanel.ActualHeight > 56 || RecorderRoot.ActualHeight > 57) throw new InvalidOperationException("Recorder capsule exceeded its reference height.");
            if (!RecorderWindowChrome.HasMatchingClientBounds(this)) throw new InvalidOperationException("Recorder still has a native non-client frame.");
            success = true;
        }
        catch (Exception error) { errorMessage = error.ToString(); }
        try
        {
            var absolute = Path.GetFullPath(outputPath); Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            await File.WriteAllTextAsync(absolute, JsonSerializer.Serialize(new { success, windowLoaded = true, version, abi = EngineSession.ExpectedAbiVersion,
                lifecycleChecks = success ? 100 : 0, capsuleWidth = AppWindow.Size.Width, capsuleHeight = AppWindow.Size.Height,
                chrome = RecorderWindowChrome.Diagnostics(this),
                processId = Environment.ProcessId, osVersion = Environment.OSVersion.VersionString, timestampUtc = DateTimeOffset.UtcNow, error = errorMessage }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { success = false; }
        Environment.ExitCode = success ? 0 : 1; allowClose = true; session?.Dispose(); Close();
    }
}
