using System.Text.Json.Nodes;
using System.Text;
using System.Runtime.InteropServices;
using FrameTrail.Domain;
using FrameTrail.Interop;
using Xunit;

namespace FrameTrail.Domain.Tests;

public sealed class AndroidProjectTests
{
    [DllImport("FrameTrail.Engine.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int ft_project_validate(byte[] json, uint length);
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));
    [Fact]
    public void Schema2AndPhoneTrackRoundtripAcrossManagedAndNative() {
        var project = ProjectJson.Deserialize(Fixture("project-v2-android.json"));
        Assert.Equal(2, project.SchemaVersion); Assert.Equal("androidUsb", project.PrimarySource!.Kind);
        Assert.Contains(project.Media, m => m.Kind == MediaKind.AndroidAudio);
        EngineSession.ValidateProject(project);
        var edited = EngineSession.Edit(project, new { op = "rate", segmentId = project.Segments[0].Id, numerator = 1, denominator = 1 });
        Assert.Equal(project.PrimarySource, edited.PrimarySource);
        Assert.Contains(edited.Media, m => m.Kind == MediaKind.AndroidAudio);
    }
    [Fact]
    public void LegacyProjectCanBeUpgradedWithoutLosingMediaOrEdits() {
        var old = ProjectJson.Deserialize(Fixture("project-v1.json"));
        var upgraded = ProjectJson.UpgradeToCurrent(old);
        EngineSession.ValidateProject(upgraded);
        Assert.Equal("desktop", upgraded.PrimarySource!.Kind); Assert.Equal(1, old.SchemaVersion);
        Assert.Equal(old.Media, upgraded.Media); Assert.Equal(old.Segments, upgraded.Segments);
        Assert.Equal(ProjectJson.Serialize(upgraded), ProjectJson.Serialize(ProjectJson.Deserialize(ProjectJson.Serialize(upgraded))));
    }
    [Theory]
    [InlineData("legacy-source")]
    [InlineData("legacy-audio")]
    [InlineData("missing-source")]
    [InlineData("desktop-phone-audio")]
    [InlineData("unknown-orientation")]
    [InlineData("pointer")]
    [InlineData("dimensions")]
    [InlineData("loopback")]
    public void SourceAndSchemaMismatchesFailBothParsers(string change) {
        var json = JsonNode.Parse(Fixture("project-v2-android.json"))!;
        switch (change) {
            case "legacy-source": json["schemaVersion"] = 1; break;
            case "legacy-audio": json["schemaVersion"] = 1; json.AsObject().Remove("primarySource"); break;
            case "missing-source": json.AsObject().Remove("primarySource"); break;
            case "desktop-phone-audio": json["primarySource"]!["kind"] = "desktop"; break;
            case "unknown-orientation": json["primarySource"]!["orientation"] = "dynamic"; break;
            case "pointer": json["media"]![2]!["kind"] = "pointerEvents"; break;
            case "dimensions": json["primarySource"]!["width"] = 320; break;
            case "loopback": json["media"]![3]!["kind"] = "systemAudio"; break;
        }
        Assert.Throws<ProjectFormatException>(() => ProjectJson.Deserialize(json.ToJsonString()));
        var bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        Assert.NotEqual(0, ft_project_validate(bytes, (uint)bytes.Length));
    }
}
