using System.Diagnostics;
using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;
using FrameTrail.Workspace;

internal static class VideoCodecMatrixValidation
{
    internal static async Task<int> RunAsync(string fixture, string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var results = new List<object>(); bool success = true;
        var copy = Path.Combine(directory, "matrix-source.frametrailproject");
        await using (var source = await ProjectWorkspace.OpenAsync(Path.GetFullPath(fixture)))
            await new EditorSession(source).CopySourcesAsync(copy, CancellationToken.None);
        await using var workspace = await ProjectWorkspace.OpenAsync(copy);
        var original = workspace.Snapshot!;
        var tiny = original with { Segments = new[] { new RecordingSegment { Id = "matrix", SourceStart = MediaTime.FromTicks(0), SourceDuration = MediaTime.FromTicks(5_000_000) } } };
        foreach (var codec in CaptureSession.GetVideoCodecs().Where(c => c.CanEncode)) {
            foreach (uint height in new uint[] { 1080, 1440, 2160 }) foreach (int fps in new[] { 30, 60 }) {
                var snapshot = tiny with { Export = tiny.Export with { Width = height * 16 / 9, Height = height, Fps = new Rational { Numerator = fps }, VideoCodec = codec.Codec } };
                var watch = Stopwatch.StartNew();
                try {
                    using var media = new MediaSession(snapshot, copy);
                    await Task.Run(() => media.Export(Path.Combine(directory, $"{codec.Codec}-{height}-{fps}.mp4")));
                    results.Add(new { codec = codec.Codec, height, fps, success = true, seconds = watch.Elapsed.TotalSeconds });
                } catch (Exception error) { success = false; results.Add(new { codec = codec.Codec, height, fps, success = false, error = error.ToString() }); }
            }
            try {
                var snapshot = original with { Export = original.Export with { Width = 3840, Height = 2160, Fps = new Rational { Numerator = 60 }, VideoCodec = codec.Codec } };
                string cancelled = Path.Combine(directory, codec.Codec + "-cancelled.mp4"), occupied = Path.Combine(directory, codec.Codec + "-occupied.mp4");
                await File.WriteAllTextAsync(cancelled, "keep-cancelled-target"); await File.WriteAllTextAsync(occupied, "keep-occupied-target");
                using (var media = new MediaSession(snapshot, copy)) {
                    var job = Task.Run(() => media.Export(cancelled));
                    var deadline = Stopwatch.StartNew();
                    while (!job.IsCompleted && media.Status.Stage < 2 && deadline.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(5);
                    media.Cancel();
                    try { await job; throw new IOException("In-progress export was not cancelled."); } catch (OperationCanceledException) { }
                }
                using (var held = new FileStream(occupied, FileMode.Open, FileAccess.Read, FileShare.None))
                using (var media = new MediaSession(tiny with { Export = tiny.Export with { VideoCodec = codec.Codec, Width = 640, Height = 360 } }, copy)) {
                    try { await Task.Run(() => media.Export(occupied)); throw new IOException("Occupied destination was overwritten."); }
                    catch (System.Runtime.InteropServices.COMException) { }
                }
                if (await File.ReadAllTextAsync(cancelled) != "keep-cancelled-target" || await File.ReadAllTextAsync(occupied) != "keep-occupied-target" || Directory.EnumerateFiles(directory, ".frametrail-*").Any())
                    throw new IOException("Failed/cancelled export damaged a target or retained temporary files.");
                results.Add(new { codec = codec.Codec, faultProtection = true, success = true });
            } catch (Exception error) { success = false; results.Add(new { codec = codec.Codec, faultProtection = true, success = false, error = error.ToString() }); }
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "export-matrix.json"), JsonSerializer.Serialize(new { success, durationTicks = 5_000_000, results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { success, cases = results.Count })); return success ? 0 : 1;
    }
}
