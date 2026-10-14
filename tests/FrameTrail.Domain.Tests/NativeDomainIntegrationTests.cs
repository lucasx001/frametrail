using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;
using Xunit;

namespace FrameTrail.Domain.Tests;

public sealed class NativeDomainIntegrationTests
{
    private static ProjectSnapshot Project() => ProjectJson.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "project-v1.json")));

    [Fact]
    public void ManagedSerializationIsAcceptedByAuthoritativeNativeValidator()
    {
        var project = Project();
        EngineSession.ValidateProject(project);
        var roundtrip = ProjectJson.Deserialize(ProjectJson.Serialize(project));
        EngineSession.ValidateProject(roundtrip);
    }

    [Fact]
    public void BothDirectionsOfSharedMappingVectorsExecuteThroughTheActualAbi()
    {
        using var session = new EngineSession();
        session.LoadProject(Project());
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "mapping-vectors.json")));
        Assert.Equal(vectors.RootElement.GetProperty("outputDurationTicks").GetInt64(), session.OutputDurationTicks);
        foreach (var sample in vectors.RootElement.GetProperty("outputToSource").EnumerateArray())
        {
            var mapped = session.MapOutputTime(sample.GetProperty("outputTicks").GetInt64());
            if (sample.GetProperty("sourceTicks").ValueKind == JsonValueKind.Null) Assert.Null(mapped);
            else
            {
                Assert.NotNull(mapped);
                Assert.Equal(sample.GetProperty("sourceTicks").GetInt64(), mapped.Ticks);
                Assert.Equal(sample.GetProperty("segmentId").GetString(), mapped.SegmentId);
            }
        }
        foreach (var sample in vectors.RootElement.GetProperty("sourceToOutput").EnumerateArray())
        {
            var mapped = session.MapSourceTime(sample.GetProperty("sourceTicks").GetInt64());
            if (sample.GetProperty("outputTicks").ValueKind == JsonValueKind.Null) Assert.Null(mapped);
            else
            {
                Assert.NotNull(mapped);
                Assert.Equal(sample.GetProperty("outputTicks").GetInt64(), mapped.Ticks);
                Assert.Equal(sample.GetProperty("segmentId").GetString(), mapped.SegmentId);
            }
        }
    }

    [Fact]
    public void SceneStructMarshalsDimensionsOffsetsColorsAndOptionalCamera()
    {
        using var session = new EngineSession();
        session.LoadProject(Project());
        var frame = session.EvaluateScene(0)!;
        Assert.Equal(7, frame.Revision);
        Assert.Equal("screen-2", frame.Screen.MediaId);
        Assert.Equal("later-fast", frame.SegmentId);
        Assert.Equal(1920u, frame.Width);
        Assert.Equal(1080u, frame.Height);
        Assert.Equal(1728, frame.Screen.OutputRect.Width, precision: 6);
        Assert.Equal(0.08, frame.Background.R, precision: 6);
        Assert.NotNull(frame.Camera);
        Assert.Equal(40_000_000L, frame.Camera.LocalTicks);
        Assert.True(frame.Camera.Mirror);
        Assert.Null(session.EvaluateScene(10_000_000)!.Camera);
        Assert.Null(session.EvaluateScene(session.OutputDurationTicks));
    }

    [Fact]
    public void NativeSemanticValidationRejectsOverlappingSourceIntervals()
    {
        var project = Project();
        var segments = project.Segments.ToArray();
        segments[1] = segments[1] with { SourceStart = MediaTime.FromTicks(70_000_000) };
        var invalid = project with { Segments = segments };
        // Protocol shape remains valid. C++ alone implements edit semantics.
        _ = ProjectJson.Serialize(invalid);
        var error = Assert.Throws<EngineException>(() => EngineSession.ValidateProject(invalid));
        Assert.Equal(7, error.NativeCode);
    }

    [Fact]
    public void FailedOrStaleSnapshotCannotReplaceTheExistingEngineRevision()
    {
        using var session = new EngineSession();
        var project = Project();
        session.LoadProject(project);
        Assert.Equal(8, Assert.Throws<EngineException>(() => session.LoadProject(project)).NativeCode);
        Assert.Equal(8, Assert.Throws<EngineException>(() => session.LoadProject(project with { ProjectId = "other", Revision = 8 })).NativeCode);
        Assert.Equal(7, session.EvaluateScene(0)!.Revision);
        session.LoadProject(project with { Revision = 8 });
        Assert.Equal(8, session.EvaluateScene(0)!.Revision);
    }

    [Fact]
    public void UnloadedCancelledAndDisposedSessionsReturnExplicitFailures()
    {
        using var session = new EngineSession();
        Assert.Equal(10, Assert.Throws<EngineException>(() => session.EvaluateScene(0)).NativeCode);
        session.Cancel();
        Assert.Equal(6, Assert.Throws<EngineException>(() => session.LoadProject(Project())).NativeCode);
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.EvaluateScene(0));
    }

    [Fact]
    public void ConcurrentIncreasingSnapshotLoadsNeverMoveRevisionBackwards()
    {
        using var session = new EngineSession();
        var project = Project();
        session.LoadProject(project);
        Parallel.For(8, 40, revision =>
        {
            try { session.LoadProject(project with { Revision = revision }); }
            catch (EngineException error) when (error.NativeCode == 8) { }
        });
        Assert.Equal(39, session.EvaluateScene(0)!.Revision);
    }
}
