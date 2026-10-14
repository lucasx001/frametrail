using System.Diagnostics;
using System.Runtime.InteropServices;
using FrameTrail.Interop;
using Xunit;

namespace FrameTrail.Audio.Tests;

[Trait("Category", "Hardware")]
public sealed class AudioInteropTests
{
    [Fact]
    public void Windows11SupportsProcessLoopbackAndRejectsInvalidWindow()
    {
        Assert.True(AudioCaptureSession.IsProcessLoopbackSupported);
        Assert.Throws<COMException>(() => AudioCaptureSession.ProcessIdFromWindow(0));
        Assert.Throws<COMException>(() => AudioCaptureSession.EnumerateDevices(AudioCaptureKind.ProcessTree));
    }

    [Fact]
    public void ActiveEndpointEnumerationMarshalsStableIdsAndNames()
    {
        foreach (var kind in new[] { AudioCaptureKind.System, AudioCaptureKind.Microphone })
        {
            var devices = AudioCaptureSession.EnumerateDevices(kind);
            Assert.All(devices, d => { Assert.Equal(kind, d.Kind); Assert.False(string.IsNullOrWhiteSpace(d.Id)); Assert.False(string.IsNullOrWhiteSpace(d.Name)); });
            Assert.Equal(devices.Count, devices.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void SilentProcessFinalizesWithExactPausedDurationAndSafeLifetime()
    {
        string directory = Path.Combine(Path.GetTempPath(), "FrameTrail.Audio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "silent.wav");
        try
        {
            using var session = AudioCaptureSession.Start(new(AudioCaptureKind.ProcessTree, path, ProcessId: (uint)Environment.ProcessId));
            long epoch = Stopwatch.GetTimestamp(); session.SetEpoch(epoch, Stopwatch.Frequency);
            Thread.Sleep(90); long pause = Stopwatch.GetTimestamp(); session.PauseAt(pause);
            Thread.Sleep(60); long resume = Stopwatch.GetTimestamp(); session.ResumeAt(resume);
            Thread.Sleep(90); long stop = Stopwatch.GetTimestamp(); session.StopAt(stop);
            var status = session.GetStatus();
            ulong expected = checked((ulong)Math.Round((decimal)(stop - epoch - (resume - pause)) * 48000 / Stopwatch.Frequency, MidpointRounding.AwayFromZero));
            Assert.Equal(AudioCaptureState.Stopped, status.State);
            Assert.Equal(48000u, status.SampleRate); Assert.Equal(2u, status.Channels);
            Assert.Equal(expected, status.FramesWritten);
            Assert.Equal(56L + checked((long)expected) * 8, new FileInfo(path).Length);
            byte[] header = File.ReadAllBytes(path)[..56];
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(header, 0, 4));
            Assert.Equal((ushort)3, BitConverter.ToUInt16(header, 20)); // IEEE float
            session.Dispose(); session.Dispose();
            Assert.Throws<ObjectDisposedException>(() => session.GetStatus());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void CaptureNeverOverwritesExistingAudio()
    {
        string path = Path.Combine(Path.GetTempPath(), $"FrameTrail.Audio.{Guid.NewGuid():N}.wav");
        File.WriteAllText(path, "preserve existing output");
        try
        {
            Assert.Throws<COMException>(() => AudioCaptureSession.Start(new(AudioCaptureKind.ProcessTree, path, ProcessId: (uint)Environment.ProcessId)));
            Assert.Equal("preserve existing output", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
