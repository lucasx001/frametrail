using System.Diagnostics;
using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;
using FrameTrail.Workspace;

internal static class RecordingMotionValidation
{
    // Uses real encoded media and the product recording/finalization path with
    // deterministic pointer samples, without injecting clicks into the user's desktop.
    internal static async Task<int> RunAsync(string screenFile, string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var checks = new List<string>();
        try
        {
            foreach (bool enabled in new[] { true, false })
            {
                string name = enabled ? "on" : "off", path = Path.Combine(output, name + ".frametrailproject");
                await using (var recorder = new RecordingCoordinator(new FixtureDriver(Path.GetFullPath(screenFile))))
                {
                    await recorder.StartAsync(new(path, new(CaptureSourceKind.Monitor, 1, "Recorded media fixture", 0, 0, 640, 360, 96, 0), AutomaticZoom: enabled));
                    var snapshot = (await recorder.StopAsync())!;
                    if (snapshot.Effects.Count != (enabled ? 1 : 0)) throw new IOException("Recorder did not honor automatic zoom switch.");
                }
                await using var reopened = await ProjectWorkspace.OpenAsync(path);
                // Export at fixture size to keep local regression fast; this changes
                // only output dimensions, not the automatically generated camera.
                var project = reopened.Snapshot! with { Export = new() { Width = 640, Height = 360 } };
                using var media = new MediaSession(project, path);
                using var neutral = new MediaSession(project with { Effects = Array.Empty<SourceEffect>() }, path);
                foreach (var (label, ticks) in new[] { ("before", 2_000_000L), ("during", 15_000_000L), ("after", 28_000_000L) })
                {
                    string preview = Path.Combine(output, $"{name}-{label}.png"), full = Path.Combine(output, $"{name}-{label}-neutral.png");
                    media.Render(ticks, png: preview); neutral.Render(ticks, png: full);
                }
                media.Export(Path.Combine(output, name + ".mp4"));
                checks.Add($"Automatic zoom {name}: recorder finalizes, project reopens, before/during/after GPU frames rendered for independent verification, MP4 exported.");
            }
            await File.WriteAllTextAsync(Path.Combine(output, "recording-motion.json"), JsonSerializer.Serialize(new { success = true, checks }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "recording-motion.json"), JsonSerializer.Serialize(new { success = false, checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine(error); return 1;
        }
    }

    private sealed class FixtureDriver(string screen) : IRecordingDriver
    {
        public Task<IRecordingSegment> StartAsync(RecordingPlan plan, SegmentPaths paths, CancellationToken cancellationToken) => Task.FromResult<IRecordingSegment>(new Segment(screen, paths));
        private sealed class Segment(string screen, SegmentPaths paths) : IRecordingSegment
        {
            public SegmentStart Start { get; } = new(Stopwatch.GetTimestamp(), Stopwatch.Frequency, 640, 360);
            public RecordingHealth Poll() => new(false, null, Array.Empty<string>(), 0);
            public async Task<CompletedCapture> FinishAsync(long boundaryQpc)
            {
                File.Copy(screen, paths.Screen);
                await File.WriteAllLinesAsync(paths.Pointer, Enumerable.Range(0, 181).Select(i => JsonSerializer.Serialize(new { type = "pointer", ticks = i * 10_000_000L / 60, timescale = 10_000_000, x = 0.72, y = 0.38, inside = true, visible = true, shape = 0, kind = i == 48 ? 1 : i == 49 ? 2 : 0 })));
                return new(30_000_000, new[] { new CompletedTrack(MediaKind.Screen, paths.Screen, 30_000_000, Width: 640, Height: 360), new CompletedTrack(MediaKind.PointerEvents, paths.Pointer, 30_000_000) }, Array.Empty<string>());
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
