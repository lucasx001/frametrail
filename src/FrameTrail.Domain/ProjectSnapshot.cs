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
    public bool BurnSubtitles { get; init; } = true;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Quality { get; init; }

    public static (uint Width, uint Height) CalculateDimensions(uint canvasWidth, uint canvasHeight, uint targetShortEdge)
    {
        uint safeWidth = Math.Max(2u, canvasWidth);
        uint safeHeight = Math.Max(2u, canvasHeight);
        if (safeWidth >= safeHeight)
        {
            uint height = Even(targetShortEdge);
            uint width = Even(targetShortEdge * ((double)safeWidth / safeHeight));
            return (width, height);
        }
        else
        {
            uint width = Even(targetShortEdge);
            uint height = Even(targetShortEdge * ((double)safeHeight / safeWidth));
            return (width, height);
        }
    }

    private static uint Even(double value)
    {
        if (!double.IsFinite(value)) return 2u;
        long rounded = (long)Math.Round(value / 2.0) * 2L;
        return (uint)Math.Clamp(rounded, 2L, 7680L);
    }
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
    public string CursorStyle { get; init; } = "recorded";
    public bool CursorHideWhenIdle { get; init; }
    public double CursorIdleDelay { get; init; } = 1.2;
    public double CursorTimeOffset { get; init; }
    public string CursorClickStyle { get; init; } = "none";
    public double CursorClickOpacity { get; init; } = 0.8;
    public double CursorClickScale { get; init; } = 1;
    public SdrColor CursorClickColor { get; init; } = new() { R = 0.35, G = 0.68, B = 1 };
    public string FrameStyle { get; init; } = "none";
    public double FrameScale { get; init; } = 1;
    public double FrameToolbarScale { get; init; } = 1;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? FrameOuterCornerRadius { get; init; }
    public string FrameTitle { get; init; } = "";
    public string FrameBrowserAddress { get; init; } = "";
    public string CameraShape { get; init; } = "rounded";
    public double MotionBlur { get; init; }
    public bool KeystrokeHud { get; init; } = true;
    public bool MicNoiseReduction { get; init; }
    public bool AudioNormalize { get; init; }
    public bool AudioDucking { get; init; } = true;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? BgmPath { get; init; }
    public double BgmGain { get; init; } = 0.35;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public BackdropSettings? Backdrop { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public SubtitleStyleSettings? SubtitleStyle { get; init; }
}

public sealed record SubtitleStyleSettings
{
    public string Preset { get; init; } = "classicOutline";
    public string FontFamily { get; init; } = "Alibaba PuHuiTi 3.0";
    public double FontSize { get; init; } = 44.0;
    public string TextColor { get; init; } = "#FFFFFF";
    public string OutlineColor { get; init; } = "#09090B";
    public double OutlineWidth { get; init; } = 3.0;
    public bool ShadowEnabled { get; init; } = true;
    public double ShadowBlur { get; init; } = 6.0;
    public double ShadowOpacity { get; init; } = 0.65;
    public string BackgroundColor { get; init; } = "#09090B";
    public double BackgroundOpacity { get; init; } = 0.0;
    public double BackgroundRadius { get; init; } = 12.0;
    public string HighlightColor { get; init; } = "#FACC15";
    public string HighlightMode { get; init; } = "none";
    public double PositionX { get; init; } = 0.5;
    public double PositionY { get; init; } = 0.88;
    public string Alignment { get; init; } = "center";
    public double MaxWidthRatio { get; init; } = 0.84;
    public int MaxCharsPerLine { get; init; } = 18;
    public bool BilingualEnabled { get; init; } = false;
    public double SecondaryScale { get; init; } = 0.72;
    public double FadeSeconds { get; init; } = 0.12;

    public static SubtitleStyleSettings ClassicOutline() => new();

    public static SubtitleStyleSettings DarkPill() => new()
    {
        Preset = "darkPill",
        FontSize = 40.0,
        OutlineWidth = 0.0,
        ShadowEnabled = false,
        ShadowBlur = 0.0,
        ShadowOpacity = 0.0,
        BackgroundColor = "#09090B",
        BackgroundOpacity = 0.78,
        BackgroundRadius = 14.0,
        HighlightMode = "none"
    };

    public static SubtitleStyleSettings KaraokePop() => new()
    {
        Preset = "karaokePop",
        FontSize = 48.0,
        OutlineWidth = 4.0,
        ShadowEnabled = true,
        ShadowBlur = 8.0,
        ShadowOpacity = 0.75,
        BackgroundOpacity = 0.0,
        HighlightColor = "#FACC15",
        HighlightMode = "wordPill"
    };

    public static SubtitleStyleSettings BilingualClean() => new()
    {
        Preset = "bilingualClean",
        FontSize = 40.0,
        OutlineWidth = 2.5,
        ShadowEnabled = true,
        ShadowBlur = 5.0,
        ShadowOpacity = 0.55,
        BackgroundColor = "#09090B",
        BackgroundOpacity = 0.55,
        BackgroundRadius = 10.0,
        PositionY = 0.86,
        BilingualEnabled = true,
        SecondaryScale = 0.72
    };

    public static SubtitleStyleSettings ForPreset(string? preset) => (preset ?? "").Trim() switch
    {
        "classicOutline" => ClassicOutline(),
        "darkPill" => DarkPill(),
        "karaokePop" => KaraokePop(),
        "bilingualClean" => BilingualClean(),
        "custom" => new SubtitleStyleSettings { Preset = "custom" },
        _ => ClassicOutline()
    };
}

public sealed record SubtitleWordTiming
{
    public string Text { get; init; } = "";
    public double StartOffset { get; init; }
    public double EndOffset { get; init; }
}

public sealed record SubtitleCueStyleOverride
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? PositionX { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? PositionY { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? FontSize { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? TextColor { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? OutlineColor { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? BackgroundColor { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? HighlightColor { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Alignment { get; init; }
}

public sealed record SubtitleCueParameters
{
    private static readonly JsonSerializerOptions CueJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string Text { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SecondaryText { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<SubtitleWordTiming>? Words { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public SubtitleCueStyleOverride? StyleOverride { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CueJsonOptions);

    public static SubtitleCueParameters FromEffect(SourceEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (effect.Parameters.ValueKind != JsonValueKind.Object)
            return new SubtitleCueParameters();
        return effect.Parameters.Deserialize<SubtitleCueParameters>(CueJsonOptions) ?? new SubtitleCueParameters();
    }
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
