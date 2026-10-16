using FrameTrail.App;
using Xunit;

namespace FrameTrail.App.Tests;

public sealed class ProjectLocationsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FrameTrail-ProjectLocations-" + Guid.NewGuid().ToString("N"));
    private string Settings => Path.Combine(root, "settings", "location.json");
    private string Default => Path.Combine(root, "videos", "FrameTrail");
    private ProjectLocations Locations() => new(Settings, Default);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    [Fact] public void FirstOpenCreatesAndStartsAtProjectStorage()
    { var locations = Locations(); Assert.Equal(Default, locations.RecordingDirectory); Assert.Equal(Default, locations.OpenDirectory()); Assert.True(Directory.Exists(Default)); }
    [Fact] public void CustomRecordingLocationSurvivesRestartAndDrivesThePicker()
    {
        var target = Directory.CreateDirectory(Path.Combine(root, "我的录制 projects")).FullName;
        Locations().SetRecordingDirectory(target); var restarted = Locations();
        Assert.Equal(target, restarted.RecordingDirectory); Assert.Equal(target, restarted.OpenDirectory());
    }
    [Fact] public void EditorPickerStartsInCurrentProjectsParent()
    {
        var project = Directory.CreateDirectory(Path.Combine(root, "external", "one.frametrailproject")).FullName;
        Assert.Equal(Path.GetDirectoryName(project), Locations().OpenDirectory(project));
    }
    [Fact] public void MissingCustomStorageFallsBackWithoutRecreatingTheMissingLocation()
    {
        var custom = Directory.CreateDirectory(Path.Combine(root, "removed drive")).FullName;
        Locations().SetRecordingDirectory(custom); Directory.Delete(custom);
        Assert.Equal(Default, Locations().OpenDirectory()); Assert.False(Directory.Exists(custom));
    }
    [Theory] [InlineData("invalid json")] [InlineData("\"relative/path\"")]
    public void DamagedOrRelativePreferenceFallsBackToDefault(string saved)
    { Directory.CreateDirectory(Path.GetDirectoryName(Settings)!); File.WriteAllText(Settings, saved); Assert.Equal(Default, Locations().RecordingDirectory); }
    [Fact] public void ProjectFolderAndExplicitProjectJsonResolveToTheSameProject()
    {
        var project = Directory.CreateDirectory(Path.Combine(root, "existing.frametrailproject")).FullName;
        File.WriteAllText(Path.Combine(project, "project.json"), "{}");
        Assert.Equal(project, ProjectLocations.ProjectDirectory(project + Path.DirectorySeparatorChar));
        Assert.Equal(project, ProjectLocations.ProjectDirectory(Path.Combine(project, "project.json")));
    }
    [Fact] public void InvalidFoldersAreRejectedWithoutCreatingProjectFiles()
    {
        var notProject = Directory.CreateDirectory(Path.Combine(root, "ordinary folder")).FullName;
        var incomplete = Directory.CreateDirectory(Path.Combine(root, "incomplete.frametrailproject")).FullName;
        Assert.Throws<IOException>(() => ProjectLocations.ProjectDirectory(notProject));
        Assert.Throws<IOException>(() => ProjectLocations.ProjectDirectory(incomplete)); Assert.Empty(Directory.GetFiles(incomplete));
    }
}
