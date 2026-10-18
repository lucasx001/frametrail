using FrameTrail.Interop;
using System.Text.Json;

namespace FrameTrail.App;

public sealed partial class MainWindow
{
    private async Task RunCameraPreviewValidationAsync(string directory)
    {
        Directory.CreateDirectory(directory);CameraPreviewWindow? window=null;bool success=false;object? presentation=null;string? failure=null;
        try {
            var device=cameras.First();var format=CameraCaptureSession.EnumerateFormats(device.Id).Where(f=>f.Width==640&&f.Height==480).OrderBy(f=>Math.Abs((double)f.FpsNumerator/f.FpsDenominator-30)).First();
            window=new();window.Activate();await window.ConnectAsync(device,format);presentation=await window.ValidatePresentationAsync();success=true;
        }catch(Exception error){failure=error.ToString();}
        finally {
            if(window is not null){await window.StopAsync();window.Close();}
            await File.WriteAllTextAsync(Path.Combine(directory,"camera-ui.json"),JsonSerializer.Serialize(new{success,presentation,failure},new JsonSerializerOptions{WriteIndented=true}));
            allowClose=true;closing=true;UnregisterHotkeys();await coordinator.DisposeAsync();session?.Dispose();Environment.ExitCode=success?0:1;Close();
        }
    }
}
