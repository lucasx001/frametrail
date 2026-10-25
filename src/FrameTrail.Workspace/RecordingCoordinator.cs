using System.Diagnostics;
using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;

namespace FrameTrail.Workspace;

public enum RecordingWorkspaceState { Setup, Preparing, Recording, Pausing, Paused, Resuming, Finalizing, RecordingComplete }
public sealed record AudioSelection(AudioCaptureKind Kind, string? DeviceId = null, uint ProcessId = 0);
public sealed record RecordingPlan(string ProjectPath, CaptureSource Source, CaptureRegion? Region = null,
    uint Fps = 30, bool AllowSoftware = false, IReadOnlyList<AudioSelection>? Audio = null,
    CameraDevice? Camera = null, CameraFormat? CameraFormat = null, bool RecordPointer = true, int CheckpointSeconds = 60,
    AndroidSelection? Android = null, AndroidConnection? PreparedAndroid = null, bool AutomaticZoom = true, string VideoCodec = VideoCodecs.H264);
public sealed record SegmentPaths(string Screen, string Pointer, string Shapes, string Camera,
    IReadOnlyList<string> Audio, string Diagnostics, string? PhoneAudio = null);
public sealed record SegmentStart(long EpochQpc, long QpcFrequency, uint Width, uint Height);
public sealed record CompletedTrack(MediaKind Kind, string Path, long DurationTicks, long OffsetTicks = 0,
    uint Width = 0, uint Height = 0, uint SampleRate = 0, uint Channels = 0);
public sealed record CompletedCapture(long DurationTicks, IReadOnlyList<CompletedTrack> Tracks, IReadOnlyList<string> Warnings);
public sealed record RecordingHealth(bool MainSourceFailed, string? MainError, IReadOnlyList<string> Warnings, double MicrophonePeak);
public interface IRecordingSegment : IAsyncDisposable
{
    SegmentStart Start { get; }
    RecordingHealth Poll();
    Task<CompletedCapture> FinishAsync(long boundaryQpc);
}
public interface IRecordingDriver
{
    Task<IRecordingSegment> StartAsync(RecordingPlan plan, SegmentPaths paths, CancellationToken cancellationToken);
}
public interface IRecordingRunDriver { Task EndRunAsync(); }
public sealed record RecordingWorkspaceStatus(Guid RunId, RecordingWorkspaceState State, TimeSpan Duration,
    string? ProjectPath, IReadOnlyList<string> Warnings, double MicrophonePeak);

/// <summary>Serialized product state machine. A stopped segment is finalized before it enters project/recovery manifests.</summary>
public sealed class RecordingCoordinator : IAsyncDisposable
{
    private readonly SemaphoreSlim transition = new(1, 1);
    private readonly IRecordingDriver driver;
    private readonly Action<ProjectSnapshot>? validator;
    private readonly Action<string, bool>? mediaProbe;
    private readonly Func<VisualPresetLibrary>? presetLibrary;
    private readonly List<MediaAsset> media = new();
    private readonly List<RecordingSegment> segments = new();
    private readonly List<RecordingMarker> markers = new();
    private readonly List<string> warnings = new();
    private readonly HashSet<string> warningSet = new(StringComparer.Ordinal);
    private ProjectWorkspace? workspace;
    private IRecordingSegment? active;
    private RecordingPlan? plan;
    private Guid runId;
    private long sourceTicks, revision, lastBoundary;
    private int segmentIndex;
    private CancellationTokenSource? monitorCancellation;
    private Task? monitor;
    private RecordingWorkspaceState state;
    private double peak;
    private bool disposed;
    private bool closing;
    private readonly object disposalGate = new();
    private Task? disposal;
    private bool checkpointDisabled;
    private bool startTimelineClip;
    private AutomaticZoomPlanner zoomPlanner = new();
    private Task planningWork = Task.CompletedTask;
    private CancellationTokenSource planningCancellation = new();
    internal Func<CancellationToken, Task>? PlanningBarrier { get; init; }
    private IReadOnlyList<string> activePending = Array.Empty<string>();
    private readonly List<string> abandonedPending = new();
    public event EventHandler<RecordingWorkspaceStatus>? StatusChanged;
    public RecordingWorkspaceState State => state;
    public Guid RunId => runId;
    public ProjectWorkspace? Workspace => workspace;

    public RecordingCoordinator(IRecordingDriver? driver = null, Action<ProjectSnapshot>? validator = null, Action<string, bool>? mediaProbe = null, Func<VisualPresetLibrary>? presetLibrary = null)
    { this.driver = driver ?? new NativeRecordingDriver(); this.validator = validator; this.mediaProbe = mediaProbe; this.presetLibrary = presetLibrary; }

    public async Task StartAsync(RecordingPlan requested, CancellationToken cancellationToken = default)
    {
        await transition.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed || closing, this);
            if (state is not (RecordingWorkspaceState.Setup or RecordingWorkspaceState.RecordingComplete)) throw new InvalidOperationException("A recording is already active.");
            if (requested.CheckpointSeconds < 5 || requested.CheckpointSeconds > 300) throw new ArgumentOutOfRangeException(nameof(requested.CheckpointSeconds));
            if (!VideoCodecs.IsSupported(requested.VideoCodec)) throw new ArgumentException("Unsupported recording codec.", nameof(requested));
            if (requested.Android is not null && requested.VideoCodec != VideoCodecs.H264) throw new ArgumentException("Phone source recording currently uses H.264; choose another codec when exporting.", nameof(requested));
            if (requested.Camera is not null && requested.CameraFormat is null) throw new ArgumentException("Choose a camera format.");
            if (requested.Android is not null && (requested.RecordPointer || requested.Camera is not null || requested.Audio?.Any(a => a.Kind != AudioCaptureKind.Microphone) == true))
                throw new ArgumentException("Android recording uses phone audio and optional computer microphone; disable Windows pointer, camera and system audio.");
            if (requested.Audio?.GroupBy(a => a.Kind == AudioCaptureKind.Microphone).Any(g => g.Count() > 1) == true) throw new ArgumentException("Only one microphone and one system/process audio track are supported.");
            planningCancellation.Cancel();
            if (workspace is not null) { await workspace.DisposeAsync(); workspace = null; }
            runId = Guid.NewGuid(); plan = requested with { Audio = requested.Audio?.ToArray() ?? Array.Empty<AudioSelection>(), ProjectPath = Path.GetFullPath(requested.ProjectPath) };
            media.Clear(); segments.Clear(); markers.Clear(); warnings.Clear(); warningSet.Clear(); sourceTicks = 0; revision = 0; segmentIndex = 0; lastBoundary = 0;
            zoomPlanner = new();
            planningCancellation.Dispose(); planningCancellation = new();
            planningWork = Task.CompletedTask;
            SetState(RecordingWorkspaceState.Preparing);
            createdProjectId = Guid.NewGuid().ToString("N");
            checkpointDisabled = false; activePending = Array.Empty<string>(); abandonedPending.Clear();
            startTimelineClip = true;
            workspace = ProjectWorkspace.Create(plan.ProjectPath, createdProjectId, validator, mediaProbe);
            await BeginSegmentAsync(cancellationToken);
            SetState(RecordingWorkspaceState.Recording);
            monitorCancellation?.Cancel(); monitorCancellation = new();
            var expectedRun = runId;
            monitor = MonitorAsync(expectedRun, monitorCancellation.Token);
        }
        catch (Exception error)
        {
            AddWarning("Recording startup rolled back: " + error.Message);
            try { if (active is not null) { await active.DisposeAsync(); active = null; } if (workspace is not null) await WriteManifestAsync("failed"); }
            catch (Exception cleanup) { AddWarning("Startup cleanup: " + cleanup.Message); }
            finally { if (driver is IRecordingRunDriver run) await run.EndRunAsync(); SetState(RecordingWorkspaceState.Setup); }
            throw;
        }
        finally { transition.Release(); }
    }

    public async Task PauseAsync()
    {
        Task pendingPlanning;
        Guid expectedRun;
        await transition.WaitAsync();
        try
        {
            if (state != RecordingWorkspaceState.Recording) throw new InvalidOperationException("Only an active recording can be paused.");
            SetState(RecordingWorkspaceState.Pausing);
            await CompleteSegmentAsync(Stopwatch.GetTimestamp());
            await WriteManifestAsync("paused");
            pendingPlanning = planningWork; expectedRun = runId;
        }
        catch (Exception error) { AddWarning(error.Message); SetState(RecordingWorkspaceState.Paused); throw; }
        finally { transition.Release(); }
        await pendingPlanning;
        await transition.WaitAsync();
        try
        {
            if (runId == expectedRun && state == RecordingWorkspaceState.Pausing)
            {
                await WriteManifestAsync("paused");
                SetState(RecordingWorkspaceState.Paused);
            }
        }
        finally { transition.Release(); }
    }
    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await transition.WaitAsync(cancellationToken);
        try
        {
            if (state != RecordingWorkspaceState.Paused) throw new InvalidOperationException("Recording is not paused.");
            SetState(RecordingWorkspaceState.Resuming);
            await BeginSegmentAsync(cancellationToken);
            startTimelineClip = true;
            SetState(RecordingWorkspaceState.Recording);
        }
        catch (Exception error) { AddWarning("Resume failed: " + error.Message); SetState(RecordingWorkspaceState.Paused); throw; }
        finally { transition.Release(); }
    }
    public async Task<ProjectSnapshot?> StopAsync()
    {
        Task pendingPlanning;
        Guid expectedRun;
        ProjectWorkspace stoppedWorkspace;
        await transition.WaitAsync();
        try
        {
            if (state is RecordingWorkspaceState.Setup or RecordingWorkspaceState.RecordingComplete) return workspace?.Snapshot;
            SetState(RecordingWorkspaceState.Finalizing); monitorCancellation?.Cancel();
            if (active is not null) await CompleteSegmentAsync(Stopwatch.GetTimestamp());
            await WriteManifestAsync(media.Any(m => m.Kind == MediaKind.Screen) ? "complete" : "failed");
            await workspace!.BarrierAsync();
            pendingPlanning = planningWork; expectedRun = runId; stoppedWorkspace = workspace;
        }
        catch (Exception error)
        {
            AddWarning("Finalization failed; completed checkpoints remain recoverable: " + error.Message);
            if (driver is IRecordingRunDriver run) await run.EndRunAsync();
            SetState(RecordingWorkspaceState.RecordingComplete);
            throw;
        }
        finally { transition.Release(); }
        await CompletePlanningAsync(expectedRun, pendingPlanning);
        return stoppedWorkspace.Snapshot;
    }
    public async Task AddMarkerAsync(string label = "")
    {
        await transition.WaitAsync();
        try
        {
            if (state != RecordingWorkspaceState.Recording || active is null) throw new InvalidOperationException("Markers are available while recording.");
            if (System.Text.Encoding.UTF8.GetByteCount(label) > 512) throw new ArgumentException("Marker label exceeds 512 UTF-8 bytes.");
            markers.Add(new() { Id = "marker-" + Guid.NewGuid().ToString("N"), Time = MediaTime.FromTicks(CurrentTicks()), Label = label });
            // Events are journaled immediately; only markers within finalized source duration enter a checkpoint.
            await File.AppendAllTextAsync(workspace!.Resolve("recovery/markers.jsonl"), JsonSerializer.Serialize(markers[^1]) + Environment.NewLine);
            Publish();
        }
        finally { transition.Release(); }
    }

    private async Task BeginSegmentAsync(CancellationToken token)
    {
        bool overlapping = active is not null;
        var index = ++segmentIndex; var prefix = $"media/segment-{index:D4}";
        var shapes = $"events/pointer-{index:D4}-shapes"; Directory.CreateDirectory(workspace!.Resolve(shapes));
        var paths = new SegmentPaths(prefix + "-screen.mp4", $"events/pointer-{index:D4}.jsonl", shapes, prefix + "-camera.mp4",
            plan!.Audio!.Select((a, i) => prefix + (a.Kind == AudioCaptureKind.Microphone ? "-microphone.wav" : "-system.wav")).ToArray(), $"recovery/segment-{index:D4}-audio.jsonl",
            plan.Android?.InternalAudio == true ? prefix + "-phone.wav" : null);
        var pending = new List<string> { paths.Screen };
        if (plan.RecordPointer) pending.Add(paths.Pointer); if (plan.Camera is not null) pending.Add(paths.Camera); pending.AddRange(paths.Audio);
        if (paths.PhoneAudio is not null) pending.Add(paths.PhoneAudio);
        await WriteManifestAsync("recording", activePending.Concat(abandonedPending).Concat(pending).ToArray());
        var absolute = paths with { Screen = workspace.Resolve(paths.Screen), Pointer = workspace.Resolve(paths.Pointer), Shapes = workspace.Resolve(paths.Shapes),
            Camera = workspace.Resolve(paths.Camera), Audio = paths.Audio.Select(workspace.Resolve).ToArray(), Diagnostics = workspace.Resolve(paths.Diagnostics),
            PhoneAudio = paths.PhoneAudio is null ? null : workspace.Resolve(paths.PhoneAudio) };
        try { active = await driver.StartAsync(plan, absolute, token); activePending = pending; }
        catch { abandonedPending.AddRange(pending); await WriteManifestAsync("recording"); throw; }
        if (lastBoundary != 0 && !overlapping && state != RecordingWorkspaceState.Resuming)
        {
            var gap = (active.Start.EpochQpc - lastBoundary) * 1000.0 / active.Start.QpcFrequency;
            var message = $"Checkpoint {index - 1} restart gap: {gap:F1} ms. Source timeline concatenates finalized media and excludes this interval.";
            AddWarning(message);
            await File.AppendAllTextAsync(workspace.Resolve("recovery/diagnostics.jsonl"), JsonSerializer.Serialize(new { type = "checkpoint-gap", runId, segment = index, gapMilliseconds = gap, previousBoundaryQpc = lastBoundary, nextEpochQpc = active.Start.EpochQpc }) + Environment.NewLine);
        }
    }
    private async Task CompleteSegmentAsync(long boundary, IRecordingSegment? previous = null)
    {
        if (active is null && previous is null) return;
        var recording = previous ?? active!;
        if (previous is null) { active = null; activePending = Array.Empty<string>(); }
        lastBoundary = boundary;
        CompletedCapture result;
        try { result = await recording.FinishAsync(boundary); }
        finally { await recording.DisposeAsync(); }
        foreach (var warning in result.Warnings) AddWarning(warning);
        if (result.DurationTicks <= 0 || !result.Tracks.Any(t => t.Kind == MediaKind.Screen)) throw new IOException("Screen segment has no finalized video frames.");
        foreach (var track in result.Tracks)
        {
            if (track.OffsetTicks >= result.DurationTicks || track.DurationTicks <= 0) continue;
            var relative = Path.GetRelativePath(workspace!.DirectoryPath, track.Path).Replace('\\', '/');
            media.Add(new() { Id = "media-" + Guid.NewGuid().ToString("N"), Kind = track.Kind, Path = relative,
                Duration = MediaTime.FromTicks(Math.Min(track.DurationTicks, result.DurationTicks - track.OffsetTicks)),
                StartOffset = MediaTime.FromTicks(sourceTicks + track.OffsetTicks), Width = track.Width, Height = track.Height,
                Channels = track.Channels, SampleRate = track.SampleRate });
        }
        // Recovery files are physical media boundaries, not authored timeline cuts.
        // Extend the current take across checkpoints; only pause/resume starts another clip.
        if (startTimelineClip)
            segments.Add(new() { Id = "segment-" + Guid.NewGuid().ToString("N"), SourceStart = MediaTime.FromTicks(sourceTicks), SourceDuration = MediaTime.FromTicks(result.DurationTicks), Rate = new() });
        else
            segments[^1] = segments[^1] with { SourceDuration = MediaTime.FromTicks(checked(segments[^1].SourceDuration.Ticks + result.DurationTicks)) };
        startTimelineClip = false;
        sourceTicks += result.DurationTicks;
        var checkpoint = MakeSnapshot();
        if (workspace!.Snapshot is null && presetLibrary is not null) {
            try { var library = presetLibrary(); var catalog = await library.ListAsync(); if (catalog.DefaultId is string id) checkpoint = await library.ApplyAsync(id, checkpoint, workspace.DirectoryPath); }
            catch (Exception error) { AddWarning(L.T("SDB4C03498049") + error.Message); }
        }
        // Recovery must precede optional planning. No planner parsing or native
        // camera computation runs while holding the recording transition lock.
        if (!await workspace!.SaveAsync(checkpoint, workspace.SessionEpoch))
            throw new IOException("The recording checkpoint was superseded before it could be saved.");
        await WriteManifestAsync("recording");
        QueueAutomaticZoom(checkpoint);
    }
    private void QueueAutomaticZoom(ProjectSnapshot checkpoint)
    {
        if (!plan!.AutomaticZoom || !plan.RecordPointer || plan.Android is not null ||
            !checkpoint.Media.Any(m => m.Kind == MediaKind.PointerEvents)) return;
        var previous = planningWork;
        var target = workspace!;
        var expectedRun = runId;
        var planner = zoomPlanner;
        var token = planningCancellation.Token;
        planningWork = Task.Run(async () =>
        {
            try
            {
                await previous.ConfigureAwait(false);
                if (PlanningBarrier is not null) await PlanningBarrier(token).ConfigureAwait(false);
                var planned = await planner.AppendAsync(target, checkpoint, token).ConfigureAwait(false);
                await transition.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (disposed || runId != expectedRun || workspace != target || token.IsCancellationRequested) return;
                    var latest = target.Snapshot!;
                    var authored = latest.Effects.Where(e => !AutomaticZoomPlanner.IsGenerated(e)).ToArray();
                    var derived = planned.Effects.Where(AutomaticZoomPlanner.IsGenerated)
                        .Where(e => !authored.Any(a => a.Id == e.Id || a.Kind == "zoom" &&
                            VisualResources.Ticks(a.SourceStart) < VisualResources.Ticks(e.SourceStart) + VisualResources.Ticks(e.SourceDuration) &&
                            VisualResources.Ticks(e.SourceStart) < VisualResources.Ticks(a.SourceStart) + VisualResources.Ticks(a.SourceDuration))).ToArray();
                    // Merge only derived effects into the latest media checkpoint;
                    // a slow earlier result must never roll back newer source tracks.
                    revision = checked(Math.Max(revision, latest.Revision) + 1);
                    var candidate = latest with { Revision = revision, Effects = authored.Concat(derived).ToArray() };
                    if (!await target.SaveAsync(candidate, target.SessionEpoch).ConfigureAwait(false))
                        throw new IOException("Automatic camera planning was superseded before commit.");
                    await WriteManifestAsync(state == RecordingWorkspaceState.Pausing || state == RecordingWorkspaceState.Paused ? "paused" :
                        state == RecordingWorkspaceState.Finalizing || state == RecordingWorkspaceState.RecordingComplete ? "complete" : "recording").ConfigureAwait(false);
                }
                finally { transition.Release(); }
            }
            catch (Exception) when (token.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            {
                await transition.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (runId == expectedRun && workspace == target && !disposed)
                    {
                        AddWarning(L.T("S542AA18A92E5") + error.Message);
                        try
                        {
                            await WriteManifestAsync(state == RecordingWorkspaceState.Finalizing ? "complete" :
                                state == RecordingWorkspaceState.Pausing ? "paused" : "recording").ConfigureAwait(false);
                        }
                        catch (Exception manifestError) when (manifestError is IOException or UnauthorizedAccessException)
                        {
                            AddWarning("Planning warning could not update the recovery manifest: " + manifestError.Message);
                        }
                        Publish();
                    }
                }
                finally { transition.Release(); }
            }
        }, CancellationToken.None);
    }
    private async Task CompletePlanningAsync(Guid expectedRun, Task pendingPlanning)
    {
        // Await outside transition: the planner needs a brief commit turn under it.
        await pendingPlanning.ConfigureAwait(false);
        await transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (runId != expectedRun || state != RecordingWorkspaceState.Finalizing) return;
            await WriteManifestAsync(media.Any(m => m.Kind == MediaKind.Screen) ? "complete" : "failed").ConfigureAwait(false);
            await workspace!.BarrierAsync().ConfigureAwait(false);
            if (driver is IRecordingRunDriver run) await run.EndRunAsync().ConfigureAwait(false);
            SetState(RecordingWorkspaceState.RecordingComplete);
        }
        finally { transition.Release(); }
    }
    private ProjectSnapshot MakeSnapshot()
    {
        var main = media.First(m => m.Kind == MediaKind.Screen);
        return new() { SchemaVersion = plan!.Android is null ? 1 : 2,
            PrimarySource = plan.Android is null ? null : new() { Kind = "androidUsb", Width = main.Width, Height = main.Height },
            ProjectId = workspace!.Snapshot?.ProjectId ?? projectIdentity(), Revision = revision = checked(Math.Max(revision, workspace.Snapshot?.Revision ?? 0) + 1),
            SourceDuration = MediaTime.FromTicks(sourceTicks), Media = media.ToArray(), Segments = segments.ToArray(),
            Effects = workspace.Snapshot?.Effects ?? Array.Empty<SourceEffect>(),
            Markers = markers.Where(m => m.Time.Ticks <= sourceTicks).ToArray(),
            Canvas = workspace.Snapshot?.Canvas ?? new() { Width = main.Width, Height = main.Height, Camera = new() { Enabled = plan!.Camera is not null } },
            Editor = workspace.Snapshot?.Editor,
            Export = new() { Fps = new() { Numerator = (int)plan!.Fps }, VideoCodec = plan.VideoCodec } };
    }
    private string? createdProjectId;
    private string projectIdentity() => createdProjectId ??= Guid.NewGuid().ToString("N");
    private Task WriteManifestAsync(string status, IReadOnlyList<string>? pending = null)
    {
        if (workspace is null) return Task.CompletedTask;
        return workspace.WriteManifestAsync(new() { ProjectId = workspace.Snapshot?.ProjectId ?? projectIdentity(), RunId = runId,
            Status = status, CheckpointJson = workspace.Snapshot is null ? null : ProjectJson.Serialize(workspace.Snapshot),
            PendingFiles = pending ?? activePending.Concat(abandonedPending).ToArray(), Warnings = warnings.ToArray() });
    }
    private async Task MonitorAsync(Guid expectedRun, CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (await timer.WaitForNextTickAsync(token))
            {
                Task? finalPlanning = null;
                await transition.WaitAsync(token);
                try
                {
                    if (runId != expectedRun || active is null || state != RecordingWorkspaceState.Recording) continue;
                    var health = active.Poll(); peak = health.MicrophonePeak;
                    foreach (var warning in health.Warnings) AddWarning(warning);
                    if (health.MainSourceFailed)
                    {
                        AddWarning(health.MainError ?? "Screen source ended."); SetState(RecordingWorkspaceState.Finalizing);
                        await CompleteSegmentAsync(Stopwatch.GetTimestamp()); await WriteManifestAsync("complete");
                        finalPlanning = planningWork;
                    }
                    else if (!checkpointDisabled && CurrentTicks() - sourceTicks >= TimeSpan.FromSeconds(plan!.CheckpointSeconds).Ticks)
                    {
                        var previous = active;
                        try { await BeginSegmentAsync(token); }
                        catch (Exception error) when (error is not OperationCanceledException)
                        { checkpointDisabled = true; AddWarning("Overlapping checkpoint unavailable; active recording continues. Pause to finalize a recoverable checkpoint. " + error.Message); }
                        if (!ReferenceEquals(active, previous)) await CompleteSegmentAsync(active!.Start.EpochQpc, previous);
                    }
                    Publish();
                }
                finally { transition.Release(); }
                if (finalPlanning is not null)
                {
                    await CompletePlanningAsync(expectedRun, finalPlanning);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            await transition.WaitAsync();
            try { AddWarning("Recording monitor ended: " + error.Message); if (active is not null) { await active.DisposeAsync(); active = null; } await WriteManifestAsync("failed"); if (driver is IRecordingRunDriver run) await run.EndRunAsync(); SetState(RecordingWorkspaceState.RecordingComplete); }
            finally { transition.Release(); }
        }
    }
    private long CurrentTicks() => sourceTicks + (active is null ? 0 : Math.Max(0, (long)((decimal)(Stopwatch.GetTimestamp() - active.Start.EpochQpc) * 10_000_000 / active.Start.QpcFrequency)));
    private void AddWarning(string value) { if (warningSet.Add(value)) warnings.Add(value); }
    private void SetState(RecordingWorkspaceState value) { state = value; Publish(); }
    private void Publish() => StatusChanged?.Invoke(this, new(runId, state, TimeSpan.FromTicks(CurrentTicks()), workspace?.DirectoryPath, warnings.ToArray(), peak));
    public ValueTask DisposeAsync()
    {
        lock (disposalGate) return new(disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        if (disposed) return;
        closing = true;
        // Closing may abandon derived work; completed media is persisted before
        // it is queued, and Stop still finalizes any active capture first.
        await transition.WaitAsync();
        try { planningCancellation.Cancel(); }
        finally { transition.Release(); }
        try { await StopAsync(); }
        finally
        {
            monitorCancellation?.Cancel();
            try
            {
                await planningWork;
                if (monitor is not null) await monitor;
            }
            finally
            {
                await transition.WaitAsync();
                try
                {
                    disposed = true;
                    if (active is not null) { await active.DisposeAsync(); active = null; }
                    if (workspace is not null) await workspace.DisposeAsync();
                }
                finally { transition.Release(); monitorCancellation?.Dispose(); planningCancellation.Dispose(); }
            }
        }
    }
}
