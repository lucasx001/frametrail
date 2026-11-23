using FrameTrail.Domain;
using FrameTrail.Interop;
using Xunit;

namespace FrameTrail.Workspace.Tests;

public sealed class CreateFromVideoClipsTests : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "FrameTrail-NewVideoProj-" + Guid.NewGuid().ToString("N"));

    public CreateFromVideoClipsTests()
    {
        Directory.CreateDirectory(tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
    }

    private string CreateDummyVideo(string fileName, int byteCount = 256)
    {
        string path = Path.Combine(tempDir, fileName);
        byte[] data = new byte[byteCount];
        // Minimal MP4/MOV container with ftyp (20 bytes) + moov (rest of file) so HasMp4MoovAsync passes.
        data[3] = 20;
        data[4] = (byte)'f'; data[5] = (byte)'t'; data[6] = (byte)'y'; data[7] = (byte)'p';
        int moovSize = byteCount - 20;
        data[20] = (byte)(moovSize >> 24);
        data[21] = (byte)(moovSize >> 16);
        data[22] = (byte)(moovSize >> 8);
        data[23] = (byte)moovSize;
        data[24] = (byte)'m'; data[25] = (byte)'o'; data[26] = (byte)'o'; data[27] = (byte)'v';
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public void ResolveUniqueProjectPath_DerivesFromVideoAndDeduplicates()
    {
        string video = Path.Combine(tempDir, "Demo Video 01.mp4");
        string first = ProjectWorkspace.ResolveUniqueProjectPath(tempDir, null, video);
        Assert.Equal(Path.Combine(tempDir, "Demo Video 01.frametrailproject"), first);

        Directory.CreateDirectory(first);
        string second = ProjectWorkspace.ResolveUniqueProjectPath(tempDir, null, video);
        Assert.Equal(Path.Combine(tempDir, "Demo Video 01 (2).frametrailproject"), second);

        string named = ProjectWorkspace.ResolveUniqueProjectPath(tempDir, "My Custom Edit", video);
        Assert.Equal(Path.Combine(tempDir, "My Custom Edit.frametrailproject"), named);

        Assert.Throws<InvalidOperationException>(() =>
            ProjectWorkspace.ResolveUniqueProjectPath(tempDir, "bad:name*", video));
    }

    [Fact]
    public async Task CreateFromVideoClipsAsync_CreatesValidEditableReopenableProject()
    {
        string clipA = CreateDummyVideo("intro.mp4");
        string clipB = CreateDummyVideo("body.mov");
        string projectPath = ProjectWorkspace.ResolveUniqueProjectPath(tempDir, null, clipA);

        int probeCalls = 0;
        (uint Width, uint Height, long Duration, bool HasAudio, uint SampleRate, uint Channels) StubProbe(string path)
        {
            probeCalls++;
            if (probeCalls == 1)
                return (1920u, 1080u, 50_000_000L, true, 48000u, 2u); // 5.0s with stereo audio
            return (1280u, 720u, 30_000_000L, false, 0u, 0u);          // 3.0s video-only
        }

        await using (var workspace = await ProjectWorkspace.CreateFromVideoClipsAsync(
            projectPath,
            [clipA, clipB],
            validator: EngineSession.ValidateProject,
            mediaProbe: (_, _) => { },
            clipProbe: StubProbe))
        {
            Assert.NotNull(workspace.Snapshot);
            var snap = workspace.Snapshot!;
            Assert.Equal(1, snap.SchemaVersion);
            Assert.Equal(80_000_000L, VisualResources.Ticks(snap.SourceDuration));
            Assert.Equal(1920u, snap.Canvas.Width);
            Assert.Equal(1080u, snap.Canvas.Height);
            Assert.False(snap.Canvas.Camera.Enabled);
            Assert.Equal(2, snap.Segments.Count);
            Assert.Equal(3, snap.Media.Count); // 2 screen + 1 systemAudio
            Assert.Contains(snap.Media, m => m.Kind == MediaKind.SystemAudio);
            Assert.All(snap.Media, m => Assert.StartsWith("media/import-", m.Path, StringComparison.Ordinal));

            var timeline = EngineSession.ResolveTimeline(snap);
            Assert.Equal(80_000_000L, timeline.Duration);
            Assert.Equal(2, timeline.Clips.Count);

            // Verify immediate editing works on the newly created project (split + undo)
            var session = new EditorSession(workspace);
            await session.ExecuteAsync(new { op = "split", segmentId = snap.Segments[0].Id, sourceTicks = 20_000_000L, newId = "split-1" });
            Assert.Equal(3, session.Snapshot.Segments.Count);
            await session.UndoAsync();
            Assert.Equal(2, session.Snapshot.Segments.Count);
            await session.FlushAsync();
        }

        // Verify the project can be reopened cleanly from disk
        await using var reopened = await ProjectWorkspace.OpenAsync(
            projectPath,
            validator: EngineSession.ValidateProject,
            mediaProbe: (_, _) => { });
        Assert.NotNull(reopened.Snapshot);
        Assert.Equal(2, reopened.Snapshot!.Segments.Count);
    }

    [Fact]
    public async Task CreateFromVideoClipsAsync_RollsBackDirectoryOnUnsupportedExtensionOrProbeFailure()
    {
        string badExt = CreateDummyVideo("notes.txt");
        string projectPath1 = Path.Combine(tempDir, "should-not-exist-1.frametrailproject");
        await Assert.ThrowsAsync<IOException>(() =>
            ProjectWorkspace.CreateFromVideoClipsAsync(projectPath1, [badExt], mediaProbe: (_, _) => { }));
        Assert.False(Directory.Exists(projectPath1));

        string validFile = CreateDummyVideo("corrupt.mp4");
        string projectPath2 = Path.Combine(tempDir, "should-not-exist-2.frametrailproject");
        await Assert.ThrowsAsync<IOException>(() =>
            ProjectWorkspace.CreateFromVideoClipsAsync(
                projectPath2,
                [validFile],
                mediaProbe: (_, _) => { },
                clipProbe: _ => (0u, 0u, 0L, false, 0u, 0u)));
        Assert.False(Directory.Exists(projectPath2));
    }
}
