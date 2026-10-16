using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;
using FrameTrail.Workspace;

if (args.Length >= 4 && args[0] == "--m5") return await AndroidValidation.RunAsync(args[1], args[2], args[3], args.Contains("--crash"));

if (args.Length >= 3 && args[0] == "--m2") return await M2Validation.RunAsync(args[1], args[2], args.Contains("--matrix"));
if (args.Length == 3 && args[0] == "--m3") return await M3Validation.RunAsync(args[1], args[2]);
if (args.Length == 2 && args[0] == "--camera-continuity") return await CameraContinuityValidation.RunAsync(args[1]);

if (args.Length < 1) { Console.Error.WriteLine("Usage: FrameTrail.Workspace.Smoke <new.frametrailproject> [--camera] [--checkpoint] [--fixture] [--fps60] | --camera-continuity <new-directory> | <existing.frametrailproject> --recover"); return 2; }
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
    bool fixture = args.Contains("--fixture");
    var source = CaptureSession.EnumerateSources().First(s => s.IsSdr && (fixture
        ? s.Kind == CaptureSourceKind.Window && s.Name.Contains("1080p capture benchmark")
        : s.Kind == CaptureSourceKind.Monitor));
    CaptureRegion? region = fixture ? null : new CaptureRegion(0, 0, Math.Min(640, source.Width), Math.Min(360, source.Height));
    var audio = new List<AudioSelection>();
    var system = AudioCaptureSession.EnumerateDevices(AudioCaptureKind.System).FirstOrDefault(d => d.IsDefault);
    if (!fixture && system is not null) audio.Add(new(AudioCaptureKind.System, system.Id));
    var microphone = AudioCaptureSession.EnumerateDevices(AudioCaptureKind.Microphone).FirstOrDefault(d => d.IsDefault);
    if (!fixture && microphone is not null) audio.Add(new(AudioCaptureKind.Microphone, microphone.Id));
    CameraDevice? camera = null; CameraFormat? format = null;
    if (args.Contains("--camera")) { camera = CameraCaptureSession.EnumerateDevices().First(); format = CameraCaptureSession.EnumerateFormats(camera.Id)
        .Where(f => f.Width <= 1280 && f.Height <= 720 && (double)f.FpsNumerator / f.FpsDenominator >= 29).OrderByDescending(f => f.Width * f.Height).First(); }
    long sourceDuration; int mediaCount, markerCount, segmentCount; string[] warnings;
    var seen = new List<string>();
    await using (var coordinator = new RecordingCoordinator())
    {
        coordinator.StatusChanged += (_, status) => { foreach (var warning in status.Warnings) if (!seen.Contains(warning)) seen.Add(warning); Console.WriteLine($"{status.State}: {status.Duration.TotalSeconds:F2}s"); };
        await coordinator.StartAsync(new(path, source, region, Fps: args.Contains("--fps60") ? 60u : 30u, AllowSoftware: true, Audio: audio, Camera: camera, CameraFormat: format, RecordPointer: !fixture, CheckpointSeconds: args.Contains("--checkpoint") ? 5 : 60));
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
