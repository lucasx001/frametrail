using FrameTrail.App;
using FrameTrail.Domain;
using Xunit;

namespace FrameTrail.App.Tests;

public sealed class EditorMediaCacheTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FrameTrail-Cache-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    [Fact] public void BudgetEvictsOldFilesByBytesAndProtectsActiveMixAndForeignFiles()
    {
        var cache = new EditorMediaCache(root, 100);
        string old = cache.FilePath("old", "mix.wav"), active = cache.FilePath("active", "mix.wav"), foreign = Path.Combine(root, "cache", "user.wav");
        // Files created in the same filesystem clock tick can tie on hosted CI.
        // Give this eviction-order test distinct timestamps instead of relying on write speed.
        var oldest = DateTime.UtcNow.AddDays(-1);
        File.WriteAllBytes(old, new byte[90]); File.SetLastWriteTimeUtc(old, oldest);
        File.WriteAllBytes(active, new byte[80]); File.SetLastWriteTimeUtc(active, oldest.AddHours(1));
        File.WriteAllBytes(foreign, new byte[80]); cache.Pin(active); cache.Trim();
        Assert.False(File.Exists(old)); Assert.True(File.Exists(active)); Assert.True(File.Exists(foreign));
        cache.Unpin(active); string newest = cache.FilePath("new", "frame.png");
        File.WriteAllBytes(newest, new byte[80]); File.SetLastWriteTimeUtc(newest, oldest.AddHours(2)); cache.Trim();
        Assert.False(File.Exists(active));
    }
    [Fact] public void AudioKeySurvivesRevisionAndPictureChangesButTracksAudioAndSourceReplacement()
    {
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "audio.wav"), "sample");
        var p = new ProjectSnapshot { Media = new[] { new MediaAsset { Kind = MediaKind.Microphone, Path = "audio.wav" } }, Editor = new() };
        string key = EditorMediaCache.AudioKey(p, root);
        Assert.Equal(key, EditorMediaCache.AudioKey(p with { Revision = 90, Editor = p.Editor with { Shadow = 0.9 } }, root));
        Assert.NotEqual(key, EditorMediaCache.AudioKey(p with { Editor = p.Editor with { MicrophoneGain = 0.1 } }, root));
        File.WriteAllText(Path.Combine(root, "audio.wav"), "replacement");
        Assert.NotEqual(key, EditorMediaCache.AudioKey(p, root));
    }
    [Fact] public void PictureKeySurvivesGainAndRevisionButTracksVisualSettings()
    {
        Directory.CreateDirectory(root); var p = new ProjectSnapshot { Editor = new() };
        string key = EditorMediaCache.PictureKey(p, root);
        Assert.Equal(key, EditorMediaCache.PictureKey(p with { Revision = 50, Editor = p.Editor with { MicrophoneGain = 0.4 } }, root));
        Assert.NotEqual(key, EditorMediaCache.PictureKey(p with { Editor = p.Editor with { CursorScale = 2 } }, root));
    }
    [Fact] public void CorruptCompletedFilesAreTreatedAsCacheMisses()
    {
        var cache = new EditorMediaCache(root);
        string wav = cache.FilePath("bad", "mix.wav"), png = cache.FilePath("bad", "frame.png");
        File.WriteAllBytes(wav, new byte[100]); File.WriteAllBytes(png, new byte[40]);
        Assert.False(cache.Use(wav)); Assert.False(cache.Use(png));
    }
}
