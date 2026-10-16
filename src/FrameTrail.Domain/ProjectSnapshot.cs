using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrameTrail.Domain;

// DTOs carry immutable snapshots. Timeline mapping and scene evaluation live
// exclusively in FrameTrail.Core; this assembly contains no duplicate mapper.
public sealed record MediaTime
{
    [JsonRequired] public long Ticks { get; init; }
    [JsonRequired] public int Timescale { get; init; } = 10_000_000;
    public static MediaTime FromTicks(long ticks) => new() { Ticks = ticks, Timescale = 10_000_000 };
}

public sealed record Rational
{
    [JsonRequired] public int Numerator { get; init; } = 1;
    [JsonRequired] public int Denominator { get; init; } = 1;
}

public enum MediaKind { Screen, Camera, SystemAudio, Microphone, PointerEvents, AndroidAudio }

public sealed record MediaAsset
{
    [JsonRequired] public string Id { get; init; } = "";
    [JsonRequired] public MediaKind Kind { get; init; }
    [JsonRequired] public string Path { get; init; } = "";
    [JsonRequired] public MediaTime Duration { get; init; } = new();
    [JsonRequired] public MediaTime StartOffset { get; init; } = new();
    public uint Width { get; init; }
    public uint Height { get; init; }
    public uint SampleRate { get; init; }
    public uint Channels { get; init; }
}

public sealed record RecordingSegment
{
    [JsonRequired] public string Id { get; init; } = "";
    [JsonRequired] public MediaTime SourceStart { get; init; } = new();
    [JsonRequired] public MediaTime SourceDuration { get; init; } = new();
    [JsonRequired] public Rational Rate { get; init; } = new();
}

public sealed record NormalizedRect
{
    [JsonRequired] public double X { get; init; }
    [JsonRequired] public double Y { get; init; }
    [JsonRequired] public double Width { get; init; } = 1;
    [JsonRequired] public double Height { get; init; } = 1;
}

public sealed record SdrColor
{
    [JsonRequired] public double R { get; init; } = 0.08;
    [JsonRequired] public double G { get; init; } = 0.08;
    [JsonRequired] public double B { get; init; } = 0.1;
    [JsonRequired] public double A { get; init; } = 1;
}

public sealed record CameraLayout
{
    [JsonRequired] public bool Enabled { get; init; }
    [JsonRequired] public double X { get; init; } = 0.72;
    [JsonRequired] public double Y { get; init; } = 0.68;
    [JsonRequired] public double Width { get; init; } = 0.25;
    [JsonRequired] public double Height { get; init; } = 0.28;
    [JsonRequired] public bool Mirror { get; init; }
}

public sealed record CanvasSettings
{
    [JsonRequired] public uint Width { get; init; } = 1920;
    [JsonRequired] public uint Height { get; init; } = 1080;
    [JsonRequired] public double Padding { get; init; } = 0.05;
    [JsonRequired] public SdrColor Background { get; init; } = new();
    [JsonRequired] public NormalizedRect Screen { get; init; } = new();
    [JsonRequired] public CameraLayout Camera { get; init; } = new();
}

public sealed record SourceEffect
{
    [JsonRequired] public string Id { get; init; } = "";
    [JsonRequired] public string Kind { get; init; } = "";
    [JsonRequired] public MediaTime SourceStart { get; init; } = new();
    [JsonRequired] public MediaTime SourceDuration { get; init; } = new();
    [JsonRequired] public JsonElement Parameters { get; init; }
}

public sealed record ExportSettings
{
    [JsonRequired] public uint Width { get; init; } = 1920;
    [JsonRequired] public uint Height { get; init; } = 1080;
    [JsonRequired] public Rational Fps { get; init; } = new() { Numerator = 30 };
    [JsonRequired] public string VideoCodec { get; init; } = "h264";
    [JsonRequired] public string AudioCodec { get; init; } = "aac";
    [JsonRequired] public string Container { get; init; } = "mp4";
}

public sealed record PrimarySource
{
    [JsonRequired] public string Kind { get; init; } = "desktop";
    [JsonRequired] public uint Width { get; init; }
    [JsonRequired] public uint Height { get; init; }
    [JsonRequired] public string Orientation { get; init; } = "locked";
}

public sealed record ProjectSnapshot
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PrimarySource? PrimarySource { get; init; }
    [JsonRequired] public int SchemaVersion { get; init; } = 1;
    [JsonRequired] public string ProjectId { get; init; } = "";
    [JsonRequired] public long Revision { get; init; }
    [JsonRequired] public MediaTime SourceDuration { get; init; } = new();
    [JsonRequired] public IReadOnlyList<MediaAsset> Media { get; init; } = Array.Empty<MediaAsset>();
    [JsonRequired] public IReadOnlyList<RecordingSegment> Segments { get; init; } = Array.Empty<RecordingSegment>();
    [JsonRequired] public CanvasSettings Canvas { get; init; } = new();
    [JsonRequired] public IReadOnlyList<SourceEffect> Effects { get; init; } = Array.Empty<SourceEffect>();
    public IReadOnlyList<RecordingMarker> Markers { get; init; } = Array.Empty<RecordingMarker>();
    [JsonRequired, JsonPropertyName("export")] public ExportSettings Export { get; init; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public EditorSettings? Editor { get; init; }
}

public sealed record AudioLevel
{
    public double SystemGain { get; init; } = 1;
    public double MicrophoneGain { get; init; } = 1;
    public bool SystemMuted { get; init; }
    public bool MicrophoneMuted { get; init; }
}

public sealed record EditorSettings
{
    public double SystemGain { get; init; } = 1;
    public double MicrophoneGain { get; init; } = 1;
    public bool SystemMuted { get; init; }
    public bool MicrophoneMuted { get; init; }
    public IReadOnlyDictionary<string, AudioLevel> ClipAudio { get; init; } = new Dictionary<string, AudioLevel>();
    public double CornerRadius { get; init; } = 16;
    public double Shadow { get; init; } = 0.35;
    public double CursorScale { get; init; } = 1;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public NormalizedRect? Crop { get; init; }
    public double CursorSmoothing { get; init; } = 0.08;
    public string FrameStyle { get; init; } = "none";
    public string CameraShape { get; init; } = "rounded";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public BackdropSettings? Backdrop { get; init; }
}

public sealed record BackdropSettings
{
    public string Kind { get; init; } = "solid";
    public string Path { get; init; } = "";
    public SdrColor Secondary { get; init; } = new() { R = 0.16, G = 0.25, B = 0.42 };
}

public sealed record RecordingMarker
{
    [JsonRequired] public string Id { get; init; } = "";
    [JsonRequired] public MediaTime Time { get; init; } = new();
    [JsonRequired] public string Label { get; init; } = "";
}
