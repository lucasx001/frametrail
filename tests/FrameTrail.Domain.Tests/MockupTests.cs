using FrameTrail.Domain;
using FrameTrail.Interop;
using Xunit;

namespace FrameTrail.Domain.Tests;

public sealed class MockupTests
{
    private static ProjectSnapshot Project() => ProjectJson.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "project-v1.json")));
    [Theory]
    [InlineData("none")][InlineData("windowLight")][InlineData("windowDark")][InlineData("browserLight")][InlineData("browserDark")][InlineData("devicePhone")]
    [InlineData("window")][InlineData("browser")][InlineData("device")]
    public void StylesAndMetadataRoundTripThroughNativeEditingWithoutLosingOtherSettings(string style)
    {
        var settings = new EditorSettings { FrameTitle = "演示窗口 FrameTrail", FrameBrowserAddress = "example.com/演示", SystemGain = 0.6, MicrophoneMuted = true, CursorScale = 2.1, Crop = new() { X = 0.1, Y = 0.1, Width = 0.8, Height = 0.8 } };
        var project = Project() with { Editor = settings };
        var authored = FrameStyles.Apply(settings, style);
        var edited = EngineSession.Edit(project, new { op = "settings", editor = authored });
        var restored = ProjectJson.Deserialize(ProjectJson.Serialize(edited)); EngineSession.ValidateProject(restored);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(authored), System.Text.Json.JsonSerializer.Serialize(restored.Editor));
        Assert.Equal(settings.FrameTitle, restored.Editor!.FrameTitle); Assert.Equal(settings.FrameBrowserAddress, restored.Editor.FrameBrowserAddress);
        Assert.Equal(settings.Crop, restored.Editor.Crop); Assert.Equal(0.6, restored.Editor.SystemGain); Assert.True(restored.Editor.MicrophoneMuted);
        using var session = new EngineSession(); session.LoadProject(restored); var scene = session.EvaluateScene(0)!;
        Assert.True(scene.Screen.OutputRect.X >= 0 && scene.Screen.OutputRect.Y >= 0);
        Assert.True(scene.Screen.OutputRect.X + scene.Screen.OutputRect.Width <= scene.Width + 0.001);
        Assert.True(scene.Screen.OutputRect.Y + scene.Screen.OutputRect.Height <= scene.Height + 0.001);
        EngineSession.ValidateProject(project with { Editor = settings with { FrameStyle = style } });
    }
    [Theory]
    [InlineData(0.59, 1, 20)][InlineData(1.61, 1, 20)][InlineData(1, 0.64, 20)][InlineData(1, 1.61, 20)][InlineData(1, 1, -1)][InlineData(1, 1, 161)]
    public void NativeValidatorRejectsOutOfRangeFrameGeometry(double scale, double toolbar, double radius)
    {
        var project = Project() with { Editor = new() { FrameStyle = "windowLight", FrameScale = scale, FrameToolbarScale = toolbar, FrameOuterCornerRadius = radius } };
        Assert.ThrowsAny<Exception>(() => EngineSession.ValidateProject(project));
    }
    [Fact]
    public void TextLimitsHandleUnicodeAndResetPreservesAuthoredText()
    {
        var settings = new EditorSettings { FrameStyle = "browserLight", FrameTitle = new string('窗', 120), FrameBrowserAddress = new string('址', 240), FrameScale = 1.4, FrameToolbarScale = 1.4, FrameOuterCornerRadius = 110 };
        EngineSession.ValidateProject(Project() with { Editor = settings });
        var reset = FrameStyles.Apply(settings, settings.FrameStyle);
        Assert.Equal(settings.FrameTitle, reset.FrameTitle); Assert.Equal(settings.FrameBrowserAddress, reset.FrameBrowserAddress); Assert.NotEqual(settings.FrameScale, reset.FrameScale);
        Assert.ThrowsAny<Exception>(() => EngineSession.ValidateProject(Project() with { Editor = settings with { FrameTitle = new string('x', 121) } }));
        Assert.ThrowsAny<Exception>(() => EngineSession.ValidateProject(Project() with { Editor = settings with { FrameBrowserAddress = new string('址', 241) } }));
    }
}
