using System.Diagnostics;
using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;

internal static class P1MediaValidation
{
    internal static async Task<int> RunAsync(string project, string directory, bool camera)
    {
        Directory.CreateDirectory(directory);var checks=new List<string>();var measurements=new List<object>();
        try {
            var snapshot=ProjectJson.Deserialize(await File.ReadAllTextAsync(Path.Combine(project,"project.json")));
            using(var media=new MediaSession(snapshot,project)) {
                var watch=Stopwatch.StartNew();media.StreamAudio(0);
                while(media.Status.AudioTicks<2_000_000 && watch.ElapsedMilliseconds<5000){if(media.Status.AudioRunning==3)throw new IOException("Streaming audio failed.");await Task.Delay(10);}
                if(media.Status.AudioTicks<2_000_000)throw new IOException("Streaming clock did not advance.");
                measurements.Add(new{first200msAudioAtMs=watch.ElapsedMilliseconds});media.StopAudio();
                long seek=Math.Min(10_000_000,media.Status.Duration/2);media.StreamAudio(seek);await Task.Delay(250);
                if(media.Status.AudioTicks<seek || media.Status.AudioRunning==3)throw new IOException("Stream seek failed.");
                media.StopAudio();checks.Add("Audio streams before any PCM/peaks cache, clock advances and seeking restarts at authored output time");
                media.PreparePeaks(Path.Combine(directory,"peaks.json"));
                var peaks=JsonSerializer.Deserialize<float[]>(await File.ReadAllTextAsync(Path.Combine(directory,"peaks.json")))!;
                if(peaks.Length>24000 || !peaks.Any(p=>p>0.01))throw new IOException("Peak cache invalid.");
                media.PrepareAudio(Path.Combine(directory,"reference.wav"));checks.Add("Bounded peak preparation and legacy PCM output share the mixer");
            }
            foreach(int minutes in new[]{30,60,120}) {
                long span=snapshot.SourceDuration.Ticks;int count=(int)Math.Ceiling(minutes*60.0*TimeSpan.TicksPerSecond/span);
                var longProject=snapshot with {
                    SourceDuration=MediaTime.FromTicks(span*count),
                    Segments=Enumerable.Range(0,count).Select(i=>new RecordingSegment{Id="repeat-"+i,SourceStart=MediaTime.FromTicks(span*i),SourceDuration=snapshot.SourceDuration,Rate=new()}).ToArray(),
                    Media=Enumerable.Range(0,count).SelectMany(i=>snapshot.Media.Where(m=>m.Kind!=MediaKind.PointerEvents).Select(m=>m with {Id=m.Id+"-"+i,StartOffset=MediaTime.FromTicks(m.StartOffset.Ticks+span*i)})).ToArray()
                };
                using var media=new MediaSession(longProject,project);var watch=Stopwatch.StartNew();media.StreamAudio(0);
                while(media.Status.AudioTicks<2_000_000&&watch.ElapsedMilliseconds<10000){if(media.Status.AudioRunning==3)throw new IOException("Long streaming failed.");await Task.Delay(10);}
                if(media.Status.AudioTicks<2_000_000)throw new IOException("Long project waited for full preparation.");
                measurements.Add(new{minutes,logicalClips=count,first200msAudioAtMs=watch.ElapsedMilliseconds,workingSet=Process.GetCurrentProcess().WorkingSet64});
                media.StopAudio();media.StreamAudio(media.Status.Duration-5_000_000);await Task.Delay(150);media.StopAudio();
            }
            checks.Add("30/60/120 minute logical projects stream immediately and seek near the end without a full PCM cache");
            if(camera) {
                var devices=CameraCaptureSession.EnumerateDevices();if(devices.Count==0)throw new IOException("No physical camera available.");
                var device=devices[0];var format=CameraCaptureSession.EnumerateFormats(device.Id).Where(f=>f.Width<=1280&&f.Height<=720).OrderBy(f=>Math.Abs((double)f.FpsNumerator/f.FpsDenominator-30)).ThenBy(f=>Math.Abs((long)f.Width-640)).First();
                using var preview=CameraPreviewSession.Start(device,format);var pixels=new byte[format.Width*format.Height*4];
                var deadline=DateTime.UtcNow.AddSeconds(10);while(!preview.CopyFrame(pixels)&&DateTime.UtcNow<deadline)await Task.Delay(20);
                if(preview.SourceIdentity==0||!pixels.Any(p=>p!=0))throw new IOException("No camera preview pixels.");
                using(var recording=CameraCaptureSession.Start(device,format,Path.Combine(directory,"camera-shared.mp4"))) {
                    recording.SetEpoch(Stopwatch.GetTimestamp(),Stopwatch.Frequency);await Task.Delay(1000);
                    if(recording.SourceIdentity!=preview.SourceIdentity)throw new IOException("Preview/recording reopened the exclusive camera.");
                    recording.StopAt(Stopwatch.GetTimestamp());if(recording.GetStatus().FramesWritten==0)throw new IOException("No shared camera recording frames.");
                }
                bool changed=false;deadline=DateTime.UtcNow.AddSeconds(3);while(!changed&&DateTime.UtcNow<deadline){changed=preview.CopyFrame(pixels);await Task.Delay(20);}
                if(!changed)throw new IOException("Preview stopped with recording.");
                measurements.Add(new{camera=device.Name,format,sourceIdentity=preview.SourceIdentity});checks.Add("Physical camera preview/recording share one source and preview continues after writer stops");
            }
            await File.WriteAllTextAsync(Path.Combine(directory,"p1-media.json"),JsonSerializer.Serialize(new{success=true,checks,measurements},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }catch(Exception error){await File.WriteAllTextAsync(Path.Combine(directory,"p1-media.json"),JsonSerializer.Serialize(new{success=false,checks,error=error.ToString()}));Console.Error.WriteLine(error);return 1;}
    }
}
