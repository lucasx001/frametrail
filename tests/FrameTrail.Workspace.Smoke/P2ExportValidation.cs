using System.Diagnostics;
using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;

internal static class P2ExportValidation
{
    internal static async Task<int> RunAsync(string project,string directory)
    {
        Directory.CreateDirectory(directory);var results=new List<object>();
        try {
            var original=ProjectJson.Deserialize(await File.ReadAllTextAsync(Path.Combine(project,"project.json")));
            // Keep a short, identical authored interval for all eight paths.
            var first=original.Segments[0];long length=Math.Min(20_000_000,first.SourceDuration.Ticks);
            var snapshot=original with{Segments=new[]{first with{SourceDuration=MediaTime.FromTicks(length),Rate=new()}}};
            foreach(var (width,height,fps) in new[]{(1920u,1080u,30),(1920u,1080u,60),(3840u,2160u,30),(3840u,2160u,60)})
            foreach(bool cpu in new[]{true,false}) {
                var candidate=snapshot with{Export=snapshot.Export with{Width=width,Height=height,Fps=new Rational{Numerator=fps},VideoCodec="h264"}};
                string name=$"{width}x{height}-{fps}-{(cpu?"cpu":"auto")}.mp4";
                using var media=new MediaSession(candidate,project);media.ForceCpuExport(cpu);
                var process=Process.GetCurrentProcess();var cpuStart=process.TotalProcessorTime;var watch=Stopwatch.StartNew();long peak=0;
                var task=Task.Run(()=>media.Export(Path.Combine(directory,name)));
                while(!task.IsCompleted){process.Refresh();peak=Math.Max(peak,process.PrivateMemorySize64);await Task.WhenAny(task,Task.Delay(20));}
                await task;watch.Stop();var metrics=media.ExportMetrics;
                ulong frames=metrics.GetProperty("gpuFrames").GetUInt64()+metrics.GetProperty("cpuFrames").GetUInt64();
                long expected=(length*fps+9_999_999)/10_000_000;
                if(frames!=(ulong)expected || (cpu&&metrics.GetProperty("gpuFrames").GetUInt64()!=0))throw new IOException("Export path/frame count mismatch.");
                results.Add(new{name,width,height,fps,cpu,milliseconds=watch.ElapsedMilliseconds,cpuMilliseconds=(process.TotalProcessorTime-cpuStart).TotalMilliseconds,peakPrivateBytes=peak,metrics});
                await File.WriteAllTextAsync(Path.Combine(directory,"progress.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
            }
            string protectedFile=Path.Combine(directory,"protected.mp4");await File.WriteAllTextAsync(protectedFile,"original");
            using(var cancelled=new MediaSession(snapshot,project)){cancelled.Cancel();try{cancelled.Export(protectedFile);throw new IOException("Cancellation was ignored.");}catch(OperationCanceledException){}}
            if(await File.ReadAllTextAsync(protectedFile)!="original")throw new IOException("Cancellation replaced the original.");
            await File.WriteAllTextAsync(Path.Combine(directory,"p2-export.json"),JsonSerializer.Serialize(new{success=true,results,cancelledTargetPreserved=true},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }catch(Exception error){await File.WriteAllTextAsync(Path.Combine(directory,"p2-export.json"),JsonSerializer.Serialize(new{success=false,results,error=error.ToString()},new JsonSerializerOptions{WriteIndented=true}));return 1;}
    }
}
