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
        long bucket = QuantizeBucket(pixelsPerSecond);
        long prevTicks = start - 1;
        double first = Math.Floor((left - clipLeft) / tileWidth) * tileWidth;
        for (double offset = first; clipLeft + offset < right && result.Count < 64; offset += tileWidth) {
            double x = Math.Max(left, clipLeft + offset), end = Math.Min(right, clipLeft + offset + tileWidth);
            double center = Math.Min(clipRight - 0.001, clipLeft + offset + tileWidth / 2);
            long raw = Math.Clamp(start + (long)Math.Round((center - clipLeft) / pixelsPerSecond * 10_000_000), start, start + duration - 1);
            long snapped = start + (long)Math.Round((raw - start) / (double)bucket) * bucket;
            long ticks = Math.Clamp(snapped, start, start + duration - 1);
            if (ticks <= prevTicks) ticks = raw;
            prevTicks = ticks;
            result.Add(new(x - left, end - x, ticks));
        }
        return result;
    }

    internal static IReadOnlyList<long> PrefetchFrames(long start, long duration, double pixelsPerSecond, double visibleStart, double visibleEnd, long frameTicks = 333_333)
    {
        if (duration <= 0 || pixelsPerSecond < 200 || frameTicks <= 0) return Array.Empty<long>();
        const double bufferPx = 246;
        double clipLeft = 16 + start / 10_000_000.0 * pixelsPerSecond;
        double clipRight = clipLeft + duration / 10_000_000.0 * pixelsPerSecond;
        double left = Math.Max(clipLeft, visibleStart - bufferPx), right = Math.Min(clipRight - 0.001, visibleEnd + bufferPx);
        if (right <= left) return Array.Empty<long>();
        long t0 = Math.Clamp(start + (long)Math.Floor((left - clipLeft) / pixelsPerSecond * 10_000_000), start, start + duration - 1);
        long t1 = Math.Clamp(start + (long)Math.Ceiling((right - clipLeft) / pixelsPerSecond * 10_000_000), t0, start + duration - 1);
        long alignedStart = start + ((t0 - start) / frameTicks) * frameTicks;
        long span = Math.Max(1, t1 - alignedStart);
        long multiplier = Math.Max(1, (long)Math.Ceiling((span / (double)frameTicks + 1) / 120.0));
        long step = frameTicks * multiplier;
        var frames = new List<long>();
        for (long t = alignedStart; t <= t1 && frames.Count < 120; t += step)
            frames.Add(Math.Clamp(t, start, start + duration - 1));
        return frames;
    }

    private static long QuantizeBucket(double pixelsPerSecond) => pixelsPerSecond switch
    {
        >= 600 => 333_333,
        >= 250 => 1_000_000,
        >= 110 => 2_500_000,
        >= 50 => 5_000_000,
        >= 15 => 20_000_000,
        >= 4 => 100_000_000,
        _ => 300_000_000
    };
}
