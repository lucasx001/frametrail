using FrameTrail.Domain;
using FrameTrail.Workspace;
using Xunit;
namespace FrameTrail.Workspace.Tests;

public sealed class VisualPresetLibraryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FrameTrail-Presets-" + Guid.NewGuid().ToString("N"));
    private VisualPresetLibrary Library => new(Path.Combine(root, "library"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private ProjectSnapshot Project()
    {
        string source = Path.Combine(root, "source", "assets"); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "background.png"), "test image bytes");
        return new() { ProjectId = "preset-project", SourceDuration = MediaTime.FromTicks(30_000_000),
            Media = new[] { new MediaAsset { Id = "screen", Kind = MediaKind.Screen, Path = "media/screen.mp4", Duration = MediaTime.FromTicks(30_000_000), Width = 640, Height = 360 } },
            Segments = new[] { new RecordingSegment { Id = "clip", SourceDuration = MediaTime.FromTicks(30_000_000) } },
            Editor = new() { CursorStyle = "dot", Backdrop = new() { Kind = "image", Path = "assets/background.png" } } };
    }
    [Fact] public async Task NamedPresetsRenameDefaultAndDeleteSurviveRestart()
    {
        var p = Project(); var first = await Library.SaveAsync("课程", p, Path.Combine(root, "source"));
        var second = await Library.SaveAsync("访谈", p, Path.Combine(root, "source"));
        await Library.RenameAsync(first.Id, "课程 v2"); await Library.SetDefaultAsync(first.Id);
        var catalog = await Library.ListAsync(); Assert.Equal(2, catalog.Presets.Count); Assert.Equal(first.Id, catalog.DefaultId); Assert.Contains(catalog.Presets, p => p.Name == "课程 v2");
        await Library.DeleteAsync(first.Id); catalog = await Library.ListAsync(); Assert.Single(catalog.Presets); Assert.Null(catalog.DefaultId); Assert.Equal(second.Id, catalog.Presets[0].Id);
    }
    [Fact] public async Task CrossProjectApplyCopiesBackgroundAndPreservesCutsEffectsAndClipAudio()
    {
        var p = Project(); var saved = await Library.SaveAsync("portable", p, Path.Combine(root, "source"));
        Directory.Delete(Path.Combine(root, "source"), true);
        var destination = Path.Combine(root, "destination"); Directory.CreateDirectory(destination);
        var target = p with { Editor = new() { ClipAudio = new Dictionary<string, AudioLevel> { ["clip"] = new() { MicrophoneGain = 0.4 } } } };
        var applied = await Library.ApplyAsync(saved.Id, target, destination);
        Assert.Equal("dot", applied.Editor!.CursorStyle); Assert.Equal(0.4, applied.Editor.ClipAudio["clip"].MicrophoneGain);
        Assert.Equal(target.Segments, applied.Segments); Assert.Equal(target.Effects, applied.Effects);
        Assert.Equal("test image bytes", await File.ReadAllTextAsync(Path.Combine(destination, applied.Editor.Backdrop!.Path)));
        await Library.DeleteAsync(saved.Id); Assert.True(File.Exists(Path.Combine(destination, applied.Editor.Backdrop.Path)));
    }
    [Fact] public async Task DamagedPresetDoesNotHideValidEntriesAndInvalidNamesFailWithoutWrites()
    {
        var p = Project(); var saved = await Library.SaveAsync("valid", p, Path.Combine(root, "source"));
        var bad = Directory.CreateDirectory(Path.Combine(root, "library", Guid.NewGuid().ToString("N"))); await File.WriteAllTextAsync(Path.Combine(bad.FullName, "preset.json"), "{");
        var catalog = await Library.ListAsync(); Assert.Single(catalog.Presets); Assert.Single(catalog.Warnings);
        await Assert.ThrowsAsync<IOException>(() => Library.RenameAsync(saved.Id, " ")); Assert.Equal("valid", (await Library.ListAsync()).Presets.Single().Name);
        await Assert.ThrowsAsync<IOException>(() => Library.DeleteAsync("../outside"));
    }
}
