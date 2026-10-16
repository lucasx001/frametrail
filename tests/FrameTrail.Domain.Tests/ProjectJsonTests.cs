using System.Text.Json;
using System.Text.Json.Nodes;
using FrameTrail.Domain;
using Xunit;

namespace FrameTrail.Domain.Tests;

public sealed class ProjectJsonTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Fact]
    public void VisualSettingsAndProjectAssetsRoundtripWithoutMachinePaths()
    {
        var project = ProjectJson.Deserialize(Fixture("project-v1.json")) with { Editor = new() { CursorSmoothing = 0.12, CameraShape = "circle", FrameStyle = "browser", Backdrop = new() { Kind = "video", Path = "assets/background.mp4" } } };
        var restored = ProjectJson.Deserialize(ProjectJson.Serialize(project));
        Assert.Equal(ProjectJson.Serialize(project), ProjectJson.Serialize(restored));
        Assert.Equal("assets/background.mp4", restored.Editor!.Backdrop!.Path);
        Assert.Contains("\"cursorSmoothing\": 0.12", ProjectJson.Serialize(restored), StringComparison.Ordinal);
    }

    [Fact]
    public void SharedSnapshotRoundtripRetainsTypedMetadataAndParameters()
    {
        var project = ProjectJson.Deserialize(Fixture("project-v1.json"));
        Assert.Equal(1, project.SchemaVersion);
        Assert.Equal(7, project.Revision);
        Assert.Equal(5, project.Media.Count);
        Assert.Equal(MediaKind.SystemAudio, project.Media[3].Kind);
        Assert.Equal(100, project.SourceDuration.Ticks);
        Assert.Equal(10, project.SourceDuration.Timescale);
        var roundtrip = ProjectJson.Deserialize(ProjectJson.Serialize(project));
        Assert.Equal("later-fast", roundtrip.Segments[0].Id);
        Assert.True(roundtrip.Effects[0].Parameters.GetProperty("futureEditableData").GetProperty("preserve").GetBoolean());
        Assert.Equal(60, roundtrip.Export.Fps.Numerator);
        Assert.Contains("\"systemAudio\"", ProjectJson.Serialize(roundtrip), StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> ProtocolCases()
    {
        var cases = JsonNode.Parse(Fixture("protocol-invalid-cases.json"))!.AsArray();
        foreach (var test in cases) yield return new object[] { test!["name"]!.GetValue<string>(), test.ToJsonString() };
    }

    [Theory]
    [MemberData(nameof(ProtocolCases))]
    public void SharedInvalidProtocolCasesAreRejected(string name, string testJson)
    {
        _ = name;
        var root = JsonNode.Parse(Fixture("project-v1.json"))!;
        var test = JsonNode.Parse(testJson)!;
        var path = test["path"]!.AsArray();
        var node = root;
        foreach (var part in path.Take(path.Count - 1))
            node = part!.GetValueKind() == JsonValueKind.String ? node[part.GetValue<string>()]! : node[part.GetValue<int>()]!;
        var last = path.Last()!;
        if (test["remove"]?.GetValue<bool>() == true) node.AsObject().Remove(last.GetValue<string>());
        else if (last.GetValueKind() == JsonValueKind.String) node[last.GetValue<string>()] = test["value"]?.DeepClone();
        else node[last.GetValue<int>()] = test["value"]?.DeepClone();
        Assert.ThrowsAny<ProjectFormatException>(() => ProjectJson.Deserialize(root.ToJsonString()));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    [InlineData("{\"value\":NaN}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    public void MalformedOrIncompleteJsonIsRejected(string json) => Assert.Throws<ProjectFormatException>(() => ProjectJson.Deserialize(json));

    [Fact]
    public void NonfiniteInMemoryValuesCannotBecomeNullJson()
    {
        var snapshot = ProjectJson.Deserialize(Fixture("project-v1.json"));
        var invalid = snapshot with { Canvas = snapshot.Canvas with { Padding = double.NaN } };
        Assert.Throws<ProjectFormatException>(() => ProjectJson.Serialize(invalid));
    }

    [Fact]
    public void CallerCannotSerializeInvalidDefaults()
    {
        Assert.Throws<ProjectFormatException>(() => ProjectJson.Serialize(new ProjectSnapshot()));
        var source = ProjectJson.Deserialize(Fixture("project-v1.json"));
        Assert.Throws<ProjectFormatException>(() => ProjectJson.Serialize(source with { SourceDuration = null! }));
    }

    [Theory]
    [InlineData("media/screen-1.mp4", true)]
    [InlineData("assets/背景.png", true)]
    [InlineData("../outside", false)]
    [InlineData("media/../outside", false)]
    [InlineData("C:/recording.mp4", false)]
    [InlineData("media\\recording.mp4", false)]
    [InlineData("media/NUL.txt", false)]
    [InlineData("media/LPT1", false)]
    [InlineData("media/file.", false)]
    [InlineData("media/file ", false)]
    [InlineData("media//file.mp4", false)]
    [InlineData("media/stream:ads", false)]
    public void RelativePathsHaveWindowsFileSemantics(string path, bool expected) => Assert.Equal(expected, ProjectJson.IsProjectRelativePath(path));

    [Fact]
    public void UnknownNestedParametersRemainLossless()
    {
        var root = JsonNode.Parse(Fixture("project-v1.json"))!;
        root["effects"]![0]!["parameters"]!["newValue"] = JsonNode.Parse("{\"x\":[1,2,3],\"label\":\"保留\"}");
        var roundtrip = JsonNode.Parse(ProjectJson.Serialize(ProjectJson.Deserialize(root.ToJsonString())))!;
        Assert.True(JsonNode.DeepEquals(root["effects"]![0]!["parameters"], roundtrip["effects"]![0]!["parameters"]));
    }

    [Fact]
    public void SharedMappingVectorsDescribeExpectedContractWithoutManagedMapper()
    {
        using var document = JsonDocument.Parse(Fixture("mapping-vectors.json"));
        Assert.Equal(110_000_000L, document.RootElement.GetProperty("outputDurationTicks").GetInt64());
        Assert.Equal(8, document.RootElement.GetProperty("outputToSource").GetArrayLength());
        // Actual evaluation is exercised by FrameTrail.Core.Tests. There is
        // intentionally no second TimelineMap implementation in managed code.
    }
}
