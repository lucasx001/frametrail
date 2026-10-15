using FrameTrail.Domain;
using FrameTrail.Interop;
using Xunit;

namespace FrameTrail.Domain.Tests;

public sealed class EditorCommandTests
{
    private static ProjectSnapshot Project() => ProjectJson.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "project-v1.json")));
    [Fact] public void SplitInheritsAudioAndPreservesAllUnrelatedContent()
    {
        var p = Project() with { Editor = new() { ClipAudio = new Dictionary<string, AudioLevel> { ["later-fast"] = new() { SystemGain = 0.3, MicrophoneMuted = true } } } };
        var result = EngineSession.Edit(p, new { op = "split", segmentId = "later-fast", sourceTicks = 80_000_000L, newId = "new-right" });
        Assert.Equal(p.Revision + 1, result.Revision); Assert.Equal(4, result.Segments.Count);
        Assert.Equal(20_000_000, result.Segments[0].SourceDuration.Ticks); Assert.Equal(80_000_000, result.Segments[1].SourceStart.Ticks);
        Assert.Equal(result.Editor!.ClipAudio["later-fast"], result.Editor.ClipAudio["new-right"]);
        Assert.Equal(1.5, result.Effects[0].Parameters.GetProperty("scale").GetDouble());
        Assert.True(result.Effects[0].Parameters.GetProperty("futureEditableData").GetProperty("preserve").GetBoolean());
        Assert.Equal(EngineSession.ResolveTimeline(p).Duration, EngineSession.ResolveTimeline(result).Duration);
    }
    [Fact] public void TrimUsesNativeOutputToSourceConversionAtFourTimesRate()
    {
        var result = EngineSession.Edit(Project(), new { op = "trimOutput", segmentId = "later-fast", outputTicks = 5_000_000L, edge = "start" });
        Assert.Equal(80_000_000, result.Segments[0].SourceStart.Ticks); Assert.Equal(20_000_000, result.Segments[0].SourceDuration.Ticks);
        Assert.Equal(105_000_000, EngineSession.ResolveTimeline(result).Duration);
    }
    [Fact] public void BatchIsAtomicAndRejectsInvalidResultWithoutChangingInput()
    {
        var p = Project(); var json = ProjectJson.Serialize(p);
        Assert.Throws<EngineException>(() => EngineSession.Edit(p, new { op = "batch", commands = new object[] { new { op = "delete", segmentId = "later-fast" }, new { op = "rate", segmentId = "middle-normal", numerator = 5, denominator = 1 } } }));
        Assert.Equal(json, ProjectJson.Serialize(p));
    }
    [Fact] public void RestoreOrdersOriginalMediaAndResetsRates()
    {
        var p = EngineSession.Edit(Project(), new { op = "restore", idPrefix = "restored-" });
        Assert.Equal(2, p.Segments.Count); Assert.Equal(0, p.Segments[0].SourceStart.Ticks); Assert.Equal(new Rational(), p.Segments[0].Rate);
        Assert.Equal(100_000_000, EngineSession.ResolveTimeline(p).Duration); Assert.Equal("effect-1", p.Effects.Single().Id);
    }
    [Fact] public void LastClipCannotBeDeletedAndInvalidCropOrGainCannotBePersisted()
    {
        var p = Project() with { Segments = new[] { Project().Segments[0] } };
        Assert.Throws<EngineException>(() => EngineSession.Edit(p, new { op = "delete", segmentId = "later-fast" }));
        Assert.Throws<EngineException>(() => EngineSession.Edit(p, new { op = "settings", editor = new EditorSettings { SystemGain = 5 } }));
        Assert.Throws<EngineException>(() => EngineSession.Edit(p, new { op = "settings", editor = new EditorSettings { Crop = new() { X = 0.8, Width = 0.5 } } }));
    }
    [Fact] public void OptionalEditorEnvelopeRoundTripsAndOldSnapshotsRetainSchemaOne()
    {
        var old = Project(); Assert.Null(ProjectJson.Deserialize(ProjectJson.Serialize(old)).Editor);
        var candidate = old with { Editor = new() { CornerRadius = 24, SystemGain = 0.4, Crop = new() { X = 0.1, Y = 0.1, Width = 0.8, Height = 0.8 } } };
        EngineSession.ValidateProject(candidate); var result = ProjectJson.Deserialize(ProjectJson.Serialize(candidate)); Assert.Equal(1, result.SchemaVersion); Assert.Equal(0.4, result.Editor!.SystemGain);
    }
}
