using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;
using FrameTrail.Workspace;

internal static class VideoCodecValidation
{
    internal static async Task<int> RunAsync(string directory, string? audioFixture)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var results = new List<object>(); bool success = true;
        var capabilities = CaptureSession.GetVideoCodecs();
        var source = CaptureSession.EnumerateSources().First(s => s.Kind == CaptureSourceKind.Monitor && s.IsSdr);
        // AV1 export does not require an AV1 decoder when the source is H.264.
        // Cover that path separately on systems where the extension is absent.
        var exportOnly = capabilities.Where(c => c.CanEncode && !c.CanRecord).ToArray();
        foreach (var codec in capabilities) {
            if (!codec.CanRecord) { results.Add(new { codec = codec.Codec, skipped = codec.Availability }); continue; }
            try {
                var projectPath = Path.Combine(directory, codec.Codec + ".frametrailproject");
                await using (var recorder = new RecordingCoordinator()) {
                    await recorder.StartAsync(new(projectPath, source, new CaptureRegion(0, 0, 640, 360), AllowSoftware: true, RecordPointer: false, VideoCodec: codec.Codec));
                    await Task.Delay(1400); await recorder.PauseAsync(); await recorder.ResumeAsync(); await Task.Delay(1000);
                    var saved = await recorder.StopAsync() ?? throw new IOException("Recording has no project.");
                    if (saved.Export.VideoCodec != codec.Codec || saved.Segments.Count != 2) throw new IOException("Codec or pause/resume segments were not persisted.");
                }
                await using var project = await ProjectWorkspace.OpenAsync(projectPath);
                if (audioFixture is not null) {
                    File.Copy(Path.GetFullPath(audioFixture), project.Resolve("media/codec-system.wav"));
                    var saved = project.Snapshot!;
                    await project.SaveAsync(saved with { Revision = saved.Revision + 1, Media = saved.Media.Append(new MediaAsset {
                        Id = "codec-audio", Kind = MediaKind.SystemAudio, Path = "media/codec-system.wav", Duration = saved.SourceDuration,
                        SampleRate = 48000, Channels = 2
                    }).ToArray() }, project.SessionEpoch);
                }
                var editor = new EditorSession(project);
                await editor.ExecuteAsync(new { op = "settings", export = editor.Snapshot.Export with { Width = 640, Height = 360 } });
                using (var media = new MediaSession(editor.Snapshot, projectPath)) {
                    media.Render(0, png: Path.Combine(directory, codec.Codec + "-preview.png"));
                    media.Render(editor.Timeline.Duration / 2);
                }
                await editor.ExportAsync(Path.Combine(directory, codec.Codec + "-export.mp4"), false, null, CancellationToken.None);
                await editor.ExecuteAsync(new { op = "settings", export = editor.Snapshot.Export with { Width = 1920, Height = 1080, Fps = new Rational { Numerator = 60 } } });
                await editor.ExportAsync(Path.Combine(directory, codec.Codec + "-export-60.mp4"), false, null, CancellationToken.None);
                results.Add(new { codec = codec.Codec, success = true, duration = editor.Timeline.Duration });
            } catch (Exception error) { success = false; results.Add(new { codec = codec.Codec, success = false, error = error.ToString() }); }
        }
        foreach (var codec in exportOnly) {
            try {
                await using var project = await ProjectWorkspace.OpenAsync(Path.Combine(directory, "h264.frametrailproject"));
                var editor = new EditorSession(project);
                await editor.ExecuteAsync(new { op = "settings", export = editor.Snapshot.Export with { Width = 640, Height = 360, VideoCodec = codec.Codec } });
                await editor.ExportAsync(Path.Combine(directory, codec.Codec + "-export.mp4"), false, null, CancellationToken.None);
                results.Add(new { codec = codec.Codec, exportOnly = true, success = true });
            } catch (Exception error) { success = false; results.Add(new { codec = codec.Codec, exportOnly = true, success = false, error = error.ToString() }); }
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "codec-validation.json"), JsonSerializer.Serialize(new { success, capabilities, results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { success, results })); return success ? 0 : 1;
    }
}
