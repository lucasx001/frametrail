using FrameTrail.Domain;
using FrameTrail.Interop;
using System.Text.Json;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    internal async Task ValidateRecordedWorkflowAsync(string directory)
    {
        await Startup.WaitAsync(TimeSpan.FromSeconds(30));
        var clip = editor.Snapshot.Segments.First();
        int count = editor.Snapshot.Segments.Count;
        await PerformAsync(() => editor.ExecuteAsync(new { op = "split", segmentId = clip.Id, sourceTicks = clip.SourceStart.Ticks + clip.SourceDuration.Ticks / 2, newId = "workflow-split" }));
        if (editor.Snapshot.Segments.Count != count + 1 || editor.IsDirty) throw new IOException("Recorded clip split was not saved.");
        await PerformAsync(() => editor.ExecuteAsync(new { op = "settings", export = editor.Snapshot.Export with { Width = 640, Height = 360 } }));
        await preview.RenderAsync(0, png: Path.Combine(directory, "recorded-preview.png"));
        string output = Path.Combine(directory, "recorded-edited.mp4");
        await editor.ExportAsync(output, false, null, CancellationToken.None);
        RecordingUtilities.ProbeMedia(output, true);
        await editor.FlushAsync();
    }

    internal async Task<string> ValidateDragUndoWorkflowAsync(string directory)
    {
        position = Math.Min(10_000_000, editor.Timeline.Duration / 2);
        await PerformAsync(() => AddVisualAsync("mask"));
        string id = selectedEffect ?? throw new IOException("No mask was selected.");
        var effect = CurrentEffect!;
        var parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(effect.Parameters.GetRawText())!;
        parameters["x"] = JsonSerializer.SerializeToElement(0.2);
        parameters["y"] = JsonSerializer.SerializeToElement(0.2);
        parameters["width"] = JsonSerializer.SerializeToElement(0.25);
        parameters["height"] = JsonSerializer.SerializeToElement(0.25);
        await PerformAsync(() => editor.UpsertEffectAsync(effect with { Parameters = JsonSerializer.SerializeToElement(parameters) }));
        await preview.RenderAsync(position);
        QueueCanvasGeometry();
        await Task.Delay(160);
        var item = (await Task.Run(() => preview.Session!.EditGeometry(position))).Single(i => i.Id == id);
        var plane = new CanvasPlane(item.Base);
        var center = plane.Map(0.325, 0.325);
        string before = WorkflowContent(editor.Snapshot);
        if (!canvasOverlay.BeginGesture(center.X, center.Y)) throw new IOException("The projected mask did not accept the integration gesture.");
        var end = plane.Map(0.375, 0.375);
        canvasOverlay.MoveGesture(end.X, end.Y);
        canvasOverlay.Finish(true);
        await canvasCommitWork;
        var moved = editor.Snapshot.Effects.Single(e => e.Id == id);
        if (Math.Abs(NumberValue(moved.Parameters, "x", 0) - 0.25) > 0.00001) throw new IOException("The gesture did not move the mask by the expected amount.");
        await preview.RenderAsync(position, png: Path.Combine(directory, "dragged-mask.png"));
        await PerformAsync(editor.UndoAsync);
        if (WorkflowContent(editor.Snapshot) != before) throw new IOException("One undo did not restore the complete pre-gesture content.");
        await editor.FlushAsync();
        return before;
    }

    internal async Task ValidateReopenedWorkflowAsync(string expected, string png)
    {
        await Startup.WaitAsync(TimeSpan.FromSeconds(30));
        if (WorkflowContent(editor.Snapshot) != expected) throw new IOException("Closing and reopening did not preserve the undone project.");
        await preview.RenderAsync(Math.Min(10_000_000, editor.Timeline.Duration / 2), png: png);
    }

    internal async Task CopyWorkflowProjectAsync(string destination)
    {
        await FlushVisualChangesAsync();
        await FlushPictureInputsAsync();
        await editor.FlushAsync();
        await editor.CopySourcesAsync(destination, CancellationToken.None);
    }

    internal async Task StageWorkflowPlaybackAndAssetsAsync()
    {
        await FlushVisualChangesAsync();
        await FlushPictureInputsAsync();
        // The real peak worker waits on this cancellable barrier. The fixture has
        // a silent audio track so native playback opens without recording sound.
        audioPreparationBarrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        audioCacheKey = null;
        await StartAssetsAsync(prepareAudio: true, updateThumbnails: true);
        await StartPlaybackAsync();
        await Task.Delay(180);
        if (!playing || !IsWorkflowAudioPending || preview.Session?.Status.AudioRunning == 3)
            throw new IOException("Playback and a pending audio preparation worker were not both active.");
    }

    private static string WorkflowContent(ProjectSnapshot snapshot) => ProjectJson.Serialize(snapshot with { Revision = 0 });
}
