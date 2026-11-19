using FrameTrail.Workspace;
using Microsoft.UI.Xaml;

namespace FrameTrail.App;

public partial class App : Application
{
    // Presence only: multiple app instances remain supported. The installer
    // waits for all handles to disappear instead of closing media tasks.
    private static readonly Mutex InstallGuard = new(false, @"Local\FrameTrail.App.Running");
    private static readonly AiAgentBridge SidecarHealthBridge = new();

    internal static AiSidecarProcessManager AiSidecar { get; } = new();
    internal static event EventHandler? AiSidecarStatusChanged;

    private Window? window;

    public App()
    {
        var args = Environment.GetCommandLineArgs();
        int language = Array.IndexOf(args, "--language");
        FrameTrail.Domain.AppText.Initialize(language >= 0 && args.Length > language + 1 ? args[language + 1] : null);
        InitializeComponent();
        UnhandledException += (_, args) => SafeDiagnostics.Record(args.Exception);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => AiSidecar.Dispose();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        GC.KeepAlive(InstallGuard);

        // Auto-start the Python AI Agent sidecar alongside the application lifecycle
        // (skipped only during automated UI self-tests so CI/validation runs stay deterministic).
        var cmdArgs = Environment.GetCommandLineArgs();
        bool isSelfTest = cmdArgs.Any(a => a.Contains("self-test", StringComparison.OrdinalIgnoreCase) ||
                                           string.Equals(a, "--inspect-ui", StringComparison.OrdinalIgnoreCase) ||
                                           string.Equals(a, "--smoke-test", StringComparison.OrdinalIgnoreCase));
        if (!isSelfTest)
        {
            _ = EnsureAiSidecarStartedAsync();
        }

        window = new MainWindow();
        window.Closed += (_, _) => AiSidecar.Dispose();
        window.Activate();
    }

    internal static async Task<bool> EnsureAiSidecarStartedAsync()
    {
        bool ok = await AiSidecar.EnsureStartedAsync(SidecarHealthBridge).ConfigureAwait(false);
        AiSidecarStatusChanged?.Invoke(null, EventArgs.Empty);
        return ok;
    }
}
