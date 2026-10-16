using Microsoft.UI.Xaml;

namespace FrameTrail.App;

public partial class App : Application
{
    private Window? window;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) => SafeDiagnostics.Record(args.Exception);
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Activate();
    }
}
