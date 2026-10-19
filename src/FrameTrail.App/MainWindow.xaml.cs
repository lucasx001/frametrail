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
    private const int SetupWidth = 462;
    private EngineSession? session;
    private RecordingCoordinator coordinator = new(presetLibrary: () => new VisualPresetLibrary());
    private ProjectWorkspace? opened;
    private MemoWindow? memo;
    private RecordingCompletionWindow? completion;
    private EditorWindow? editorWindow;
    private EditorSettingsWindow? settingsWindow;
    private AndroidDeviceWindow? androidWindow;
    private CaptureSelectionController? selection;
    private IReadOnlyList<CaptureSource> sources = Array.Empty<CaptureSource>();
    private IReadOnlyList<AudioDevice> microphones = Array.Empty<AudioDevice>(), outputs = Array.Empty<AudioDevice>();
    private IReadOnlyList<CameraDevice> cameras = Array.Empty<CameraDevice>();
    private IReadOnlyList<VideoCodecCapabilities> videoCodecs = Array.Empty<VideoCodecCapabilities>();
    private AudioDevice? microphone, output;
    private CameraDevice? camera;
    private CaptureSource? chosenSource;
    private CaptureRegion? chosenRegion;
    private int sourceMode, markerCount;
    private bool loaded, viewReady, busy, allowClose, closing;
    private nint hwnd, previousProcedure;
    private readonly WndProc procedure;
    private bool stopRegistered, markerRegistered;
    private readonly ProjectLocations projectLocations = new();
    private string projects;
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
        projects = projectLocations.RecordingDirectory;
        RememberCatalogFolder(projects);
        InitializeComponent();
        RecorderRoot.RequestedTheme = EditorAppearance.Current;
        EditorAppearance.Changed += SetAppearance;
        Closed += (_, _) => { EditorAppearance.Changed -= SetAppearance; settingsWindow?.Close(); projectLibraryWindow?.Close(); };
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        flyouts.AddRange(new[] { MicrophoneFlyout, CameraFlyout, SystemFlyout, MoreFlyout, RecordingMoreFlyout, MarkerFlyout, FeedbackFlyout, RecoveryFlyout });
        foreach (var flyout in flyouts) flyout.ShouldConstrainToRootBounds = false;
        viewReady = true;
        try { session = new(); engineStatus = $"FrameTrail Engine {EngineSession.Version} · ABI {EngineSession.ExpectedAbiVersion}"; }
        catch (Exception error) { engineStatus = error.Message; ShowFeedback(L.T("SDE39482A8ADF"), error.Message, false); }
        coordinator.StatusChanged += UpdateStatus;
        RecorderWindowChrome.Configure(this, SetupWidth, 56, 28, center: true);
        var launchArgs = Environment.GetCommandLineArgs(); var dragTrace = Array.IndexOf(launchArgs, "--drag-diagnostics");
        RecorderWindowChrome.EnableDragging(this, RecorderRoot, HideFlyouts, dragTrace >= 0 && launchArgs.Length > dragTrace + 1
            ? line => File.AppendAllText(launchArgs[dragTrace + 1], line + Environment.NewLine) : null);
        RecorderRoot.ActualThemeChanged += (_, _) => ApplyIcons();
        AppWindow.Changed += (_, args) => { if (args.DidSizeChange) RecorderWindowChrome.Round(this, SetupPanel.Visibility == Visibility.Visible ? 28 : 24); };
        AppWindow.Closing += async (_, args) =>
        {
            if (allowClose) return;
            args.Cancel = true;
            if (editorWindow is not null) { editorWindow.Activate(); return; }
            if (busy) { ShowFeedback(L.T("SCB3734A5E3A1"), L.T("S498C41790F69")); return; }
            busy = true; closing = true;
            try
            {
                if (memo is { } activeMemo && !await activeMemo.RequestCloseAsync()) throw new IOException(L.T("S4C0E1E7A6623"));
                selection?.Dispose(); selection = null;
                if (androidWindow is { } activeAndroid) await activeAndroid.ShutdownAsync();
                HideFlyouts();
                if (cameraPreview is { } cp) { await cp.StopAsync(); cp.Close(); }
                await coordinator.DisposeAsync();
                if (opened is not null) await opened.DisposeAsync();
                completion?.Close(); editorWindow?.Close(); UnregisterHotkeys();
                if (previousProcedure != 0) SetWindowLongPtr(hwnd, -4, previousProcedure);
                session?.Dispose(); allowClose = true; Close();
            }
            catch (Exception error) { closing = false; busy = false; AppWindow.Show(); ShowFeedback(L.T("S5BE9CE24B37B"), error.Message); }
        };
        RecorderRoot.Loaded += async (_, _) =>
        {
            if (loaded) return;
            loaded = true;
            RecorderWindowChrome.Resize(this, SetupWidth, 56, 28);
            var args = Environment.GetCommandLineArgs();
            // Local visual QA must be able to capture the recorder itself. This
            // switch changes only this process; ordinary launches remain excluded.
            if (args.Contains("--inspect-ui")) ExcludeControl.IsChecked = false;
            var smokeIndex = Array.IndexOf(args, "--smoke-test");
            if (smokeIndex >= 0) { await RunSmokeTestAsync(args.Length > smokeIndex + 1 ? args[smokeIndex + 1] : Path.Combine(AppContext.BaseDirectory, "smoke-result.json")); return; }
            var memoTest = Array.IndexOf(args, "--memo-self-test");
            var memoPreview = Array.IndexOf(args, "--memo-preview");
            if ((memoTest >= 0 || memoPreview >= 0) && args.Length > Math.Max(memoTest, memoPreview) + 1) {
                string directory = Path.GetFullPath(args[Math.Max(memoTest, memoPreview) + 1]); Directory.CreateDirectory(directory);
                var note = new MemoWindow(Path.Combine(directory, "memo.txt"), line => File.AppendAllText(Path.Combine(directory, "memo-drag.log"), line + Environment.NewLine));
                memo = note; note.Activate(); AppWindow.Hide();
                if (memoTest >= 0) { await note.ValidateAsync(directory); allowClose = true; closing = true; await coordinator.DisposeAsync(); session?.Dispose(); Close(); Application.Current.Exit(); }
                else note.Closed += async (_, _) => { memo = null; if (closing) return; await InitializeRecorderAsync(); AppWindow.Show(); Activate(); };
                return;
            }
            var editorIndex = Array.IndexOf(args, "--editor");
            if (editorIndex >= 0 && args.Length > editorIndex + 1) {
                await ExecuteAsync(async () => {
                    opened = await ProjectWorkspace.OpenAsync(Path.GetFullPath(args[editorIndex + 1]));
                    var testIndex = Array.IndexOf(args, "--p1-self-test");
                    if (testIndex < 0) testIndex = Array.IndexOf(args, "--wallpapers-self-test");
                    if (testIndex < 0) testIndex = Array.IndexOf(args, "--p2-self-test");
                    if (testIndex < 0) testIndex = Array.IndexOf(args, "--m2-self-test");
                    if (testIndex < 0) testIndex = Array.IndexOf(args, "--m3-self-test");
                    if (testIndex < 0) testIndex = Array.IndexOf(args, "--m4-self-test");
                    if (testIndex < 0) testIndex = Array.IndexOf(args, "--mockup-self-test");
                    if (testIndex < 0) testIndex = Array.IndexOf(args, "--fullscreen-self-test");
                    if (testIndex < 0) testIndex = Array.IndexOf(args, "--editor-polish-self-test");
                    PresentEditor(opened, testIndex >= 0 && args.Length > testIndex + 1 ? args[testIndex + 1] : null);
                }); return;
            }
            await InitializeRecorderAsync();
            int cameraPreviewTest=Array.IndexOf(args,"--p1-camera-ui-self-test");
            if(cameraPreviewTest>=0&&args.Length>cameraPreviewTest+1){await RunCameraPreviewValidationAsync(args[cameraPreviewTest+1]);return;}
            var libraryTest = Array.IndexOf(args, "--project-library-self-test");
            var p2Library=Array.IndexOf(args,"--p2-library-self-test");
            if(p2Library>=0&&args.Length>p2Library+2){await RunP2LibraryValidationAsync(args[p2Library+1],args[p2Library+2]);return;}
            if (libraryTest >= 0 && args.Length > libraryTest + 2) { await RunProjectLibraryValidationAsync(args[libraryTest + 1], args[libraryTest + 2]); return; }
            var libraryPreview = Array.IndexOf(args, "--project-library-preview");
            if (libraryPreview >= 0 && args.Length > libraryPreview + 1) {
                var folder = Path.GetFullPath(args[libraryPreview + 1]); projectCatalog = new ProjectCatalog(Path.Combine(folder, "preferences", "catalog.json"));
                ShowProjectLibrary([folder]); return;
            }
            var projectTest = Array.IndexOf(args, "--project-open-self-test");
            if (projectTest >= 0 && args.Length > projectTest + 2) { await RunProjectOpenValidationAsync(args[projectTest + 1], args[projectTest + 2]); return; }
            var androidUiIndex = Array.IndexOf(args, "--android-ui-self-test");
            if (androidUiIndex >= 0 && args.Length > androidUiIndex + 1) {
                try { OpenAndroid(); await androidWindow!.ValidateUiAsync(Path.GetFullPath(args[androidUiIndex + 1])); }
                finally { allowClose = true; closing = true; UnregisterHotkeys(); await coordinator.DisposeAsync(); session?.Dispose(); Close(); }
                return;
            }
            if (args.Contains("--android")) OpenAndroid();
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
            int onboardingTest = Array.IndexOf(args, "--onboarding-self-test");
            if (onboardingTest >= 0 && args.Length > onboardingTest + 1) { await RunOnboardingValidationAsync(args[onboardingTest + 1]); return; }
            if(args.Length==1)await GettingStarted.ShowAsync(this);
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
    private void System_Click(object sender, RoutedEventArgs e) { if (sourceMode == 3) OpenAndroid(); else ToggleFlyout(SystemFlyout, SystemButton); }
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
    private void ShowFeedback(string title, string message, bool show = true, bool deviceIssue = false)
    {
        FeedbackTitle.Text = title; Feedback.Text = message; NoticeBadge.Visibility = Visibility.Visible;
        DeviceHelp.Visibility=deviceIssue?Visibility.Visible:Visibility.Collapsed;
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
        HideFlyouts(); sourceMode = (sourceMode + step + 4) % 4;
        SourceGlyph.Kind = new[] { "Display", "Window", "Region", "Phone" }[sourceMode];
        SourceModeLabel.Text = new[] { L.T("S1A13EA5910A0"), L.T("S9EFE01F647D6"), L.T("S3382870BC4AD"), L.T("S2E07333C8C74") }[sourceMode];
        AutomationProperties.SetName(SourceWheel, L.T("SA330D00541FF") + SourceModeLabel.Text);
        ProcessAudio.IsEnabled = sourceMode == 1;
        if (sourceMode != 1) AllSystemAudio.IsChecked = true;
        ApplyIcons(); ApplyInteraction();
    }
    private async Task RefreshDevicesAsync()
    {
        var issues = new List<string>();
        IReadOnlyList<T> Optional<T>(string name, Func<IReadOnlyList<T>> enumerate)
        { try { return enumerate(); } catch (Exception error) { issues.Add(L.T("DeviceUnavailable", name, error.Message)); return Array.Empty<T>(); } }
        var data = await Task.Run(() => (
            Sources: CaptureSession.EnumerateSources(),
            Outputs: Optional(L.T("SAAA9A8F072BD"), () => AudioCaptureSession.EnumerateDevices(AudioCaptureKind.System)),
            Microphones: Optional(L.T("S714CAC30E2FF"), () => AudioCaptureSession.EnumerateDevices(AudioCaptureKind.Microphone)),
            Cameras: Optional(L.T("SFAC308FBABB8"), CameraCaptureSession.EnumerateDevices),
            Codecs: Optional(L.T("S780DA4F96096"), CaptureSession.GetVideoCodecs)));
        sources = data.Sources.Where(s => s.Handle != (nuint)hwnd).ToArray();
        microphones = data.Microphones; outputs = data.Outputs; cameras = data.Cameras;
        videoCodecs = data.Codecs;
        VideoCodecSelector.Populate(CodecPicker, videoCodecs, true, VideoCodecSelector.Selected(CodecPicker));
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
        if (issues.Count > 0) ShowFeedback(L.T("S67CCA2746A80"), string.Join(Environment.NewLine, issues), false, true);
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
        catch (Exception error) { CameraEnabled.IsChecked = false; ShowFeedback(L.T("SD74FF5C01751"), error.Message,deviceIssue:true); }
    }
    private async void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (!viewReady) return;
        if (ReferenceEquals(sender, CameraEnabled) && CameraEnabled.IsChecked != true && cameraPreview is { } cp) { await cp.StopAsync(); cp.Close(); }
        ApplyIcons();
        if (ReferenceEquals(sender, CameraEnabled) && CameraEnabled.IsChecked == true && camera is null && cameras.Count > 0)
            await SelectCameraAsync(cameras[0]);
    }
    private void AudioScope_Changed(object sender, RoutedEventArgs e) { if (viewReady) ApplyIcons(); }
    private void AutomaticZoom_Changed(object sender, RoutedEventArgs e) { if (viewReady) ApplyAutomaticZoom(); }
    private void ApplyAutomaticZoom()
    {
        bool available = sourceMode != 3 && PointerEnabled.IsChecked == true;
        AutomaticZoomEnabled.IsEnabled = available && !busy && coordinator.State is RecordingWorkspaceState.Setup or RecordingWorkspaceState.RecordingComplete;
        AutomaticZoomGlyph.IsOn = available && AutomaticZoomEnabled.IsChecked == true;
        string label = sourceMode == 3 ? L.T("SB01237F774BF") : !available ? L.T("S0117EC73DDE2") : L.T("S1A25A6B07073") + (AutomaticZoomGlyph.IsOn ? L.T("S8DA97DDDA990") : L.T("StateOff"));
        AutomationProperties.SetName(AutomaticZoomEnabled, label);
        ToolTipService.SetToolTip(AutomaticZoomEnabled, label + (available ? L.T("SEB8D442FD580") : ""));
    }
    private void ApplyIcons()
    {
        if (!viewReady) return;
        ApplyAutomaticZoom();
        MicrophoneGlyph.IsOn = MicEnabled.IsChecked == true; CameraGlyph.IsOn = sourceMode != 3 && CameraEnabled.IsChecked == true; SystemGlyph.IsOn = sourceMode != 3 && SystemEnabled.IsChecked == true;
        CameraButton.IsEnabled = sourceMode != 3; PointerEnabled.IsEnabled = sourceMode != 3;
        ToolTipService.SetToolTip(SystemButton, sourceMode == 3 ? L.T("SF334B74234D0") : L.T("SAAA9A8F072BD"));
        AutomationProperties.SetName(MicrophoneButton, L.T("S8DC874705A7D") + (MicrophoneGlyph.IsOn ? L.T("S8DA97DDDA990") : L.T("StateOff")));
        AutomationProperties.SetName(CameraButton, L.T("SA3B71E9CD2D3") + (CameraGlyph.IsOn ? L.T("S8DA97DDDA990") : L.T("StateOff")));
        AutomationProperties.SetName(SystemButton, L.T("SEF7FCC74314C") + (SystemGlyph.IsOn ? L.T("S8DA97DDDA990") : L.T("StateOff")));
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
            using var picker = new ProjectFolderPicker(projectLocations.OpenDirectory(), L.T("S53EC764876D9"), L.T("S314C49AF454C"));
            var folder = picker.Pick(hwnd); if (folder is null) return;
            RememberCatalogFolder(projects);
            projectLocations.SetRecordingDirectory(folder);
            RememberCatalogFolder(folder);
            projects = folder; DestinationName.Text = Path.GetFileName(folder); ToolTipService.SetToolTip(DestinationButton, projects);
            var candidates = await ProjectWorkspace.ScanRecoveryAsync(projects);
            RecoveryPicker.ItemsSource = candidates; RecoveryPicker.SelectedIndex = candidates.Count > 0 ? 0 : -1; RecoverButton.IsEnabled = candidates.Count > 0;
        });
    }
    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (busy || session is null || selection is not null) return;
        HideFlyouts();
        if (sourceMode == 3) { OpenAndroid(); return; }
        try
        {
            selection = new CaptureSelectionController(this, sourceMode, sources, async (source, region) =>
            {
                selection = null; AppWindow.Show(); Activate();
                await ExecuteAsync(() => BeginRecordingAsync(source, region));
            }, () => { selection = null; if (!closing) { AppWindow.Show(); Activate(); } });
            selection.Show(); AppWindow.Hide();
        }
        catch (Exception error) { selection?.Dispose(); selection = null; AppWindow.Show(); ShowFeedback(L.T("SEE647954C91D"), error.Message); }
    }
    private async Task BeginRecordingAsync(CaptureSource source, CaptureRegion? region, AndroidSelection? android = null, AndroidConnection? prepared = null, CancellationToken cancellationToken = default)
    {
        await cameraPreviewWork;
        if (android is null && CodecPicker.SelectedItem is null) throw new InvalidOperationException(L.T("S0F65B358F75E"));
        if (opened is not null) { await opened.DisposeAsync(); opened = null; }
        var audio = new List<AudioSelection>();
        if (android is null && SystemEnabled.IsChecked == true)
        {
            if (ProcessAudio.IsChecked == true)
            { if (source.Kind != CaptureSourceKind.Window) throw new InvalidOperationException(L.T("S94861104A52D")); audio.Add(new(AudioCaptureKind.ProcessTree, ProcessId: AudioCaptureSession.ProcessIdFromWindow(source.Handle))); }
            else if (output is not null) audio.Add(new(AudioCaptureKind.System, output.Id));
            else throw new InvalidOperationException(L.T("S56C44104B573"));
        }
        if (MicEnabled.IsChecked == true)
        { if (microphone is null) throw new InvalidOperationException(L.T("S84BEFCA99B7D")); audio.Add(new(AudioCaptureKind.Microphone, microphone.Id)); }
        var title = RecordingName.Text.Trim();
        if (title.Length == 0) title = DateTime.Now.ToString("yyyy-MM-dd-HHmmss");
        if (title.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || title is "." or ".." || title.EndsWith('.') || title.EndsWith(' '))
            throw new InvalidOperationException(L.T("SCCCB2973C89F"));
        var path = Path.Combine(projects, title + ".frametrailproject");
        markerCount = 0; MarkerCount.Text = ""; lastWarning = ""; NoticeBadge.Visibility = Visibility.Collapsed;
        chosenSource = source; chosenRegion = region;
        await coordinator.StartAsync(new(path, source, region, prepared?.Capture.Status.FpsLimit ?? (FpsPicker.SelectedIndex == 1 ? 60u : 30u),
            SoftwareAllowed.IsChecked == true, audio, android is null && CameraEnabled.IsChecked == true ? camera : null,
            android is null && CameraEnabled.IsChecked == true ? CameraFormatPicker.SelectedItem as CameraFormat : null, android is null && PointerEnabled.IsChecked == true,
            Android: android, PreparedAndroid: prepared, AutomaticZoom: android is null && AutomaticZoomEnabled.IsChecked == true,
            VideoCodec: android is null ? VideoCodecSelector.Selected(CodecPicker) : VideoCodecs.H264), cancellationToken);
    }
    private void Android_Click(object sender, RoutedEventArgs e) { HideFlyouts(); OpenAndroid(); }
    private void OpenAndroid() {
        if (androidWindow is not null) { androidWindow.Activate(); return; }
        if (sourceMode != 3) RollSource(3 - sourceMode);
        androidWindow = new(FpsPicker.SelectedIndex == 1 ? 60u : 30u, async (chosen, connection, token) => {
            if (busy || coordinator.State is not (RecordingWorkspaceState.Setup or RecordingWorkspaceState.RecordingComplete)) throw new InvalidOperationException(L.T("S021783014233"));
            var info = connection.Capture.Status;
            busy = true; ApplyInteraction();
            try { await BeginRecordingAsync(new(CaptureSourceKind.Monitor, 0, chosen.Device.Model + " · Android USB", 0, 0, (int)info.Width, (int)info.Height, 96, 0), null, chosen, connection, token); }
            finally { busy = false; ApplyInteraction(); }
        });
        androidWindow.Closed += (_, _) => androidWindow = null;
        androidWindow.Activate();
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
            if (validationIndex < 0) validationIndex = Array.IndexOf(arguments, "--m3-self-test");
            if (validationIndex >= 0 && arguments.Length > validationIndex + 1) { var directory = Path.GetFullPath(arguments[validationIndex + 1]); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "editor-startup-error.txt"), error.ToString()); }
            AppWindow.Show(); ShowFeedback(L.T("S79BE13BDF0C5"), error.Message);
        }
        finally { busy = false; ApplyInteraction(); }
    }
    private void ApplyInteraction()
    {
        bool idle = coordinator.State is RecordingWorkspaceState.Setup or RecordingWorkspaceState.RecordingComplete;
        SetPanelEnabled(SetupPanel, idle && !busy);
        ApplyAutomaticZoom();
        StartButton.IsEnabled = session is not null && (sources.Count > 0 || sourceMode == 3) && !busy;
        PauseButton.IsEnabled = !busy && coordinator.State is RecordingWorkspaceState.Recording or RecordingWorkspaceState.Paused;
        StopButton.IsEnabled = PauseButton.IsEnabled;
        MarkerButton.IsEnabled = !busy && coordinator.State == RecordingWorkspaceState.Recording;
        RefreshButton.IsEnabled = idle && !busy;
        CodecPicker.IsEnabled = idle && !busy && sourceMode != 3;
        ToolTipService.SetToolTip(CodecPicker, sourceMode == 3 ? L.T("S57D25A245EEC") : L.T("S511A34AA29A2"));
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
        RecordingStatus.Text = value.State switch { RecordingWorkspaceState.Preparing => L.T("SF07335163E36"), RecordingWorkspaceState.Pausing => L.T("SB59E96CF258E"), RecordingWorkspaceState.Resuming => L.T("SA181DDAF01EF"), _ => L.T("SE2DBD39CEC91") };
        DurationText.Text = value.Duration.TotalHours >= 1 ? value.Duration.ToString(@"hh\:mm\:ss") : $"{(int)value.Duration.TotalMinutes:00}:{value.Duration.Seconds:00}";
        DurationText.Foreground = RecorderTheme.Brush(RecorderRoot, paused ? "RecorderMuted" : "RecorderInk");
        PauseGlyph.Kind = paused ? "Play" : "Pause";
        AutomationProperties.SetName(PauseButton, paused ? L.T("S377F85C59DC4") : L.T("SD9917E419D62")); ToolTipService.SetToolTip(PauseButton, paused ? L.T("S377F85C59DC4") : L.T("SD9917E419D62"));
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
            RecorderWindowChrome.Resize(this, idle ? SetupWidth : recording ? RecordingPanel.DesiredSize.Width + 2 : 240, idle ? 56 : 48, idle ? 28 : 24);
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
        { lastWarning = warning; ShowFeedback(L.T("S0FCBAC2BF934"), string.Join(Environment.NewLine, value.Warnings.TakeLast(6)), false); }
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
            if (workspace is null) throw new InvalidOperationException(L.T("S8772BA412557"));
            PresentEditor(workspace);
        });
        completion.Closed += (_, _) => { if (!closing) { AppWindow.Show(); } };
        completion.Activate(); AppWindow.Hide();
    }
    private void Open_Click(object sender, RoutedEventArgs e) => ShowProjectLibrary();
    private async Task OpenExistingProjectAsync(string path)
    {
        if (coordinator.State is not (RecordingWorkspaceState.Setup or RecordingWorkspaceState.RecordingComplete)) throw new InvalidOperationException(L.T("S0CAF0D47B58B"));
        path = ProjectLocations.ProjectDirectory(path);
        var current = editorWindow; var active = opened ?? coordinator.Workspace;
        if (active is not null && string.Equals(active.DirectoryPath, path, StringComparison.OrdinalIgnoreCase))
        { if (current is not null) current.Activate(); else PresentEditor(active); return; }
        ProjectWorkspace? candidate = await ProjectWorkspace.OpenAsync(path);
        EditorWindow? next = null;
        try
        {
            // Validate and render the destination while the current project and lease
            // are still intact. Invalid/locked projects never close the current editor.
            next = CreateEditor(candidate); next.SetProjectTransition(true);
            if (current is not null) next.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(current.AppWindow.Position.X, current.AppWindow.Position.Y, current.AppWindow.Size.Width, current.AppWindow.Size.Height));
            next.Activate();
            if (!await next.Startup.WaitAsync(TimeSpan.FromSeconds(30))) throw new IOException(L.T("S3A00B1E15B6E"));
            editorWindow = next;
            if (current is not null)
            {
                await current.RequestCloseAsync();
                if (!current.HasClosed) throw new IOException(L.T("S2BE3D82D2928"));
            }
            await ResetWorkspaceAsync(); opened = candidate; candidate = null;
            next.SetProjectTransition(false); AppWindow.Hide(); next.Activate();
        }
        catch
        {
            editorWindow = current;
            if (next is not null) await next.DiscardWindowAsync();
            if (current is not null) current.Activate(); else { AppWindow.Show(); Activate(); }
            throw;
        }
        finally { if (candidate is not null) await candidate.DisposeAsync(); }
    }
    private async Task DeleteExistingProjectAsync(string path)
    {
        if (coordinator.State is not (RecordingWorkspaceState.Setup or RecordingWorkspaceState.RecordingComplete)) throw new InvalidOperationException(L.T("S5FC185BBF2E0"));
        path = ProjectLocations.DeletionDirectory(path);
        bool active = string.Equals((opened ?? coordinator.Workspace)?.DirectoryPath, path, StringComparison.OrdinalIgnoreCase);
        var current = editorWindow;
        if (active)
        {
            if (current is not null) {
                editorWindow = null;
                await current.RequestCloseAsync();
                if (!current.HasClosed) { editorWindow = current; throw new IOException(L.T("S77FF5FC35802")); }
            }
            completion?.Close(); completion = null;
            await ResetWorkspaceAsync();
        }
        else
        {
            // Acquire a writable lease before touching an inactive project. A
            // project being recorded/edited by another process must stay intact.
            await ProjectWorkspace.VerifyExclusiveAccessAsync(path);
        }
        try { await Task.Run(() => ProjectDeletion.Recycle(path)); }
        catch (Exception error) {
            if (active && Directory.Exists(path)) {
                try { await OpenExistingProjectAsync(path); } catch { AppWindow.Show(); Activate(); }
            }
            if (editorWindow is not null) editorWindow.ReportProjectError(L.T("S41BB2FD4B435"), error.Message);
            else { AppWindow.Show(); Activate(); ShowFeedback(L.T("S41BB2FD4B435"), error.Message); }
            throw;
        }
        var recoverable = await ProjectWorkspace.ScanRecoveryAsync(projects);
        RecoveryPicker.ItemsSource = recoverable; RecoveryPicker.SelectedIndex = recoverable.Count > 0 ? 0 : -1; RecoverButton.IsEnabled = recoverable.Count > 0;
        if (editorWindow is null) { AppWindow.Show(); Activate(); ShowFeedback(L.T("SDD35672E3B9D"), L.T("SF647773138F8"), false); }
    }
    private async void Recover_Click(object sender, RoutedEventArgs e)
    {
        HideFlyouts();
        await ExecuteAsync(async () =>
        {
            if (RecoveryPicker.SelectedItem is not RecoveryCandidate candidate) throw new InvalidOperationException(L.T("S4FF22B1FB079"));
            await ResetWorkspaceAsync(); var result = await ProjectWorkspace.RecoverAsync(candidate.Directory); opened = result.Workspace;
            PresentCompletion(opened.DirectoryPath, opened.Snapshot!);
            if (result.Warnings.Count > 0) completion?.SetNotice(string.Join(Environment.NewLine, result.Warnings));
        });
    }
    private async Task ResetWorkspaceAsync()
    {
        await coordinator.DisposeAsync(); coordinator.StatusChanged -= UpdateStatus;
        coordinator = new(presetLibrary: () => new VisualPresetLibrary()); coordinator.StatusChanged += UpdateStatus;
        if (opened is not null) { await opened.DisposeAsync(); opened = null; }
        surface = ""; SetupPanel.Visibility = Visibility.Visible; RecordingPanel.Visibility = ProgressPanel.Visibility = Visibility.Collapsed;
        RecorderRoot.CornerRadius = new CornerRadius(28); RecorderWindowChrome.Resize(this, SetupWidth, 56, 28);
    }
    private void PresentEditor(ProjectWorkspace workspace, string? validation = null)
    {
        if (editorWindow is not null) { editorWindow.Activate(); return; }
        completion?.Close(); completion = null;
        editorWindow = CreateEditor(workspace, validation);
        editorWindow.Activate(); AppWindow.Hide();
    }
    private EditorWindow CreateEditor(ProjectWorkspace workspace, string? validation = null)
    {
        RememberCatalogProject(workspace.DirectoryPath);
        var window = new EditorWindow(workspace, validation, OpenExistingProjectAsync, DeleteExistingProjectAsync, ShowProjectLibrary);
        window.Closed += async (_, _) => {
            if (!ReferenceEquals(editorWindow, window)) return;
            editorWindow = null;
            if (validation is not null) { if (opened is not null) await opened.DisposeAsync(); allowClose = true; closing = true; Close(); Application.Current.Exit(); }
            else if (!closing) { await InitializeRecorderAsync(); AppWindow.Show(); Activate(); }
        };
        return window;
    }
    private async void Memo_Click(object sender, RoutedEventArgs e)
    {
        HideFlyouts();
        if (memo is not null) { await memo.RequestCloseAsync(); return; }
        memo = new MemoWindow(); memo.Closed += (_, _) => { memo = null; MemoMenuButton.Content = L.T("SD5E96F55FB67"); };
        memo.Activate(); MemoMenuButton.Content = L.T("S01927B273B65"); ApplyExclusion();
    }
    private void ShowSource_Click(object sender, RoutedEventArgs e)
    {
        HideFlyouts();
        ShowFeedback(L.T("SABF772F02953"), chosenSource is null ? L.T("S49990BE55E2E") : chosenSource.Name + (chosenRegion is CaptureRegion r ? L.T("SE16B08A48653", r.Width, r.Height, r.X, r.Y) : $"\n{chosenSource.Width} × {chosenSource.Height}"));
    }
    private void Root_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Escape) { HideFlyouts(); e.Handled = true; } }
    private void Quit_Click(object sender, RoutedEventArgs e) { HideFlyouts(); Close(); }
    private void Exclusion_Changed(object sender, RoutedEventArgs e) { if (loaded) ApplyExclusion(); }
    private void ApplyExclusion()
    {
        try { RecordingUtilities.ExcludeWindow((nuint)hwnd, ExcludeControl.IsChecked == true); if (memo is not null) RecordingUtilities.ExcludeWindow((nuint)WinRT.Interop.WindowNative.GetWindowHandle(memo), ExcludeControl.IsChecked == true); }
        catch (Exception error) { ShowFeedback(L.T("S384EE0E968C5"), error.Message, false); }
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
            HotkeyStatus.Text = L.T("S65A0B7C0169D");
            RecordingShortcutHint.Text = L.T("SC6DCD0176BB5", 6 + StopKey.SelectedIndex);
        }
        catch (Exception error) { HotkeyStatus.Text = L.T("S63139AE36672") + error.Message; }
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
                runtimeDirectory = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
                nativeModules = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>()
                    .Where(module => module.ModuleName.StartsWith("FrameTrail.", StringComparison.OrdinalIgnoreCase) || module.ModuleName == "Microsoft.UI.Xaml.dll").Select(module => module.FileName).ToArray(),
                processId = Environment.ProcessId, osVersion = Environment.OSVersion.VersionString, timestampUtc = DateTimeOffset.UtcNow, error = errorMessage }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { success = false; }
        Environment.ExitCode = success ? 0 : 1; allowClose = true; session?.Dispose(); Close();
    }
}
