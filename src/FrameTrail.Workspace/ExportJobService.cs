using FrameTrail.Domain;
using FrameTrail.Interop;

namespace FrameTrail.Workspace;

/// <summary>Runs one frozen media export, retaining the session until native work has stopped.</summary>
public sealed class ExportJobService
{
    public interface IMediaJob : IDisposable
    {
        MediaStatus Status { get; }
        void Export(string path, bool audioOnly);
        void Cancel();
    }

    private sealed class NativeJob(ProjectSnapshot snapshot, string directory) : IMediaJob
    {
        private readonly MediaSession media = new(snapshot, directory);
        public MediaStatus Status => media.Status;
        public void Export(string path, bool audioOnly) => media.Export(path, audioOnly);
        public void Cancel() => media.Cancel();
        public void Dispose() => media.Dispose();
    }

    private readonly Func<ProjectSnapshot, string, IMediaJob> create;
    public ExportJobService() : this((snapshot, directory) => new NativeJob(snapshot, directory)) { }
    public ExportJobService(Func<ProjectSnapshot, string, IMediaJob> create) => this.create = create;

    public async Task RunAsync(ProjectSnapshot snapshot, string directory, string path, bool audioOnly,
        IProgress<MediaStatus>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frozen = ProjectJson.Deserialize(ProjectJson.Serialize(snapshot));
        using var media = await Task.Run(() => create(frozen, directory), cancellationToken);
        using var registration = cancellationToken.Register(media.Cancel);
        var work = Task.Run(() => media.Export(path, audioOnly), CancellationToken.None);
        try
        {
            while (!work.IsCompleted)
            {
                var status = media.Status;
                progress?.Report(status);
                if (status.Total > 0 && Environment.TickCount64 - (long)status.LastProgressMilliseconds > 120_000)
                    media.Cancel();
                await Task.WhenAny(work, Task.Delay(150, CancellationToken.None));
            }
            await work;
            progress?.Report(media.Status);
        }
        catch
        {
            // A failing progress consumer must not dispose a session while its native export still uses it.
            media.Cancel();
            try { await work; } catch { }
            throw;
        }
    }
}
