using System.Text.Json;

namespace FrameTrail.App;

internal sealed record BuiltInWallpaper(int Number, string File, int Width, int Height, long Bytes, string Sha256)
{
    public string SourcePath => Path.Combine(AppContext.BaseDirectory, "Assets", "Wallpapers", File);
    public string ProjectPath => "assets/wallpaper-" + Sha256 + ".jpg";
    public string Label => L.T("WallpaperNumber", Number.ToString("00"));
}

internal static class BuiltInWallpapers
{
    internal static IReadOnlyList<BuiltInWallpaper> All { get; } = Load();

    private static IReadOnlyList<BuiltInWallpaper> Load()
    {
        using var stream = typeof(BuiltInWallpapers).Assembly.GetManifestResourceStream("FrameTrail.App.Wallpapers.catalog.json")!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("wallpapers").Deserialize<BuiltInWallpaper[]>()!;
    }
}
