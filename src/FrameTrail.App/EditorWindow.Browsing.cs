using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    private sealed record BrowseRequest(long Ticks, bool Hover, bool Resume);
    private (long Position, bool Playing)? hoverOrigin;
    private LatestRequestQueue<BrowseRequest>? browseRequests;
    private bool seekResume;
    private LatestRequestQueue<BrowseRequest> BrowseRequests => browseRequests ??= new(RenderBrowseAsync);
    private void InitializeBrowsing()
    {
        timeline.Hover += ticks => _ = SafeAsync(() => HoverAsync(ticks));
        AutomationProperties.SetName(timeline, L.T("SF1241A97B082"));
        AutomationProperties.SetLiveSetting(timeline, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        AutomationProperties.SetHelpText(timeline, L.T("SDC745D4D8F00"));
        timeline.PreviewKeyDown += Timeline_KeyDown;
    }
    private bool CanBrowse => ready && !closing && !working && !switchingProjects && !projectTransition &&
        !editor.IsExporting && !showingExport && !showingDeleteConfirmation && !showingPresets &&
        !visualTransaction && !visualPending && !canvasTransaction && !cropEditing && !previewFullscreen;
    private Task HoverAsync(long? ticks)
    {
        if (ticks is null) {
            if (hoverOrigin is not { } origin) return Task.CompletedTask;
            hoverOrigin = null; preview.InvalidateRequests();
            return BrowseRequests.Request(new(origin.Position, false, origin.Playing));
        }
        if (!CanBrowse) return Task.CompletedTask;
        if (hoverOrigin is null) { hoverOrigin = (position, playing); Pause(false); canvasOverlay.SetItems([]); }
        preview.InvalidateRequests();
        return BrowseRequests.Request(new(Math.Clamp(ticks.Value, 0, Math.Max(0, editor.Timeline.Duration-1)), true, false));
    }
    private void CancelBrowsing()
    {
        hoverOrigin = null; seekResume = false; browseRequests?.Cancel(); preview.InvalidateRequests();
        timeline.HideHover();
    }
    private Task BrowseSeekAsync(long ticks)
    {
        bool resume = hoverOrigin?.Playing ?? (playing || seekResume);
        hoverOrigin = null; Pause(false); seekResume = resume; timeline.HideHover();
        position=Math.Clamp(ticks,0,Math.Max(0,editor.Timeline.Duration-1));
        ClockLabel.Text=$"{Format(position)} / {Format(editor.Timeline.Duration)}";timeline.Position(position);
        preview.InvalidateRequests();
        return BrowseRequests.Request(new(position,false,resume));
    }
    private async Task RenderBrowseAsync(BrowseRequest request,long version)
    {
        if (closing || editor.IsExporting) return;
        await preview.RenderAsync(request.Ticks, false);
        if (!BrowseRequests.IsCurrent(version) || closing) return;
        if (request.Hover) return;
        position = request.Ticks; seekResume = false; UpdateClock();
        if (request.Resume && CanBrowse) await StartPlaybackAsync();
    }
    private async void Timeline_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!CanBrowse) return;
        bool Down(VirtualKey key) => (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        bool ctrl=Down(VirtualKey.Control), shift=Down(VirtualKey.Shift), alt=Down(VirtualKey.Menu);
        int direction=e.Key is VirtualKey.Up or VirtualKey.Left ? -1 : 1;
        if(e.Key is VirtualKey.Up or VirtualKey.Down) {
            e.Handled=true;
            await SafeAsync(async()=> {
                await FlushVisualChangesAsync();
                if(ctrl) {
                    var effects=editor.Timeline.Effects.DistinctBy(v=>v.Id).ToArray();if(effects.Length==0)return;
                    int index=Math.Clamp(Array.FindIndex(effects,v=>v.Id==selectedEffect)+direction,0,effects.Length-1);
                    selectedEffect=effects[index].Id;ShowInspector(2);RefreshVisualInspector();timeline.SelectVisual(selectedEffect);
                    timeline.Reveal(effects[index].OutputStart);
                } else {
                    int index=Math.Clamp(editor.Timeline.Clips.ToList().FindIndex(v=>v.Id==selected)+direction,0,editor.Timeline.Clips.Count-1);
                    selected=editor.Timeline.Clips[index].Id;selectedEffect=null;ShowInspector(6);LoadInspector();RefreshTimeline();timeline.Reveal(editor.Timeline.Clips[index].OutputStart);
                }
                // Expanding a previously hidden inspector may restore one of its
                // inputs' focus. Keep keyboard navigation on the timeline.
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,()=>timeline.FocusTimeline(FocusState.Keyboard));
            });
        } else if(e.Key==VirtualKey.Enter) {
            e.Handled=true;var effect=editor.Timeline.Effects.FirstOrDefault(v=>v.Id==selectedEffect);
            await SafeAsync(()=>SeekAsync(effect?.OutputStart??SelectedClip?.OutputStart??position));
        } else if((shift||alt) && e.Key is VirtualKey.Left or VirtualKey.Right && SelectedClip is { } clip) {
            e.Handled=true;
            if(alt)await EditAsync(new{op="move",segmentId=clip.Id,index=Math.Clamp(editor.Timeline.Clips.IndexOf(clip)+direction,0,editor.Timeline.Clips.Count-1)});
            else {
                string edge=ctrl?"end":"start";long at=(ctrl?clip.OutputStart+clip.OutputDuration:clip.OutputStart)+direction*TimeSpan.TicksPerSecond/editor.Snapshot.Export.Fps.Numerator;
                await EditAsync(new{op="trimOutput",segmentId=clip.Id,outputTicks=at,edge});
            }
        }
    }
    private Task PasteAttributesAsync() => copiedEffect is not null && selectedEffect is not null
        ? editor.PasteEffectAttributesAsync(copiedEffect,selectedEffect) : Task.CompletedTask;
}
