using FrameTrail.Interop;
using FrameTrail.Workspace;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using WinRT;

namespace FrameTrail.App;

internal sealed class AndroidDeviceWindow : Window
{
    private readonly Grid root = new() { Padding = new Thickness(24), RowSpacing = 12 };
    private readonly ComboBox picker = new() { HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "DisplayName", PlaceholderText = L.T("SC21E6DECDCA7") };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 13 };
    private readonly CheckBox phoneAudio = new() { Content = L.T("S59F2A791D96E"), IsChecked = true };
    private readonly CheckBox fallback = new() { Content = L.T("S766717FB2D8B"), IsChecked = true };
    private readonly Button refresh = new() { Content = L.T("SDC54091453CC") }, connect = new() { Content = L.T("S32B435884FA9") }, record = new() { Content = L.T("S415D8F361894"), IsEnabled = false }, cancel = new() { Content = L.T("S2CD0F3BE8738") };
    private readonly SwapChainPanel preview = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border previewHost = new() { CornerRadius = new CornerRadius(12), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black), MinHeight = 128 };
    private readonly TextBlock placeholder = new() { Text = L.T("S3D8726276046"), Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<AndroidSelection, AndroidConnection, CancellationToken, Task> start;
    private readonly uint fps;
    private AndroidConnection? connection;
    private bool working, handedOff, closeRequested;
    private TaskCompletionSource? operation;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    internal AndroidDeviceWindow(uint fps, Func<AndroidSelection, AndroidConnection, CancellationToken, Task> start)
    {
        this.fps = fps; this.start = start; Title = "FrameTrail · Android USB";
        AppIcon.Apply(this);
        root.RequestedTheme = EditorAppearance.Current;
        root.Background = RecorderTheme.Brush(root, "EditorPanel");
        for (int i = 0; i < 6; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        void Add(FrameworkElement child, int row) { Grid.SetRow(child, row); root.Children.Add(child); }
        Add(new TextBlock { Text = L.T("SA4B055F32AE0"), FontSize = 24 }, 0);
        Add(picker, 1);
        var help = new StackPanel { Spacing = 6 };
        help.Children.Add(new TextBlock { Text = L.T("S049D8ADC88E1"), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var guide = new HyperlinkButton { Content = L.T("S8ADFB4BA8CE3"), NavigateUri = new Uri("https://developer.android.com/studio/run/oem-usb"), Padding = new Thickness(0) }; help.Children.Add(guide); Add(help, 2);
        var previewContent = new Grid(); previewContent.Children.Add(preview); previewContent.Children.Add(placeholder); previewHost.Child = previewContent; Add(previewHost, 3);
        var options = new StackPanel { Spacing = 4 }; options.Children.Add(phoneAudio); options.Children.Add(fallback); options.Children.Add(status); Add(options, 4);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; foreach (var button in new[] { refresh, connect, record, cancel }) { actions.Children.Add(button); button.CornerRadius = new CornerRadius(16); } Add(actions, 5);
        AutomationProperties.SetName(picker, L.T("SF38CB9F9F0A8")); AutomationProperties.SetName(preview, L.T("SC6D12BCD325C")); AutomationProperties.SetName(status, L.T("S3185B4EE849D"));
        Content = root; var scale = RecorderWindowChrome.Scale(this);
        var workArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Min((int)(580 * scale), workArea.Width), Math.Min((int)(820 * scale), workArea.Height)));
        AppWindow.Move(new Windows.Graphics.PointInt32(workArea.X + (workArea.Width - AppWindow.Size.Width) / 2, workArea.Y + (workArea.Height - AppWindow.Size.Height) / 2));
        preview.SizeChanged += (_, _) => FitPreview();
        previewHost.SizeChanged += (_, _) => FitPreview();
        root.SizeChanged += (_, _) => FitPreview();
        refresh.Click += async (_, _) => await RunAsync(RefreshAsync);
        connect.Click += async (_, _) => await RunAsync(ConnectAsync);
        picker.SelectionChanged += async (_, _) => { if (!working) await RunAsync(DisconnectAsync); };
        phoneAudio.Click += async (_, _) => await RunAsync(DisconnectAsync);
        fallback.Click += async (_, _) => await RunAsync(DisconnectAsync);
        record.Click += async (_, _) => await RunAsync(async () => {
            if (connection is null || picker.SelectedItem is not AndroidDevice device) return;
            var current = connection; connection = null; Detach();
            try { await start(new(device, phoneAudio.IsChecked == true, fallback.IsChecked == true), current, lifetime.Token); handedOff = true; Close(); }
            catch { await current.DisposeAsync(); throw; }
        });
        cancel.Click += (_, _) => Close();
        AppWindow.Closing += (_, args) => { if (working) { args.Cancel = true; closeRequested = true; lifetime.Cancel(); status.Text = L.T("SDC43E9CB286E"); } };
        timer.Tick += (_, _) => {
            if (connection is null) return;
            var value = connection.Capture.Status;
            if (value.State == 3) { record.IsEnabled = false; status.Text = L.T("S1E79AAD84794") + value.Message + L.T("SF03D4A8AE2A9"); }
        };
        Closed += async (_, _) => { lifetime.Cancel(); timer.Stop(); if (!handedOff) await DisconnectAsync(); lifetime.Dispose(); };
        root.Loaded += async (_, _) => { await RunAsync(RefreshAsync); timer.Start(); };
    }
    private async Task RunAsync(Func<Task> action) {
        if (working) return; working = true; operation = new(TaskCreationOptions.RunContinuationsAsynchronously); SetEnabled(false);
        try { await action(); } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { status.Text = error is FileNotFoundException or DirectoryNotFoundException ? L.T("SDDCA240462C2") : error.Message; }
        finally { working = false; operation.TrySetResult(); if (closeRequested) Close(); else if (!lifetime.IsCancellationRequested) SetEnabled(true); }
    }
    private void SetEnabled(bool value) { refresh.IsEnabled = connect.IsEnabled = picker.IsEnabled = phoneAudio.IsEnabled = fallback.IsEnabled = value; record.IsEnabled = value && connection is not null && connection.Capture.Status.State == 2; }
    private async Task RefreshAsync() {
        await DisconnectAsync(); status.Text = L.T("SABF9B0B229AA");
        var devices = await new AndroidRuntime().DevicesAsync(lifetime.Token); picker.ItemsSource = devices; picker.SelectedIndex = devices.Count == 1 ? 0 : -1;
        status.Text = devices.Count == 0 ? L.T("SB779BDE455DC") : L.T("S7FA2CC3330FC");
    }
    private async Task ConnectAsync() {
        await DisconnectAsync();
        if (picker.SelectedItem is not AndroidDevice device) throw new IOException(L.T("S3E95A6DF709D"));
        if (!device.Ready) throw new IOException(device.State == "unauthorized" ? L.T("S5F5FD6CE2063") : L.T("S1F371277126D"));
        status.Text = L.T("S848EF36AE74D");
        connection = await AndroidConnection.ConnectAsync(new AndroidRuntime(), new(device, phoneAudio.IsChecked == true, fallback.IsChecked == true), fps, lifetime.Token);
        nint swapchain = connection.Capture.AcquireSwapChain();
        try { preview.As<ISwapChainPanelNative>().SetSwapChain(swapchain); } finally { Marshal.Release(swapchain); }
        placeholder.Visibility = Visibility.Collapsed;
        FitPreview(); var value = connection.Capture.Status;
        status.Text = L.T("S53E84FD332E1", value.Width, value.Height, fps) + (connection.InternalAudioAvailable ? L.T("S70CEDBDC1E2E") : L.T("SEE48C03306C3")) + L.T("S1459734B1AEA");
    }
    private void FitPreview() {
        if (connection is null || previewHost.ActualWidth < 1 || previewHost.ActualHeight < 1) return;
        var value = connection.Capture.Status; if (value.Height == 0) return;
        var ratio = (double)value.Width / value.Height; var maxWidth = previewHost.ActualWidth; var maxHeight = previewHost.ActualHeight;
        preview.Width = Math.Min(maxWidth, maxHeight * ratio); preview.Height = preview.Width / ratio;
    }
    private void Detach() => preview.As<ISwapChainPanelNative>().SetSwapChain(0);
    private async Task DisconnectAsync() { Detach(); placeholder.Visibility = Visibility.Visible; var current = connection; connection = null; record.IsEnabled = false; if (current is not null) await current.DisposeAsync(); }
    internal async Task ShutdownAsync() { closeRequested = true; lifetime.Cancel(); if (working && operation is not null) await operation.Task; await DisconnectAsync(); Close(); }
    internal async Task ValidateUiAsync(string directory) {
        Directory.CreateDirectory(directory); bool success = false; string? error = null;
        try {
            for (int i = 0; i < 200 && (root.ActualWidth == 0 || working); i++) await Task.Delay(50);
            if (working || record.IsEnabled || root.ActualWidth == 0) throw new InvalidOperationException("Android setup did not settle safely.");
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark }) {
                root.RequestedTheme = theme; root.Background = RecorderTheme.Brush(root, "EditorPanel"); await Task.Delay(150);
                await MainWindow.SaveUiFrameAsync(root, Path.Combine(directory, $"android-{theme.ToString().ToLowerInvariant()}.png"));
            }
            root.RequestedTheme = ElementTheme.Light; root.Background = RecorderTheme.Brush(root, "EditorPanel");
            picker.ItemsSource = new[] { new AndroidDevice("private-validation-id", "unauthorized", L.T("S0F92787652F8"), "1", "USB") }; picker.SelectedIndex = 0;
            await RunAsync(ConnectAsync);
            if (!status.Text.Contains(L.T("S6CF2531AB111"), StringComparison.Ordinal) || record.IsEnabled) throw new InvalidOperationException("Unauthorized device was allowed to record.");
            await MainWindow.SaveUiFrameAsync(root, Path.Combine(directory, "android-unauthorized.png"));
            success = true;
        } catch (Exception exception) { error = exception.ToString(); }
        await File.WriteAllTextAsync(Path.Combine(directory, "android-ui-validation.json"), System.Text.Json.JsonSerializer.Serialize(new { success, error, width = root.ActualWidth, height = root.ActualHeight, imageSource = "WinUI RenderTargetBitmap; no physical phone/input automation" }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Close(); if (!success) throw new IOException(error);
    }
}
