using Microsoft.UI.Xaml;

namespace FrameTrail.App;

public partial class App : Application
{
    // Presence only: multiple app instances remain supported. The installer
    // waits for all handles to disappear instead of closing media tasks.
    private static readonly Mutex InstallGuard = new(false, @"Local\FrameTrail.App.Running");
    private Window? window;
    public App()
    {
        var args=Environment.GetCommandLineArgs();int language=Array.IndexOf(args,"--language");
        FrameTrail.Domain.AppText.Initialize(language>=0&&args.Length>language+1?args[language+1]:null);
        InitializeComponent();
        UnhandledException += (_, args) => SafeDiagnostics.Record(args.Exception);
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        GC.KeepAlive(InstallGuard);
        window = new MainWindow();
        window.Activate();
    }
}
