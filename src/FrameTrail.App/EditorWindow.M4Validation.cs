using FrameTrail.Domain;
using FrameTrail.Interop;
using System.Text.Json;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    // Disposable recorded-project acceptance, also used on the self-contained candidate.
    private async Task RunM4ValidationAsync(string directory)
    {
        Directory.CreateDirectory(directory); bool success = false; string? failure = null;
        var checks = new List<string>();
        try {
            await ReloadAsync(); await preview.RenderAsync(0, true, Path.Combine(directory, "preview.png"));
            var clip = editor.Snapshot.Segments.First();
            if (clip.SourceDuration.Ticks < 1_000_000) throw new IOException("Recorded fixture is too short to edit.");
            await editor.ExecuteAsync(new { op = "split", segmentId = clip.Id, sourceTicks = clip.SourceStart.Ticks + clip.SourceDuration.Ticks / 2, newId = "m4-split" });
            var segments = editor.Snapshot.Segments.Count;
            await editor.UndoAsync(); await editor.RedoAsync(); await editor.FlushAsync();
            if (editor.Snapshot.Segments.Count != segments || editor.IsDirty) throw new IOException("Edit history or save failed.");
            checks.Add("Recorded project preview, split, undo/redo and automatic save.");
            var export = Path.Combine(directory, "recorded-edited.mp4");
            await editor.ExportAsync(export, false, null, CancellationToken.None);
            RecordingUtilities.ProbeMedia(export, true);
            checks.Add("Edited recording exported through bundled native renderer and reopened by Media Foundation.");
            success = true;
        } catch (Exception error) { failure = error.ToString(); }
        finally {
            await File.WriteAllTextAsync(Path.Combine(directory, "editor-validation.json"), JsonSerializer.Serialize(new { success, checks, failure }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = success ? 0 : 1; working = false; closing = false; await RequestCloseAsync();
        }
    }
}
