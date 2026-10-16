namespace FrameTrail.Domain;

public static class VideoCodecs
{
    public const string H264 = "h264", Hevc = "hevc", Av1 = "av1";
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { H264, Hevc, Av1 });
    public static bool IsSupported(string codec) => codec is H264 or Hevc or Av1;
    public static string DisplayName(string codec) => codec switch {
        H264 => "H.264", Hevc => "H.265 / HEVC", Av1 => "AV1",
        _ => throw new ArgumentOutOfRangeException(nameof(codec))
    };
}
