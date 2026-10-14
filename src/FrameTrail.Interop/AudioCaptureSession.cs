using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FrameTrail.Interop;

public enum AudioCaptureKind : uint { System = 1, Microphone = 2, ProcessTree = 3 }
public enum AudioCaptureState : uint { Preparing, Recording, Paused, Stopped, Failed }
public sealed record AudioDevice(AudioCaptureKind Kind, string Id, string Name, bool IsDefault);
public sealed record AudioCaptureOptions(AudioCaptureKind Kind, string OutputPath, string? DeviceId = null,
    uint ProcessId = 0, string? DiagnosticsPath = null);
public sealed record AudioCaptureStatus(AudioCaptureState State, int ErrorCode, uint SampleRate, uint Channels,
    uint NativeSampleRate, uint NativeChannels, bool DefaultDeviceChanged, uint Discontinuities, uint TimestampErrors, bool DriftAvailable,
    ulong Packets, ulong FramesWritten, ulong SilentFrames, ulong TrimmedFrames, ulong CorrectionFrames,
    long EpochQpc, long QpcFrequency, long Duration100ns, double Peak, double Rms, double DriftPpm,
    double MaxTimestampErrorMs, string Message);

/// <summary>One independently stored audio source. Call blocking Start/Stop/Dispose away from the UI Dispatcher.</summary>
public sealed class AudioCaptureSession : IDisposable
{
    private readonly AudioSafeHandle handle;
    private readonly object gate = new();
    private AudioCaptureSession(AudioSafeHandle handle) => this.handle = handle;

    public static IReadOnlyList<AudioDevice> EnumerateDevices(AudioCaptureKind kind)
    {
        ThrowIfFailed(AudioNative.ft_audio_enumerate_devices((uint)kind, null, 0, out uint count));
        // Device topology can change between the count and fill calls. Retry once with the new count.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var devices = new AudioNativeDevice[count];
            int hr = AudioNative.ft_audio_enumerate_devices((uint)kind, devices, count, out uint required);
            if (hr == unchecked((int)0x8007007A)) { count = required; continue; }
            ThrowIfFailed(hr);
            return devices.Take(checked((int)required)).Select(d => new AudioDevice((AudioCaptureKind)d.Kind, d.Id, d.Name, d.IsDefault != 0)).ToArray();
        }
        throw new InvalidOperationException("Audio devices changed during enumeration. Retry device selection.");
    }

    public static bool IsProcessLoopbackSupported
    {
        get { ThrowIfFailed(AudioNative.ft_audio_process_loopback_supported(out uint supported)); return supported != 0; }
    }

    /// <summary>Returns the selected window's PID. Process-tree audio may include multiple browser windows/tabs.</summary>
    public static uint ProcessIdFromWindow(nuint hwnd)
    {
        ThrowIfFailed(AudioNative.ft_audio_process_from_window(hwnd, out uint pid)); return pid;
    }

    public static AudioCaptureSession Start(AudioCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("FrameTrail audio requires x64.");
        var native = new AudioNativeOptions
        {
            Size = checked((uint)Marshal.SizeOf<AudioNativeOptions>()), Kind = (uint)options.Kind, ProcessId = options.ProcessId,
            DeviceId = options.DeviceId, OutputPath = Path.GetFullPath(options.OutputPath),
            DiagnosticsPath = options.DiagnosticsPath is null ? null : Path.GetFullPath(options.DiagnosticsPath)
        };
        ThrowIfFailed(AudioNative.ft_audio_start(in native, out nint value));
        return new AudioCaptureSession(new AudioSafeHandle(value));
    }

    /// <summary>Use the screen capture status's first-frame raw QPC and frequency. Earlier samples are trimmed.</summary>
    public void SetEpoch(long firstVideoQpc, long qpcFrequency)
    {
        lock (gate) { EnsureOpen(); ThrowIfFailed(AudioNative.ft_audio_set_epoch(handle, firstVideoQpc, qpcFrequency)); }
    }
    public void PauseAt(long qpc) { lock (gate) { EnsureOpen(); ThrowIfFailed(AudioNative.ft_audio_pause_at(handle, qpc)); } }
    public void ResumeAt(long qpc) { lock (gate) { EnsureOpen(); ThrowIfFailed(AudioNative.ft_audio_resume_at(handle, qpc)); } }
    public void StopAt(long qpc) { lock (gate) { EnsureOpen(); ThrowIfFailed(AudioNative.ft_audio_stop_at(handle, qpc)); } }
    public void Stop() => StopAt(Stopwatch.GetTimestamp());

    public AudioCaptureStatus GetStatus()
    {
        lock (gate)
        {
            EnsureOpen(); var s = new AudioNativeStatus { Size = checked((uint)Marshal.SizeOf<AudioNativeStatus>()) };
            ThrowIfFailed(AudioNative.ft_audio_get_status(handle, ref s));
            return new((AudioCaptureState)s.State, s.ErrorCode, s.SampleRate, s.Channels, s.NativeSampleRate, s.NativeChannels,
                s.DefaultDeviceChanged != 0, s.Discontinuities, s.TimestampErrors, s.DriftAvailable != 0, s.Packets, s.FramesWritten, s.SilentFrames,
                s.TrimmedFrames, s.CorrectionFrames, s.EpochQpc, s.QpcFrequency, s.Duration100ns, s.Peak, s.Rms,
                s.DriftPpm, s.MaxTimestampErrorMs, s.Message);
        }
    }
    public void Dispose() { lock (gate) handle.Dispose(); }
    private void EnsureOpen() => ObjectDisposedException.ThrowIf(handle.IsClosed, this);
    private static void ThrowIfFailed(int hr) { if (hr < 0) throw new COMException($"Audio operation failed (0x{hr:X8}).", hr); }
}
