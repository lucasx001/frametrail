using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;
using FrameTrail.Workspace;

if (args.Length >= 3 && args[0] == "--m2") return await M2Validation.RunAsync(args[1], args[2], args.Contains("--matrix"));

if (args.Length < 1) { Console.Error.WriteLine("Usage: FrameTrail.Workspace.Smoke <new.frametrailproject> [--camera] [--checkpoint] | <existing.frametrailproject> --recover"); return 2; }
var path = Path.GetFullPath(args[0]); string output = path + ".result.json";
try
{
    if (args.Contains("--recover"))
    {
        output = path + ".recovery.result.json";
        var scanned = await ProjectWorkspace.ScanRecoveryAsync(Path.GetDirectoryName(path)!);
        string[] recoveryWarnings; long recoveryDuration; int recoveredMedia, recoveredSegments;
        await using (var recovered = (await ProjectWorkspace.RecoverAsync(path)).Workspace)
        {
            EngineSession.ValidateProject(recovered.Snapshot!);
            recoveryDuration = recovered.Snapshot!.SourceDuration.Ticks; recoveredMedia = recovered.Snapshot.Media.Count; recoveredSegments = recovered.Snapshot.Segments.Count;
        }
        var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(path, "recovery", "manifest.json")));
        recoveryWarnings = manifest.RootElement.GetProperty("warnings").EnumerateArray().Select(item => item.GetString()!).ToArray();
        await using var reopenedRecovery = await ProjectWorkspace.OpenAsync(path);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { success = true, recovered = true, reopened = true, path, sourceDurationTicks = recoveryDuration,
            mediaCount = recoveredMedia, segmentCount = recoveredSegments, scannedCandidates = scanned.Count, warnings = recoveryWarnings, timestampUtc = DateTimeOffset.UtcNow }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(output); return 0;
    }
    var source = CaptureSession.EnumerateSources().First(s => s.Kind == CaptureSourceKind.Monitor && s.IsSdr);
    var region = new CaptureRegion(0, 0, Math.Min(640, source.Width), Math.Min(360, source.Height));
    var audio = new List<AudioSelection>();
    var system = AudioCaptureSession.EnumerateDevices(AudioCaptureKind.System).FirstOrDefault(d => d.IsDefault);
    if (system is not null) audio.Add(new(AudioCaptureKind.System, system.Id));
    var microphone = AudioCaptureSession.EnumerateDevices(AudioCaptureKind.Microphone).FirstOrDefault(d => d.IsDefault);
    if (microphone is not null) audio.Add(new(AudioCaptureKind.Microphone, microphone.Id));
    CameraDevice? camera = null; CameraFormat? format = null;
    if (args.Contains("--camera")) { camera = CameraCaptureSession.EnumerateDevices().First(); format = CameraCaptureSession.EnumerateFormats(camera.Id).First(); }
    long sourceDuration; int mediaCount, markerCount, segmentCount; string[] warnings;
    var seen = new List<string>();
    await using (var coordinator = new RecordingCoordinator())
    {
        coordinator.StatusChanged += (_, status) => { foreach (var warning in status.Warnings) if (!seen.Contains(warning)) seen.Add(warning); Console.WriteLine($"{status.State}: {status.Duration.TotalSeconds:F2}s"); };
        await coordinator.StartAsync(new(path, source, region, AllowSoftware: true, Audio: audio, Camera: camera, CameraFormat: format, CheckpointSeconds: args.Contains("--checkpoint") ? 5 : 60));
        await Task.Delay(args.Contains("--checkpoint") ? 7000 : 3000); await coordinator.AddMarkerAsync("before pause"); await coordinator.PauseAsync();
        await Task.Delay(1000); await coordinator.ResumeAsync(); await Task.Delay(3000); await coordinator.AddMarkerAsync("after resume");
        var snapshot = await coordinator.StopAsync() ?? throw new IOException("No project snapshot produced.");
        sourceDuration = snapshot.SourceDuration.Ticks; mediaCount = snapshot.Media.Count; markerCount = snapshot.Markers.Count; segmentCount = snapshot.Segments.Count; warnings = seen.ToArray();
    }
    await using var reopened = await ProjectWorkspace.OpenAsync(path); EngineSession.ValidateProject(reopened.Snapshot!);
    if (reopened.Snapshot!.Markers.Count != 2 || segmentCount < 2 || sourceDuration < 50_000_000) throw new IOException("Recorded project did not satisfy multi-segment/marker/duration requirements.");
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { success = true, path, sourceDurationTicks = sourceDuration, mediaCount, markerCount, segmentCount,
        reopened = true, source = source.Name, audio = audio.Select(a => a.Kind.ToString()), camera = camera?.Name, warnings, timestampUtc = DateTimeOffset.UtcNow }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(output); return 0;
}
catch (Exception error)
{
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { success = false, error = error.ToString(), timestampUtc = DateTimeOffset.UtcNow }, new JsonSerializerOptions { WriteIndented = true }));
    Console.Error.WriteLine(error); return 1;
}
