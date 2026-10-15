using Microsoft.UI.Xaml;

namespace FrameTrail.App;

public partial class App : Application
{
    private Window? window;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) => {
            // Keep the original failure behavior, but retain the managed/COM details
            // that Windows' APPCRASH entry otherwise loses.
            try {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "crashes");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.log"), args.Message + Environment.NewLine + args.Exception);
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Activate();
    }
}
