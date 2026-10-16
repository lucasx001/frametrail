using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrameTrail.Domain;

public class ProjectFormatException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class UnsupportedProjectVersionException(int version)
    : ProjectFormatException($"Project schema {version} is unsupported. Open it with a compatible version of FrameTrail.");

public static class ProjectJson
{
    public const int SchemaVersion = 2;
    public const int MaximumJsonBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        WriteIndented = true,
        Converters = { new StrictMediaKindConverter() }
    };

    public static ProjectSnapshot Deserialize(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) is 0 or > MaximumJsonBytes)
            throw new ProjectFormatException("Project JSON must be 1 byte..16 MiB.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            RejectDuplicateKeys(document.RootElement);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("schemaVersion", out var schema) && schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var version) && version != 1 && version != SchemaVersion)
                throw new UnsupportedProjectVersionException(version);
            var snapshot = JsonSerializer.Deserialize<ProjectSnapshot>(json, Options)
                ?? throw new ProjectFormatException("Project root must be an object.");
            ValidateProtocol(snapshot);
            return snapshot;
        }
        catch (JsonException error) { throw new ProjectFormatException("Invalid project JSON: " + error.Message, error); }
    }

    public static ProjectSnapshot UpgradeToCurrent(ProjectSnapshot snapshot) {
        ValidateProtocol(snapshot);
        if (snapshot.SchemaVersion == SchemaVersion) return snapshot;
        var video = snapshot.Media.First(m => m.Kind == MediaKind.Screen);
        return snapshot with { SchemaVersion = SchemaVersion, PrimarySource = new() { Width = video.Width, Height = video.Height } };
    }

    public static string Serialize(ProjectSnapshot snapshot)
    {
        ValidateProtocol(snapshot);
        var json = JsonSerializer.Serialize(snapshot, Options);
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
            throw new ProjectFormatException("Project JSON exceeds 16 MiB.");
        return json;
    }

    // Transport/schema checks only. Authoritative semantic validation (source
    // overlap, media coverage, mapping, layout) is Core::validate_project.
    public static void ValidateProtocol(ProjectSnapshot snapshot)
    {
        if (snapshot.Media is null || snapshot.Segments is null || snapshot.Effects is null || snapshot.Markers is null || snapshot.Canvas is null || snapshot.Export is null)
            throw new ProjectFormatException("Required collection or settings cannot be null.");
        if (snapshot.SchemaVersion is not (1 or 2)) throw new ProjectFormatException("Only project schemas 1 and 2 are supported.");
        if (snapshot.SchemaVersion == 1 && (snapshot.PrimarySource is not null || snapshot.Media.Any(m => m?.Kind == MediaKind.AndroidAudio)))
            throw new ProjectFormatException("Android fields require schema 2.");
        if (snapshot.SchemaVersion == 2) {
            var source = snapshot.PrimarySource ?? throw new ProjectFormatException("Schema 2 requires primarySource.");
            if (source.Kind is not ("desktop" or "androidUsb") || source.Orientation != "locked" || source.Width is 0 or > 32768 || source.Height is 0 or > 32768)
                throw new ProjectFormatException("Invalid primary source.");
            if (source.Kind != "androidUsb" && snapshot.Media.Any(m => m?.Kind == MediaKind.AndroidAudio)) throw new ProjectFormatException("Phone audio requires an Android source.");
            if (source.Kind == "androidUsb") {
                if (snapshot.Media.Any(m => m?.Kind is MediaKind.PointerEvents or MediaKind.SystemAudio)) throw new ProjectFormatException("Android capture cannot contain Windows pointer or loopback tracks.");
                if (snapshot.Media.Any(m => m?.Kind == MediaKind.Screen && (m.Width != source.Width || m.Height != source.Height))) throw new ProjectFormatException("Android dimensions must remain fixed throughout a recording.");
            }
        }
        if (!ValidId(snapshot.ProjectId) || snapshot.Revision < 0) throw new ProjectFormatException("Invalid project ID or revision.");
        ValidateTime(snapshot.SourceDuration);
        if (snapshot.SourceDuration.Ticks <= 0) throw new ProjectFormatException("Source duration must be positive.");

        if (snapshot.Media.Count is 0 or > 100_000 || snapshot.Segments.Count is 0 or > 100_000 || snapshot.Effects.Count > 100_000)
            throw new ProjectFormatException("Invalid collection count.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        void ValidateId(string id)
        {
            if (!ValidId(id) || !ids.Add(id)) throw new ProjectFormatException("Item IDs must be valid and globally unique.");
        }
        foreach (var media in snapshot.Media)
        {
            if (media is null) throw new ProjectFormatException("Media item cannot be null.");
            ValidateId(media.Id);
            if (!Enum.IsDefined(media.Kind) || !IsProjectRelativePath(media.Path)) throw new ProjectFormatException("Invalid media kind or relative path.");
            ValidateTime(media.Duration); ValidateTime(media.StartOffset);
            if (media.Duration.Ticks <= 0 || media.Width > 32768 || media.Height > 32768 || media.SampleRate > 384000 || media.Channels > 32)
                throw new ProjectFormatException("Invalid media metadata.");
        }
        foreach (var segment in snapshot.Segments)
        {
            if (segment is null) throw new ProjectFormatException("Segment cannot be null.");
            ValidateId(segment.Id); ValidateTime(segment.SourceStart); ValidateTime(segment.SourceDuration); ValidateRatio(segment.Rate);
            if (segment.SourceStart.Ticks < 0 || segment.SourceDuration.Ticks <= 0) throw new ProjectFormatException("Invalid segment time.");
        }
        foreach (var effect in snapshot.Effects)
        {
            if (effect is null) throw new ProjectFormatException("Effect cannot be null.");
            ValidateId(effect.Id); ValidateTime(effect.SourceStart); ValidateTime(effect.SourceDuration);
            if (effect.Kind is not ("zoom" or "mask" or "sticker" or "focus" or "tilt" or "intro") || effect.Parameters.ValueKind != JsonValueKind.Object)
                throw new ProjectFormatException("Invalid effect envelope.");
            RejectDuplicateKeys(effect.Parameters);
        }
        if (snapshot.Markers.Count > 100_000) throw new ProjectFormatException("Marker count limit exceeded.");
        foreach (var marker in snapshot.Markers)
        {
            if (marker is null) throw new ProjectFormatException("Marker cannot be null.");
            ValidateId(marker.Id); ValidateTime(marker.Time);
            if (marker.Time.Ticks < 0 || marker.Label is null || Encoding.UTF8.GetByteCount(marker.Label) > 512)
                throw new ProjectFormatException("Invalid marker time or label.");
        }
        var canvas = snapshot.Canvas;
        if (canvas.Width is 0 or > 16384 || canvas.Height is 0 or > 16384 || !double.IsFinite(canvas.Padding) || canvas.Padding is < 0 or >= 0.5 || canvas.Background is null || canvas.Screen is null || canvas.Camera is null)
            throw new ProjectFormatException("Invalid canvas settings.");
        foreach (var value in new[] { canvas.Background.R, canvas.Background.G, canvas.Background.B, canvas.Background.A })
            if (!double.IsFinite(value) || value is < 0 or > 1) throw new ProjectFormatException("Invalid SDR color channel.");
        ValidateRect(canvas.Screen.X, canvas.Screen.Y, canvas.Screen.Width, canvas.Screen.Height);
        ValidateRect(canvas.Camera.X, canvas.Camera.Y, canvas.Camera.Width, canvas.Camera.Height);
        var output = snapshot.Export;
        ValidateRatio(output.Fps);
        if (output.Width is < 2 or > 7680 || output.Height is < 2 or > 7680 || output.Width % 2 != 0 || output.Height % 2 != 0 ||
            output.Fps.Denominator != 1 || output.Fps.Numerator is not (30 or 60) || output.VideoCodec != "h264" || output.AudioCodec != "aac" || output.Container != "mp4")
            throw new ProjectFormatException("Unsupported export settings.");
    }

    public static bool IsProjectRelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || Encoding.UTF8.GetByteCount(path) > 1024 || path.StartsWith('/') || path.EndsWith('/')) return false;
        foreach (var value in path)
            if (value < 32 || value == 127 || "\\:*?\"<>|".Contains(value)) return false;
        foreach (var part in path.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')) return false;
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsAsciiDigit(stem[3])) return false;
        }
        return true;
    }

    private static bool ValidId(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static void ValidateTime(MediaTime? value)
    {
        if (value is null || value.Timescale is <= 0 or > 1_000_000_000 || value.Ticks == long.MinValue ||
            Math.Abs((decimal)value.Ticks) > (decimal)value.Timescale * 7 * 24 * 60 * 60)
            throw new ProjectFormatException("Invalid integer media time or seven-day limit exceeded.");
    }
    private static void ValidateRatio(Rational? value)
    {
        if (value is null || value.Numerator is <= 0 or > 1_000_000 || value.Denominator is <= 0 or > 1_000_000)
            throw new ProjectFormatException("Invalid rational.");
    }
    private static void ValidateRect(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height) || x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > 1.000000001 || y + height > 1.000000001)
            throw new ProjectFormatException("Rectangle must fit normalized canvas.");
    }
    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ProjectFormatException("Duplicate JSON key: " + property.Name);
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var value in element.EnumerateArray()) RejectDuplicateKeys(value);
        else if (element.ValueKind == JsonValueKind.Number && (!element.TryGetDouble(out var number) || !double.IsFinite(number)))
            throw new ProjectFormatException("JSON numbers must be finite.");
    }

    private sealed class StrictMediaKindConverter : JsonConverter<MediaKind>
    {
        public override MediaKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType == JsonTokenType.String
            ? reader.GetString() switch
            {
                "screen" => MediaKind.Screen, "camera" => MediaKind.Camera, "systemAudio" => MediaKind.SystemAudio,
                "microphone" => MediaKind.Microphone, "pointerEvents" => MediaKind.PointerEvents, "androidAudio" => MediaKind.AndroidAudio,
                _ => throw new JsonException("Unknown or noncanonical media kind.")
            }
            : throw new JsonException("Media kind must be a string.");

        public override void Write(Utf8JsonWriter writer, MediaKind value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
        {
            MediaKind.Screen => "screen", MediaKind.Camera => "camera", MediaKind.SystemAudio => "systemAudio",
            MediaKind.Microphone => "microphone", MediaKind.PointerEvents => "pointerEvents", MediaKind.AndroidAudio => "androidAudio", _ => throw new JsonException("Unknown media kind.")
        });
    }
}
