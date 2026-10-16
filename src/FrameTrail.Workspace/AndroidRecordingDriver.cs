using System.Diagnostics;
using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;

namespace FrameTrail.Workspace;

/// <summary>One continuously drained phone stream, with independent keyframe-started checkpoint writers.</summary>
public sealed class AndroidRecordingDriver(AndroidCaptureSession capture) : IRecordingDriver
{
    public async Task<IRecordingSegment> StartAsync(RecordingPlan plan, SegmentPaths paths, CancellationToken cancellationToken)
    {
        if (plan.Android is null || plan.RecordPointer || plan.Camera is not null || plan.Audio!.Any(a => a.Kind != AudioCaptureKind.Microphone))
            throw new ArgumentException("Android capture supports phone video/audio and an independent computer microphone. Disable Windows pointer, loopback and camera capture.");
        var status = capture.Status;
        if (status.State != 2) throw new IOException(status.Message);
        if (plan.Android.InternalAudio && status.AudioState != 1 && !plan.Android.ContinueWithoutAudio) throw new IOException("Required phone audio is unavailable.");
        AudioCaptureSession? mic = null; ulong segment = 0;
        try {
            if (plan.Audio!.Count != 0) {
                var selection = plan.Audio[0];
                mic = await Task.Run(() => AudioCaptureSession.Start(new(AudioCaptureKind.Microphone, paths.Audio[0], selection.DeviceId, DiagnosticsPath: paths.Diagnostics)), cancellationToken);
            }
            segment = capture.Begin(paths.Screen, plan.Android.InternalAudio && status.AudioState == 1 ? paths.PhoneAudio : null);
            var wait = Stopwatch.GetTimestamp(); AndroidSegmentStatus started;
            while (true) {
                cancellationToken.ThrowIfCancellationRequested(); started = capture.SegmentStatus(segment); status = capture.Status;
                if (status.State == 3) throw new IOException(status.Message);
                if (started.Ready != 0 && started.Frames > 0) break;
                if (Stopwatch.GetElapsedTime(wait) > TimeSpan.FromSeconds(8)) throw new TimeoutException("Phone encoder did not produce a checkpoint keyframe.");
                await Task.Delay(20, cancellationToken);
            }
            mic?.SetEpoch(started.EpochQpc, status.QpcFrequency);
            return new Segment(capture, segment, mic, paths, new(started.EpochQpc, status.QpcFrequency, status.Width, status.Height), plan.Android.InternalAudio, !plan.Android.ContinueWithoutAudio);
        } catch {
            mic?.Dispose();
            if (segment != 0) { try { capture.Finish(segment, Stopwatch.GetTimestamp()); } catch { } }
            throw;
        }
    }
    private sealed class Segment(AndroidCaptureSession capture, ulong id, AudioCaptureSession? mic, SegmentPaths paths, SegmentStart start, bool requestedAudio, bool requiredAudio) : IRecordingSegment
    {
        private bool finished;
        public SegmentStart Start => start;
        public RecordingHealth Poll() {
            var status = capture.Status; var warnings = new List<string>();
            if (requestedAudio && status.AudioState != 1) warnings.Add("手机内部音频不可用或已中断；已保留有效声音，视频与电脑麦克风继续录制。");
            var microphone = mic?.GetStatus();
            if (microphone?.State == AudioCaptureState.Failed) warnings.Add("电脑麦克风已中断：" + microphone.Message);
            bool failed = status.State == 3 || requestedAudio && requiredAudio && status.AudioState != 1;
            return new(failed, status.State == 3 ? status.Message : "必选手机音频已中断。", warnings, microphone?.Peak ?? 0);
        }
        public async Task<CompletedCapture> FinishAsync(long boundaryQpc) {
            if (finished) throw new InvalidOperationException("Android segment already finalized."); finished = true;
            var warnings = new List<string>();
            if (mic is not null) { try { await Task.Run(() => { mic.PauseAt(boundaryQpc); mic.StopAt(boundaryQpc); }); } catch (Exception error) { warnings.Add("Microphone finalize: " + error.Message); } }
            var value = await Task.Run(() => capture.Finish(id, boundaryQpc));
            if (value.Error < 0 || value.Frames == 0 || value.DurationTicks <= 0) throw new IOException("Phone video segment could not be finalized; earlier checkpoints remain recoverable.");
            await Task.Run(() => RecordingUtilities.ProbeMedia(paths.Screen, true));
            var tracks = new List<CompletedTrack> { new(MediaKind.Screen, paths.Screen, value.DurationTicks, Width: start.Width, Height: start.Height) };
            if (value.AudioFrames > 0 && paths.PhoneAudio is not null) {
                try { await Task.Run(() => RecordingUtilities.ProbeMedia(paths.PhoneAudio, false)); tracks.Add(new(MediaKind.AndroidAudio, paths.PhoneAudio, Math.Min(value.DurationTicks, checked((long)value.AudioFrames) * 10_000_000 / 48000), SampleRate: 48000, Channels: 2)); }
                catch (Exception error) { warnings.Add("Phone audio excluded: " + error.Message); }
            }
            if (mic is not null) {
                var status = mic.GetStatus();
                if (status.Duration100ns > 0) {
                    try { await Task.Run(() => RecordingUtilities.ProbeMedia(paths.Audio[0], false)); tracks.Add(new(MediaKind.Microphone, paths.Audio[0], Math.Min(status.Duration100ns, value.DurationTicks), SampleRate: status.SampleRate, Channels: status.Channels)); }
                    catch (Exception error) { warnings.Add("Microphone excluded: " + error.Message); }
                }
            }
            var source = capture.Status;
            try { await File.WriteAllTextAsync(Path.ChangeExtension(paths.Diagnostics, ".android.json"), JsonSerializer.Serialize(new {
                protocol = AndroidRuntime.ServerVersion, fpsLimit = source.FpsLimit, segmentWrittenFrames = value.Frames, logicalDurationTicks = value.DurationTicks,
                receivedVideoPackets = source.Frames, observedPacketCadenceFps = source.LastPtsUs > source.FirstPtsUs ? (source.Frames - 1) * 1_000_000.0 / (source.LastPtsUs - source.FirstPtsUs) : 0,
                originPtsUs = value.OriginPtsUs, start.EpochQpc, start.QpcFrequency,
                source.MinimumOffsetQpc, source.LatestOffsetQpc, arrivalOffsetVariationMs = (source.LatestOffsetQpc - source.MinimumOffsetQpc) * 1000.0 / source.QpcFrequency,
                note = "PTS drives video/audio. Arrival offset includes USB latency and is diagnostic, not a measured A/V sync guarantee."
            }, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { warnings.Add("Android diagnostics could not be saved: " + error.Message); }
            return new(value.DurationTicks, tracks, warnings);
        }
        public async ValueTask DisposeAsync() {
            if (!finished) { try { await Task.Run(() => capture.Finish(id, Stopwatch.GetTimestamp())); } catch { } finished = true; }
            if (mic is not null) await Task.Run(mic.Dispose);
        }
    }
}
