using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using FrameTrail.Domain;
using System.Text.Json;

namespace FrameTrail.Interop;

internal sealed class EngineSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal EngineSafeHandle(ulong value) : base(true) => SetHandle(checked((nint)value));
    protected override bool ReleaseHandle() => EngineNative.ft_session_destroy((ulong)handle) == 0;
}

public sealed class EngineException(int code, string message) : Exception(message)
{
    public int NativeCode { get; } = code;
}

/// <summary>A native engine session. SafeHandle pins lifetime throughout every P/Invoke.</summary>
public sealed class EngineSession : IDisposable
{
    public const uint ExpectedAbiVersion = 1;
    private readonly EngineSafeHandle handle;

    public EngineSession()
    {
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("FrameTrail requires x64.");
        if (EngineNative.ft_engine_abi_version() != ExpectedAbiVersion)
            throw new InvalidOperationException("Native engine ABI does not match the application.");
        ThrowIfFailed(EngineNative.ft_session_create(ExpectedAbiVersion, out var value));
        handle = new EngineSafeHandle(value);
    }

    public static string Version
    {
        get
        {
            ThrowIfFailed(EngineNative.ft_engine_version(null, 0, out var required));
            var bytes = new byte[required];
            ThrowIfFailed(EngineNative.ft_engine_version(bytes, required, out _));
            return Encoding.UTF8.GetString(bytes, 0, bytes.Length - 1);
        }
    }

    public bool IsCancelled
    {
        get
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, this);
            ThrowIfFailed(EngineNative.ft_session_is_cancelled(handle, out var value));
            return value != 0;
        }
    }

    public void Cancel()
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        ThrowIfFailed(EngineNative.ft_session_cancel(handle));
    }

    /// <summary>Authoritative C++ validation; use before committing a candidate or saving it.</summary>
    public static void ValidateProject(ProjectSnapshot project)
    {
        var bytes = Encoding.UTF8.GetBytes(ProjectJson.Serialize(project));
        ThrowIfFailed(EngineNative.ft_project_validate(bytes, checked((uint)bytes.Length)));
    }

    public static ProjectSnapshot Edit(ProjectSnapshot project, object command)
    {
        var bytes = Encoding.UTF8.GetBytes(ProjectJson.Serialize(project));
        var edit = JsonSerializer.SerializeToUtf8Bytes(command, EditorJson);
        ThrowIfFailed(EngineNative.ft_project_edit(bytes, (uint)bytes.Length, edit, (uint)edit.Length, null, 0, out var required));
        var output = new byte[required];
        ThrowIfFailed(EngineNative.ft_project_edit(bytes, (uint)bytes.Length, edit, (uint)edit.Length, output, required, out _));
        return ProjectJson.Deserialize(Encoding.UTF8.GetString(output, 0, output.Length - 1));
    }

    private static readonly JsonSerializerOptions EditorJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public static ResolvedTimeline ResolveTimeline(ProjectSnapshot project)
    {
        var bytes = Encoding.UTF8.GetBytes(ProjectJson.Serialize(project));
        ThrowIfFailed(EngineNative.ft_project_timeline(bytes, (uint)bytes.Length, null, 0, out var required));
        var output = new byte[required];
        ThrowIfFailed(EngineNative.ft_project_timeline(bytes, (uint)bytes.Length, output, required, out _));
        return JsonSerializer.Deserialize<ResolvedTimeline>(output.AsSpan(0, output.Length - 1), EditorJson)!;
    }

    /// <summary>Atomically replaces the engine snapshot. Revisions must increase and project identity is fixed.</summary>
    public void LoadProject(ProjectSnapshot project)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        var bytes = Encoding.UTF8.GetBytes(ProjectJson.Serialize(project));
        ThrowIfFailed(EngineNative.ft_session_load_project(handle, bytes, checked((uint)bytes.Length)));
    }

    public long OutputDurationTicks
    {
        get
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, this);
            ThrowIfFailed(EngineNative.ft_session_output_duration(handle, out var ticks));
            return ticks;
        }
    }

    public MappedTimelineTime? MapOutputTime(long ticks)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        var result = EngineNative.ft_session_map_output(handle, ticks, out var mapped);
        if (result == 9) return null;
        ThrowIfFailed(result);
        return new(mapped.SegmentId, mapped.Ticks);
    }

    public MappedTimelineTime? MapSourceTime(long ticks)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        var result = EngineNative.ft_session_map_source(handle, ticks, out var mapped);
        if (result == 9) return null;
        ThrowIfFailed(result);
        return new(mapped.SegmentId, mapped.Ticks);
    }

    public EvaluatedFrameScene? EvaluateScene(long outputTicks)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        var result = EngineNative.ft_session_evaluate_scene(handle, outputTicks, out var scene);
        if (result == 9) return null;
        ThrowIfFailed(result);
        static EvaluatedMediaFrame Frame(NativeMediaFrame value) => new(value.MediaId, value.LocalTicks,
            new OutputRect(value.OutputRect.X, value.OutputRect.Y, value.OutputRect.Width, value.OutputRect.Height), value.Mirror != 0);
        return new(scene.Revision, scene.OutputTicks, scene.SourceTicks, scene.SegmentId, scene.Width, scene.Height,
            new SdrColor { R = scene.BackgroundR, G = scene.BackgroundG, B = scene.BackgroundB, A = scene.BackgroundA },
            Frame(scene.Screen), scene.HasCamera != 0 ? Frame(scene.Camera) : null);
    }

    public void Dispose() => handle.Dispose();

    private static void ThrowIfFailed(int result)
    {
        if (result == 0) return;
        // Read immediately on this thread, before an await or another engine call.
        var bytes = new byte[1024];
        var copied = EngineNative.ft_error_copy(bytes, (uint)bytes.Length, out var required);
        var message = copied == 0 ? Encoding.UTF8.GetString(bytes, 0, checked((int)required - 1)) : $"Native error {result}";
        throw new EngineException(result, message);
    }
}

public sealed record MappedTimelineTime(string SegmentId, long Ticks);
public sealed record ResolvedClip(string Id, long SourceStart, long SourceDuration, long OutputStart, long OutputDuration, int Numerator, int Denominator);
public sealed record ResolvedMarker(string Id, long Ticks, string Label);
public sealed record ResolvedEffect(string Id, string Kind, long OutputStart, long OutputDuration);
public sealed record ResolvedTimeline(long Duration, IReadOnlyList<ResolvedClip> Clips, IReadOnlyList<ResolvedMarker> Markers)
{ public IReadOnlyList<ResolvedEffect> Effects { get; init; } = Array.Empty<ResolvedEffect>(); }
public sealed record OutputRect(double X, double Y, double Width, double Height);
public sealed record EvaluatedMediaFrame(string MediaId, long LocalTicks, OutputRect OutputRect, bool Mirror);
public sealed record EvaluatedFrameScene(long Revision, long OutputTicks, long SourceTicks, string SegmentId,
    uint Width, uint Height, SdrColor Background, EvaluatedMediaFrame Screen, EvaluatedMediaFrame? Camera);
