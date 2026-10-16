using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using FrameTrail.Interop;
using FrameTrail.Workspace;
using Xunit;

namespace FrameTrail.Workspace.Tests;

public sealed class AndroidDiscoveryTests
{
    private static byte[] Varint(ulong value) { var bytes = new List<byte>(); do { byte b = (byte)(value & 127); value >>= 7; bytes.Add(value != 0 ? (byte)(b | 128) : b); } while (value != 0); return bytes.ToArray(); }
    private static byte[] Number(byte tag, ulong value) => new[] { tag }.Concat(Varint(value)).ToArray();
    private static byte[] Text(byte tag, string value) { var bytes = Encoding.UTF8.GetBytes(value); return new[] { tag }.Concat(Varint((ulong)bytes.Length)).Concat(bytes).ToArray(); }
    private static byte[] Device(string serial, ulong type, ulong state, ulong transport, string? bus = null) {
        var value = Text(10, serial).Concat(Number(16, state)).Concat(Text(42, "Pixel_8")).Concat(Number(56, type)).Concat(Number(80, transport)).ToArray();
        if (bus is not null) value = value.Concat(Text(26, bus)).ToArray();
        return new byte[] { 10 }.Concat(Varint((ulong)value.Length)).Concat(value).ToArray();
    }
    [Fact]
    public void UsbTypeIsAuthoritativeEvenWithoutWindowsBusPath() {
        var data = Device("private-one", 1, 8, 1).Concat(Device("private-two", 1, 3, 2)).Concat(Device("private-three", 1, 6, 3))
            .Concat(Device("192.168.0.1:5555", 2, 8, 4)).Concat(Device("emulator-5554", 2, 8, 5)).Concat(Device("mdns-service", 2, 8, 6)).ToArray();
        var devices = AndroidRuntime.ParseUsbDevices(data);
        Assert.Equal(3, devices.Count); Assert.True(devices[0].Ready); Assert.False(devices[1].Ready); Assert.Equal("offline", devices[2].State);
        Assert.Equal("2", devices[1].TransportId); Assert.DoesNotContain("private", devices[0].DisplayName, StringComparison.Ordinal);
        Assert.NotEqual(devices[0].Identity, devices[1].Identity);
    }
    [Fact]
    public void MultipleUsbPhonesRetainDistinctTransportIds() {
        var devices = AndroidRuntime.ParseUsbDevices(Device("phone-one", 1, 8, 20, "usb:1-1").Concat(Device("phone-two", 1, 8, 21, "usb:1-2")).ToArray());
        Assert.Equal(new[] { "20", "21" }, devices.Select(d => d.TransportId));
    }
    [Theory]
    [InlineData(new byte[] { 10, 100, 1 })]
    [InlineData(new byte[] { 10, 1, 16 })]
    [InlineData(new byte[] { 10, 2, 0, 1 })]
    [InlineData(new byte[] { 128 })]
    public void TruncatedOrMalformedDeviceListsAreRejected(byte[] bytes) => Assert.Throws<IOException>(() => AndroidRuntime.ParseUsbDevices(bytes));
    [Fact]
    public void InvalidAndUnknownTransportsCannotBeSelected() {
        var devices = AndroidRuntime.ParseUsbDevices(Device("a", 0, 8, 2).Concat(Device("b", 1, 8, 0)).Concat(Device("emulator-5", 1, 8, 3)).ToArray());
        Assert.Empty(devices);
    }
    [Fact]
    public async Task ChangingManifestAndExecutableTogetherCannotBypassVersionPins() {
        var directory = Path.Combine(Path.GetTempPath(), "FrameTrail-Android-pin-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try {
            var manifest = new Dictionary<string, string>();
            foreach (var name in new[] { "adb.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll", "scrcpy-server", "ADB-NOTICE.txt", "scrcpy-LICENSE.txt" }) {
                var bytes = Encoding.UTF8.GetBytes("tampered runtime fixture"); await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes); manifest[name] = Convert.ToHexString(SHA256.HashData(bytes));
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "sha256.json"), JsonSerializer.Serialize(manifest));
            var error = await Assert.ThrowsAsync<IOException>(() => new AndroidRuntime(directory).ValidateAsync());
            Assert.Contains("Unsupported Android runtime", error.Message, StringComparison.Ordinal);
        } finally { Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AndroidPlanRejectsDesktopPointerAndLoopbackBeforeCreatingAnEmptyProject(bool pointer, bool loopback) {
        var path = Path.Combine(Path.GetTempPath(), "FrameTrail-invalid-phone-" + Guid.NewGuid().ToString("N"));
        await using var coordinator = new RecordingCoordinator(new NeverDriver());
        var source = new CaptureSource(CaptureSourceKind.Monitor, 0, "phone", 0, 0, 320, 240, 96, 0);
        var audio = loopback ? new[] { new AudioSelection(AudioCaptureKind.System) } : Array.Empty<AudioSelection>();
        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.StartAsync(new(path, source, Audio: audio, RecordPointer: pointer, Android: new(new("fixture", "device", "Phone", "1", "USB")))));
        Assert.False(Directory.Exists(path));
    }
    private sealed class NeverDriver : IRecordingDriver { public Task<IRecordingSegment> StartAsync(RecordingPlan plan, SegmentPaths paths, CancellationToken cancellationToken) => throw new Exception("Invalid Android plan reached the hardware driver."); }
}
