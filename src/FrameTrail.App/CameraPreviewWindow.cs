using FrameTrail.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;

namespace FrameTrail.App;

internal sealed class CameraPreviewWindow : Window
{
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    private readonly Image picture = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock status = new() { Text = L.T("S84539A1C1AB0"), TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox mirror = new() { Content = L.T("SDEC5F943BECC"), IsChecked = true };
    private readonly HyperlinkButton permissions=new(){Content=L.T("SB4763A13DAEA"),NavigateUri=new Uri("ms-settings:privacy-webcam"),Visibility=Visibility.Collapsed};
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer timer;
    private readonly SemaphoreSlim gate = new(1);
    private CameraPreviewSession? session;
    private WriteableBitmap? bitmap;
    private byte[] pixels = [];
    private bool closed, reading;
    private int displayedFrames;
    internal CameraPreviewWindow()
    {
        Title = L.T("S6A32148E5AB1"); AppIcon.Apply(this);
        var root = new Grid { Padding = new Thickness(14), RowSpacing = 10, Background = RecorderTheme.Brush(new Grid(), "RecorderBase") };
        root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(picture); Grid.SetRow(mirror, 1); root.Children.Add(mirror);
        var details = new StackPanel { Spacing = 5 }; details.Children.Add(status);
        details.Children.Add(permissions);
        details.Children.Add(new TextBlock { Text = L.T("S5BABD446561D"), FontSize = 11, TextWrapping = TextWrapping.Wrap });
        Grid.SetRow(details, 2); root.Children.Add(details); Content = root;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(440, 380));
        SetWindowDisplayAffinity(WinRT.Interop.WindowNative.GetWindowHandle(this), 0x11);
        mirror.Click += (_, _) => ApplyMirror();
        picture.SizeChanged += (_, _) => ApplyMirror();
        timer = DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(40); timer.Tick += async (_, _) => await ReadAsync();
        Closed += async (_, _) => { closed = true; timer.Stop(); await StopAsync(); };
    }
    private void ApplyMirror() => picture.RenderTransform = new ScaleTransform { ScaleX = mirror.IsChecked == true ? -1 : 1, CenterX = picture.ActualWidth / 2 };
    internal async Task ConnectAsync(CameraDevice device, CameraFormat format)
    {
        await gate.WaitAsync();
        try {
            timer.Stop(); if (session is not null) { var old = session; session = null; await Task.Run(old.Dispose); }
            if (closed) return;
            status.Text = L.T("S84539A1C1AB0");permissions.Visibility=Visibility.Collapsed;
            var created = await Task.Run(() => CameraPreviewSession.Start(device, format));
            if (closed) { await Task.Run(created.Dispose); return; }
            session = created; pixels = new byte[checked((int)(format.Width * format.Height * 4))];
            bitmap = new WriteableBitmap((int)format.Width, (int)format.Height); picture.Source = bitmap;
            status.Text = device.Name; timer.Start();
        } catch (Exception error) { status.Text = L.T("S5A304E397727") + error.Message; permissions.Visibility=Visibility.Visible; }
        finally { gate.Release(); }
    }
    private async Task ReadAsync()
    {
        if (reading || closed || !await gate.WaitAsync(0)) return; reading = true;
        try {
            var current = session; if (current is null || bitmap is null) return;
            if (await Task.Run(() => current.CopyFrame(pixels)) && !closed) {
                using var output = bitmap.PixelBuffer.AsStream(); await output.WriteAsync(pixels); bitmap.Invalidate();displayedFrames++;
            }
        } catch { timer.Stop(); status.Text = L.T("S58F3BF87AAEC"); permissions.Visibility=Visibility.Visible; }
        finally { reading = false; gate.Release(); }
    }
    internal async Task StopAsync()
    {
        timer.Stop(); await gate.WaitAsync();
        try { var old = session; session = null; if (old is not null) await Task.Run(old.Dispose); }
        finally { gate.Release(); }
    }
    internal async Task<object> ValidatePresentationAsync()
    {
        var deadline=DateTime.UtcNow.AddSeconds(10);while(displayedFrames<3&&DateTime.UtcNow<deadline)await Task.Delay(40);
        if(displayedFrames<3)throw new IOException("Camera window did not present three frames.");
        mirror.IsChecked=true;ApplyMirror();if((picture.RenderTransform as ScaleTransform)?.ScaleX!=-1)throw new IOException("Mirror presentation failed.");
        mirror.IsChecked=false;ApplyMirror();if((picture.RenderTransform as ScaleTransform)?.ScaleX!=1)throw new IOException("Unmirrored presentation failed.");
        return new {displayedFrames,sourceIdentity=session!.SourceIdentity,width=session.Format.Width,height=session.Format.Height,mirrorToggle=true};
    }
}
