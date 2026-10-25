using System.Diagnostics;
using System.Globalization;
using FrameTrail.Domain;
using FrameTrail.Interop;
using FrameTrail.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace FrameTrail.Workspace.Tests;

public sealed class IncrementalZoomPerformanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(180)]
    public async Task LongContinuousActivityIsReadOnceWithBoundedNativeBatches(int minutes)
    {
        string directory = Path.Combine(Path.GetTempPath(), "FrameTrail-Zoom-Long-" + Guid.NewGuid().ToString("N") + ".frametrailproject");
        try
        {
            await using var workspace = ProjectWorkspace.Create(directory, "long-input", EngineSession.ValidateProject, (_, _) => { });
            int count = minutes * 600;
            long duration = (long)minutes * TimeSpan.TicksPerMinute;
            var pointer = workspace.Resolve("events/long.jsonl");
            await using (var writer = new StreamWriter(pointer))
            {
                for (int i = 0; i < count; i++)
                {
                    string tick = ((long)i * 1_000_000).ToString(CultureInfo.InvariantCulture);
                    await writer.WriteLineAsync("{\"type\":\"pointer\",\"ticks\":" + tick +
                        ",\"timescale\":10000000,\"x\":" + (i % 2 == 0 ? "0.2" : "0.8") +
                        ",\"y\":0.4,\"inside\":true,\"kind\":" + (i == 10 ? "1" : "0") + "}");
                }
            }
            var snapshot = new ProjectSnapshot
            {
                ProjectId = "long-input", Revision = 1, SourceDuration = MediaTime.FromTicks(duration),
                Segments = new[] { new RecordingSegment { Id = "clip", SourceDuration = MediaTime.FromTicks(duration) } },
                Media = new[]
                {
                    new MediaAsset { Id = "screen", Kind = MediaKind.Screen, Path = "media/screen.mp4", Duration = MediaTime.FromTicks(duration), Width = 640, Height = 360 },
                    new MediaAsset { Id = "pointer", Kind = MediaKind.PointerEvents, Path = "events/long.jsonl", Duration = MediaTime.FromTicks(duration) }
                }
            };
            var planner = new AutomaticZoomPlanner();
            long allocated = GC.GetTotalAllocatedBytes(precise: true);
            var clock = Stopwatch.StartNew();
            var planned = await planner.AppendAsync(workspace, snapshot, CancellationToken.None);
            clock.Stop();
            allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
            Assert.Single(planned.Effects);
            Assert.Equal(1, planner.FilesRead);
            Assert.InRange(planner.MaximumBatchSize, 1, 8192);
            Assert.InRange(planner.ActivitiesRead, count - 2, count);
            Assert.Single(planned.Effects[0].Parameters.GetProperty("focusPoints").EnumerateArray());
            Assert.InRange(planned.Effects[0].Parameters.GetProperty("plannerState").GetProperty("recentActivity").GetArrayLength(), 1, 4);
            // No second read, including when the original event file is unavailable.
            File.Delete(pointer);
            var repeated = await planner.AppendAsync(workspace, planned, CancellationToken.None);
            Assert.Single(repeated.Effects); Assert.Equal(1, planner.FilesRead);
            output.WriteLine($"Synthetic {minutes} min: activities={planner.ActivitiesRead}; maxBatch={planner.MaximumBatchSize}; filesRead={planner.FilesRead}; elapsedMs={clock.ElapsedMilliseconds}; allocatedBytes={allocated}; savedEffectBytes={planned.Effects[0].Parameters.GetRawText().Length}");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
