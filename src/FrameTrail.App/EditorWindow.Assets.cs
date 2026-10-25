using FrameTrail.Domain;
using FrameTrail.Interop;
using System.Text.Json;
using System.Threading.Channels;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    private EditorMediaCache? mediaCache;
    private string? audioCacheKey;
    private string? pinnedAudio;
    private int audioPreparationCount, thumbnailRenderCount;
    private Task? audioPreparationBarrier;
    private bool IsWorkflowAudioPending => !assetCoordinator.AudioCompleted;
    private void RequestThumbnails(IReadOnlyList<long> ticks) => assetCoordinator.RequestThumbnails(ticks);
    private async Task StopAssetsAsync()
    {
        await assetCoordinator.StopAsync(transport.StopWork);
        mediaCache?.Unpin(pinnedAudio);
        pinnedAudio = null;
    }

    private async Task StartAssetsAsync(bool prepareAudio, bool updateThumbnails)
    {
        mediaCache ??= new EditorMediaCache(editor.DirectoryPath);
        var snapshot = editor.Snapshot;
        mediaCache.ProtectSources(snapshot.Media.Select(m=>m.Path).Concat(FrameTrail.Workspace.VisualResources.Paths(snapshot)));
        if (updateThumbnails) {
            await assetCoordinator.ReplaceThumbnailsAsync((queue, token) => BuildThumbnailsAsync(snapshot, queue, token));
            timeline.InvalidateThumbnails();
        }
        if (prepareAudio) {
            string key = EditorMediaCache.AudioKey(snapshot, editor.DirectoryPath);
            if (audioCacheKey != key || assetCoordinator.AudioCompleted && preparedAudio is null) {
                await assetCoordinator.ReplaceAudioAsync(() => {
                    mediaCache.Unpin(pinnedAudio);
                    preparedAudio = null;
                    timeline.SetWaveform(null);
                    audioCacheKey = key;
                    pinnedAudio = mediaCache.FilePath(key, "peaks.json");
                }, token => BuildAudioAsync(snapshot, key, token));
            }
        }
    }
    private async Task BuildAudioAsync(ProjectSnapshot snapshot, string key, CancellationToken token)
    {
        string? partial = null, pinned = null;
        try {
            if (audioPreparationBarrier is not null) await audioPreparationBarrier.WaitAsync(token);
            if (!snapshot.Media.Any(m => m.Kind is MediaKind.SystemAudio or MediaKind.Microphone or MediaKind.AndroidAudio)) {
                DispatcherQueue.TryEnqueue(() => { if (!token.IsCancellationRequested) { timeline.SetWaveform(Array.Empty<float>()); AudioPreparationLabel.Text = ""; } }); return;
            }
            var cache = mediaCache!; string path = cache.FilePath(key, "peaks.json"); float[]? peaks = null;
            cache.Pin(path); pinned = path;
            async Task<float[]> ReadPeaksAsync() {
                if (new FileInfo(path).Length > 1024 * 1024) throw new IOException(L.T("SF33B2C16A362"));
                var data=JsonSerializer.Deserialize<float[]>(await File.ReadAllTextAsync(path,token));
                if(data is null || data.Length is 0 or > 24000 || data.Any(p=>!float.IsFinite(p)||p<0||p>1))throw new IOException(L.T("S62448511B275"));
                return data;
            }
            if(cache.Use(path)) {try {peaks=await ReadPeaksAsync();}catch(Exception error) when(error is IOException or JsonException) { }}
            if (peaks is null) {
                partial = path + ".partial"; cache.Pin(partial);
                using var media = new MediaSession(snapshot, editor.DirectoryPath); using var registration = token.Register(media.Cancel);
                Interlocked.Increment(ref audioPreparationCount);
                var job = Task.Run(() => media.PreparePeaks(partial), token);
                while (!job.IsCompleted) {
                    var status = media.Status;
                    int percent = status.Total == 0 ? 0 : (int)(100.0 * status.Completed / status.Total);
                    DispatcherQueue.TryEnqueue(() => { if (!token.IsCancellationRequested) AudioPreparationLabel.Text = L.T("S57624B8AA29E", percent); });
                    await Task.WhenAny(job, Task.Delay(250));
                }
                await job; token.ThrowIfCancellationRequested(); File.Move(partial, path, true);
            }
            peaks ??= await ReadPeaksAsync();
            token.ThrowIfCancellationRequested();
            DispatcherQueue.TryEnqueue(() => { if (!token.IsCancellationRequested) { preparedAudio = path; timeline.SetWaveform(peaks); AudioPreparationLabel.Text = ""; UpdateState(); } });
            cache.Trim();
        } catch (OperationCanceledException) { }
        catch (Exception error) { DispatcherQueue.TryEnqueue(() => { if (!token.IsCancellationRequested) { AudioPreparationLabel.Text = L.T("S232C31FF306B"); ShowNotice(L.T("SD34B24A33A32"), error.Message, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning); } }); }
        finally { mediaCache?.Unpin(pinned); if (partial is not null) { mediaCache!.Unpin(partial); try { File.Delete(partial); } catch (IOException) { } } }
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
                    if (!assetCoordinator.WantsThumbnail(ticks)) continue;
                    assetCoordinator.BeginThumbnail(ticks);
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
                            assetCoordinator.PublishedThumbnail(ticks); timeline.AddThumbnail(ticks, path);
                            if (ticks == position || BackgroundPreview.Source is null) BackgroundPreview.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path));
                        });
                    } finally { cache.Unpin(path); assetCoordinator.EndThumbnail(); }
                    if (++rendered % 16 == 0) cache.Trim();
                }
            }
        } catch (OperationCanceledException) { }
        catch (Exception error) { DispatcherQueue.TryEnqueue(() => { if (!token.IsCancellationRequested) ShowNotice(L.T("SDB9B0D95B298"), error.Message, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning); }); }
    }
}
