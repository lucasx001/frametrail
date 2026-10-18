using FrameTrail.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FrameTrail.App;

public sealed partial class MainWindow
{
    private CameraPreviewWindow? cameraPreview;
    private Task cameraPreviewWork = Task.CompletedTask;
    private async void CameraPreview_Click(object sender, RoutedEventArgs e)
    {
        if (camera is null || CameraFormatPicker.SelectedItem is not CameraFormat format) return;
        if (cameraPreview is null) { cameraPreview = new(); cameraPreview.Closed += (_, _) => cameraPreview = null; }
        cameraPreview.Activate(); await (cameraPreviewWork = cameraPreview.ConnectAsync(camera, format));
    }
    private async void CameraFormat_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (cameraPreview is { } window && camera is { } device && CameraFormatPicker.SelectedItem is CameraFormat format)
            await (cameraPreviewWork = window.ConnectAsync(device, format));
    }
}
