namespace FrameTrail.Domain;

// DogSC ScreenFrameStyle.allCases / ScreenFrameDefaults (MIT).
public static class FrameStyles
{
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { "none", "windowLight", "windowDark", "browserLight", "browserDark", "devicePhone" });
    public static string Normalize(string style) => style switch { "window" => "windowDark", "browser" => "browserDark", "device" => "devicePhone", _ => style };
    public static string Name(string style) => Normalize(style) switch {
        "none" => "无边框", "windowLight" => "macOS 窗口·浅色", "windowDark" => "macOS 窗口·深色",
        "browserLight" => "浏览器·浅色", "browserDark" => "浏览器·深色", "devicePhone" => "自适应设备框", _ => throw new ArgumentException("Unknown frame style.")
    };
    public static bool IsWindow(string style) => Normalize(style) is "windowLight" or "windowDark";
    public static bool IsBrowser(string style) => Normalize(style) is "browserLight" or "browserDark";
    public static double OuterRadius(string style) => Normalize(style) switch { "none" => 24, "devicePhone" => 64, "browserLight" or "browserDark" => 22, _ => 18 };
    public static EditorSettings Apply(EditorSettings prior, string style)
    {
        style = Normalize(style); if (!All.Contains(style)) throw new ArgumentException("Unknown frame style.");
        return prior with {
            FrameStyle = style, FrameScale = style == "devicePhone" ? 0.9 : 1,
            FrameToolbarScale = IsWindow(style) ? 0.9 : IsBrowser(style) ? 1.08 : 1,
            FrameOuterCornerRadius = OuterRadius(style), CornerRadius = style == "devicePhone" ? 40 : style == "none" ? 24 : 0,
            Shadow = style == "none" ? 0.28 : style == "devicePhone" ? 0.24 : style.EndsWith("Dark", StringComparison.Ordinal) ? 0.30 : 0.22
        };
    }
}
