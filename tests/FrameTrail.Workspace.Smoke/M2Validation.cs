using System.Diagnostics;
using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;
using FrameTrail.Workspace;

internal static class M2Validation
{
    internal static async Task<int> RunAsync(string fixture, string output, bool matrix)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output); var checks = new List<string>(); var measurements = new List<object>();
        string projectPath = Path.Combine(output, "编辑验证.frametrailproject"); bool success = false; string? failure = null;
        try {
            await using var workspace = ProjectWorkspace.Create(projectPath, "m2-fixture");
            foreach (var name in new[] { "screen.mp4", "camera.mp4", "system.wav", "microphone.wav" }) File.Copy(Path.Combine(fixture, "media", name), workspace.Resolve("media/" + name));
            await File.WriteAllLinesAsync(workspace.Resolve("events/pointer.jsonl"), Enumerable.Range(0, 31).Select(i => JsonSerializer.Serialize(new { type = "pointer", ticks = i * 1_000_000L, timescale = 10_000_000, x = 0.2 + i / 60.0, y = 0.4, visible = true, inside = true, shape = 0, kind = 0 })));
            MediaAsset Asset(string id, MediaKind kind, string file, long duration = 30_000_000, long start = 0, uint w = 0, uint h = 0) => new() { Id = id, Kind = kind, Path = file, Duration = MediaTime.FromTicks(duration), StartOffset = MediaTime.FromTicks(start), Width = w, Height = h, SampleRate = kind is MediaKind.SystemAudio or MediaKind.Microphone ? 48000u : 0, Channels = kind is MediaKind.SystemAudio or MediaKind.Microphone ? 2u : 0 };
            var initial = new ProjectSnapshot { ProjectId = "m2-fixture", Revision = 1, SourceDuration = MediaTime.FromTicks(30_000_000),
                Media = new[] { Asset("screen", MediaKind.Screen, "media/screen.mp4", w: 640, h: 360), Asset("camera", MediaKind.Camera, "media/camera.mp4", 20_000_000, 5_000_000, 320, 240), Asset("system", MediaKind.SystemAudio, "media/system.wav"), Asset("mic", MediaKind.Microphone, "media/microphone.wav"), Asset("pointer", MediaKind.PointerEvents, "events/pointer.jsonl") },
                Segments = new[] { new RecordingSegment { Id = "a", SourceStart = MediaTime.FromTicks(0), SourceDuration = MediaTime.FromTicks(30_000_000) } },
                Canvas = new() { Width = 640, Height = 360, Padding = 0.06, Camera = new() { Enabled = true, Mirror = true } }, Export = new() { Width = 640, Height = 360 },
                Markers = new[] { new RecordingMarker { Id = "marker", Time = MediaTime.FromTicks(5_000_000), Label = "同步标记" } } };
            await workspace.SaveAsync(initial, workspace.SessionEpoch); var editor = new EditorSession(workspace);
            using (var media = new MediaSession(initial, projectPath)) {
                media.Resize(960, 540, 1.5, 1.5); media.Render(2_500_000, png: Path.Combine(output, "before-camera.png")); media.Render(7_500_000, png: Path.Combine(output, "with-camera.png"));
                media.SetGeneration(4); try { media.Render(0, 3); throw new Exception("Stale generation accepted."); } catch (OperationCanceledException) { }
                media.Render(5_000_000, 4); if (media.Status.RenderedTicks != 5_000_000) throw new Exception("Newest seek missing.");
                measurements.Add(new { previewSoftwareDevice = media.Status.SoftwareDevice, cpuDecodeFallback = media.Status.CpuDecodeFallbacks });
            }
            checks.Add("D3D11 actual decode/composition, camera start offset, mirrored camera, pointer and generation rejection; Chinese project path.");
            await editor.ExecuteAsync(new { op = "split", segmentId = "a", sourceTicks = 10_000_000, newId = "b" });
            await editor.ExecuteAsync(new { op = "split", segmentId = "b", sourceTicks = 20_000_000, newId = "c" });
            await editor.ExecuteAsync(new { op = "trim", segmentId = "a", sourceStart = 2_500_000, sourceEnd = 10_000_000 });
            await editor.ExecuteAsync(new { op = "move", segmentId = "c", index = 0 });
            await editor.ExecuteAsync(new { op = "rate", segmentId = "b", numerator = 2, denominator = 1 });
            if (editor.Timeline.Duration != 22_500_000) throw new Exception("Edited output duration differs from 2.25 seconds.");
            var content = ProjectJson.Serialize(editor.Snapshot with { Revision = 0 }); await editor.UndoAsync(); await editor.RedoAsync();
            if (ProjectJson.Serialize(editor.Snapshot with { Revision = 0 }) != content) throw new Exception("Undo/redo changed content.");
            editor.BeginTransaction(); editor.PreviewCommand(new { op = "delete", segmentId = "b" }); editor.CancelTransaction();
            if (ProjectJson.Serialize(editor.Snapshot with { Revision = 0 }) != content) throw new Exception("Cancelled drag changed saved content.");
            var levels = new EditorSettings { SystemGain = 0.5, MicrophoneMuted = true, ClipAudio = new Dictionary<string, AudioLevel> { ["c"] = new() { SystemMuted = true }, ["b"] = new() { SystemGain = 2 } }, CornerRadius = 18, Shadow = 0.4 };
            await editor.ExecuteAsync(new { op = "settings", editor = levels }); await editor.FlushAsync();
            checks.Add("Trim + split + reorder + 2x rate => 2.25s, undo/redo, cancel, global/clip audio settings and autosave.");
            var temporaryAudio = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var audioWatcher = new FileSystemWatcher(output, ".frametrail-*.wav");
            audioWatcher.Created += (_, e) => temporaryAudio.Enqueue(e.Name ?? "unknown");
            audioWatcher.Error += (_, _) => temporaryAudio.Enqueue("watcher-overflow");
            audioWatcher.EnableRaisingEvents = true;
            var mp4 = Path.Combine(output, "edited-30.mp4"); await editor.ExportAsync(mp4, false, null, CancellationToken.None);
            await editor.ExportAsync(Path.Combine(output, "edited.m4a"), true, null, CancellationToken.None);
            using (var media = new MediaSession(editor.Snapshot, projectPath)) {
                media.Render(0, png: Path.Combine(output, "edited-preview.png")); media.PrepareAudio(Path.Combine(output, "mixed.wav"));
                var watch = Stopwatch.StartNew(); for (int i = 0; i < 60; ++i) media.Render(i * 10_000_000L / 30); watch.Stop(); measurements.Add(new { previewFrames = 60, seconds = watch.Elapsed.TotalSeconds, fps = 60 / watch.Elapsed.TotalSeconds, raster = "640x360" });
            }
            foreach (var rate in new[] { (1, 4), (1, 2), (1, 1), (3, 2), (2, 1), (4, 1) }) {
                var p = initial with { Segments = new[] { initial.Segments[0] with { Rate = new Rational { Numerator = rate.Item1, Denominator = rate.Item2 } } }, Editor = new() { MicrophoneMuted = true } };
                using var media = new MediaSession(p, projectPath);
                media.PrepareAudio(Path.Combine(output, $"pitch-{rate.Item1}-{rate.Item2}.wav"));
                media.Export(Path.Combine(output, $"pitch-{rate.Item1}-{rate.Item2}.m4a"), true);
                var status = media.Status;
                if (status.Stage != 4 || status.Completed != status.Total || status.Total != (ulong)(status.Duration * 48000 / TimeSpan.TicksPerSecond))
                    throw new Exception("Streaming audio export lost its sample progress or completion stage.");
            }
            // Odd tick boundaries exercise PCM sample rounding independently of
            // video frame boundaries, across three different continuous rates.
            var fractional = initial with {
                Segments = new[] {
                    new RecordingSegment { Id = "fraction-a", SourceStart = MediaTime.FromTicks(0), SourceDuration = MediaTime.FromTicks(8_765_431), Rate = new() { Numerator = 3, Denominator = 2 } },
                    new RecordingSegment { Id = "fraction-b", SourceStart = MediaTime.FromTicks(10_000_003), SourceDuration = MediaTime.FromTicks(9_876_543), Rate = new() { Numerator = 1, Denominator = 4 } },
                    new RecordingSegment { Id = "fraction-c", SourceStart = MediaTime.FromTicks(22_345_679), SourceDuration = MediaTime.FromTicks(7_654_321), Rate = new() { Numerator = 4 } }
                },
                Editor = new() { SystemGain = 0.7, MicrophoneGain = 0.4, ClipAudio = new Dictionary<string, AudioLevel> { ["fraction-b"] = new() { MicrophoneMuted = true } } },
                Export = initial.Export with { Width = 320, Height = 180 }
            };
            using (var media = new MediaSession(fractional, projectPath)) {
                media.PrepareAudio(Path.Combine(output, "fractional.wav"));
                media.Export(Path.Combine(output, "fractional.m4a"), true);
                media.Export(Path.Combine(output, "fractional.mp4"));
                var probe = await ProbeAsync(Path.Combine(output, "fractional.mp4"));
                var video = probe.GetProperty("streams").EnumerateArray().First(s => s.GetProperty("codec_type").GetString() == "video");
                var expectedFrames = (media.Status.Duration * 30 + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond;
                if (video.GetProperty("nb_read_frames").GetString() != expectedFrames.ToString()) throw new Exception("Fractional mixed-rate export lost its scheduled video tail.");
                media.ForceCpuExport(true);
                media.Export(Path.Combine(output, "fractional-cpu.mp4"));
                measurements.Add(new { fractionalDurationTicks = media.Status.Duration, expectedFrames, cpu = media.ExportMetrics, probe });
            }
            checks.Add("AAC streams from the continuous mixer at 0.25/0.5/1/1.5/2/4x; odd source ticks and three mixed rates retain sample/frame tails on default and CPU export paths.");
            using (var both = new MediaSession(initial, projectPath)) both.PrepareAudio(Path.Combine(output, "both-tracks.wav"));
            var exactFrame = initial with { Canvas = initial.Canvas with { Padding = 0, Camera = new() }, Editor = new() { CornerRadius = 0, Shadow = 0, CursorScale = 0 } };
            using (var media = new MediaSession(exactFrame, projectPath)) media.Render(20_000_000, png: Path.Combine(output, "exact-source-frame60.png"));
            var held = Path.Combine(fixture, "media", "screen-held.mp4");
            if (File.Exists(held)) {
                File.Copy(held, workspace.Resolve("media/held.mp4")); const long heldDuration = 10_000_269;
                var p = initial with { SourceDuration = MediaTime.FromTicks(heldDuration), Media = new[] { initial.Media[0] with { Path = "media/held.mp4", Duration = MediaTime.FromTicks(heldDuration) } }, Segments = new[] { initial.Segments[0] with { SourceDuration = MediaTime.FromTicks(heldDuration) } }, Markers = Array.Empty<RecordingMarker>(), Canvas = initial.Canvas with { Camera = new() } };
                using (var media = new MediaSession(p, projectPath)) media.Render(heldDuration - 1, png: Path.Combine(output, "sparse-tail.png"));
                var invalid = p with { SourceDuration = MediaTime.FromTicks(20_000_000), Media = new[] { p.Media[0] with { Duration = MediaTime.FromTicks(20_000_000) } }, Segments = new[] { p.Segments[0] with { SourceDuration = MediaTime.FromTicks(20_000_000) } } };
                using var broken = new MediaSession(invalid, projectPath); try { broken.Render(15_000_000); throw new Exception("Missing source interval was silently extended."); } catch (System.Runtime.InteropServices.COMException) { }
                checks.Add("Sparse held-frame tail seek tolerates container quantization; genuinely missing source intervals are rejected.");
            }
            var fullHd = Path.Combine(fixture, "media", "screen1080.mp4");
            if (File.Exists(fullHd)) {
                File.Copy(fullHd, workspace.Resolve("media/screen1080.mp4"));
                var p = initial with { Canvas = initial.Canvas with { Width = 1920, Height = 1080 }, Media = initial.Media.Select(m => m.Kind == MediaKind.Screen ? m with { Path = "media/screen1080.mp4", Width = 1920, Height = 1080 } : m).ToArray() };
                using var media = new MediaSession(p, projectPath); media.Render(0); var watch = Stopwatch.StartNew();
                for (int i = 1; i <= 120; ++i) media.Render(i % 90 * 10_000_000L / 30); watch.Stop();
                measurements.Add(new { previewFrames = 120, seconds = watch.Elapsed.TotalSeconds, fps = 120 / watch.Elapsed.TotalSeconds, raster = "1920x1080", note = "native decode/compositor throughput, not monitor presentation fps" });
            }
            var info = await ProbeAsync(mp4); var v = info.GetProperty("streams").EnumerateArray().First(s => s.GetProperty("codec_type").GetString() == "video");
            if (v.GetProperty("nb_read_frames").GetString() != "68") throw new Exception("30fps schedule did not produce exactly 68 frames.");
            measurements.Add(new { export30 = info }); checks.Add("Edited MP4 and M4A finalized and reopened; 30fps rational schedule produced exactly 68 frames.");
            var fps60 = editor.Snapshot.Export with { Fps = new Rational { Numerator = 60 } }; await editor.ExecuteAsync(new { op = "settings", export = fps60 });
            var mp460 = Path.Combine(output, "edited-60.mp4"); await editor.ExportAsync(mp460, false, null, CancellationToken.None);
            var info60 = await ProbeAsync(mp460); if (info60.GetProperty("streams").EnumerateArray().First(s => s.GetProperty("codec_type").GetString() == "video").GetProperty("nb_read_frames").GetString() != "135") throw new Exception("60fps schedule did not produce 135 frames.");
            measurements.Add(new { export60 = info60 }); checks.Add("60fps schedule produced exactly 135 frames including the tail.");
            var occupied = Path.Combine(output, "occupied.mp4"); await File.WriteAllTextAsync(occupied, "original-target");
            using (var targetLock = new FileStream(occupied, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                try { await editor.ExportAsync(occupied, false, null, CancellationToken.None); throw new Exception("Occupied target unexpectedly replaced."); } catch (System.Runtime.InteropServices.COMException) { }
            }
            if (await File.ReadAllTextAsync(occupied) != "original-target") throw new Exception("Occupied target was damaged.");
            var cancelled = Path.Combine(output, "cancelled.mp4"); await File.WriteAllTextAsync(cancelled, "keep-cancelled-target");
            using (var media = new MediaSession(editor.Snapshot, projectPath)) {
                var job = Task.Run(() => media.Export(cancelled)); var deadline = DateTime.UtcNow.AddSeconds(30);
                while (!job.IsCompleted && media.Status.Stage < 2 && DateTime.UtcNow < deadline) await Task.Delay(5); media.Cancel();
                try { await job; throw new Exception("Cancellation was not observed."); } catch (OperationCanceledException) { }
            }
            if (await File.ReadAllTextAsync(cancelled) != "keep-cancelled-target" || Directory.EnumerateFiles(output, ".frametrail-*").Any()) throw new Exception("Cancellation left damage or temporary files.");
            checks.Add("Occupied destination and mid-job cancellation preserve existing targets and clean job-local temporary files.");
            const int repeats = 600;
            var longAudio = initial with {
                SourceDuration = MediaTime.FromTicks(30_000_000L * repeats),
                Media = Enumerable.Range(0, repeats).SelectMany(i => initial.Media.Where(m => m.Kind is MediaKind.Screen or MediaKind.SystemAudio or MediaKind.Microphone)
                    .Select(m => m with { Id = m.Id + "-" + i, StartOffset = MediaTime.FromTicks(i * 30_000_000L) })).ToArray(),
                Segments = Enumerable.Range(0, repeats).Select(i => new RecordingSegment { Id = "long-" + i, SourceStart = MediaTime.FromTicks(i * 30_000_000L), SourceDuration = MediaTime.FromTicks(30_000_000) }).ToArray(),
                Canvas = initial.Canvas with { Camera = new() }, Markers = Array.Empty<RecordingMarker>()
            };
            var cancelledAudio = Path.Combine(output, "cancelled.m4a");
            await File.WriteAllTextAsync(cancelledAudio, "keep-audio-target");
            using (var media = new MediaSession(longAudio, projectPath)) {
                var watch = Stopwatch.StartNew();
                var job = Task.Run(() => media.Export(cancelledAudio, true));
                bool streamed = false;
                while (!job.IsCompleted && watch.ElapsedMilliseconds < 10_000) {
                    var status = media.Status;
                    if (status.Stage == 2 && status.Completed >= 4096) { streamed = true; break; }
                    await Task.Delay(2);
                }
                media.Cancel();
                try { await job; throw new Exception("Audio cancellation was not observed."); } catch (OperationCanceledException) { }
                if (!streamed) throw new Exception("Long audio export did not begin encoding before preparing its entire soundtrack.");
                measurements.Add(new { logicalAudioMinutes = 30, firstStreamingPacketsAtMilliseconds = watch.ElapsedMilliseconds, cancelledStage = media.Status.Stage });
            }
            if (await File.ReadAllTextAsync(cancelledAudio) != "keep-audio-target" || Directory.EnumerateFiles(output, ".frametrail-*").Any()) throw new Exception("Audio cancellation damaged destination or leaked job files.");
            checks.Add("30-minute logical audio export begins encoding immediately; mid-mix audio cancellation preserves destination and cleans temporary output.");
            await Task.Delay(100); // Drain asynchronous FileSystemWatcher delivery.
            if (!temporaryAudio.IsEmpty) throw new Exception("Export created temporary PCM audio: " + string.Join(", ", temporaryAudio));
            checks.Add("No job-local .frametrail-*.wav was created during audio/video exports or cancellation.");
            var copy = Path.Combine(output, "素材副本.frametrailproject"); await editor.CopySourcesAsync(copy, CancellationToken.None);
            await using (var reopened = await ProjectWorkspace.OpenAsync(copy)) { if (ProjectJson.Serialize(reopened.Snapshot!) != ProjectJson.Serialize(editor.Snapshot)) throw new Exception("Copied/reopened project differs."); }
            checks.Add("Source copy opens with all media and edited snapshot, without cache or write lease files.");
            if (matrix) {
                var tiny = editor.Snapshot with { Segments = new[] { new RecordingSegment { Id = "matrix-clip", SourceStart = MediaTime.FromTicks(5_000_000), SourceDuration = MediaTime.FromTicks(2_000_000) } } };
                foreach (var height in new uint[] { 1080, 1440, 2160 }) foreach (var fps in new[] { 30, 60 }) {
                    var p = tiny with { Export = tiny.Export with { Width = height * 16 / 9, Height = height, Fps = new Rational { Numerator = fps } } };
                    var target = Path.Combine(output, $"matrix-{height}-{fps}.mp4"); var watch = Stopwatch.StartNew();
                    using var media = new MediaSession(p, projectPath); media.Export(target); watch.Stop(); var probe = await ProbeAsync(target);
                    var video = probe.GetProperty("streams").EnumerateArray().First(s => s.GetProperty("codec_type").GetString() == "video");
                    if (video.GetProperty("nb_read_frames").GetString() != (fps / 5).ToString()) throw new Exception("Capability matrix frame count mismatch.");
                    measurements.Add(new { height, fps, seconds = watch.Elapsed.TotalSeconds, probe });
                }
                checks.Add("1080p/1440p/4K at 30/60fps each encoded and decoded with exact scheduled frame count (0.2s fixtures; not a throughput/long-duration guarantee).");
            }
            success = true;
        } catch (Exception error) { failure = error.ToString(); Console.Error.WriteLine(error); }
        await File.WriteAllTextAsync(Path.Combine(output, "m2-validation.json"), JsonSerializer.Serialize(new { success, timestampUtc = DateTimeOffset.UtcNow, projectPath, checks, measurements, failure }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(Path.Combine(output, "m2-validation.json")); return success ? 0 : 1;
    }
    private static async Task<JsonElement> ProbeAsync(string path)
    {
        var info = new ProcessStartInfo("ffprobe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-count_frames", "-show_streams", "-show_format", "-of", "json", path }) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!; var output = await process.StandardOutput.ReadToEndAsync(); var errors = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception(errors); return JsonDocument.Parse(output).RootElement.Clone();
    }
}
