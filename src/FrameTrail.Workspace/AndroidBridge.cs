using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FrameTrail.Interop;

namespace FrameTrail.Workspace;

public sealed record AndroidDevice(string Serial, string State, string Model, string TransportId, string Usb)
{
    public bool Ready => State == "device";
    public string DisplayName => L.T("S21CA73AA7256", Model, State switch { "device" => L.T("S5BE0323E8ADC"), "unauthorized" => L.T("SBD29B6148512"), "offline" => L.T("S6E1E714DF035"), _ => State }, Identity);
    public string Identity => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serial)))[..8];
    public override string ToString() => DisplayName;
}
public sealed record AndroidSelection(AndroidDevice Device, bool InternalAudio = true, bool ContinueWithoutAudio = true);

public sealed class AndroidRuntime
{
    public const string ServerVersion = "3.3.4";
    public const string AdbVersion = "36.0.0-13206524";
    public string DirectoryPath { get; }
    public string AdbPath => Path.Combine(DirectoryPath, "adb.exe");
    public string ServerPath => Path.Combine(DirectoryPath, "scrcpy-server");
    public AndroidRuntime(string? directory = null) => DirectoryPath = Path.GetFullPath(directory ?? Path.Combine(AppContext.BaseDirectory, "android-runtime"));
    public async Task ValidateAsync(CancellationToken token = default)
    {
        var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(Path.Combine(DirectoryPath, "sha256.json"), token)) ?? throw new IOException("Android runtime manifest missing.");
        foreach (var name in new[] { "adb.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll", "scrcpy-server", "ADB-NOTICE.txt", "scrcpy-LICENSE.txt" }) {
            if (!manifest.TryGetValue(name, out var expected)) throw new IOException("Android runtime manifest is incomplete. Run scripts/setup-android.cmd.");
            await using var file = File.OpenRead(Path.Combine(DirectoryPath, name));
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) throw new IOException("Android runtime checksum mismatch: " + name);
        }
        // The trust anchor is code, not a mutable manifest beside the executable.
        var pins = new Dictionary<string, string> {
            ["adb.exe"]="1E1C2280B90B3F01AD84CD8DF4858B1B1995012814F3CA8893BCC3BA3848EDEC",
            ["AdbWinApi.dll"]="9A56E72FE1372CB722A80C00B79DCECF2B37165884E470ED05F00C668C0043B0",
            ["AdbWinUsbApi.dll"]="5E77CCB2F25CD3A97553745ADF1CD28A5FE8137CF64613B7FEF9C6F92FF91F37",
            ["scrcpy-server"]="8588238C9A5A00AA542906B6EC7E6D5541D9FFB9B5D0F6E1BC0E365E2303079E"
        };
        foreach (var pin in pins) if (!string.Equals(manifest[pin.Key], pin.Value, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsupported Android runtime version: " + pin.Key);
        var version = await RunAsync(new[] { "version" }, token);
        if (!version.Contains("Version " + AdbVersion, StringComparison.Ordinal)) throw new IOException("Unsupported ADB version.");
    }
    public static IReadOnlyList<AndroidDevice> ParseUsbDevices(byte[] message)
    {
        var devices = new List<AndroidDevice>();
        if (message.Length > 65535) throw new IOException("ADB device list exceeds limit.");
        var outer = new ProtoReader(message);
        while (!outer.End) {
            var tag = outer.Varint();
            if (tag != 10) { outer.Skip(tag); continue; }
            var device = new ProtoReader(outer.Bytes());
            string serial = "", model = "Android", bus = "USB"; ulong state = 0, type = 0, transport = 0;
            while (!device.End) {
                var field = device.Varint();
                switch (field) {
                    case 10: serial = device.Text(); break;
                    case 16: state = device.Varint(); break;
                    case 26: bus = device.Text(); break;
                    case 42: model = device.Text(); break;
                    case 56: type = device.Varint(); break;
                    case 80: transport = device.Varint(); break;
                    default: device.Skip(field); break;
                }
            }
            // ADB's explicit transport type also works with Windows' null USB devpath.
            if (type != 1 || transport == 0 || serial.Length == 0 || serial.StartsWith("emulator-", StringComparison.Ordinal)) continue;
            devices.Add(new(serial, state switch { 8 => "device", 3 => "unauthorized", 6 => "offline", 4 => "no-permission", _ => "not-ready" }, model.Replace('_', ' '), transport.ToString(CultureInfo.InvariantCulture), bus));
        }
        return devices;
    }
    private sealed class ProtoReader(byte[] bytes)
    {
        private int offset;
        public bool End => offset == bytes.Length;
        public ulong Varint() { ulong value = 0; for (int i = 0; i < 10; i++) { if (offset == bytes.Length) throw new IOException("Truncated ADB protobuf."); var b = bytes[offset++]; if (i == 9 && b > 1) throw new IOException("Invalid ADB varint."); value |= (ulong)(b & 127) << (i * 7); if (b < 128) return value; } throw new IOException("Oversized ADB varint."); }
        public byte[] Bytes() { var count = Varint(); if (count > (ulong)(bytes.Length - offset)) throw new IOException("Truncated ADB protobuf field."); var result = bytes.AsSpan(offset, (int)count).ToArray(); offset += (int)count; return result; }
        public string Text() => new UTF8Encoding(false, true).GetString(Bytes());
        public void Skip(ulong tag) { if (tag >> 3 == 0) throw new IOException("Invalid ADB protobuf field number."); switch (tag & 7) { case 0: Varint(); return; case 2: Bytes(); return; case 1: Advance(8); return; case 5: Advance(4); return; default: throw new IOException("Unsupported ADB protobuf wire type."); } }
        private void Advance(int count) { if (count > bytes.Length - offset) throw new IOException("Truncated ADB protobuf field."); offset += count; }
    }
    public async Task<IReadOnlyList<AndroidDevice>> DevicesAsync(CancellationToken token = default)
    {
        await ValidateAsync(token); await RunAsync(new[] { "start-server" }, token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var client = new TcpClient(); await client.ConnectAsync("127.0.0.1", 5037, timeout.Token);
        var stream = client.GetStream(); var request = "host:track-devices-proto-binary";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.Length.ToString("x4", CultureInfo.InvariantCulture) + request), timeout.Token);
        var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, timeout.Token);
        if (Encoding.ASCII.GetString(prefix) != "OKAY") throw new IOException("ADB server does not support typed USB discovery. Update the shared platform-tools to version 36 or newer.");
        await stream.ReadExactlyAsync(prefix, timeout.Token);
        if (!int.TryParse(Encoding.ASCII.GetString(prefix), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var length) || length is < 0 or > 65535) throw new IOException("Invalid ADB response length.");
        var body = new byte[length]; await stream.ReadExactlyAsync(body, timeout.Token);
        return ParseUsbDevices(body);
    }
    internal async Task<string> RunAsync(IEnumerable<string> args, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var process = Start(args);
        try {
            var output = ReadBoundedAsync(process.StandardOutput, timeout.Token); var error = ReadBoundedAsync(process.StandardError, timeout.Token);
            await process.WaitForExitAsync(timeout.Token); var value = await output; await error;
            if (process.ExitCode != 0) throw new IOException("ADB command failed. Check USB connection, phone authorization and OEM driver.");
            if (value.Length > 1_048_576) throw new IOException("ADB output exceeds limit.");
            return value;
        } catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token) {
        var result = new StringBuilder(); var buffer = new char[2048];
        while (true) { var count = await reader.ReadAsync(buffer, token); if (count == 0) return result.ToString(); if (result.Length + count > 1_048_576) throw new IOException("ADB output exceeds limit."); result.Append(buffer, 0, count); }
    }
    internal Process Start(IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(AdbPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = DirectoryPath };
        foreach (var key in new[] { "ADB_SERVER_SOCKET", "ANDROID_ADB_SERVER_ADDRESS", "ANDROID_ADB_SERVER_PORT", "ANDROID_SERIAL" }) info.Environment.Remove(key);
        info.ArgumentList.Add("-L"); info.ArgumentList.Add("tcp:127.0.0.1:5037");
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException("Cannot start bundled ADB.");
    }
}

/// <summary>Owns only this scid, remote jar, adb shell and forward. Never kills the shared adb server.</summary>
public sealed class AndroidConnection : IAsyncDisposable
{
    private readonly AndroidRuntime runtime;
    private readonly AndroidSelection selection;
    private readonly string scid = RandomNumberGenerator.GetInt32(1, int.MaxValue).ToString("x8", CultureInfo.InvariantCulture);
    private string Jar => "/data/local/tmp/frametrail-" + scid + ".jar";
    private string[] Target => new[] { "-t", selection.Device.TransportId };
    private Process? server;
    private Task? drainOutput, drainError;
    private int port;
    private int disposed;
    private bool uploadAttempted;
    public AndroidCaptureSession Capture { get; private set; } = null!;
    public int ApiLevel { get; private set; }
    public bool InternalAudioAvailable => Capture.Status.AudioState == 1;
    private AndroidConnection(AndroidRuntime runtime, AndroidSelection selection) { this.runtime = runtime; this.selection = selection; }
    public static async Task<AndroidConnection> ConnectAsync(AndroidRuntime runtime, AndroidSelection selection, uint fps, CancellationToken token)
    {
        var result = new AndroidConnection(runtime, selection);
        try { await result.StartAsync(fps, token); return result; } catch { await result.DisposeAsync(); throw; }
    }
    private async Task StartAsync(uint fps, CancellationToken token)
    {
        var devices = await runtime.DevicesAsync(token);
        var actual = devices.SingleOrDefault(d => d.Serial == selection.Device.Serial && d.TransportId == selection.Device.TransportId && d.Usb == selection.Device.Usb);
        if (actual is null || !actual.Ready) throw new IOException(L.T("S055724288586"));
        var api = (await runtime.RunAsync(Target.Concat(new[] { "shell", "getprop", "ro.build.version.sdk" }), token)).Trim();
        if (!int.TryParse(api, out var level) || level < 21) throw new IOException(L.T("SFD10917D8983"));
        ApiLevel = level;
        if (selection.InternalAudio && level < 30 && !selection.ContinueWithoutAudio) throw new IOException(L.T("SFFC9010C9080"));
        uploadAttempted = true;
        await runtime.RunAsync(Target.Concat(new[] { "push", runtime.ServerPath, Jar }), token);
        var forwarded = (await runtime.RunAsync(Target.Concat(new[] { "forward", "tcp:0", "localabstract:scrcpy_" + scid }), token)).Trim();
        if (!int.TryParse(forwarded, out port) || port is < 1 or > 65535) throw new IOException("Cannot allocate this recording's USB tunnel.");
        var audio = selection.InternalAudio && level >= 30;
        var args = Target.Concat(new[] { "shell", "CLASSPATH=" + Jar, "app_process", "/", "com.genymobile.scrcpy.Server", AndroidRuntime.ServerVersion,
            "scid=" + scid, "tunnel_forward=true", "control=false", "video=true", "audio=" + audio.ToString().ToLowerInvariant(), "audio_codec=raw", "audio_source=output",
            "video_codec=h264", "max_size=1920", "max_fps=" + fps, "video_bit_rate=8000000", "capture_orientation=@", "downsize_on_error=false",
            "video_codec_options=i-frame-interval=1,max-bframes=0,repeat-previous-frame-after:long=100000", "send_device_meta=false", "send_dummy_byte=true", "send_codec_meta=true", "send_frame_meta=true", "log_level=warn" });
        server = runtime.Start(args);
        // Output may contain serials or app information; drain it without writing ordinary logs.
        drainOutput = DrainAsync(server.StandardOutput); drainError = DrainAsync(server.StandardError);
        Capture = new((uint)port, audio, fps);
        var started = Stopwatch.GetTimestamp();
        while (true) {
            token.ThrowIfCancellationRequested(); var status = Capture.Status;
            if (status.State == 3) throw new IOException(status.Message);
            if (server.HasExited) throw new IOException(L.T("SDACAE6A024B0"));
            if (status.State == 2 && (!audio || status.AudioState is 1 or 2 or 4)) {
                if (audio && status.AudioState != 1 && !selection.ContinueWithoutAudio) throw new IOException(L.T("SD1FAF236D145"));
                break;
            }
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(20)) throw new TimeoutException(L.T("S889678111935"));
            await Task.Delay(30, token);
        }
    }
    private static async Task DrainAsync(StreamReader reader) { var chars = new char[2048]; while (await reader.ReadAsync(chars) != 0) { } }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Exception? failure = null;
        try { if (Capture is not null) await Task.Run(Capture.Dispose); } catch (Exception error) { failure = error; } finally { Capture = null!; }
        try {
            if (server is not null) {
                if (!server.HasExited) server.Kill(entireProcessTree: true);
                await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                if (drainOutput is not null) await drainOutput.WaitAsync(TimeSpan.FromSeconds(2));
                if (drainError is not null) await drainError.WaitAsync(TimeSpan.FromSeconds(2));
            }
        } catch (Exception error) { failure ??= error; }
        finally { server?.Dispose(); server = null; }
        // A disconnected phone may reject cleanup. No global reset is attempted.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        if (port != 0) { try { await runtime.RunAsync(Target.Concat(new[] { "forward", "--remove", "tcp:" + port }), cleanup.Token); } catch { } port = 0; }
        if (uploadAttempted) { try { await runtime.RunAsync(Target.Concat(new[] { "shell", "rm", "-f", Jar }), cleanup.Token); } catch { } }
        if (failure is not null) throw new IOException("Android session cleanup failed after attempting all owned resources.", failure);
    }
}
