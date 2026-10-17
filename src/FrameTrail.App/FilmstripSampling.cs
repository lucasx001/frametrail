namespace FrameTrail.App;

internal readonly record struct FilmstripTile(double Left, double Width, long Ticks);
internal static class FilmstripSampling
{
    internal static IReadOnlyList<FilmstripTile> Visible(long start, long duration, double pixelsPerSecond, double visibleStart, double visibleEnd)
    {
        const double tileWidth = 82;
        double clipLeft = 16 + start / 10_000_000.0 * pixelsPerSecond;
        double clipRight = clipLeft + duration / 10_000_000.0 * pixelsPerSecond;
        double left = Math.Max(clipLeft, visibleStart), right = Math.Min(clipRight - 2, visibleEnd);
        var result = new List<FilmstripTile>();
        if (duration <= 0 || pixelsPerSecond <= 0 || right <= left) return result;
        double first = Math.Floor((left - clipLeft) / tileWidth) * tileWidth;
        for (double offset = first; clipLeft + offset < right && result.Count < 64; offset += tileWidth) {
            double x = Math.Max(left, clipLeft + offset), end = Math.Min(right, clipLeft + offset + tileWidth);
            double center = Math.Min(clipRight - 0.001, clipLeft + offset + tileWidth / 2);
            long ticks = Math.Clamp(start + (long)Math.Round((center - clipLeft) / pixelsPerSecond * 10_000_000), start, start + duration - 1);
            result.Add(new(x - left, end - x, ticks));
        }
        return result;
    }
}
