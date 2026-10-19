using System.Globalization;
using System.Text.Json.Nodes;
using FrameTrail.Domain;
using FrameTrail.Interop;
using FrameTrail.Workspace;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    private DispatcherQueueTimer? visualPreviewTimer, visualCommitTimer;
    private Task visualPreviewWork = Task.CompletedTask, visualCommitWork = Task.CompletedTask;
    private bool visualPending, visualTransaction, committingVisuals;
    private int visualPreviewCount;
    private EditorBarSlider[] visualSliders = [];
    private bool AnyVisualControlDragging => visualSliders.Any(s => s.IsDragging) || MockupKnobs.Any(s => s.IsDragging) || MockupMetadataFocused;

    private void InitializeLiveVisuals()
    {
        visualPreviewTimer = DispatcherQueue.CreateTimer(); visualPreviewTimer.Interval = TimeSpan.FromMilliseconds(33); visualPreviewTimer.IsRepeating = false;
        visualPreviewTimer.Tick += (_, _) => StartVisualPreview();
        visualCommitTimer = DispatcherQueue.CreateTimer(); visualCommitTimer.Interval = TimeSpan.FromMilliseconds(400); visualCommitTimer.IsRepeating = false;
        visualCommitTimer.Tick += async (_, _) => await SafeAsync(FlushVisualChangesAsync);
        visualSliders = [effectScale, effectX, effectY, effectWidth, effectHeight, effectStrength, effectOpacity, effectAngleX, effectAngleY, stickerRotation, springFrequency, springDamping];
        foreach (var slider in visualSliders) {
            slider.ValueChanged += (_, _) => QueueVisualChange();
            slider.InteractionEnded += commit => {
                if (!commit) { visualPreviewTimer.Stop(); visualCommitTimer.Stop(); visualCommitWork = CancelVisualChangesAsync(); }
                else if (visualPending || visualTransaction) { visualCommitTimer.Interval = TimeSpan.FromMilliseconds(1); visualCommitTimer.Start(); }
            };
        }
        foreach (var box in new[] { effectStart, effectEnd, effectEnter, effectExit }) {
            box.ValueChanged += (_, _) => QueueVisualChange();
            box.RegisterPropertyChangedCallback(NumberBox.TextProperty, (_, _) => {
                if (loadingVisuals || !ready || Root.XamlRoot is null) return;
                if (ReferenceEquals(InputAncestor<NumberBox>(FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject), box) &&
                    double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && value != box.Value) QueueVisualChange();
            });
        }
        foreach (var box in new[] { effectMode, effectEasing, effectAttachment, effectTarget }) box.SelectionChanged += (_, _) => QueueVisualChange();
        stickerText.TextChanged += (_, _) => QueueVisualChange();
    }
    private bool TryVisualNumber(NumberBox box, out double value)
    {
        value = box.Value;
        if (ReferenceEquals(InputAncestor<NumberBox>(FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject), box) &&
            !double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)) return false;
        return double.IsFinite(value) && value >= box.Minimum && value <= box.Maximum;
    }
    private void QueueVisualChange()
    {
        if (loadingVisuals || !ready || working || closing || editor.IsExporting || CurrentEffect is null) return;
        if (mockupEditing && (visualPending || visualTransaction)) { _ = SafeAsync(async () => { await FlushVisualChangesAsync(); QueueVisualChange(); }); return; }
        mockupEditing = false;
        var draft = SelectedVisualDraft(); if (draft is null) return;
        var prior = editor.Preview?.Effects.FirstOrDefault(e => e.Id == draft.Id) ?? CurrentEffect;
        if (prior is not null && VisualResources.Ticks(prior.SourceStart) == VisualResources.Ticks(draft.SourceStart) && VisualResources.Ticks(prior.SourceDuration) == VisualResources.Ticks(draft.SourceDuration) && JsonNode.DeepEquals(JsonNode.Parse(prior.Parameters.GetRawText()), JsonNode.Parse(draft.Parameters.GetRawText()))) return;
        Pause(); visualPending = true; timeline.EditingEnabled = false;
        preview.InvalidateRequests(); // A completed older frame must not overwrite newer input.
        if (!committingVisuals && visualPreviewWork.IsCompleted && !visualPreviewTimer!.IsRunning) visualPreviewTimer.Start();
        visualCommitTimer!.Stop();
        if (!AnyVisualControlDragging) { visualCommitTimer.Interval = TimeSpan.FromMilliseconds(400); visualCommitTimer.Start(); }
    }
    private void StartVisualPreview()
    {
        if (committingVisuals || !visualPreviewWork.IsCompleted || !visualPending) return;
        visualPreviewWork = RenderVisualDraftsAsync();
    }
    private ProjectSnapshot? StageVisualDraft()
    {
        if (mockupEditing) {
            if (!visualTransaction) { editor.BeginTransaction(); visualTransaction = true; }
            return editor.PreviewCommand(new { op = "settings", editor = MockupDraft() });
        }
        var effect = SelectedVisualDraft(); if (effect is null) return null;
        if (!visualTransaction) { editor.BeginTransaction(); visualTransaction = true; }
        return editor.PreviewCommand(new { op = "effectUpsert", effectId = effect.Id, effect });
    }
    private void LocateVisualPreview(ProjectSnapshot candidate)
    {
        if (mockupEditing) return;
        var ranges = EngineSession.ResolveTimeline(candidate).Effects.Where(e => e.Id == selectedEffect).ToArray();
        var current = ranges.FirstOrDefault(e => position > e.OutputStart + 10_000 && position < e.OutputStart + e.OutputDuration - 10_000);
        if (current is not null || ranges.Length == 0) return;
        var range = ranges.OrderBy(e => Math.Abs(e.OutputStart - position)).First();
        position = range.OutputStart + range.OutputDuration / 2;
        timeline.Position(position); UpdateClock();
    }
    private async Task UpdateVisualPreviewAsync(ProjectSnapshot snapshot)
    {
        try {
            if (mockupEditing) await preview.UpdateProjectAsync(snapshot, position);
            else await preview.UpdateEffectsAsync(snapshot, position);
        }
        // New input invalidates the frame in flight. Keep draining queued
        // drafts so rapid typing/dragging cannot strand a pending preview.
        catch (OperationCanceledException) { }
    }
    private async Task RenderVisualDraftsAsync()
    {
        try {
            while (visualPending && !committingVisuals && !closing) {
                visualPending = false;
                var candidate = StageVisualDraft();
                if (candidate is not null) {
                    LocateVisualPreview(candidate);
                    await UpdateVisualPreviewAsync(candidate);
                    visualPreviewCount++;
                    timeline.Set(EngineSession.ResolveTimeline(candidate), selected, position);
                }
                // Coalesce pointer/text bursts; no frame integration or media reload.
                await Task.Delay(33);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowNotice(L.T("SC8F68E6B43D4"), error.Message); }
    }
    private Task FlushVisualChangesAsync()
    {
        canvasOverlay.Finish(true);
        if (!canvasCommitWork.IsCompleted) return canvasCommitWork;
        visualPreviewTimer?.Stop(); visualCommitTimer?.Stop();
        if (!visualCommitWork.IsCompleted) return visualCommitWork;
        if (!visualPending && !visualTransaction) return Task.CompletedTask;
        return visualCommitWork = CommitVisualChangesAsync();
    }
    private async Task CommitVisualChangesAsync()
    {
        committingVisuals = true;
        try {
            await visualPreviewWork;
            if (visualPending) { visualPending = false; var candidate = StageVisualDraft(); if (candidate is not null) LocateVisualPreview(candidate); }
            if (!visualTransaction) return;
            var revision = editor.Snapshot.Revision;
            await editor.CommitTransactionAsync(); visualTransaction = false;
            // Keep the same surface and controls: committing must not steal input focus.
            await UpdateVisualPreviewAsync(editor.Snapshot);
            mapper.Dispose(); mapper = new EngineSession(); mapper.LoadProject(editor.Snapshot);
            RefreshTimeline();
            if (effectPicker.ItemsSource is IEnumerable<EffectEntry> entries) foreach (var entry in entries) {
                var effect = editor.Snapshot.Effects.FirstOrDefault(e => e.Id == entry.Id);
                if (effect is not null) entry.Label = $"{EffectName(effect.Kind)} · {VisualResources.Ticks(effect.SourceStart) / 10_000_000.0:0.##}s";
            }
            if (editor.Snapshot.Revision != revision) await RestartVisualAssetsAsync();
        }
        catch (Exception error) {
            if (visualTransaction) { editor.CancelTransaction(); visualTransaction = false; }
            ShowNotice(L.T("SD3E418C0AFC6"), error.Message);
        }
        finally {
            committingVisuals = false; if (!visualPending) mockupEditing = false; timeline.EditingEnabled = !working && !editor.IsExporting; UpdateState();
            if (visualPending) { visualPreviewTimer!.Start(); visualCommitTimer!.Interval = TimeSpan.FromMilliseconds(400); if (!AnyVisualControlDragging) visualCommitTimer.Start(); }
        }
    }
    private async Task RestartVisualAssetsAsync()
    {
        await RestartProjectAssetsAsync(prepareAudio: false, updateThumbnails: true);
    }
    private async Task CancelVisualChangesAsync()
    {
        committingVisuals = true; visualPending = false;
        try {
            await visualPreviewWork;
            if (visualTransaction) { editor.CancelTransaction(); visualTransaction = false; }
            await UpdateVisualPreviewAsync(editor.Snapshot);
            mockupEditing = false; loadingVisuals = true; try { RefreshMockupInspector(forceText: true); } finally { loadingVisuals = false; }
            LoadSelectedVisual(); RefreshTimeline();
        }
        catch (Exception error) { ShowNotice(L.T("SF566DA15C855"), error.Message); }
        finally { committingVisuals = false; timeline.EditingEnabled = !working && !editor.IsExporting; }
    }
}
