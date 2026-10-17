using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    private static string AudioContent(ProjectSnapshot p)
    {
        var settings = p.Editor ?? new();
        return JsonSerializer.Serialize(new { p.Segments, settings.SystemGain, settings.MicrophoneGain, settings.SystemMuted, settings.MicrophoneMuted, settings.ClipAudio });
    }
    private static string PictureContent(ProjectSnapshot p) => JsonSerializer.Serialize(new { p.Canvas, p.Effects, Editor = (p.Editor ?? new()) with { SystemGain = 1, MicrophoneGain = 1, SystemMuted = false, MicrophoneMuted = false, ClipAudio = new Dictionary<string, AudioLevel>() } });
    private async Task UpdateEditedProjectAsync(ProjectSnapshot before, long? sourcePosition)
    {
        var snapshot = editor.Snapshot;
        var updatedMapper = new EngineSession();
        try {
            updatedMapper.LoadProject(snapshot);
            long nextPosition = sourcePosition is long source ? updatedMapper.MapSourceTime(source)?.Ticks ?? position : position;
            nextPosition = Math.Clamp(nextPosition, 0, Math.Max(0, editor.Timeline.Duration - 1));
            // The last presented frame stays attached while the new scene is
            // rendered. No decoder, device, swapchain or inspector is recreated.
            await preview.UpdateProjectAsync(snapshot, nextPosition);
            mapper.Dispose(); mapper = updatedMapper; updatedMapper = null!;
            position = nextPosition;
            if (!editor.Timeline.Clips.Any(c => c.Id == selected)) selected = editor.Timeline.Clips.First().Id;
            bool timelineChanged = JsonSerializer.Serialize(before.Segments) != JsonSerializer.Serialize(snapshot.Segments);
            bool audioChanged = AudioContent(before) != AudioContent(snapshot);
            bool pictureChanged = timelineChanged || PictureContent(before) != PictureContent(snapshot);
            SizePreview(); LoadInspector(); RefreshTimeline(); UpdateClock(); UpdateState();
            if (audioChanged || pictureChanged) await RestartProjectAssetsAsync(audioChanged, pictureChanged);
        } finally { updatedMapper?.Dispose(); }
    }
    private Task RestartProjectAssetsAsync(bool prepareAudio, bool updateThumbnails) => StartAssetsAsync(prepareAudio, updateThumbnails);
}
