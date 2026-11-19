using System.Buffers.Binary;
using FrameTrail.Domain;
using FrameTrail.Interop;
using FrameTrail.Workspace;
using Xunit;

namespace FrameTrail.Workspace.Tests;

public sealed class AiMultimodalServiceTests : IAsyncLifetime
{
    private static readonly byte[] FinalizedMp4Header = { 0, 0, 0, 12, 109, 111, 111, 118, 0, 0, 0, 0 };

    private readonly string root = Path.Combine(Path.GetTempPath(), "FrameTrail-AiMultimodal-" + Guid.NewGuid().ToString("N"));
    private readonly AiMultimodalService service = new();

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public void DetectWavSilence_FindsSilenceBetweenTonesIn16BitPcmWav()
    {
        const int sampleRate = 16000;
        string wavPath = Path.Combine(root, "mic-pcm16.wav");
        var samples = BuildToneSilenceToneSamples(sampleRate, tone1Sec: 1.0, silenceSec: 2.0, tone2Sec: 1.0);
        WritePcm16Wav(wavPath, sampleRate, channels: 1, samples);

        var intervals = service.DetectWavSilence(wavPath, minSilenceSeconds: 1.0, thresholdDb: -38.0);

        var silence = Assert.Single(intervals);
        Assert.InRange(silence.StartSeconds, 0.9, 1.1);
        Assert.InRange(silence.DurationSeconds, 1.9, 2.1);
        Assert.True(silence.AverageDb <= -60.0);
        Assert.True(silence.EndTicks > silence.StartTicks);
    }

    [Fact]
    public void DetectWavSilence_Supports32BitIeeeFloatWav()
    {
        const int sampleRate = 16000;
        string wavPath = Path.Combine(root, "sys-float32.wav");
        var samples = BuildToneSilenceToneSamples(sampleRate, tone1Sec: 0.5, silenceSec: 1.6, tone2Sec: 0.5);
        WriteIeeeFloat32Wav(wavPath, sampleRate, channels: 1, samples);

        var intervals = service.DetectWavSilence(wavPath, minSilenceSeconds: 1.2, thresholdDb: -38.0);

        var silence = Assert.Single(intervals);
        Assert.InRange(silence.StartSeconds, 0.4, 0.6);
        Assert.InRange(silence.DurationSeconds, 1.5, 1.7);
    }

    [Fact]
    public async Task ExtractPointerHotspotsAsync_ReadsPointerJsonlAndClustersClicksAndDwells()
    {
        string projectPath = Path.Combine(root, "pointer-test.frametrailproject");
        await using var workspace = ProjectWorkspace.Create(projectPath, "ai-pointer", EngineSession.ValidateProject, (_, _) => { });
        await File.WriteAllBytesAsync(workspace.Resolve("media/screen.mp4"), FinalizedMp4Header);

        string eventsFile = workspace.Resolve("events/pointer-1.jsonl");
        var lines = new[]
        {
            """{"type":"pointer","ticks":500000,"timescale":1000000,"x":0.10,"y":0.20,"kind":0,"inside":true}""",
            """{"type":"pointer","ticks":1200000,"timescale":1000000,"x":0.82,"y":0.14,"kind":1,"inside":true}""",
            """{"type":"pointer","ticks":1350000,"timescale":1000000,"x":0.83,"y":0.15,"kind":1,"inside":true}""",
            """{"type":"pointer","ticks":3000000,"timescale":1000000,"x":0.40,"y":0.50,"kind":0,"inside":true}""",
            """{"type":"pointer","ticks":4100000,"timescale":1000000,"x":0.41,"y":0.51,"kind":0,"inside":true}""",
            """{"type":"pointer","ticks":5500000,"timescale":1000000,"x":0.25,"y":0.75,"kind":3,"inside":true}""",
            """{"type":"pointer","ticks":6000000,"timescale":1000000,"x":0.99,"y":0.99,"kind":1,"inside":false}"""
        };
        await File.WriteAllLinesAsync(eventsFile, lines);

        var snapshot = new ProjectSnapshot
        {
            ProjectId = "ai-pointer",
            Revision = 1,
            SourceDuration = MediaTime.FromTicks(80_000_000),
            Media = new[]
            {
                new MediaAsset
                {
                    Id = "screen",
                    Kind = MediaKind.Screen,
                    Path = "media/screen.mp4",
                    StartOffset = MediaTime.FromTicks(0),
                    Duration = MediaTime.FromTicks(80_000_000),
                    Width = 1920,
                    Height = 1080
                },
                new MediaAsset
                {
                    Id = "pointer-1",
                    Kind = MediaKind.PointerEvents,
                    Path = "events/pointer-1.jsonl",
                    StartOffset = MediaTime.FromTicks(0),
                    Duration = MediaTime.FromTicks(80_000_000)
                }
            },
            Segments = new[]
            {
                new RecordingSegment
                {
                    Id = "clip-1",
                    SourceStart = MediaTime.FromTicks(0),
                    SourceDuration = MediaTime.FromTicks(80_000_000)
                }
            }
        };
        Assert.True(await workspace.SaveAsync(snapshot, workspace.SessionEpoch));

        var hotspots = await service.ExtractPointerHotspotsAsync(workspace, snapshot);

        Assert.Equal(3, hotspots.Count);

        // Clustered double left-click around 1.2s at (0.825, 0.145)
        Assert.Equal(12_000_000L, hotspots[0].Ticks);
        Assert.Equal(1, hotspots[0].ClickKind);
        Assert.InRange(hotspots[0].X, 0.82, 0.83);
        Assert.InRange(hotspots[0].Y, 0.14, 0.15);
        Assert.Equal(2.0, hotspots[0].SuggestedZoomScale);

        // Dwell cluster between 3.0s and 4.1s
        Assert.Equal(0, hotspots[1].ClickKind);
        Assert.InRange(hotspots[1].Seconds, 3.4, 3.7);
        Assert.Equal(1.5, hotspots[1].SuggestedZoomScale);

        // Single right-click at 5.5s
        Assert.Equal(55_000_000L, hotspots[2].Ticks);
        Assert.Equal(3, hotspots[2].ClickKind);
        Assert.Equal(0.25, hotspots[2].X);
        Assert.Equal(0.75, hotspots[2].Y);
    }

    [Fact]
    public async Task BuildRemoveSilenceBatchCommand_ExecutesAndValidatesCleanlyInEngine()
    {
        string projectPath = Path.Combine(root, "silence-cut.frametrailproject");
        await using var workspace = ProjectWorkspace.Create(projectPath, "ai-cut", EngineSession.ValidateProject, (_, _) => { });
        await File.WriteAllBytesAsync(workspace.Resolve("media/screen.mp4"), FinalizedMp4Header);

        var snapshot = new ProjectSnapshot
        {
            ProjectId = "ai-cut",
            Revision = 1,
            SourceDuration = MediaTime.FromTicks(100_000_000), // 10s
            Media = new[]
            {
                new MediaAsset
                {
                    Id = "screen",
                    Kind = MediaKind.Screen,
                    Path = "media/screen.mp4",
                    StartOffset = MediaTime.FromTicks(0),
                    Duration = MediaTime.FromTicks(100_000_000),
                    Width = 1280,
                    Height = 720
                }
            },
            Segments = new[]
            {
                new RecordingSegment
                {
                    Id = "clip-main",
                    SourceStart = MediaTime.FromTicks(0),
                    SourceDuration = MediaTime.FromTicks(100_000_000)
                }
            }
        };
        Assert.True(await workspace.SaveAsync(snapshot, workspace.SessionEpoch));

        var silentIntervals = new[]
        {
            new SilentInterval(20_000_000L, 40_000_000L, 2.0, 2.0, -75.0),
            new SilentInterval(75_000_000L, 95_000_000L, 7.5, 2.0, -72.0)
        };

        object batchCommand = service.BuildRemoveSilenceBatchCommand(snapshot, silentIntervals, paddingSeconds: 0.15);
        var edited = EngineSession.Edit(snapshot, batchCommand);
        EngineSession.ValidateProject(edited);

        Assert.Equal(snapshot.Revision + 1, edited.Revision);
        Assert.Equal(3, edited.Segments.Count);

        var timeline = EngineSession.ResolveTimeline(edited);
        // Removed (2.0 - 0.3) + (2.0 - 0.3) = 3.4s -> remaining 6.6s
        Assert.Equal(66_000_000L, timeline.Duration);

        // Edge case: silence spanning entire segment must still leave >= 1 valid segment.
        var fullSilence = new[] { new SilentInterval(0L, 100_000_000L, 0.0, 10.0, -90.0) };
        object safeCommand = service.BuildRemoveSilenceBatchCommand(snapshot, fullSilence, paddingSeconds: 0.0);
        var safeEdited = EngineSession.Edit(snapshot, safeCommand);
        EngineSession.ValidateProject(safeEdited);
        Assert.True(safeEdited.Segments.Count >= 1);
    }

    [Fact]
    public async Task TranscriptCache_RoundTripsAndMatchesWithHotspots()
    {
        string projectPath = Path.Combine(root, "transcript-test.frametrailproject");
        await using var workspace = ProjectWorkspace.Create(projectPath, "ai-asr", EngineSession.ValidateProject, (_, _) => { });
        await File.WriteAllBytesAsync(workspace.Resolve("media/screen.mp4"), FinalizedMp4Header);
        await File.WriteAllLinesAsync(workspace.Resolve("events/pointer-1.jsonl"), new[]
        {
            """{"type":"pointer","ticks":6800000,"timescale":1000000,"x":0.91,"y":0.08,"kind":1,"inside":true}"""
        });

        var snapshot = new ProjectSnapshot
        {
            ProjectId = "ai-asr",
            Revision = 1,
            SourceDuration = MediaTime.FromTicks(100_000_000),
            Media = new[]
            {
                new MediaAsset { Id = "screen", Kind = MediaKind.Screen, Path = "media/screen.mp4", StartOffset = MediaTime.FromTicks(0), Duration = MediaTime.FromTicks(100_000_000), Width = 1280, Height = 720 },
                new MediaAsset { Id = "ptr", Kind = MediaKind.PointerEvents, Path = "events/pointer-1.jsonl", StartOffset = MediaTime.FromTicks(0), Duration = MediaTime.FromTicks(100_000_000) }
            },
            Segments = new[]
            {
                new RecordingSegment { Id = "clip-1", SourceStart = MediaTime.FromTicks(0), SourceDuration = MediaTime.FromTicks(100_000_000) }
            }
        };

        var segments = new[]
        {
            new TranscriptSegment(5_000_000L, 32_000_000L, "大家好，今天演示一下如何录屏", "microphone"),
            new TranscriptSegment(60_000_000L, 85_000_000L, "首先我们点击右上角的导出按钮", "microphone")
        };

        await service.SaveTranscriptCacheAsync(workspace.DirectoryPath, segments);
        var loaded = await service.LoadTranscriptCacheAsync(workspace.DirectoryPath);
        Assert.Equal(segments, loaded);

        var matches = await service.SearchTranscriptAndHotspotsAsync(workspace, snapshot, "导出");
        var match = Assert.Single(matches);
        Assert.Equal(60_000_000L, match.Segment.StartTicks);
        Assert.Single(match.Hotspots);
        Assert.Equal(0.91, match.FocusX);
        Assert.Equal(0.08, match.FocusY);
    }

    private static float[] BuildToneSilenceToneSamples(int sampleRate, double tone1Sec, double silenceSec, double tone2Sec)
    {
        int n1 = (int)(sampleRate * tone1Sec);
        int ns = (int)(sampleRate * silenceSec);
        int n2 = (int)(sampleRate * tone2Sec);
        var data = new float[n1 + ns + n2];
        for (int i = 0; i < n1; i++)
            data[i] = 0.5f * MathF.Sin(2f * MathF.PI * 440f * i / sampleRate);
        for (int i = 0; i < n2; i++)
            data[n1 + ns + i] = 0.5f * MathF.Sin(2f * MathF.PI * 440f * i / sampleRate);
        return data;
    }

    private static void WritePcm16Wav(string path, int sampleRate, short channels, float[] monoSamples)
    {
        int dataBytes = monoSamples.Length * channels * 2;
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        bw.Write("RIFF"u8);
        bw.Write(36 + dataBytes);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * 2);
        bw.Write((short)(channels * 2));
        bw.Write((short)16);
        bw.Write("data"u8);
        bw.Write(dataBytes);
        Span<byte> sampleBuf = stackalloc byte[2];
        foreach (float s in monoSamples)
        {
            short pcm = (short)Math.Clamp((int)MathF.Round(s * 32767f), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(sampleBuf, pcm);
            for (int c = 0; c < channels; c++)
                bw.Write(sampleBuf);
        }
    }

    private static void WriteIeeeFloat32Wav(string path, int sampleRate, short channels, float[] monoSamples)
    {
        int dataBytes = monoSamples.Length * channels * 4;
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        bw.Write("RIFF"u8);
        bw.Write(36 + dataBytes);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)3); // IEEE float
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * 4);
        bw.Write((short)(channels * 4));
        bw.Write((short)32);
        bw.Write("data"u8);
        bw.Write(dataBytes);
        Span<byte> sampleBuf = stackalloc byte[4];
        foreach (float s in monoSamples)
        {
            BinaryPrimitives.WriteSingleLittleEndian(sampleBuf, s);
            for (int c = 0; c < channels; c++)
                bw.Write(sampleBuf);
        }
    }
}
