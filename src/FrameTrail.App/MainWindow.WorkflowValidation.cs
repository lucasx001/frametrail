using FrameTrail.Domain;
using FrameTrail.Interop;
using FrameTrail.Workspace;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using System.Text.Json;

namespace FrameTrail.App;

public sealed partial class MainWindow
{
    // Explicit integration-test entry point. No injected OS input is claimed.
    private async Task RunWorkflowValidationAsync(string directory)
    {
        directory = Path.GetFullPath(directory);
        bool success = false;
        bool ownsDirectory = false;
        string? failure = null;
        var chains = new List<object>();
        Window? fixture = null;
        Microsoft.UI.Dispatching.DispatcherQueueTimer? animation = null;
        try
        {
            if (!Environment.GetCommandLineArgs().Contains("--inspect-ui"))
                throw new IOException("Workflow validation requires --inspect-ui to keep project/catalog preferences isolated.");
            if (Directory.Exists(directory) || File.Exists(directory))
                throw new IOException("Choose a new workflow output directory; earlier runs are retained.");
            Directory.CreateDirectory(directory);
            ownsDirectory = true;
            await coordinator.DisposeAsync();
            coordinator.StatusChanged -= UpdateStatus;
            coordinator = new RecordingCoordinator();
            coordinator.StatusChanged += UpdateStatus;
            projects = Path.Combine(directory, "projects");
            Directory.CreateDirectory(projects);

            var surface = new Canvas { Background = new SolidColorBrush(Colors.DarkSlateBlue) };
            surface.Children.Add(new TextBlock { Text = "FrameTrail workflow fixture — synthetic content only", Foreground = new SolidColorBrush(Colors.White), Margin = new Thickness(20), FontSize = 18 });
            var moving = new Rectangle { Width = 72, Height = 72, Fill = new SolidColorBrush(Colors.Gold) };
            Canvas.SetTop(moving, 100);
            surface.Children.Add(moving);
            fixture = new Window { Title = "FrameTrail workflow capture fixture", Content = surface };
            fixture.AppWindow.Resize(new Windows.Graphics.SizeInt32(640, 360));
            fixture.Activate();
            animation = DispatcherQueue.CreateTimer();
            animation.Interval = TimeSpan.FromMilliseconds(33);
            int frame = 0;
            animation.Tick += (_, _) => Canvas.SetLeft(moving, 20 + (++frame * 5 % 480));
            animation.Start();
            await Task.Delay(400);
            var handle = (nuint)WinRT.Interop.WindowNative.GetWindowHandle(fixture);
            var source = CaptureSession.EnumerateSources().Single(s => s.Kind == CaptureSourceKind.Window && s.Handle == handle);
            if (!source.IsSdr) throw new IOException("The synthetic fixture must be captured as SDR.");
            RecordingName.Text = "workflow-take";
            MicEnabled.IsChecked = SystemEnabled.IsChecked = CameraEnabled.IsChecked = false;
            PointerEnabled.IsChecked = AutomaticZoomEnabled.IsChecked = false;
            SoftwareAllowed.IsChecked = true;
            CodecPicker.SelectedIndex = FpsPicker.SelectedIndex = 0;
            await BeginRecordingAsync(source, null);
            await Task.Delay(1900);
            await coordinator.PauseAsync();
            await Task.Delay(100);
            await coordinator.ResumeAsync();
            await Task.Delay(1100);
            var snapshot = await coordinator.StopAsync() ?? throw new IOException("No recording snapshot was finalized.");
            if (snapshot.Segments.Count != 2) throw new IOException("Pause/resume did not create two authored takes.");
            string first = coordinator.Workspace!.DirectoryPath;
            animation.Stop();
            fixture.Close(); fixture = null;
            await ResetWorkspaceAsync();

            // Synthetic silence exercises real audio playback/peak-worker shutdown
            // without opening a microphone or recording the user's system audio.
            await using (var workspace = await ProjectWorkspace.OpenAsync(first))
            {
                WriteWorkflowSilence(Path.Combine(first, "media", "workflow-silence.wav"), snapshot.SourceDuration);
                var silence = new MediaAsset { Id = "workflow-silence", Kind = MediaKind.SystemAudio, Path = "media/workflow-silence.wav", Duration = snapshot.SourceDuration, StartOffset = MediaTime.FromTicks(0), SampleRate = 48000, Channels = 2 };
                if (!await workspace.SaveAsync(snapshot with { Revision = snapshot.Revision + 1, Media = snapshot.Media.Append(silence).ToArray() }, workspace.SessionEpoch))
                    throw new IOException("Could not attach the synthetic audio fixture.");
            }
            await OpenExistingProjectAsync(first);
            var current = editorWindow!;
            await current.ValidateRecordedWorkflowAsync(directory);
            chains.Add(new { id = "record-edit-export", success = true, source = "own animated WinUI window; real WGC capture", audio = "synthetic silence added after capture", project = first, output = Path.Combine(directory, "recorded-edited.mp4") });

            string expected = await current.ValidateDragUndoWorkflowAsync(directory);
            await CloseWorkflowEditorAsync(current);
            await ResetWorkspaceAsync();
            await OpenExistingProjectAsync(first);
            current = editorWindow!;
            await current.ValidateReopenedWorkflowAsync(expected, Path.Combine(directory, "reopened-after-undo.png"));
            chains.Add(new { id = "drag-undo-reopen", success = true, input = "canvas gesture methods, not injected mouse input", project = first });

            string second = Path.Combine(projects, "workflow-second.frametrailproject");
            await current.CopyWorkflowProjectAsync(second);
            await current.StageWorkflowPlaybackAndAssetsAsync();
            await current.SwitchSelectedProjectAsync(second).WaitAsync(TimeSpan.FromSeconds(20));
            if (!current.HasClosed || ReferenceEquals(current, editorWindow) || opened?.DirectoryPath != second)
                throw new IOException("Playback/background switch did not replace the active editor.");
            await using (var released = await ProjectWorkspace.OpenAsync(first)) { }
            current = editorWindow!;
            await current.StageWorkflowPlaybackAndAssetsAsync();
            await CloseWorkflowEditorAsync(current).WaitAsync(TimeSpan.FromSeconds(20));
            await ResetWorkspaceAsync();
            await using (var released = await ProjectWorkspace.OpenAsync(second)) { }
            chains.Add(new { id = "playback-assets-switch-close", success = true, background = "peak worker held at cancellable preparation barrier", leaseRelease = true });
            success = true;
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            animation?.Stop();
            fixture?.Close();
            try
            {
                if (editorWindow is { } current) await CloseWorkflowEditorAsync(current);
                await coordinator.DisposeAsync();
                if (opened is not null) { await opened.DisposeAsync(); opened = null; }
            }
            catch (Exception error) { success = false; failure += "\nCleanup: " + error; }
            // Only a directory created by this run receives results on failure.
            if (ownsDirectory)
                await File.WriteAllTextAsync(Path.Combine(directory, "workflow-validation.json"), JsonSerializer.Serialize(new
                {
                    success, timestampUtc = DateTimeOffset.UtcNow, chains, failure,
                    evidenceLevel = "real WinUI windows + native media + application-method integration self-test",
                    actualInputVerified = false,
                    actualInputStatus = "pending separate Computer Use / human keyboard-and-pointer walkthrough",
                    captureScope = "only the test's own animated window; no desktop, microphone or system-audio capture"
                }, new JsonSerializerOptions { WriteIndented = true }));
            allowClose = true; closing = true; UnregisterHotkeys(); session?.Dispose();
            Environment.ExitCode = success ? 0 : 1;
            Close(); Application.Current.Exit();
        }
    }

    private async Task CloseWorkflowEditorAsync(EditorWindow window)
    {
        // Suppress the recorder's asynchronous reopen callback while the harness
        // deliberately performs a full close/reopen and tests lease release.
        editorWindow = null;
        try
        {
            await window.RequestCloseAsync();
            if (!window.HasClosed) throw new IOException("The editor refused to close during the workflow self-test.");
        }
        catch { editorWindow = window; throw; }
    }

    private static void WriteWorkflowSilence(string path, MediaTime duration)
    {
        long ticks = VisualResources.Ticks(duration);
        int frames = checked((int)((ticks * 48000 + 9_999_999) / 10_000_000));
        int bytes = checked(frames * 4);
        using var file = File.Create(path);
        using var wave = new BinaryWriter(file);
        wave.Write("RIFF"u8); wave.Write(bytes + 36); wave.Write("WAVEfmt "u8);
        wave.Write(16); wave.Write((short)1); wave.Write((short)2); wave.Write(48000);
        wave.Write(192000); wave.Write((short)4); wave.Write((short)16); wave.Write("data"u8); wave.Write(bytes);
        wave.Write(new byte[bytes]);
    }
}
