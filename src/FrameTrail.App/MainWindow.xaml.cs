using FrameTrail.Interop;
using FrameTrail.Workspace;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FrameTrail.App;

public sealed partial class MainWindow : Window
{
    private EngineSession? session;
    private RecordingCoordinator coordinator = new();
    private ProjectWorkspace? opened;
    private Window? memo;
    private bool loaded, busy, allowClose;
    private nint hwnd, previousProcedure;
    private readonly WndProc procedure;
    private bool stopRegistered, markerRegistered;
    private readonly string projects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FrameTrail");
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WndProc(nint window, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")] private static extern nint CallWindowProc(nint procedure, nint window, uint message, nuint wparam, nint lparam);

    public MainWindow()
    {
        procedure = WindowProcedure; InitializeComponent(); AppWindow.Resize(new Windows.Graphics.SizeInt32(1120, 980));
        InitializeEngine(); coordinator.StatusChanged += UpdateStatus;
        AppWindow.Closing += async (_, args) =>
        {
            if (allowClose) return; args.Cancel = true;
            if (busy) { Feedback.Text = "正在完成操作，稍后可关闭窗口。"; return; }
            busy = true;
            try { await coordinator.DisposeAsync(); if (opened is not null) await opened.DisposeAsync(); UnregisterHotkeys();
                if (previousProcedure != 0) SetWindowLongPtr(hwnd, -4, previousProcedure); memo?.Close(); session?.Dispose(); allowClose = true; Close(); }
            catch (Exception error) { Feedback.Text = "关闭前保存失败：" + error.Message; busy = false; }
        };
        ((FrameworkElement)Content).Loaded += async (_, _) =>
        {
            if (loaded) return; loaded = true; hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var args = Environment.GetCommandLineArgs(); var smokeIndex = Array.IndexOf(args, "--smoke-test");
            if (smokeIndex >= 0) { await RunSmokeTestAsync(args.Length > smokeIndex + 1 ? args[smokeIndex + 1] : Path.Combine(AppContext.BaseDirectory, "smoke-result.json")); return; }
            previousProcedure = SetWindowLongPtr(hwnd, -4, Marshal.GetFunctionPointerForDelegate(procedure));
            SetNewPath(); ApplyHotkeys(); ApplyExclusion(); await RefreshAsync();
        };
    }
    private void InitializeEngine()
    {
        try { session = new(); EngineStatus.Text = $"FrameTrail Engine {EngineSession.Version} · ABI {EngineSession.ExpectedAbiVersion} · x64"; }
        catch (Exception error) { EngineStatus.Text = "引擎初始化失败：" + error.Message; SessionCheckButton.IsEnabled = false; StartButton.IsEnabled = false; }
    }
    private void CheckSession_Click(object sender, RoutedEventArgs e)
    {
        try { using var probe = new EngineSession(); probe.Cancel(); EngineStatus.Text = $"FrameTrail Engine {EngineSession.Version} · 会话创建、取消与释放检查{(probe.IsCancelled ? "通过" : "失败")}"; }
        catch (Exception error) { Feedback.Text = error.Message; }
    }
    private void SetNewPath() => ProjectPath.Text = Path.Combine(projects, DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".frametrailproject");
    private async Task RefreshAsync() => await ExecuteAsync(async () =>
    {
        var issues = new List<string>();
        IReadOnlyList<T> Optional<T>(string name, Func<IReadOnlyList<T>> enumerate)
        { try { return enumerate(); } catch (Exception error) { issues.Add(name + "不可用：" + error.Message); return Array.Empty<T>(); } }
        var data = await Task.Run(() => (Sources: CaptureSession.EnumerateSources(), System: Optional("系统声音设备", () => AudioCaptureSession.EnumerateDevices(AudioCaptureKind.System)),
            Microphone: Optional("麦克风设备", () => AudioCaptureSession.EnumerateDevices(AudioCaptureKind.Microphone)), Camera: Optional("摄像头", CameraCaptureSession.EnumerateDevices)));
        SourcePicker.ItemsSource = data.Sources; SourcePicker.SelectedIndex = data.Sources.Count > 0 ? 0 : -1;
        SystemPicker.ItemsSource = data.System; SystemPicker.SelectedIndex = data.System.Count > 0 ? 0 : -1;
        MicPicker.ItemsSource = data.Microphone; MicPicker.SelectedIndex = data.Microphone.Count > 0 ? 0 : -1;
        CameraPicker.ItemsSource = new[] { new CameraDevice("", "不使用摄像头") }.Concat(data.Camera).ToArray(); CameraPicker.SelectedIndex = 0;
        RecoveryPicker.ItemsSource = await ProjectWorkspace.ScanRecoveryAsync(projects); Feedback.Text = "来源与设备已刷新。" + (issues.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, issues));
    });
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void Camera_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CameraFormatPicker is null) return;
        var device = CameraPicker.SelectedItem as CameraDevice;
        if (device is null || device.Id.Length == 0) { CameraFormatPicker.ItemsSource = null; return; }
        try { var formats = await Task.Run(() => CameraCaptureSession.EnumerateFormats(device.Id)); if ((CameraPicker.SelectedItem as CameraDevice)?.Id != device.Id) return;
            CameraFormatPicker.ItemsSource = formats; CameraFormatPicker.SelectedIndex = formats.Count > 0 ? 0 : -1; }
        catch (Exception error) { Feedback.Text = "摄像头格式读取失败：" + error.Message; }
    }
    private async void Start_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (SourcePicker.SelectedItem is not CaptureSource source) throw new InvalidOperationException("请选择录制来源。");
        if (opened is not null) { await opened.DisposeAsync(); opened = null; }
        var audio = new List<AudioSelection>();
        if (SystemEnabled.IsChecked == true)
        {
            if (ProcessAudio.IsChecked == true) { if (source.Kind != CaptureSourceKind.Window) throw new InvalidOperationException("进程树音频需要选择窗口。"); audio.Add(new(AudioCaptureKind.ProcessTree, ProcessId: AudioCaptureSession.ProcessIdFromWindow(source.Handle))); }
            else if (SystemPicker.SelectedItem is AudioDevice device) audio.Add(new(AudioCaptureKind.System, device.Id));
            else throw new InvalidOperationException("请选择系统声音输出设备。");
        }
        if (MicEnabled.IsChecked == true) { if (MicPicker.SelectedItem is not AudioDevice device) throw new InvalidOperationException("请选择麦克风。"); audio.Add(new(AudioCaptureKind.Microphone, device.Id)); }
        var camera = CameraPicker.SelectedItem as CameraDevice; if (camera?.Id.Length == 0) camera = null;
        CaptureRegion? region = RegionEnabled.IsChecked == true ? new((int)RegionX.Value, (int)RegionY.Value, (int)RegionWidth.Value, (int)RegionHeight.Value) : null;
        await coordinator.StartAsync(new(ProjectPath.Text, source, region, FpsPicker.SelectedIndex == 1 ? 60u : 30u, SoftwareAllowed.IsChecked == true,
            audio, camera, CameraFormatPicker.SelectedItem as CameraFormat, PointerEnabled.IsChecked == true));
    });
    private async void Pause_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () => { if (coordinator.State == RecordingWorkspaceState.Paused) await coordinator.ResumeAsync(); else await coordinator.PauseAsync(); });
    private async void Stop_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    { var value = await coordinator.StopAsync(); if (value is not null) { Feedback.Text = $"项目已保存：{coordinator.Workspace!.DirectoryPath}\n媒体轨段：{value.Media.Count} · 标记：{value.Markers.Count} · 修订：{value.Revision}"; SetNewPath(); } });
    private async void Marker_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(() => coordinator.AddMarkerAsync(MarkerLabel.Text));
    private async Task ExecuteAsync(Func<Task> action)
    {
        if (busy) return; busy = true; RefreshButton.IsEnabled = false;
        try { await action(); } catch (Exception error) { Feedback.Text = error.Message; }
        finally { busy = false; RefreshButton.IsEnabled = coordinator.State is RecordingWorkspaceState.Setup or RecordingWorkspaceState.RecordingComplete; }
    }
    private void UpdateStatus(object? sender, RecordingWorkspaceStatus value) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!ReferenceEquals(sender, coordinator) || value.RunId != coordinator.RunId) return;
        RecordingStatus.Text = value.State switch { RecordingWorkspaceState.Preparing => "正在准备…", RecordingWorkspaceState.Recording => "录制中", RecordingWorkspaceState.Pausing => "正在暂停并保存…", RecordingWorkspaceState.Paused => "已暂停", RecordingWorkspaceState.Resuming => "正在继续…", RecordingWorkspaceState.Finalizing => "正在结束并保存…", RecordingWorkspaceState.RecordingComplete => "录制完成", _ => "就绪" };
        DurationText.Text = value.Duration.ToString(@"hh\:mm\:ss"); MicrophoneLevel.Value = value.MicrophonePeak;
        SetupPanel.IsEnabled = value.State is RecordingWorkspaceState.Setup or RecordingWorkspaceState.RecordingComplete;
        StartButton.IsEnabled = SetupPanel.IsEnabled && session is not null; PauseButton.IsEnabled = value.State is RecordingWorkspaceState.Recording or RecordingWorkspaceState.Paused;
        PauseButton.Content = value.State == RecordingWorkspaceState.Paused ? "继续" : "暂停"; StopButton.IsEnabled = PauseButton.IsEnabled; MarkerButton.IsEnabled = value.State == RecordingWorkspaceState.Recording;
        if (value.Warnings.Count > 0) Feedback.Text = string.Join(Environment.NewLine, value.Warnings.TakeLast(8));
    });
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (coordinator.State is not (RecordingWorkspaceState.Setup or RecordingWorkspaceState.RecordingComplete)) return;
        await ExecuteAsync(async () =>
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker(); picker.FileTypeFilter.Add(".json"); WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSingleFileAsync(); if (file is null) return;
            if (!string.Equals(Path.GetFileName(file.Path), "project.json", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请选择工程目录中的 project.json。");
            await ResetWorkspaceAsync(); opened = await ProjectWorkspace.OpenAsync(Path.GetDirectoryName(file.Path)!); ShowOpened();
        });
    }
    private async void Recover_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (coordinator.State is not (RecordingWorkspaceState.Setup or RecordingWorkspaceState.RecordingComplete)) throw new InvalidOperationException("请先结束录制。");
        if (RecoveryPicker.SelectedItem is not RecoveryCandidate candidate) throw new InvalidOperationException("请选择待恢复工程。");
        await ResetWorkspaceAsync(); var result = await ProjectWorkspace.RecoverAsync(candidate.Directory); opened = result.Workspace; ShowOpened();
        if (result.Warnings.Count > 0) Feedback.Text += Environment.NewLine + string.Join(Environment.NewLine, result.Warnings);
    });
    private async Task ResetWorkspaceAsync()
    {
        await coordinator.DisposeAsync(); coordinator.StatusChanged -= UpdateStatus; coordinator = new(); coordinator.StatusChanged += UpdateStatus;
        if (opened is not null) { await opened.DisposeAsync(); opened = null; }
    }
    private void ShowOpened() => Feedback.Text = $"已打开：{opened!.DirectoryPath}\n有效媒体轨段：{opened.Snapshot!.Media.Count} · 标记：{opened.Snapshot.Markers.Count} · 修订：{opened.Snapshot.Revision}。编辑界面将在后续阶段提供。";
    private void Memo_Click(object sender, RoutedEventArgs e)
    {
        if (memo is null) { memo = new Window { Title = "FrameTrail 备忘录", Content = new Grid { Padding = new Thickness(20), Children = { new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, PlaceholderText = "录制时的提词与备注" } } } }; memo.AppWindow.Resize(new Windows.Graphics.SizeInt32(420, 420)); memo.Closed += (_, _) => memo = null; }
        memo.Activate(); ApplyExclusion();
    }
    private void Exclusion_Changed(object sender, RoutedEventArgs e) { if (loaded) ApplyExclusion(); }
    private void ApplyExclusion()
    {
        try { if (hwnd != 0) RecordingUtilities.ExcludeWindow((nuint)hwnd, ExcludeControl.IsChecked == true); if (memo is not null) RecordingUtilities.ExcludeWindow((nuint)WinRT.Interop.WindowNative.GetWindowHandle(memo), ExcludeControl.IsChecked == true); }
        catch (Exception error) { Feedback.Text = "窗口排除失败：" + error.Message; }
    }
    private void Hotkey_Click(object sender, RoutedEventArgs e) => ApplyHotkeys();
    private void UnregisterHotkeys()
    {
        if (stopRegistered) { RecordingUtilities.UnregisterHotkey((nuint)hwnd, 0x4654); stopRegistered = false; }
        if (markerRegistered) { RecordingUtilities.UnregisterHotkey((nuint)hwnd, 0x4655); markerRegistered = false; }
    }
    private void ApplyHotkeys()
    {
        try { UnregisterHotkeys(); RecordingUtilities.RegisterHotkey((nuint)hwnd, 0x4654, 6, (uint)(0x75 + StopKey.SelectedIndex)); stopRegistered = true;
            RecordingUtilities.RegisterHotkey((nuint)hwnd, 0x4655, 6, (uint)(0x75 + MarkerKey.SelectedIndex)); markerRegistered = true; HotkeyStatus.Text = "全局快捷键已注册。"; }
        catch (Exception error) { HotkeyStatus.Text = "快捷键冲突或注册失败，请更换按键后重试：" + error.Message; }
    }
    private nint WindowProcedure(nint window, uint message, nuint wparam, nint lparam)
    {
        if (message == 0x0312) { if ((int)wparam == 0x4654 && StopButton.IsEnabled) DispatcherQueue.TryEnqueue(() => Stop_Click(this, new RoutedEventArgs()));
            if ((int)wparam == 0x4655 && MarkerButton.IsEnabled) DispatcherQueue.TryEnqueue(() => Marker_Click(this, new RoutedEventArgs())); return 0; }
        return CallWindowProc(previousProcedure, window, message, wparam, lparam);
    }
    private async Task RunSmokeTestAsync(string output)
    {
        bool success = false; string? errorMessage = null, version = null;
        try { if (session is null) throw new InvalidOperationException(EngineStatus.Text); version = EngineSession.Version;
            for (int i = 0; i < 100; i++) { using var probe = new EngineSession(); if (probe.IsCancelled) throw new InvalidOperationException("Fresh session was cancelled."); probe.Cancel(); if (!probe.IsCancelled) throw new InvalidOperationException("Cancel did not reach native session."); } success = true; }
        catch (Exception error) { errorMessage = error.ToString(); }
        try { var absolute = Path.GetFullPath(output); Directory.CreateDirectory(Path.GetDirectoryName(absolute)!); await File.WriteAllTextAsync(absolute, JsonSerializer.Serialize(new
            { success, windowLoaded = true, version, abi = EngineSession.ExpectedAbiVersion, lifecycleChecks = success ? 100 : 0, processId = Environment.ProcessId, osVersion = Environment.OSVersion.VersionString, timestampUtc = DateTimeOffset.UtcNow, error = errorMessage }, new JsonSerializerOptions { WriteIndented = true })); }
        catch { success = false; }
        Environment.ExitCode = success ? 0 : 1; allowClose = true; session?.Dispose(); Close();
    }
}
