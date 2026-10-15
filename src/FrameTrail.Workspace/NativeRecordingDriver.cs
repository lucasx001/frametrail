using System.Diagnostics;
using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;

namespace FrameTrail.Workspace;

/// <summary>Acquires all requested tracks transactionally. First valid screen QPC is the epoch of every auxiliary source.</summary>
public sealed class NativeRecordingDriver : IRecordingDriver
{
    public async Task<IRecordingSegment> StartAsync(RecordingPlan plan, SegmentPaths paths, CancellationToken cancellationToken)
    {
        CaptureSession? screen = null; CameraCaptureSession? camera = null; PointerCaptureSession? pointer = null;
        var audio = new List<(AudioSelection Selection, AudioCaptureSession Session, string Path)>();
        try
        {
            foreach (var entry in plan.Audio!.Select((selection, i) => (selection, path: paths.Audio[i])))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var session = await Task.Run(() => AudioCaptureSession.Start(new(entry.selection.Kind, entry.path,
                    entry.selection.DeviceId, entry.selection.ProcessId, paths.Diagnostics + "-" + audio.Count)), cancellationToken);
                audio.Add((entry.selection, session, entry.path));
            }
            if (plan.Camera is not null) camera = await Task.Run(() => CameraCaptureSession.Start(plan.Camera, plan.CameraFormat!, paths.Camera), cancellationToken);
            screen = await CaptureSession.StartAsync(new(plan.Source, paths.Screen, plan.Region, plan.Fps, AllowSoftware: plan.AllowSoftware), cancellationToken);
            CaptureStatus status;
            var waitStart = Stopwatch.GetTimestamp();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested(); status = screen.GetStatus();
                if (status.State == CaptureState.Failed) throw new IOException(status.Message);
                if (status.FirstFrameQpc > 0) break;
                if (Stopwatch.GetElapsedTime(waitStart) > TimeSpan.FromSeconds(15)) throw new TimeoutException("Screen source did not produce its first valid frame.");
                await Task.Delay(20, cancellationToken);
            }
            foreach (var entry in audio) entry.Session.SetEpoch(status.FirstFrameQpc, status.QpcFrequency);
            camera?.SetEpoch(status.FirstFrameQpc, status.QpcFrequency);
            if (plan.RecordPointer) pointer = await Task.Run(() => PointerCaptureSession.Start(plan.Source, plan.Region, paths.Pointer, paths.Shapes, status.FirstFrameQpc, status.QpcFrequency), cancellationToken);
            if (camera is not null)
            {
                var firstCameraWait = Stopwatch.GetTimestamp();
                while (true)
                {
                    var cameraStatus = camera.GetStatus();
                    if (cameraStatus.ErrorCode < 0) throw new IOException(cameraStatus.Message);
                    if (cameraStatus.FramesWritten > 0) break;
                    if (Stopwatch.GetElapsedTime(firstCameraWait) > TimeSpan.FromSeconds(10)) throw new TimeoutException("Camera initialized but did not produce a valid frame.");
                    await Task.Delay(20, cancellationToken);
                }
            }
            foreach (var entry in audio)
            { var audioStatus = entry.Session.GetStatus(); if (audioStatus.State == AudioCaptureState.Failed) throw new IOException($"{entry.Selection.Kind}: {audioStatus.Message}"); }
            return new NativeSegment(screen, camera, pointer, audio, paths,
                new(status.FirstFrameQpc, status.QpcFrequency, status.Width, status.Height));
        }
        catch
        {
            // Destruction finalizes whatever was initialized. Pending files remain outside the completed manifest.
            await Task.Run(() => { pointer?.Dispose(); camera?.Dispose(); foreach (var entry in audio) entry.Session.Dispose(); });
            if (screen is not null) await screen.DisposeAsync(); throw;
        }
    }

    private sealed class NativeSegment(CaptureSession screen, CameraCaptureSession? camera, PointerCaptureSession? pointer,
        List<(AudioSelection Selection, AudioCaptureSession Session, string Path)> audio, SegmentPaths paths, SegmentStart start) : IRecordingSegment
    {
        public SegmentStart Start { get; } = start;
        private bool finished, disposed;
        public RecordingHealth Poll()
        {
            var status = screen.GetStatus(); var warnings = new List<string>(); double peak = 0;
            foreach (var track in audio)
            {
                var value = track.Session.GetStatus(); if (value.State == AudioCaptureState.Failed) warnings.Add($"{track.Selection.Kind}: {value.Message}");
                if (value.DefaultDeviceChanged) warnings.Add($"{track.Selection.Kind}: default device changed; this recording stays on its frozen device.");
                if (track.Selection.Kind == AudioCaptureKind.Microphone) peak = value.Peak;
            }
            if (camera is not null) { var value = camera.GetStatus(); if (value.ErrorCode < 0) warnings.Add(value.Message); }
            if (pointer is not null) { var value = pointer.GetStatus(); if (value.ErrorCode < 0) warnings.Add("Pointer event file write failed."); if (value.EventsDropped > 0) warnings.Add($"Pointer queue overflow: {value.EventsDropped} events dropped."); }
            return new(status.State is CaptureState.Failed or CaptureState.Stopped, status.Message, warnings, peak);
        }
        public async Task<CompletedCapture> FinishAsync(long boundaryQpc)
        {
            if (finished) throw new InvalidOperationException("Segment was already finalized."); finished = true;
            var warnings = new List<string>();
            // Freeze every source at the same boundary before flushing independent writers.
            try { screen.Pause(boundaryQpc); } catch (Exception error) { warnings.Add("Screen pause boundary: " + error.Message); }
            await Task.Run(() =>
            {
                foreach (var track in audio)
                    try { track.Session.PauseAt(boundaryQpc); track.Session.StopAt(boundaryQpc); }
                    catch (Exception error) { warnings.Add($"{track.Selection.Kind} finalize: {error.Message}"); }
                try { camera?.StopAt(boundaryQpc); } catch (Exception error) { warnings.Add("Camera finalize: " + error.Message); }
                try { pointer?.StopAt(boundaryQpc); } catch (Exception error) { warnings.Add("Pointer finalize: " + error.Message); }
            });
            var screenStatus = await screen.StopAsync();
            try
            {
                await File.WriteAllTextAsync(Path.ChangeExtension(paths.Diagnostics, ".capture.json"), JsonSerializer.Serialize(new {
                    epochQpc = Start.EpochQpc, boundaryQpc, screen = screenStatus, performance = screen.GetPerformance(),
                    cameraSourceIdentity = camera?.SourceIdentity, camera = camera?.GetStatus()
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception error) { warnings.Add("Capture diagnostics: " + error.Message); }
            if (screenStatus.ErrorCode < 0) warnings.Add("Screen source: " + screenStatus.Message);
            var tracks = new List<CompletedTrack>();
            // A new overlapping segment may already have established the next epoch. Physical files may contain
            // a few extra trailing frames; the immutable source manifest trims them at this shared boundary.
            long logicalBoundary = (long)((decimal)(boundaryQpc - Start.EpochQpc) * 10_000_000 / Start.QpcFrequency);
            long duration = Math.Min(screenStatus.Duration100ns, logicalBoundary);
            if (screenStatus.FramesWritten > 0 && duration > 0)
            {
                await Task.Run(() => RecordingUtilities.ProbeMedia(paths.Screen, true));
                tracks.Add(new(MediaKind.Screen, paths.Screen, duration, Width: screenStatus.Width, Height: screenStatus.Height));
            }
            foreach (var track in audio)
            {
                var value = track.Session.GetStatus();
                try
                {
                    if (value.Duration100ns <= 0) continue;
                    await Task.Run(() => RecordingUtilities.ProbeMedia(track.Path, false));
                    tracks.Add(new(track.Selection.Kind == AudioCaptureKind.Microphone ? MediaKind.Microphone : MediaKind.SystemAudio,
                        track.Path, Math.Min(value.Duration100ns, duration), SampleRate: value.SampleRate, Channels: value.Channels));
                    if (value.ErrorCode < 0) warnings.Add($"{track.Selection.Kind}: kept finalized audio before device failure; {value.Message}");
                }
                catch (Exception error) { warnings.Add($"{track.Selection.Kind} segment excluded: {error.Message}"); }
            }
            if (camera is not null)
            {
                var value = camera.GetStatus();
                try
                {
                    if (value.FramesWritten > 0 && value.DurationTicks > 0)
                    {
                        await Task.Run(() => RecordingUtilities.ProbeMedia(paths.Camera, true));
                        tracks.Add(new(MediaKind.Camera, paths.Camera, value.DurationTicks, value.FirstOffsetTicks, value.Width, value.Height));
                    }
                    if (value.ErrorCode < 0) warnings.Add("Camera disconnected; kept finalized frames. " + value.Message);
                }
                catch (Exception error) { warnings.Add("Camera segment excluded: " + error.Message); }
            }
            if (pointer is not null)
            {
                var value = pointer.GetStatus();
                if (value.ErrorCode >= 0 && value.EventsWritten > 0) tracks.Add(new(MediaKind.PointerEvents, paths.Pointer, duration));
                else warnings.Add("Pointer segment is empty or unreadable and was excluded.");
            }
            return new(duration, tracks, warnings);
        }
        public async ValueTask DisposeAsync()
        {
            if (disposed) return; disposed = true;
            await Task.Run(() => { pointer?.Dispose(); camera?.Dispose(); foreach (var track in audio) track.Session.Dispose(); });
            await screen.DisposeAsync();
        }
    }
}
