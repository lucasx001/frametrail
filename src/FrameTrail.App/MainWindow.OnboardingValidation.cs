using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FrameTrail.App;

public sealed partial class MainWindow
{
    private async Task RunOnboardingValidationAsync(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        string state = Path.Combine(directory, "preferences", "getting-started-v1");
        var checks = new List<string>(); var layouts = new List<object>();
        bool success = false; string? failure = null;
        void Check(bool passed, string label) { if (!passed) throw new IOException(label); checks.Add(label); }
        try {
            Check(!File.Exists(state), "Fresh isolated preferences");
            var showing = GettingStarted.ShowAsync(this, statePath: state);
            var guide = GettingStarted.Active ?? throw new IOException("Startup guide did not open.");
            await Task.Delay(180);
            Check(!ReferenceEquals(guide.GuideRoot, RecorderRoot.XamlRoot), "Guide has an independent XamlRoot");
            Check(Math.Abs(RecorderRoot.ActualHeight - 56) < 1 && SourceWheel.IsEnabled && StartButton.IsEnabled, "Capsule remains 56 DIP and enabled");
            Check(!File.Exists(state), "Showing the guide does not prematurely acknowledge it");
            Check(ReferenceEquals(showing, GettingStarted.ShowAsync(this, true, state)) && ReferenceEquals(guide, GettingStarted.Active), "Repeated requests reuse one window");
            layouts.Add(await guide.ValidateLayoutAsync(Path.Combine(directory, "guide.png")));
            double scale = RecorderWindowChrome.Scale(guide);
            guide.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(400 * scale), (int)(360 * scale)));
            await Task.Delay(100);
            layouts.Add(await guide.ValidateLayoutAsync(Path.Combine(directory, "guide-compact.png")));
            guide.InvokeStart(); await showing.WaitAsync(TimeSpan.FromSeconds(5));
            Check(GettingStarted.Active is null && File.Exists(state), "Get started closes the guide and persists acknowledgement");
            await GettingStarted.ShowAsync(this, statePath: state);
            Check(GettingStarted.Active is null, "Subsequent launch skips the acknowledged guide");
            await SaveUiFrameAsync(RecorderRoot, Path.Combine(directory, "capsule-after.png"));
            var reopened = GettingStarted.ShowAsync(this, true, state);
            Check(GettingStarted.Active is not null, "Settings can reopen the guide after acknowledgement");
            GettingStarted.Active!.Close(); await reopened.WaitAsync(TimeSpan.FromSeconds(5));
            Check(GettingStarted.Active is null, "Window close also completes the guide");

            string unfinished = Path.Combine(directory, "preferences", "owner-closed");
            var owner = new Window { Content = new Grid() };
            owner.Activate();
            var owned = GettingStarted.ShowAsync(owner, statePath: unfinished);
            owner.Close(); await owned.WaitAsync(TimeSpan.FromSeconds(5));
            Check(GettingStarted.Active is null && !File.Exists(unfinished), "Owner shutdown closes the guide without recording acknowledgement");
            success = true;
        } catch (Exception error) { failure = error.ToString(); }
        finally {
            GettingStarted.Active?.Close();
            await File.WriteAllTextAsync(Path.Combine(directory, "onboarding-validation.json"), JsonSerializer.Serialize(new { success, language = FrameTrail.Domain.AppText.Language, checks, layouts, failure }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = success ? 0 : 1;
            closing = true; allowClose = true; UnregisterHotkeys(); await coordinator.DisposeAsync(); session?.Dispose(); Close();
        }
    }
}
