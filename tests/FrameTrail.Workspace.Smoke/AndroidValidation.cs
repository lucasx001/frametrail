using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;
using FrameTrail.Workspace;

internal static class AndroidValidation
{
    private static readonly AndroidSelection Selection = new(new("test-phone-private-serial", "device", "Protocol fixture", "1", "USB"));
    private static CaptureSource Source => new(CaptureSourceKind.Monitor, 0, "Android protocol fixture", 0, 0, 320, 240, 96, 0);
    private static async Task WaitAsync(Func<bool> ready, int seconds = 15) {
        var watch = Stopwatch.StartNew(); while (!ready()) { if (watch.Elapsed.TotalSeconds > seconds) throw new TimeoutException("Android validation condition timed out."); await Task.Delay(25); }
    }
    private static async Task ReadyAsync(AndroidCaptureSession capture, bool audio = true) {
        await WaitAsync(() => { var s = capture.Status; if (s.State == 3) throw new IOException(s.Message); return s.State == 2 && (!audio || s.AudioState is 1 or 2); });
    }
    internal static async Task<int> RunAsync(string fixture, string directory, string runtime, bool crash = false) {
        Directory.CreateDirectory(directory); var checks = new List<string>(); string? failure = null;
        try {
            var dependencies = new AndroidRuntime(runtime); await dependencies.ValidateAsync();
            var actualDevices = await dependencies.DevicesAsync(); checks.Add($"Pinned bundled ADB/server and typed USB discovery ran without PATH; USB device count {actualDevices.Count}.");
            string project = Path.Combine(directory, "phone.frametrailproject");
            await using (var phone = new FakePhone(fixture))
            using (var capture = new AndroidCaptureSession((uint)phone.Port, true))
            await using (var recorder = new RecordingCoordinator(new AndroidRecordingDriver(capture))) {
                await ReadyAsync(capture);
                var swapchain = capture.AcquireSwapChain(); if (swapchain == 0) throw new Exception("Native preview swap chain missing."); Marshal.Release(swapchain);
                capture.SavePreview(Path.Combine(directory, "phone-live.png"));
                await recorder.StartAsync(new(project, Source, Audio: Array.Empty<AudioSelection>(), RecordPointer: false, CheckpointSeconds: 5, Android: Selection));
                await Task.Delay(6400); await recorder.AddMarkerAsync("checkpoint");
                if (crash) { await File.WriteAllTextAsync(Path.Combine(directory, "crash-ready.txt"), "checkpoint finalized; process termination can now exclude pending segment"); await Task.Delay(Timeout.Infinite); }
                await recorder.PauseAsync(); await Task.Delay(500); await recorder.ResumeAsync(); await Task.Delay(1500); await recorder.AddMarkerAsync("resumed");
                var snapshot = await recorder.StopAsync() ?? throw new Exception("No Android project.");
                if (snapshot.SchemaVersion != 2 || snapshot.PrimarySource?.Kind != "androidUsb" || snapshot.Media.Any(m => m.Kind is MediaKind.PointerEvents or MediaKind.SystemAudio) || snapshot.Media.Count(m => m.Kind == MediaKind.Screen) < 3 || snapshot.Segments.Count != 2 || snapshot.Markers.Count != 2)
                    throw new Exception("Android project metadata, checkpoint, marker or pointer isolation failed.");
                if (snapshot.Media.Count(m => m.Kind == MediaKind.AndroidAudio) != snapshot.Media.Count(m => m.Kind == MediaKind.Screen)) throw new Exception("Phone audio was not saved independently in every segment.");
                checks.Add("Real H.264/PCM: native D3D preview, checkpoint, pause/resume, markers and schema-2 independent phone audio passed.");
            }
            // The phone and its sockets are gone before any editing/rendering/export.
            await using (var reopened = await ProjectWorkspace.OpenAsync(project)) {
                var snapshot = reopened.Snapshot! with { Revision = reopened.Snapshot!.Revision + 1, Export = new() { Width = 320, Height = 240 }, Canvas = reopened.Snapshot.Canvas with { Padding = 0 } };
                File.Copy(Path.Combine(fixture, "microphone.wav"), reopened.Resolve("media/synthetic-microphone.wav"));
                snapshot = snapshot with { Media = snapshot.Media.Append(new MediaAsset { Id = "synthetic-microphone", Kind = MediaKind.Microphone, Path = "media/synthetic-microphone.wav", Duration = snapshot.SourceDuration, StartOffset = MediaTime.FromTicks(0), SampleRate = 48000, Channels = 2 }).ToArray() };
                await reopened.SaveAsync(snapshot, reopened.SessionEpoch); var editor = new EditorSession(reopened);
                var first = editor.Snapshot.Segments[0];
                await editor.ExecuteAsync(new { op = "split", segmentId = first.Id, sourceTicks = first.SourceStart.Ticks + first.SourceDuration.Ticks / 2, newId = "phone-split" });
                await editor.ExecuteAsync(new { op = "rate", segmentId = "phone-split", numerator = 2, denominator = 1 });
                await editor.UndoAsync(); await editor.RedoAsync();
                await editor.UpsertEffectAsync(new() { Id = "phone-focus", Kind = "focus", SourceStart = MediaTime.FromTicks(0), SourceDuration = MediaTime.FromTicks(10_000_000), Parameters = JsonSerializer.SerializeToElement(new { x = 0.25, y = 0.25, width = 0.5, height = 0.5, dim = 0.25, blur = 0 }) });
                await editor.FlushAsync();
                using var media = new MediaSession(editor.Snapshot, project); media.Resize(320, 240); media.Render(5_000_000, png: Path.Combine(directory, "phone-preview.png")); media.PrepareAudio(Path.Combine(directory, "phone-mix.wav"));
                await editor.ExportAsync(Path.Combine(directory, "phone-export.mp4"), false, null, CancellationToken.None);
                await editor.ExportAsync(Path.Combine(directory, "phone-export.m4a"), true, null, CancellationToken.None);
                await editor.CopySourcesAsync(Path.Combine(directory, "portable.frametrailproject"), CancellationToken.None);
                checks.Add("Phone disconnected: reopen, split, rate, undo/redo, focus effect, preview PNG, phone + synthetic microphone mix, MP4/M4A and portable source copy passed.");
            }
            await using (var portable = await ProjectWorkspace.OpenAsync(Path.Combine(directory, "portable.frametrailproject"))) EngineSession.ValidateProject(portable.Snapshot!);
            await using (var phone = new FakePhone(Path.Combine(fixture, "portrait")))
            using (var capture = new AndroidCaptureSession((uint)phone.Port, true))
            await using (var recorder = new RecordingCoordinator(new AndroidRecordingDriver(capture))) {
                await ReadyAsync(capture); var status = capture.Status;
                if (status.Width != 360 || status.Height != 800) throw new Exception("Portrait source dimensions changed.");
                capture.SavePreview(Path.Combine(directory, "portrait-live.png"));
                await recorder.StartAsync(new(Path.Combine(directory, "portrait.frametrailproject"), Source, Audio: Array.Empty<AudioSelection>(), RecordPointer: false, Android: Selection));
                await Task.Delay(600); await recorder.StopAsync();
            }
            checks.Add("Portrait 360x800 source: native preview/decode and independent MP4 checkpoint passed.");
            foreach (var required in new[] { false, true }) {
                await using var phone = new FakePhone(fixture, disabledAudio: true); using var capture = new AndroidCaptureSession((uint)phone.Port, true); await ReadyAsync(capture);
                await using var recorder = new RecordingCoordinator(new AndroidRecordingDriver(capture));
                var path = Path.Combine(directory, required ? "required-audio.frametrailproject" : "silent.frametrailproject");
                var plan = new RecordingPlan(path, Source, Audio: Array.Empty<AudioSelection>(), RecordPointer: false, Android: Selection with { ContinueWithoutAudio = !required });
                if (required) { try { await recorder.StartAsync(plan); throw new Exception("Missing required audio was accepted."); } catch (IOException) { } }
                else { await recorder.StartAsync(plan); await Task.Delay(400); var p = await recorder.StopAsync(); if (p!.Media.Any(m => m.Kind == MediaKind.AndroidAudio)) throw new Exception("A fictitious phone audio track was created."); }
            }
            checks.Add("Unsupported audio: explicit silent-video fallback and required-audio rollback passed; no fabricated track.");
            foreach (var mode in new[] { "disconnect", "reconfigure", "oversized", "stall" }) {
                await using var phone = new FakePhone(fixture); using var capture = new AndroidCaptureSession((uint)phone.Port, true); await ReadyAsync(capture);
                var path = Path.Combine(directory, mode + ".frametrailproject");
                await using (var recorder = new RecordingCoordinator(new AndroidRecordingDriver(capture))) {
                    await recorder.StartAsync(new(path, Source, Audio: Array.Empty<AudioSelection>(), RecordPointer: false, CheckpointSeconds: 5, Android: Selection));
                    await Task.Delay(mode == "disconnect" ? 6200 : 400);
                    phone.Fault = mode;
                    await WaitAsync(() => recorder.State == RecordingWorkspaceState.RecordingComplete);
                    if (recorder.Workspace!.Snapshot is null) throw new Exception("No valid media retained after " + mode);
                }
                await using var recovered = (await ProjectWorkspace.RecoverAsync(path)).Workspace; EngineSession.ValidateProject(recovered.Snapshot!);
                checks.Add(mode + ": recording ended safely and finalized project remained recoverable.");
            }
            foreach (var required in new[] { false, true }) {
                await using var phone = new FakePhone(fixture); using var capture = new AndroidCaptureSession((uint)phone.Port, true); await ReadyAsync(capture);
                await using var recorder = new RecordingCoordinator(new AndroidRecordingDriver(capture));
                await recorder.StartAsync(new(Path.Combine(directory, required ? "audio-lost-required.frametrailproject" : "audio-lost-optional.frametrailproject"), Source, Audio: Array.Empty<AudioSelection>(), RecordPointer: false, Android: Selection with { ContinueWithoutAudio = !required }));
                await Task.Delay(500); phone.Fault = "audio";
                if (required) await WaitAsync(() => recorder.State == RecordingWorkspaceState.RecordingComplete);
                else { await Task.Delay(800); if (recorder.State != RecordingWorkspaceState.Recording) throw new Exception("Optional audio failure stopped video."); await recorder.StopAsync(); }
            }
            checks.Add("Audio lost mid-run: optional audio keeps video alive, required audio safely ends recording.");
            var pendingListener = new TcpListener(IPAddress.Loopback, 0); pendingListener.Start();
            try {
                var capture = new AndroidCaptureSession((uint)((IPEndPoint)pendingListener.LocalEndpoint).Port, false); await Task.Delay(100);
                var elapsed = Stopwatch.StartNew(); capture.Dispose(); if (elapsed.Elapsed > TimeSpan.FromSeconds(2)) throw new Exception("Cancellation left a blocking Android socket.");
            } finally { pendingListener.Stop(); }
            checks.Add("Cancellation during handshake releases native sockets in under two seconds.");
            await using (var phone = new FakePhone(fixture)) {
                for (int i = 0; i < 2; i++) { using var capture = new AndroidCaptureSession((uint)phone.Port, true); await ReadyAsync(capture); }
            }
            checks.Add("Sequential native sessions release sockets/decoder/writers; no callbacks are shared between handles.");
        } catch (Exception error) { failure = error.ToString(); Console.Error.WriteLine(error); }
        await File.WriteAllTextAsync(Path.Combine(directory, "android-validation.json"), JsonSerializer.Serialize(new { success = failure is null, checks, failure, hardware = false, timestampUtc = DateTimeOffset.UtcNow }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;
    }
    private sealed record Packet(int Position, int Size, bool Key);
    private sealed class FakePhone : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new(); private readonly Task producer;
        private readonly byte[] video, config; private readonly Packet[] packets; private readonly bool disabledAudio; private readonly uint width, height;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public string? Fault;
        private const long Origin = 5_000_000_000;
        internal FakePhone(string fixture, bool disabledAudio = false) {
            video = File.ReadAllBytes(Path.Combine(fixture, "phone.h264")); config = File.ReadAllBytes(Path.Combine(fixture, "config.bin"));
            packets = JsonSerializer.Deserialize<Packet[]>(File.ReadAllText(Path.Combine(fixture, "packets.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            using var size = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "size.json"))); width = size.RootElement.GetProperty("width").GetUInt32(); height = size.RootElement.GetProperty("height").GetUInt32();
            this.disabledAudio = disabledAudio; listener.Start(); producer = RunAsync();
        }
        private async Task RunAsync() {
            try { while (!stop.IsCancellationRequested) {
                using var v = await listener.AcceptTcpClientAsync(stop.Token); var vs = v.GetStream(); await vs.WriteAsync(new byte[] { 0 }, stop.Token);
                using var a = await listener.AcceptTcpClientAsync(stop.Token); var aus = a.GetStream();
                byte[] header = new byte[12]; BinaryPrimitives.WriteUInt32BigEndian(header, 0x68323634); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), width); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), height); await vs.WriteAsync(header, stop.Token);
                var codec = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(codec, disabledAudio ? 0u : 0x00726177u); await aus.WriteAsync(codec, stop.Token);
                await SendAsync(vs, config, 1UL << 63, stop.Token);
                using var run = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                var audioTask = disabledAudio ? Task.CompletedTask : AudioAsync(aus, run.Token);
                try {
                    var time = Stopwatch.StartNew();
                    for (int i = 0; !run.IsCancellationRequested; i++) {
                        var fault = Fault;
                        if (fault == "stall") await Task.Delay(Timeout.Infinite, run.Token);
                        if (fault == "disconnect") { a.Close(); break; }
                        if (fault == "reconfigure") { await SendAsync(vs, config.Concat(new byte[] { 0 }).ToArray(), 1UL << 63, run.Token); break; }
                        if (fault == "oversized") { var invalid = new byte[12]; BinaryPrimitives.WriteUInt32BigEndian(invalid.AsSpan(8), uint.MaxValue); await vs.WriteAsync(invalid, run.Token); break; }
                        var p = packets[i % packets.Length];
                        await SendAsync(vs, video.AsMemory(p.Position, p.Size), (ulong)(Origin + i * 1_000_000L / 30) | (p.Key ? 1UL << 62 : 0), run.Token);
                        var delay = (i + 1) * 1000.0 / 30 - time.Elapsed.TotalMilliseconds + (i % 2 == 0 ? 8 : 0);
                        if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), run.Token);
                    }
                } catch (Exception e) when (e is IOException or SocketException or OperationCanceledException) { }
                finally { run.Cancel(); try { await audioTask; } catch (Exception e) when (e is IOException or SocketException or OperationCanceledException) { } }
            } } catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
        private async Task AudioAsync(NetworkStream stream, CancellationToken token) {
            var time = Stopwatch.StartNew();
            for (long frame = 0; ; frame += 1024) {
                if (Fault == "audio") { stream.Close(); return; }
                var data = new byte[4096];
                for (int i = 0; i < 1024; i++) { short sample = (short)(4000 * Math.Sin(2 * Math.PI * 440 * (frame + i) / 48000)); BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 4), sample); BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 4 + 2), sample); }
                await SendAsync(stream, data, (ulong)(Origin - 100_000 + frame * 1_000_000 / 48000), token);
                var delay = (frame + 1024) * 1000.0 / 48000 - time.Elapsed.TotalMilliseconds; if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), token);
            }
        }
        private static async Task SendAsync(NetworkStream stream, ReadOnlyMemory<byte> payload, ulong pts, CancellationToken token) { var header = new byte[12]; BinaryPrimitives.WriteUInt64BigEndian(header, pts); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)payload.Length); await stream.WriteAsync(header, token); await stream.WriteAsync(payload, token); }
        public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await producer; stop.Dispose(); }
    }
}
