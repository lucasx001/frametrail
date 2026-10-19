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
            await StartPlaybackAsync();await Task.Delay(200);await HoverAsync(middle);long anchor=position;
            await HoverAsync(null);if(!playing||position<anchor)throw new IOException("Hover exit did not resume playback.");Pause();
            await HoverAsync(middle);await SeekAsync(100000);await HoverAsync(null);if(position!=100000)throw new IOException("Click did not commit hover position.");
            if(editor.Snapshot.Revision!=revision)throw new IOException("Browsing modified the project.");
            checks.Add("Paused/playing hover, 100 rapid requests, restore and click commit preserve authored revision.");
            var peer=FrameworkElementAutomationPeer.CreatePeerForElement(timeline);
            if(peer.GetPattern(PatternInterface.Selection) is not ISelectionProvider selection||peer.GetPattern(PatternInterface.RangeValue) is not IRangeValueProvider range)throw new IOException("Timeline automation patterns missing.");
            RefreshTimeline();timeline.Reveal(0);await Task.Delay(100);
            if(!peer.GetName().Contains(L.T("S9D4973468893",1)))throw new IOException("Initial timeline accessibility selection did not match the clip inspector context.");
            if(selection.GetSelection().Length!=1)throw new IOException("Timeline selection was not accessible.");
            if(!timeline.FocusTimeline(FocusState.Keyboard)||!timeline.HasFocus)throw new IOException("Timeline keyboard focus failed.");range.SetValue(0.1);await Task.Delay(100);await BrowseRequests.Completion;
            if(position!=1000000)throw new IOException("Accessible playhead seek did not reach transport.");
            checks.Add("UI Automation exposes selected clip and RangeValue playhead; keyboard focus and accessible seek work.");
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
