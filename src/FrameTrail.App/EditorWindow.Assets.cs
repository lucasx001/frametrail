using FrameTrail.Domain;
using FrameTrail.Interop;
using System.Text.Json;
using System.Threading.Channels;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    private EditorMediaCache? mediaCache;
    private CancellationTokenSource thumbnailCancellation = new(), audioCancellation = new();
    private Task thumbnailWorker = Task.CompletedTask, audioWorker = Task.CompletedTask;
    private Task assetWorker => Task.WhenAll(thumbnailWorker, audioWorker);
    private string? audioCacheKey;
    private string? pinnedAudio;
    private int audioPreparationCount, thumbnailRenderCount;
    private Task? audioPreparationBarrier;
    private HashSet<long> thumbnailDemand = new();
    private long thumbnailInFlight = -1;
    private void RequestThumbnails(IReadOnlyList<long> ticks)
    {
        Volatile.Write(ref thumbnailDemand, ticks.ToHashSet());
        if (thumbnails is null) return;
        // Replace queued work after a scroll/zoom; only the current decode may finish.
        while (thumbnails.Reader.TryRead(out _)) { }
        pendingThumbnails.Clear();
        foreach (long value in ticks) if (value != Interlocked.Read(ref thumbnailInFlight) && pendingThumbnails.Add(value))
            if (!thumbnails.Writer.TryWrite(value)) pendingThumbnails.Remove(value);
    }
    private async Task StopAssetsAsync()
    { thumbnailCancellation.Cancel(); audioCancellation.Cancel(); await assetWorker; mediaCache?.Unpin(pinnedAudio); pinnedAudio = null; }

    private async Task StartAssetsAsync(bool prepareAudio, bool updateThumbnails)
    {
        mediaCache ??= new EditorMediaCache(editor.DirectoryPath);
        var snapshot = editor.Snapshot;
        if (updateThumbnails) {
            thumbnailCancellation.Cancel(); await thumbnailWorker; thumbnailCancellation.Dispose(); thumbnailCancellation = new();
            thumbnails = Channel.CreateBounded<long>(128); pendingThumbnails.Clear();
            var queue = thumbnails; var token = thumbnailCancellation.Token;
            thumbnailWorker = Task.Run(() => BuildThumbnailsAsync(snapshot, queue, token));
            timeline.InvalidateThumbnails();
        }
        if (prepareAudio) {
            string key = EditorMediaCache.AudioKey(snapshot, editor.DirectoryPath);
            if (audioCacheKey != key || audioWorker.IsCompleted && preparedAudio is null) {
                audioCancellation.Cancel(); await audioWorker; audioCancellation.Dispose(); audioCancellation = new();
                mediaCache.Unpin(pinnedAudio); preparedAudio = null; timeline.SetWaveform(null); audioCacheKey = key;
                pinnedAudio = mediaCache.FilePath(key, "mix.wav");
                var token = audioCancellation.Token;
                audioWorker = Task.Run(() => BuildAudioAsync(snapshot, key, token));
            }
        }
    }
    private async Task BuildAudioAsync(ProjectSnapshot snapshot, string key, CancellationToken token)
    {
        string? partial = null, wav = null;
        try {
            if (audioPreparationBarrier is not null) await audioPreparationBarrier.WaitAsync(token);
            if (!snapshot.Media.Any(m => m.Kind is MediaKind.SystemAudio or MediaKind.Microphone or MediaKind.AndroidAudio)) {
                DispatcherQueue.TryEnqueue(() => { if (!token.IsCancellationRequested) timeline.SetWaveform(Array.Empty<float>()); }); return;
            }
            var cache = mediaCache!; wav = cache.FilePath(key, "mix.wav"); cache.Pin(wav);
            long duration = EngineSession.ResolveTimeline(snapshot).Duration;
            long frames = duration / TimeSpan.TicksPerSecond * 48000 + duration % TimeSpan.TicksPerSecond * 48000 / TimeSpan.TicksPerSecond;
            if (!cache.Use(wav) || new FileInfo(wav).Length != frames * 4 + 44) {
                partial = wav + ".partial"; cache.Pin(partial);
                using var media = new MediaSession(snapshot, editor.DirectoryPath); using var registration = token.Register(media.Cancel);
                Interlocked.Increment(ref audioPreparationCount);
                media.PrepareAudio(partial); token.ThrowIfCancellationRequested(); File.Move(partial, wav, true);
            }
            string peaksPath = cache.FilePath(key, "peaks.json"); float[] peaks;
            try {
                if (!cache.Use(peaksPath) || new FileInfo(peaksPath).Length > 1024 * 1024) throw new IOException();
                peaks = JsonSerializer.Deserialize<float[]>(await File.ReadAllTextAsync(peaksPath, token)) ?? throw new IOException();
                if (peaks.Length is 0 or > 24000 || peaks.Any(p => !float.IsFinite(p) || p < 0 || p > 1)) throw new IOException();
            } catch (Exception error) when (error is IOException or JsonException) {
                peaks = await ReadWaveformAsync(wav, token);
                await File.WriteAllTextAsync(peaksPath, JsonSerializer.Serialize(peaks), token);
            }
            token.ThrowIfCancellationRequested();
            DispatcherQueue.TryEnqueue(() => { if (!token.IsCancellationRequested) { preparedAudio = wav; timeline.SetWaveform(peaks); UpdateState(); } });
            cache.Trim();
        } catch (OperationCanceledException) { }
        catch (Exception error) { DispatcherQueue.TryEnqueue(() => { if (!token.IsCancellationRequested) ShowNotice("声音准备未完成", error.Message, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning); }); }
        finally {
            if (partial is not null) { mediaCache!.Unpin(partial); try { File.Delete(partial); } catch (IOException) { } }
            if (token.IsCancellationRequested) mediaCache!.Unpin(wav);
        }
    }
    private async Task BuildThumbnailsAsync(ProjectSnapshot snapshot, Channel<long> requests, CancellationToken token)
    {
        try {
            var cache = mediaCache!; string key = EditorMediaCache.PictureKey(snapshot, editor.DirectoryPath);
            // Thumbnail sessions use a small composition; the attached preview keeps its full resolution.
            double ratio = Math.Min(1, Math.Min(384.0 / snapshot.Canvas.Width, 216.0 / snapshot.Canvas.Height));
            using var media = new MediaSession(snapshot, editor.DirectoryPath); using var registration = token.Register(media.Cancel);
            bool resized = false;
            int rendered = 0;
            while (await requests.Reader.WaitToReadAsync(token)) {
                var batch = new List<long>(); while (requests.Reader.TryRead(out var value)) batch.Add(value);
                foreach (long ticks in batch) {
                    if (!Volatile.Read(ref thumbnailDemand).Contains(ticks)) continue;
                    Interlocked.Exchange(ref thumbnailInFlight, ticks);
                    token.ThrowIfCancellationRequested(); string path = cache.FilePath(key, ticks + ".png");
                    cache.Pin(path);
                    try {
                        if (!cache.Use(path)) {
                            if (!resized) { media.Resize((uint)Math.Max(2, Math.Round(snapshot.Canvas.Width * ratio)), (uint)Math.Max(2, Math.Round(snapshot.Canvas.Height * ratio))); resized = true; }
                            string temporary = path + ".partial"; cache.Pin(temporary);
                            try { media.Render(ticks, png: temporary); token.ThrowIfCancellationRequested(); File.Move(temporary, path, true); Interlocked.Increment(ref thumbnailRenderCount); }
                            finally { cache.Unpin(temporary); try { File.Delete(temporary); } catch (IOException) { } }
                        }
                        token.ThrowIfCancellationRequested();
                        DispatcherQueue.TryEnqueue(() => {
                            if (token.IsCancellationRequested) return;
                            pendingThumbnails.Remove(ticks); timeline.AddThumbnail(ticks, path);
                            if (ticks == position || BackgroundPreview.Source is null) BackgroundPreview.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path));
                        });
                    } finally { cache.Unpin(path); Interlocked.Exchange(ref thumbnailInFlight, -1); }
                    if (++rendered % 16 == 0) cache.Trim();
                }
            }
        } catch (OperationCanceledException) { }
        catch (Exception error) { DispatcherQueue.TryEnqueue(() => { if (!token.IsCancellationRequested) ShowNotice("缩略图准备未完成", error.Message, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning); }); }
    }
}
