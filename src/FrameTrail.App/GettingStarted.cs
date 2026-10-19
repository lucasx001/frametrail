using Microsoft.UI.Xaml;

namespace FrameTrail.App;

internal static class GettingStarted
{
    private static string StatePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "getting-started-v1");
    internal static GettingStartedWindow? Active { get; private set; }

    // ContentDialog is constrained to its XamlRoot. The recorder is only 462 x
    // 56 DIP, so even the ordinary first launch needs an independent window.
    internal static Task ShowAsync(Window owner, bool force = false, string? statePath = null)
    {
        if (Active is { } existing) { existing.Activate(); return existing.Completion; }
        string path = statePath ?? StatePath;
        if (!force && File.Exists(path)) return Task.CompletedTask;
        var guide = new GettingStartedWindow(owner);
        Active = guide;
        guide.Closed += (_, _) => Active = null;
        guide.Completion = CompleteAsync(guide, path);
        guide.Activate();
        return guide.Completion;
    }

    private static async Task CompleteAsync(GettingStartedWindow guide, string path)
    {
        if (!await guide.Dismissed) return;
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "1");
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { SafeDiagnostics.Record(error); }
    }
}
