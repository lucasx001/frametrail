using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;
using FrameTrail.Workspace;

internal static class CheckpointTimelineValidation
{
    // Record only the benchmark's own animated window, with no audio, camera or pointer.
    // Use the product's default 60-second checkpoints rather than a shortened test interval.
    internal static async Task<int> RunAsync(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var checks = new List<string>();
        try
        {
            var source = CaptureSession.EnumerateSources().Single(s => s.Kind == CaptureSourceKind.Window && s.Name.Contains("1080p capture benchmark"));
            var path = Path.Combine(output, "continuous.frametrailproject");
            var checkpointIds = new List<string>();
            await using (var recorder = new RecordingCoordinator())
            {
                recorder.StatusChanged += (_, _) => {
                    var snapshot = recorder.Workspace?.Snapshot;
                    if (snapshot is not null) checkpointIds.Add(AssertClip(snapshot));
                };
                await recorder.StartAsync(new(path, source, AllowSoftware: true, Audio: Array.Empty<AudioSelection>(), RecordPointer: false, AutomaticZoom: false));
                for (int elapsed = 0; elapsed < 125; elapsed += 5)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    if (recorder.State != RecordingWorkspaceState.Recording) throw new IOException("Recording ended before the duration check.");
                    if ((elapsed + 5) % 30 == 0) Console.WriteLine($"Recorded {elapsed + 5}s; saved clips: {recorder.Workspace!.Snapshot?.Segments.Count ?? 0}");
                }
                var snapshot = (await recorder.StopAsync())!;
                if (snapshot.SourceDuration.Ticks < TimeSpan.FromSeconds(120).Ticks || snapshot.Media.Count != 3)
                    throw new IOException("Expected two real automatic checkpoints and the final tail.");
                if (checkpointIds.Distinct().Count() != 1) throw new IOException("A checkpoint replaced the editable clip identity.");
                checks.Add("125-second WGC recording: three finalized screen files, one stable editable clip.");
            }
            await using var reopened = await ProjectWorkspace.OpenAsync(path);
            var project = reopened.Snapshot!;
            var editor = new EditorSession(reopened);
            if (editor.Timeline.Clips.Count != 1 || editor.Timeline.Duration != project.SourceDuration.Ticks)
                throw new IOException("Reopened editor did not resolve the entire recording as one clip.");
            using (var engine = new EngineSession())
            using (var preview = new MediaSession(project, path))
            {
                engine.LoadProject(project); preview.Resize(960, 540);
                foreach (var (asset, index) in project.Media.OrderBy(m => m.StartOffset.Ticks).Skip(1).Select((asset, index) => (asset, index)))
                {
                    var boundary = asset.StartOffset.Ticks;
                    var before = engine.EvaluateScene(boundary - 1)!;
                    var after = engine.EvaluateScene(boundary)!;
                    if (before.SegmentId != after.SegmentId || after.Screen.MediaId != asset.Id || after.Screen.LocalTicks != 0)
                        throw new IOException("Media selection failed across a checkpoint inside the clip.");
                    preview.Render(boundary - 333_333, png: Path.Combine(output, $"boundary-{index + 1}-before.png"));
                    preview.Render(boundary, png: Path.Combine(output, $"boundary-{index + 1}-after.png"));
                }
            }
            checks.Add("Reopened editor resolves one full-duration clip; native decoder seeks across both file boundaries.");
            long cut = project.Media.OrderBy(m => m.StartOffset.Ticks).ElementAt(1).StartOffset.Ticks;
            await editor.ExecuteAsync(new { op = "split", segmentId = project.Segments[0].Id, sourceTicks = cut, newId = "manual-cut" });
            if (editor.Timeline.Clips.Count != 2) throw new IOException("Manual split failed.");
            await editor.UndoAsync();
            if (editor.Timeline.Clips.Count != 1) throw new IOException("Undo did not restore the single clip.");
            await editor.ExecuteAsync(new { op = "trim", segmentId = project.Segments[0].Id, sourceStart = cut - 10_000_000, sourceEnd = cut + 10_000_000 });
            var excerpt = editor.Snapshot with { Export = new() { Width = 640, Height = 360 } };
            using (var export = new MediaSession(excerpt, path)) export.Export(Path.Combine(output, "cross-checkpoint.mp4"));
            await editor.UndoAsync(); await editor.FlushAsync();
            checks.Add("Manual split/undo and a two-second trim/export spanning a physical file boundary passed; full recording restored.");
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new {
                success = true, checks, sourceDurationTicks = project.SourceDuration.Ticks, physicalScreenFiles = project.Media.Count,
                editorClips = editor.Timeline.Clips.Count, source = "own animated fixture; no desktop, microphone or camera", timestampUtc = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { success = false, checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine(error); return 1;
        }
    }

    private static string AssertClip(ProjectSnapshot snapshot) => snapshot.Segments.Count == 1
        ? snapshot.Segments[0].Id : throw new IOException("An automatic checkpoint created a timeline cut.");
}
