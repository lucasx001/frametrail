using System.Diagnostics;
using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;
using FrameTrail.Workspace;
using Xunit;

namespace FrameTrail.Workspace.Tests;

public sealed class RecordingMotionTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FrameTrail-Motion-" + Guid.NewGuid().ToString("N"));
    private string Project => Path.Combine(root, "recording.frametrailproject");
    private RecordingPlan Plan => new(Project, new(CaptureSourceKind.Monitor, 1, "Fixture display", 0, 0, 640, 360, 96, 0));
    public Task InitializeAsync() { Directory.CreateDirectory(root); return Task.CompletedTask; }
    public Task DisposeAsync() { Directory.Delete(root, true); return Task.CompletedTask; }
    private static RecordingCoordinator Coordinator(Driver driver) => new(driver, EngineSession.ValidateProject, (_, _) => { });
    private static string Pointer(double seconds, int kind = 0, double x = 0.3, double y = 0.4, bool inside = true, int timescale = 10_000_000) =>
        JsonSerializer.Serialize(new { type = "pointer", ticks = (long)(seconds * timescale), timescale, x, y, inside, kind });
    private static IEnumerable<string> Stationary(double click = 1) => Enumerable.Range(0, 361).Select(i => Pointer(i / 60.0, i == (int)(click * 60) ? 1 : 0));
    private sealed class Driver(Func<int, IEnumerable<string>> events, bool sourceFailure = false) : IRecordingDriver
    {
        private int index;
        public Task<IRecordingSegment> StartAsync(RecordingPlan plan, SegmentPaths paths, CancellationToken cancellationToken) =>
            Task.FromResult<IRecordingSegment>(new Segment(paths, plan.RecordPointer, events(++index), sourceFailure));
        private sealed class Segment(SegmentPaths paths, bool pointer, IEnumerable<string> events, bool sourceFailure) : IRecordingSegment
        {
            public SegmentStart Start { get; } = new(Stopwatch.GetTimestamp(), Stopwatch.Frequency, 640, 360);
            public RecordingHealth Poll() => new(sourceFailure, sourceFailure ? "Fixture source closed" : null, Array.Empty<string>(), 0);
            public async Task<CompletedCapture> FinishAsync(long boundaryQpc)
            {
                await File.WriteAllBytesAsync(paths.Screen, PersistenceTests.FinalizedMp4Header);
                var tracks = new List<CompletedTrack> { new(MediaKind.Screen, paths.Screen, 60_000_000, Width: 640, Height: 360) };
                if (pointer) { await File.WriteAllLinesAsync(paths.Pointer, events); tracks.Add(new(MediaKind.PointerEvents, paths.Pointer, 60_000_000)); }
                return new(60_000_000, tracks, Array.Empty<string>());
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact] public async Task DefaultRecordingSavesZoomAndStationarySamplesAllowItToExit()
    {
        await using (var recorder = Coordinator(new(_ => Stationary())))
        {
            await recorder.StartAsync(Plan); var result = (await recorder.StopAsync())!;
            var zoom = Assert.Single(result.Effects);
            Assert.Equal("automatic", zoom.Parameters.GetProperty("origin").GetString());
            Assert.Equal(8_740_000, zoom.SourceStart.Ticks);
            Assert.InRange(zoom.SourceStart.Ticks + zoom.SourceDuration.Ticks, 25_000_000, 27_000_000);
            Assert.Equal(1.6, zoom.Parameters.GetProperty("scale").GetDouble());
        }
        await using var reopened = await ProjectWorkspace.OpenAsync(Project, EngineSession.ValidateProject, (_, _) => { });
        var editor = new EditorSession(reopened); var saved = Assert.Single(editor.Snapshot.Effects);
        await editor.UpsertEffectAsync(saved with { Parameters = JsonSerializer.SerializeToElement(new { scale = 2.0, origin = "automatic" }) });
        Assert.Equal("edited", Assert.Single(editor.Snapshot.Effects).Parameters.GetProperty("origin").GetString());
        await editor.UndoAsync(); Assert.Equal(saved.Parameters.GetRawText(), Assert.Single(editor.Snapshot.Effects).Parameters.GetRawText());
    }

    [Theory]
    [InlineData("hevc")]
    [InlineData("av1")]
    public async Task RecordingCodecPersistsAcrossPauseAndReopen(string codec)
    {
        await using (var recorder = Coordinator(new(_ => Stationary()))) {
            await recorder.StartAsync(Plan with { VideoCodec = codec });
            await recorder.PauseAsync(); await recorder.ResumeAsync();
            var snapshot = (await recorder.StopAsync())!;
            Assert.Equal(codec, snapshot.Export.VideoCodec); Assert.Equal(2, snapshot.Segments.Count);
        }
        await using var project = await ProjectWorkspace.OpenAsync(Project, EngineSession.ValidateProject, (_, _) => { });
        Assert.Equal(codec, project.Snapshot!.Export.VideoCodec);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task RecordingSwitchAndPointerSwitchIndependentlyPreventAutomaticZoom(bool automaticZoom, bool pointer)
    {
        await using var recorder = Coordinator(new(_ => Stationary()));
        await recorder.StartAsync(Plan with { AutomaticZoom = automaticZoom, RecordPointer = pointer });
        var result = (await recorder.StopAsync())!; Assert.Empty(result.Effects);
        Assert.Equal(pointer, result.Media.Any(m => m.Kind == MediaKind.PointerEvents));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MovementAloneAndClicksOutsideSourceDoNotCreateZooms(bool outsideClick)
    {
        await using var recorder = Coordinator(new(_ => new[] { Pointer(0), Pointer(1, outsideClick ? 1 : 0, x: 0.6, inside: !outsideClick), Pointer(2, x: 0.8) }));
        await recorder.StartAsync(Plan); Assert.Empty((await recorder.StopAsync())!.Effects);
    }

    [Fact] public async Task MovingAfterClickKeepsZoomUntilActivityStops()
    {
        var events = new[] { Pointer(0), Pointer(1, 1), Pointer(1.5, x: 0.4), Pointer(2, x: 0.5), Pointer(2.5, 3, x: 0.8), Pointer(3, x: 0.85), Pointer(3.5, x: 0.9), Pointer(4, x: 0.85), Pointer(5, x: 0.85) };
        await using var recorder = Coordinator(new(_ => events)); await recorder.StartAsync(Plan);
        var zoom = Assert.Single((await recorder.StopAsync())!.Effects);
        Assert.Equal(2, zoom.Parameters.GetProperty("focusPoints").GetArrayLength());
        Assert.InRange(zoom.SourceStart.Ticks + zoom.SourceDuration.Ticks, 52_000_000, 53_000_000);
    }

    [Fact] public async Task PauseResumeRegeneratesOneContinuousPlanAcrossTrackOffsetsAndRecovery()
    {
        ProjectSnapshot paused;
        await using (var recorder = Coordinator(new(i => new[] { Pointer(0), Pointer(i == 1 ? 5.5 : 0.1, 1), Pointer(5.9) })))
        {
            await recorder.StartAsync(Plan); await recorder.PauseAsync();
            paused = recorder.Workspace!.Snapshot!; Assert.Single(paused.Effects);
            var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(recorder.Workspace.Resolve("recovery/manifest.json")));
            Assert.Contains("auto-zoom-", manifest.RootElement.GetProperty("checkpointJson").GetString()); manifest.Dispose();
            await recorder.ResumeAsync(); var result = (await recorder.StopAsync())!;
            var zoom = Assert.Single(result.Effects);
            Assert.InRange(zoom.SourceStart.Ticks + zoom.SourceDuration.Ticks, 76_000_000, 78_000_000);
            Assert.Equal(2, result.Segments.Count); Assert.True(result.Revision > paused.Revision);
            Assert.Equal(60_000_000, result.Media.Last(m => m.Kind == MediaKind.PointerEvents).StartOffset.Ticks);
        }
        await using var reopened = await ProjectWorkspace.OpenAsync(Project, EngineSession.ValidateProject, (_, _) => { });
        Assert.Single(reopened.Snapshot!.Effects);
    }

    [Theory]
    [InlineData("coordinate")]
    [InlineData("time")]
    public async Task InvalidPlanningDataRetainsFinalizedMediaAndWarns(string invalid)
    {
        var warned = new List<string>();
        await using var recorder = Coordinator(new(_ => new[] { invalid == "time" ? Pointer(1, 1, timescale: 0) : Pointer(1, 1, x: -1) }));
        recorder.StatusChanged += (_, status) => warned.AddRange(status.Warnings);
        await recorder.StartAsync(Plan); var result = (await recorder.StopAsync())!;
        Assert.Single(result.Segments); Assert.Empty(result.Effects); Assert.Contains(warned, w => w.Contains(AppText.T("S542AA18A92E5")));
        Assert.True(File.Exists(Path.Combine(Project, "project.json")));
    }

    [Fact] public async Task SourceClosureAlsoFinalizesAutomaticMotion()
    {
        await using var recorder = Coordinator(new(_ => Stationary(), sourceFailure: true));
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.StatusChanged += (_, status) => { if (status.State == RecordingWorkspaceState.RecordingComplete) completed.TrySetResult(); };
        await recorder.StartAsync(Plan); await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(recorder.Workspace!.Snapshot!.Effects);
    }
    [Fact] public async Task DefaultVisualPresetAppliesOnceAndSurvivesRecordingSegments()
    {
        var library = new VisualPresetLibrary(Path.Combine(root, "presets"));
        var saved = await library.SaveAsync("default", new ProjectSnapshot { Canvas = new() { Width = 1280, Height = 720 }, Editor = new() { CursorStyle = "crosshair", Shadow = 0.8 } }, root);
        await library.SetDefaultAsync(saved.Id);
        await using var recorder = new RecordingCoordinator(new Driver(_ => Stationary()), EngineSession.ValidateProject, (_, _) => { }, () => library);
        await recorder.StartAsync(Plan); await recorder.PauseAsync();
        Assert.Equal("crosshair", recorder.Workspace!.Snapshot!.Editor!.CursorStyle);
        await library.SetDefaultAsync(null); await recorder.ResumeAsync(); var snapshot = (await recorder.StopAsync())!;
        Assert.Equal(2, snapshot.Segments.Count); Assert.Equal("crosshair", snapshot.Editor!.CursorStyle); Assert.Equal(1280u, snapshot.Canvas.Width);
        Assert.Equal(2, snapshot.Effects.Count); // Each separated click group keeps its own automatic motion.
    }
}
