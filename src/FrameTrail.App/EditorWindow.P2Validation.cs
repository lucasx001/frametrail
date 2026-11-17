using System.Diagnostics;
using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    private async Task RunP2ValidationAsync(string directory)
    {
        Directory.CreateDirectory(directory);bool success=false;string? failure=null;var checks=new List<string>();var measures=new List<object>();
        try {
            await Task.Delay(200);Pause();await SeekAsync(0);long revision=editor.Snapshot.Revision;
            long middle=editor.Timeline.Duration/2;
            await HoverAsync(middle);
            if(position!=0||playing||preview.Session!.Status.RenderedTicks!=middle)throw new IOException("Paused hover changed transport or did not render.");
            var requests=new List<Task>();for(int i=0;i<100;i++)requests.Add(HoverAsync(middle+i));await Task.WhenAll(requests);
            if(preview.Session.Status.RenderedTicks!=middle+99)throw new IOException("Hover retained an old result.");
            await HoverAsync(null);if(position!=0||playing||preview.Session.Status.RenderedTicks!=0)throw new IOException("Hover exit did not restore paused position.");
            await StartPlaybackAsync();await Task.Delay(200);long anchor=position;await HoverAsync(middle);
            if (!playing || preview.Session!.Status.RenderedTicks >= middle - 1_000_000) throw new IOException("Hover while playing paused playback or jumped preview.");
            await Task.Delay(150);await HoverAsync(null);if(!playing||position<anchor)throw new IOException("Playback did not continue unobstructed across hover.");Pause();
            await HoverAsync(middle);await SeekAsync(100000);await HoverAsync(null);if(position!=100000)throw new IOException("Click did not commit hover position.");
            if(editor.Snapshot.Revision!=revision)throw new IOException("Browsing modified the project.");
            timeline.SimulateHoverAt(middle);
            await Task.Delay(80);
            if (!timeline.IsHoverIndicatorVisible || timeline.IsMoveCursorActive || position != 100000 || preview.Session!.Status.RenderedTicks != 100000)
                throw new IOException("Timeline hover indicator changed preview/move cursor or failed to render.");
            await SaveUiImageAsync(Root, Path.Combine(directory, "timeline-hover.png"));
            timeline.HideHover();
            long scrubberTarget = Math.Clamp(editor.Timeline.Duration / 3, 1, Math.Max(1, editor.Timeline.Duration - 1));
            PreviewProgressScrubber.SimulateSeek(scrubberTarget);
            for (int i = 0; i < 20 && (Math.Abs(position - scrubberTarget) > 500 || Math.Abs(preview.Session!.Status.RenderedTicks - scrubberTarget) > 500); i++)
                await Task.Delay(40);
            if (Math.Abs(position - scrubberTarget) > 500 || Math.Abs(preview.Session!.Status.RenderedTicks - scrubberTarget) > 500 || Math.Abs(PreviewProgressScrubber.PositionTicks - position) > 500 || !timeline.IsHoverIndicatorVisible)
                throw new IOException($"Preview scrubber seek did not sync timeline/preview (pos={position}, rendered={preview.Session!.Status.RenderedTicks}, target={scrubberTarget}, scrubber={PreviewProgressScrubber.PositionTicks}, hoverVisible={timeline.IsHoverIndicatorVisible}).");
            await SaveUiImageAsync(Root, Path.Combine(directory, "scrubber-ui.png"));
            PreviewProgressScrubber.SimulateHover(null);
            checks.Add("Paused/playing hover, 100 rapid requests, restore, click commit, and preview scrubber seek/hover sync preserve authored revision and update timeline indicator + preview.");
            var peer=FrameworkElementAutomationPeer.CreatePeerForElement(timeline);
            if(peer.GetPattern(PatternInterface.Selection) is not ISelectionProvider selection||peer.GetPattern(PatternInterface.RangeValue) is not IRangeValueProvider range)throw new IOException("Timeline automation patterns missing.");
            RefreshTimeline();timeline.Reveal(0);await Task.Delay(100);
            if(!peer.GetName().Contains(L.T("S9D4973468893",1)))throw new IOException("Initial timeline accessibility selection did not match the clip inspector context.");
            if(selection.GetSelection().Length!=1)throw new IOException("Timeline selection was not accessible.");
            if(!timeline.FocusTimeline(FocusState.Keyboard)||!timeline.HasFocus)throw new IOException("Timeline keyboard focus failed.");range.SetValue(0.1);await Task.Delay(100);await BrowseRequests.Completion;
            if(position!=1000000)throw new IOException("Accessible playhead seek did not reach transport.");
            checks.Add("UI Automation exposes selected clip and RangeValue playhead; keyboard focus and accessible seek work.");
            long fullSource = editor.Snapshot.SourceDuration.Ticks;
            var trimClip = editor.Timeline.Clips.First();
            Notice.IsOpen = false;
            await EditAsync(new { op = "trimOutput", segmentId = trimClip.Id, outputTicks = Math.Max(200_000L, trimClip.OutputDuration / 2), edge = "end" });
            await Task.Delay(150);
            editController.Begin(EditorEditOwner.Timeline);
            var extendedCandidate = editController.Preview(EditorEditOwner.Timeline, new { op = "trimOutput", segmentId = trimClip.Id, outputTicks = fullSource * 2, edge = "end" });
            var extendedResolved = EngineSession.ResolveTimeline(extendedCandidate);
            timeline.Set(extendedResolved, selected, Math.Clamp(position, 0, Math.Max(0, extendedResolved.Duration - 1)));
            await Task.Delay(250);
            if (Notice.IsOpen) throw new IOException($"Trim extension raised notice: {Notice.Title} - {Notice.Message}");
            if (extendedResolved.Duration > fullSource) throw new IOException($"Clip extended beyond max source duration ({extendedResolved.Duration} > {fullSource}).");
            editController.Cancel(EditorEditOwner.Timeline);
            await PerformAsync(editor.UndoAsync);
            RefreshTimeline();
            checks.Add("Trimming right past max source duration clamps within bounds and extending a trimmed clip never fails thumbnail preparation.");
            double priorScale = timeline.PixelsPerSecond;
            timeline.PixelsPerSecond = 400;
            await Task.Delay(60);
            Root.UpdateLayout();
            long edgeTicks = Math.Clamp((long)(Math.Max(0.5, (timeline.ViewportWidth - 60) / 400.0) * 10_000_000L), 0, Math.Max(0, editor.Timeline.Duration - 10_000_000L));
            await SeekAsync(0);
            await Task.Delay(60);
            position = edgeTicks;
            UpdateClock();
            await StartPlaybackAsync();
            await Task.Delay(500);
            Pause();
            Root.UpdateLayout();
            if (timeline.HorizontalScrollOffset <= 0 || timeline.PlayheadX < timeline.HorizontalScrollOffset || timeline.PlayheadX > timeline.HorizontalScrollOffset + timeline.ViewportWidth)
                throw new IOException($"Playback did not auto-scroll zoomed timeline to keep playhead visible (offset={timeline.HorizontalScrollOffset}, playheadX={timeline.PlayheadX}, viewport={timeline.ViewportWidth}).");
            timeline.PixelsPerSecond = priorScale;
            await SeekAsync(0);
            checks.Add("Playback automatically scrolls zoomed-in timeline horizontally so the playhead remains visible.");
            Notice.IsOpen = false;
            int priorAspect = AspectRatio.SelectedIndex;
            await StartPlaybackAsync();
            await Task.Delay(80);
            loadingInspector = true; AspectRatio.SelectedIndex = 2; loadingInspector = false;
            await ApplyPictureAsync();
            if (Notice.IsOpen || editor.Snapshot.Canvas.Height <= editor.Snapshot.Canvas.Width)
                throw new IOException($"Switching aspect ratio while playing failed: {Notice.Title} - {Notice.Message} ({editor.Snapshot.Canvas.Width}x{editor.Snapshot.Canvas.Height}).");
            loadingInspector = true; AspectRatio.SelectedIndex = priorAspect >= 0 ? priorAspect : 1; loadingInspector = false;
            await ApplyPictureAsync();
            if (Notice.IsOpen) throw new IOException($"Restoring aspect ratio failed: {Notice.Title} - {Notice.Message}");
            checks.Add("Switching aspect ratio (16:9 -> 9:16) during playback stops audio cleanly and never fails with incomplete edit.");
            // Exercise actual WinUI drawing with a multi-hour, dense timeline.
            const long clipLength=60_000_000;const int clipCount=2400;
            var stress=new ResolvedTimeline(clipLength*clipCount,Enumerable.Range(0,clipCount).Select(i=>new ResolvedClip("c"+i,i*clipLength,clipLength,i*clipLength,clipLength,1,1)).ToArray(),[]){Effects=Enumerable.Range(0,12000).Select(i=>new ResolvedEffect("e"+i,"mask",i*10_000_000L,30_000_000L)).ToArray()};
            timeline.ThumbnailsEnabled=false;var timer=Stopwatch.StartNew();timeline.Set(stress,"c0",0);await Task.Delay(100);
            var times=new List<long>();int maximumElements=0;
            foreach(double scale in new[]{0.1,8,80,400}){timeline.PixelsPerSecond=scale;for(int i=0;i<12;i++){var tick=Stopwatch.StartNew();timeline.Reveal(stress.Duration*i/12);await Task.Delay(20);Root.UpdateLayout();times.Add(tick.ElapsedMilliseconds);maximumElements=Math.Max(maximumElements,timeline.RenderedElementCount);}}
            measures.Add(new{clips=clipCount,effects=12000,durationHours=stress.Duration/(double)TimeSpan.TicksPerHour,elapsedMilliseconds=timer.ElapsedMilliseconds,maximumDrawElements=maximumElements,uiStepP95Milliseconds=times.Order().ElementAt((int)(times.Count*0.95)),privateBytes=Process.GetCurrentProcess().PrivateMemorySize64});
            if(maximumElements>2500)throw new IOException("Timeline realized too many elements.");
            timeline.ThumbnailsEnabled=true;RefreshTimeline();timeline.Reveal(position);await Task.Delay(100);
            checks.Add("2400 clips / 12000 effects, four-hour WinUI timeline survives 48 scroll/zoom steps with bounded visible visuals.");
            await SaveUiImageAsync(Root,Path.Combine(directory,"editor-"+AppText.Language+".png"));
            var settings=new EditorSettingsWindow(editor.DirectoryPath,theme=>Root.RequestedTheme=theme,mediaCache);settings.Activate();await Task.Delay(150);await settings.ValidateP2Async(directory);settings.Close();
            success=true;
        }catch(Exception error){failure=error.ToString();}
        finally{Pause();await File.WriteAllTextAsync(Path.Combine(directory,"p2-ui.json"),JsonSerializer.Serialize(new{success,language=AppText.Language,checks,measures,failure},new JsonSerializerOptions{WriteIndented=true}));Environment.ExitCode=success?0:1;working=false;closing=false;await RequestCloseAsync();}
    }
}
