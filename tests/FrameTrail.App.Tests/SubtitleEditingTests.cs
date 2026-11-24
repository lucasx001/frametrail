using FrameTrail.App;
using FrameTrail.Domain;
using FrameTrail.Workspace;
using Xunit;

namespace FrameTrail.App.Tests;

public sealed class SubtitleEditingTests
{
    private static SourceEffect MakeCue(
        string id,
        long startSec,
        long durSec,
        string text,
        string? secondary = null,
        IReadOnlyList<SubtitleWordTiming>? words = null,
        SubtitleCueStyleOverride? styleOverride = null)
    {
        var p = new SubtitleCueParameters
        {
            Text = text,
            SecondaryText = secondary,
            Words = words,
            StyleOverride = styleOverride
        };
        return new SourceEffect
        {
            Id = id,
            Kind = "subtitle",
            SourceStart = MediaTime.FromTicks(startSec * SubtitleCueMath.TicksPerSecond),
            SourceDuration = MediaTime.FromTicks(durSec * SubtitleCueMath.TicksPerSecond),
            Parameters = p.ToJsonElement()
        };
    }

    [Fact]
    public void CanvasSnap_SnapsCenterXWithinThreshold()
    {
        // Width 0.40 -> centered X is 0.30. Offset by +0.010 (within 0.015).
        var rect = new NormalizedRect { X = 0.310, Y = 0.50, Width = 0.40, Height = 0.10 };
        var result = SubtitleCanvasSnap.Snap(rect, 16.0 / 9.0);

        Assert.True(result.SnappedCenterX);
        Assert.Equal(0.5, result.GuideX, 4);
        Assert.Equal(0.30, result.Rect.X, 4);
        Assert.False(result.SnappedSafeY);
    }

    [Fact]
    public void CanvasSnap_DoesNotSnapCenterXOutsideThreshold()
    {
        // Width 0.40 -> centered X is 0.30. Offset by +0.025 (outside 0.015).
        var rect = new NormalizedRect { X = 0.325, Y = 0.50, Width = 0.40, Height = 0.10 };
        var result = SubtitleCanvasSnap.Snap(rect, 16.0 / 9.0);

        Assert.False(result.SnappedCenterX);
        Assert.Equal(0.325, result.Rect.X, 4);
    }

    [Fact]
    public void CanvasSnap_LandscapeSnapsToBottomSafeZone090()
    {
        // Height 0.10 -> bottom = 0.892 (within 0.015 of 0.90)
        var rect = new NormalizedRect { X = 0.10, Y = 0.792, Width = 0.40, Height = 0.10 };
        var result = SubtitleCanvasSnap.Snap(rect, 16.0 / 9.0);

        Assert.True(result.SnappedSafeY);
        Assert.Equal(0.90, result.GuideY, 4);
        Assert.Equal(0.80, result.Rect.Y, 4);
        Assert.Equal(0.90, result.Rect.Y + result.Rect.Height, 4);
    }

    [Fact]
    public void CanvasSnap_PortraitSnapsToSafeZone080()
    {
        // 9:16 portrait -> safe zone Y = 0.80; test center-Y snap near 0.80
        var rect = new NormalizedRect { X = 0.305, Y = 0.758, Width = 0.40, Height = 0.10 };
        // centerY = 0.808 (within 0.015 of 0.80), bottomY = 0.858 (outside)
        var result = SubtitleCanvasSnap.Snap(rect, 9.0 / 16.0);

        Assert.True(result.SnappedCenterX);
        Assert.True(result.SnappedSafeY);
        Assert.Equal(0.80, result.GuideY, 4);
        Assert.Equal(0.75, result.Rect.Y, 4);
    }

    [Fact]
    public void SplitByCaret_DividesDurationAndRebasesWords()
    {
        var words = new[]
        {
            new SubtitleWordTiming { Text = "Hello", StartOffset = 0.0, EndOffset = 1.0 },
            new SubtitleWordTiming { Text = "world", StartOffset = 1.0, EndOffset = 2.0 },
            new SubtitleWordTiming { Text = "today", StartOffset = 2.5, EndOffset = 4.0 }
        };
        var ov = new SubtitleCueStyleOverride { TextColor = "#FACC15" };
        var cue = MakeCue("sub-1", 10, 4, "Hello world today", "你好世界今天", words, ov);

        // Split right after "Hello world" (index 11 of 17)
        var split = SubtitleCueMath.SplitByCaret(cue, "Hello world today", 11, secondId: "sub-2");
        Assert.NotNull(split);

        var (first, second) = split.Value;
        Assert.Equal("sub-1", first.Id);
        Assert.Equal("sub-2", second.Id);
        Assert.Equal(cue.SourceStart.Ticks, first.SourceStart.Ticks);
        Assert.Equal(cue.SourceDuration.Ticks, first.SourceDuration.Ticks + second.SourceDuration.Ticks);
        Assert.Equal(first.SourceStart.Ticks + first.SourceDuration.Ticks, second.SourceStart.Ticks);

        var p1 = SubtitleCueParameters.FromEffect(first);
        var p2 = SubtitleCueParameters.FromEffect(second);
        Assert.Equal("Hello world", p1.Text);
        Assert.Equal("today", p2.Text);
        Assert.Equal("#FACC15", p1.StyleOverride?.TextColor);
        Assert.Equal("#FACC15", p2.StyleOverride?.TextColor);
        Assert.NotNull(p1.Words);
        Assert.NotNull(p2.Words);
        Assert.Equal(2, p1.Words!.Count);
        Assert.Single(p2.Words!);
        Assert.True(p2.Words[0].StartOffset >= 0);
        Assert.True(p2.Words[0].EndOffset <= second.SourceDuration.Ticks / (double)SubtitleCueMath.TicksPerSecond + 0.001);
    }

    [Fact]
    public void SplitByCaret_InvalidBoundsReturnNull()
    {
        var cue = MakeCue("sub-1", 0, 2, "Hello");
        Assert.Null(SubtitleCueMath.SplitByCaret(cue, "Hello", 0));
        Assert.Null(SubtitleCueMath.SplitByCaret(cue, "Hello", 5));
        Assert.Null(SubtitleCueMath.SplitByCaret(cue, "   Hello", 2));
    }

    [Fact]
    public void SplitAtSourceTime_UsesWordBoundariesWhenPresent()
    {
        var words = new[]
        {
            new SubtitleWordTiming { Text = "FrameTrail", StartOffset = 0.0, EndOffset = 0.8 },
            new SubtitleWordTiming { Text = "renders", StartOffset = 0.9, EndOffset = 1.6 },
            new SubtitleWordTiming { Text = "subtitles", StartOffset = 2.2, EndOffset = 3.0 }
        };
        var cue = MakeCue("sub-1", 5, 3, "FrameTrail renders subtitles", words: words);

        // Split at 7.0s (offset 2.0s inside cue)
        var split = SubtitleCueMath.SplitAtSourceTime(cue, 7 * SubtitleCueMath.TicksPerSecond, secondId: "sub-2");
        Assert.NotNull(split);
        var (first, second) = split.Value;

        Assert.Equal(2 * SubtitleCueMath.TicksPerSecond, first.SourceDuration.Ticks);
        Assert.Equal(7 * SubtitleCueMath.TicksPerSecond, second.SourceStart.Ticks);
        Assert.Equal(1 * SubtitleCueMath.TicksPerSecond, second.SourceDuration.Ticks);

        var p1 = SubtitleCueParameters.FromEffect(first);
        var p2 = SubtitleCueParameters.FromEffect(second);
        Assert.Equal("FrameTrail renders", p1.Text);
        Assert.Equal("subtitles", p2.Text);
    }

    [Fact]
    public void MergeCues_CombinesChronologicallyAndRebasesWords()
    {
        var w1 = new[] { new SubtitleWordTiming { Text = "Hello", StartOffset = 0.1, EndOffset = 0.9 } };
        var w2 = new[] { new SubtitleWordTiming { Text = "world", StartOffset = 0.2, EndOffset = 0.8 } };
        var c1 = MakeCue("sub-1", 2, 2, "Hello", "你好", w1);
        var c2 = MakeCue("sub-2", 4, 3, "world", "世界", w2);

        // Pass in reverse order to verify chronological sorting
        var merged = SubtitleCueMath.MergeCues(c2, c1);
        Assert.Equal("sub-1", merged.Id);
        Assert.Equal(2 * SubtitleCueMath.TicksPerSecond, merged.SourceStart.Ticks);
        Assert.Equal(5 * SubtitleCueMath.TicksPerSecond, merged.SourceDuration.Ticks);

        var mp = SubtitleCueParameters.FromEffect(merged);
        Assert.Equal("Hello world", mp.Text);
        Assert.Equal("你好世界", mp.SecondaryText);
        Assert.NotNull(mp.Words);
        Assert.Equal(2, mp.Words!.Count);
        Assert.Equal(2.2, mp.Words[1].StartOffset, 3);
        Assert.Equal(2.8, mp.Words[1].EndOffset, 3);
    }

    [Fact]
    public void FindAndReplace_UpdatesMatchingCuesAndClearsStaleWords()
    {
        var w1 = new[] { new SubtitleWordTiming { Text = "frame", StartOffset = 0.0, EndOffset = 0.5 } };
        var c1 = MakeCue("sub-1", 0, 2, "Frame trail demo", "frame demo", w1);
        var c2 = MakeCue("sub-2", 2, 2, "Unchanged line", words: w1);

        var (updated, count) = SubtitleCueMath.FindAndReplace([c1, c2], "frame", "FrameTrail");
        Assert.Equal(2, count);

        var p1 = SubtitleCueParameters.FromEffect(updated[0]);
        var p2 = SubtitleCueParameters.FromEffect(updated[1]);
        Assert.Equal("FrameTrail trail demo", p1.Text);
        Assert.Equal("FrameTrail demo", p1.SecondaryText);
        Assert.Null(p1.Words); // cleared because primary text changed
        Assert.Equal("Unchanged line", p2.Text);
        Assert.NotNull(p2.Words); // preserved on untouched cue
    }

    private static ProjectSnapshot MakeSnapshot(long totalSec, params SourceEffect[] effects) => new()
    {
        ProjectId = "proj-sub-cut",
        SourceDuration = MediaTime.FromTicks(totalSec * SubtitleCueMath.TicksPerSecond),
        Media =
        [
            new MediaAsset
            {
                Id = "screen-1",
                Kind = MediaKind.Screen,
                Path = "media/screen.mp4",
                Duration = MediaTime.FromTicks(totalSec * SubtitleCueMath.TicksPerSecond),
                Width = 1920,
                Height = 1080
            }
        ],
        Segments =
        [
            new RecordingSegment
            {
                Id = "seg-1",
                SourceStart = MediaTime.FromTicks(0),
                SourceDuration = MediaTime.FromTicks(totalSec * SubtitleCueMath.TicksPerSecond)
            }
        ],
        Effects = effects
    };

    [Fact]
    public void ResolveSubtitleCutRanges_SelectedCues_ExtractsExactRanges()
    {
        var c1 = MakeCue("sub-1", 1, 2, "First");
        var c2 = MakeCue("sub-2", 4, 3, "Second");
        var c3 = MakeCue("sub-3", 8, 2, "Third");
        var snap = MakeSnapshot(15, c1, c2, c3);

        var ranges = VisualResources.ResolveSubtitleCutRanges(snap, ["sub-1", "sub-3"]);
        Assert.Equal(2, ranges.Count);
        Assert.Equal((1 * SubtitleCueMath.TicksPerSecond, 3 * SubtitleCueMath.TicksPerSecond), ranges[0]);
        Assert.Equal((8 * SubtitleCueMath.TicksPerSecond, 10 * SubtitleCueMath.TicksPerSecond), ranges[1]);
    }

    [Fact]
    public void ResolveSubtitleCutRanges_OverlappingAndAdjacentCues_MergeChronologically()
    {
        var c1 = MakeCue("sub-1", 2, 3, "A"); // [2, 5)
        var c2 = MakeCue("sub-2", 4, 2, "B"); // [4, 6) overlaps
        var c3 = MakeCue("sub-3", 6, 2, "C"); // [6, 8) adjacent
        var c4 = MakeCue("sub-4", 10, 2, "D"); // [10, 12) separate
        var snap = MakeSnapshot(15, c4, c2, c3, c1);

        var ranges = VisualResources.ResolveSubtitleCutRanges(snap, ["sub-1", "sub-2", "sub-3", "sub-4"]);
        Assert.Equal(2, ranges.Count);
        Assert.Equal((2 * SubtitleCueMath.TicksPerSecond, 8 * SubtitleCueMath.TicksPerSecond), ranges[0]);
        Assert.Equal((10 * SubtitleCueMath.TicksPerSecond, 12 * SubtitleCueMath.TicksPerSecond), ranges[1]);
    }

    [Fact]
    public void ResolveSubtitleCutRanges_SearchQueryMatchesPrimaryOrSecondaryCaseInsensitive()
    {
        var c1 = MakeCue("sub-1", 1, 2, "Um hello", "你好");
        var c2 = MakeCue("sub-2", 4, 2, "Clean speech", "嗯 对的");
        var c3 = MakeCue("sub-3", 7, 2, "Another UM pause");
        var snap = MakeSnapshot(15, c1, c2, c3);

        var umRanges = VisualResources.ResolveSubtitleCutRanges(snap, [], matchingSearchQuery: "um");
        Assert.Equal(2, umRanges.Count);
        Assert.Equal((1 * SubtitleCueMath.TicksPerSecond, 3 * SubtitleCueMath.TicksPerSecond), umRanges[0]);
        Assert.Equal((7 * SubtitleCueMath.TicksPerSecond, 9 * SubtitleCueMath.TicksPerSecond), umRanges[1]);

        var secRanges = VisualResources.ResolveSubtitleCutRanges(snap, [], matchingSearchQuery: "嗯");
        Assert.Single(secRanges);
        Assert.Equal((4 * SubtitleCueMath.TicksPerSecond, 6 * SubtitleCueMath.TicksPerSecond), secRanges[0]);

        Assert.Empty(VisualResources.ResolveSubtitleCutRanges(snap, ["sub-1"], matchingSearchQuery: "   "));
    }

    [Fact]
    public void ResolveSubtitleCutRanges_WholeTimelineSafety_LeavesAtLeast100ms()
    {
        var c1 = MakeCue("sub-1", 0, 5, "First half");
        var c2 = MakeCue("sub-2", 5, 5, "Second half");
        var snap = MakeSnapshot(10, c1, c2);

        var ranges = VisualResources.ResolveSubtitleCutRanges(snap, ["sub-1", "sub-2"]);
        Assert.Single(ranges);
        Assert.Equal(0L, ranges[0].StartTicks);
        Assert.Equal(10 * SubtitleCueMath.TicksPerSecond - 1_000_000L, ranges[0].EndTicks);
    }
}
