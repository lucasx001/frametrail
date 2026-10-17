using FrameTrail.Domain;
using FrameTrail.Interop;
using Xunit;
namespace FrameTrail.Domain.Tests;
public sealed class CursorTests
{
    private static ProjectSnapshot Project() => ProjectJson.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "project-v1.json")));
    [Theory] [InlineData("recorded")] [InlineData("arrow")] [InlineData("dot")] [InlineData("crosshair")]
    public void CursorSettingsPersistAndUndoThroughNativeCommand(string style)
    {
        var settings = new EditorSettings { CursorStyle = style, CursorHideWhenIdle = true, CursorIdleDelay = 2.3, CursorTimeOffset = -0.15, CursorClickStyle = "ripple", CursorClickScale = 1.4 };
        var edited = EngineSession.Edit(Project(), new { op = "settings", editor = settings });
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(settings), System.Text.Json.JsonSerializer.Serialize(ProjectJson.Deserialize(ProjectJson.Serialize(edited)).Editor));
        EngineSession.ValidateProject(edited);
    }
    [Theory] [InlineData(-2.01, 1.2)] [InlineData(2.01, 1.2)] [InlineData(0, 0.1)] [InlineData(0, 8.1)]
    public void NativeRejectsInvalidCalibrationAndIdleRange(double offset, double delay) => Assert.ThrowsAny<Exception>(() => EngineSession.ValidateProject(Project() with { Editor = new() { CursorTimeOffset = offset, CursorIdleDelay = delay } }));
    [Fact] public void LegacyProjectsRetainRecordedCursorAndNoAddedFeedback()
    { var p = Project(); Assert.Equal("recorded", (p.Editor ?? new()).CursorStyle); Assert.Equal("none", (p.Editor ?? new()).CursorClickStyle); }
}
