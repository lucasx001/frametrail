using FrameTrail.Domain;
using FrameTrail.Interop;
using FrameTrail.Workspace;
using Xunit;

namespace FrameTrail.Workspace.Tests;

public sealed class SrtVttCodecTests
{
    private const long Sec = SrtVttCodec.TicksPerSecond;

    [Fact]
    public void ParseSrtHandlesUtf8BomHtmlTagsAndBilingualLines()
    {
        const string srt = "\uFEFF1\r\n" +
                           "00:00:01,250 --> 00:00:03,500\r\n" +
                           "<i>欢迎使用</i> <b>FrameTrail &amp; AI</b>\r\n" +
                           "Welcome to FrameTrail &amp; AI\r\n" +
                           "\r\n" +
                           "2\r\n" +
                           "00:00:04,000 --> 00:00:06,200\r\n" +
                           "Second line only\r\n";

        var cues = SrtVttCodec.Parse(srt);
        Assert.Equal(2, cues.Count);

        Assert.Equal(12_500_000L, cues[0].StartTicks);
        Assert.Equal(35_000_000L, cues[0].EndTicks);
        Assert.Equal("欢迎使用 FrameTrail & AI", cues[0].Text);
        Assert.Equal("Welcome to FrameTrail & AI", cues[0].SecondaryText);

        Assert.Equal(40_000_000L, cues[1].StartTicks);
        Assert.Equal(62_000_000L, cues[1].EndTicks);
        Assert.Equal("Second line only", cues[1].Text);
        Assert.Null(cues[1].SecondaryText);
    }

    [Fact]
    public void ParseVttSkipsHeaderNotesStylesAndSupportsShortTimestamps()
    {
        const string vtt = "WEBVTT - Demo\n" +
                           "\n" +
                           "NOTE This comment should be ignored\n" +
                           "\n" +
                           "STYLE\n" +
                           "::cue { color: white; }\n" +
                           "\n" +
                           "intro-cue\n" +
                           "00:01.500 --> 00:03.750 align:center position:50%\n" +
                           "<c.highlight>Hello</c> <00:02.000>world\n" +
                           "\n" +
                           "01:02:03.125 --> 01:02:05.000\n" +
                           "Hour mark\n";

        var cues = SrtVttCodec.Parse(vtt);
        Assert.Equal(2, cues.Count);
        Assert.Equal(15_000_000L, cues[0].StartTicks);
        Assert.Equal(37_500_000L, cues[0].EndTicks);
        Assert.Equal("Hello world", cues[0].Text);
        Assert.Equal((3600L + 123L) * Sec + 1_250_000L, cues[1].StartTicks);
        Assert.Equal("Hour mark", cues[1].Text);
    }

    [Fact]
    public void ExportProjectsSourceSubtitlesAcrossCutsReorderingAnd2xSpeed()
    {
        // Source timeline is 10s.
        // Output timeline:
        //   Clip A: source [5s..9s] at 2x speed -> output [0s..2s] (rate 2/1)
        //   Clip B: source [0s..3s] at 1x speed -> output [2s..5s] (rate 1/1)
        // Note: source [3s..5s] and [9s..10s] were cut out!
        var timeline = new ResolvedTimeline(
            Duration: 5 * Sec,
            Clips:
            [
                new ResolvedClip("clip-a", SourceStart: 5 * Sec, SourceDuration: 4 * Sec, OutputStart: 0, OutputDuration: 2 * Sec, Numerator: 2, Denominator: 1),
                new ResolvedClip("clip-b", SourceStart: 0, SourceDuration: 3 * Sec, OutputStart: 2 * Sec, OutputDuration: 3 * Sec, Numerator: 1, Denominator: 1)
            ],
            Markers: Array.Empty<ResolvedMarker>());

        var snapshot = new ProjectSnapshot
        {
            ProjectId = "srt-proj",
            Revision = 1,
            SourceDuration = MediaTime.FromTicks(10 * Sec),
            Editor = new EditorSettings { SubtitleStyle = SubtitleStyleSettings.BilingualClean() },
            Effects =
            [
                // Inside Clip B: source [1s..2.5s] -> output [3s..4.5s]
                MakeSub("sub-1", 1 * Sec, 15_000_000L, "第一句", "First line"),
                // Completely cut out: source [3.5s..4.5s] -> excluded!
                MakeSub("sub-cut", 35_000_000L, 10_000_000L, "被剪掉的句子"),
                // Inside Clip A (2x speed): source [6s..8s] -> offset 1s..3s in clip -> output [0.5s..1.5s]!
                MakeSub("sub-2", 6 * Sec, 2 * Sec, "加速片段字幕", "Fast clip caption")
            ]
        };

        var projected = SrtVttCodec.ProjectToOutputCues(snapshot, timeline);
        Assert.Equal(2, projected.Count);

        // Clip A's subtitle comes first on output timeline at [0.5s .. 1.5s]
        Assert.Equal(5_000_000L, projected[0].StartTicks);
        Assert.Equal(15_000_000L, projected[0].EndTicks);
        Assert.Equal("加速片段字幕", projected[0].Text);
        Assert.Equal("Fast clip caption", projected[0].SecondaryText);

        // Clip B's subtitle comes second at [3.0s .. 4.5s]
        Assert.Equal(30_000_000L, projected[1].StartTicks);
        Assert.Equal(45_000_000L, projected[1].EndTicks);
        Assert.Equal("第一句", projected[1].Text);
        Assert.Equal("First line", projected[1].SecondaryText);

        string srt = SrtVttCodec.Export(snapshot, timeline, SubtitleExportFormat.Srt);
        Assert.Contains("00:00:00,500 --> 00:00:01,500", srt);
        Assert.Contains("00:00:03,000 --> 00:00:04,500", srt);
        Assert.DoesNotContain("被剪掉的句子", srt);

        string vtt = SrtVttCodec.Export(snapshot, timeline, SubtitleExportFormat.Vtt);
        Assert.StartsWith("WEBVTT", vtt);
        Assert.Contains("00:00:00.500 --> 00:00:01.500", vtt);

        string txt = SrtVttCodec.Export(snapshot, timeline, SubtitleExportFormat.Txt);
        Assert.Contains("加速片段字幕", txt);
        Assert.Contains("第一句", txt);
        Assert.DoesNotContain("-->", txt);
    }

    [Fact]
    public void ImportToSourceEffectsMapsOutputTimesBackThroughSpeedAndOffsets()
    {
        // Clip 1: source [2s..6s] at 2x speed -> output [0s..2s]
        var timeline = new ResolvedTimeline(
            Duration: 2 * Sec,
            Clips:
            [
                new ResolvedClip("clip-1", SourceStart: 2 * Sec, SourceDuration: 4 * Sec, OutputStart: 0, OutputDuration: 2 * Sec, Numerator: 2, Denominator: 1)
            ],
            Markers: Array.Empty<ResolvedMarker>());

        const string srt = "1\n00:00:00,500 --> 00:00:01,500\nMapped back\n";
        var effects = SrtVttCodec.ImportToSourceEffects(srt, timeline, maxSourceDurationTicks: 10 * Sec);

        var eff = Assert.Single(effects);
        Assert.Equal("subtitle", eff.Kind);
        // Output 0.5s..1.5s at 2x speed from sourceStart 2s => source 3s..5s (duration 2s)
        Assert.Equal(3 * Sec, eff.SourceStart.Ticks);
        Assert.Equal(2 * Sec, eff.SourceDuration.Ticks);
        Assert.Equal("Mapped back", SubtitleCueParameters.FromEffect(eff).Text);
    }

    private static SourceEffect MakeSub(string id, long startTicks, long durationTicks, string text, string? secondary = null) => new()
    {
        Id = id,
        Kind = "subtitle",
        SourceStart = MediaTime.FromTicks(startTicks),
        SourceDuration = MediaTime.FromTicks(durationTicks),
        Parameters = new SubtitleCueParameters { Text = text, SecondaryText = secondary }.ToJsonElement()
    };
}
