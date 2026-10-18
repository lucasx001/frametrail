using System.Text.Json;
using FrameTrail.App;
using FrameTrail.Domain;
using Xunit;

namespace FrameTrail.App.Tests;

public sealed class ProjectCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FrameTrail-Catalog-" + Guid.NewGuid().ToString("N"));
    private string Settings => Path.Combine(root, "preferences", "catalog.json");
    private string Storage => Path.Combine(root, "recordings");
    private ProjectCatalog Catalog() => new(Settings);
    private string Project(string folder, string name)
    {
        var path = Directory.CreateDirectory(Path.Combine(folder, name + ".frametrailproject")).FullName;
        var snapshot = new ProjectSnapshot { ProjectId = Guid.NewGuid().ToString("N"), SourceDuration = new() { Ticks = 45, Timescale = 1 },
            Media = [new() { Id = "screen", Kind = MediaKind.Screen, Path = "media/screen.mp4", Duration = new() { Ticks = 45, Timescale = 1 }, StartOffset = MediaTime.FromTicks(0), Width = 1920, Height = 1080 }],
            Segments = [new() { Id = "segment", SourceStart = MediaTime.FromTicks(0), SourceDuration = new() { Ticks = 45, Timescale = 1 } }] };
        File.WriteAllText(Path.Combine(path, "project.json"), ProjectJson.Serialize(snapshot));
        return path;
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact] public void ExistingStorageIsListedWithoutOpeningMediaOrCreatingProjectFiles()
    {
        string project = Project(Storage, "课程演示");
        var entry = Assert.Single(Catalog().List([Storage]));
        Assert.Equal("课程演示", entry.Name); Assert.Equal(project, entry.Directory); Assert.True(entry.CanOpen);
        Assert.Equal(45, entry.RecordingSeconds); Assert.Equal(1920u, entry.Width); Assert.Equal(1080u, entry.Height);
        Assert.Single(Directory.GetFiles(project)); Assert.False(File.Exists(Path.Combine(project, ".write.lock")));
    }
    [Fact] public void RecentProjectsAndOtherLocationsSurviveRestartAndDeduplicate()
    {
        string project = Project(Storage, "同名"); string external = Project(Path.Combine(root, "external"), "同名");
        var catalog = Catalog(); catalog.RememberProject(project); catalog.RememberProject(project.ToUpperInvariant());
        catalog.RememberFolder(Path.GetDirectoryName(external)!);
        var entries = Catalog().List([Storage, Storage]);
        Assert.Equal(2, entries.Count); Assert.Contains(entries, entry => entry.Directory.Equals(external, StringComparison.OrdinalIgnoreCase));
    }
    [Fact] public void LatestSavedProjectAppearsFirstAndNestedOrdinaryFoldersAreNotScanned()
    {
        string old = Project(Storage, "older"); string latest = Project(Storage, "latest");
        _ = Project(Path.Combine(Storage, "unrelated"), "nested");
        File.SetLastWriteTimeUtc(Path.Combine(old, "project.json"), DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(Path.Combine(latest, "project.json"), DateTime.UtcNow);
        var entries = Catalog().List([Storage]); Assert.Equal(2, entries.Count); Assert.Equal(latest, entries[0].Directory);
    }
    [Fact] public void BrokenAndIncompleteProjectsRemainVisibleForDeletion()
    {
        string broken = Directory.CreateDirectory(Path.Combine(Storage, "broken.frametrailproject")).FullName;
        File.WriteAllText(Path.Combine(broken, "project.json"), "bad json");
        Directory.CreateDirectory(Path.Combine(Storage, "incomplete.frametrailproject"));
        var entries = Catalog().List([Storage]); Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => { Assert.False(entry.CanOpen); Assert.NotNull(entry.Error); });
        Assert.False(File.Exists(Path.Combine(broken, ".write.lock")));
    }
    [Fact] public void MissingRecentProjectsAndDamagedPreferencesDoNotBlockStorage()
    {
        string project = Project(Storage, "keep"); string missing = Project(Path.Combine(root, "external"), "removed");
        Catalog().RememberProject(missing); Directory.Delete(missing, true);
        Assert.Equal(project, Assert.Single(Catalog().List([Storage])).Directory);
        File.WriteAllText(Settings, "invalid json"); Assert.Equal(project, Assert.Single(Catalog().List([Storage])).Directory);
    }
    [Fact] public void ProjectBundlesSelectedAsOtherLocationAreNotEnumeratedInternally()
    {
        string project = Project(Storage, "direct"); Catalog().RememberFolder(project);
        Assert.Equal(project, Assert.Single(Catalog().List([])).Directory);
    }
    [Fact] public void MetadataCanBeListedWhileAnotherSessionHoldsTheWriterLease()
    {
        string project = Project(Storage, "active");
        using var lease = new FileStream(Path.Combine(project, ".write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.True(Assert.Single(Catalog().List([Storage])).CanOpen);
    }
    [Fact] public void CancelledScanDoesNotReturnPartialResults()
    {
        _ = Project(Storage, "one"); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => Catalog().List([Storage], cancellation.Token));
    }
}
