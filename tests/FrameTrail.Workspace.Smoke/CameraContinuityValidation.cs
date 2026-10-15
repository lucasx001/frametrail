using System.Diagnostics;
using System.Text.Json;
using FrameTrail.Interop;

internal static class CameraContinuityValidation
{
    // Explicit hardware QA: camera only, no audio, desktop capture or upload.
    internal static async Task<int> RunAsync(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        try
        {
            var device = CameraCaptureSession.EnumerateDevices().First();
            var format = CameraCaptureSession.EnumerateFormats(device.Id)
                .Where(f => f.Width <= 1280 && f.Height <= 720 && (double)f.FpsNumerator / f.FpsDenominator >= 29)
                .OrderByDescending(f => f.Width * f.Height).ThenByDescending(f => (double)f.FpsNumerator / f.FpsDenominator).First();
            var takes = new List<object>();
            CameraCaptureSession? active = null;
            try
            {
                active = CameraCaptureSession.Start(device, format, Path.Combine(directory, "camera-0.mp4"));
                long epoch = Stopwatch.GetTimestamp(); active.SetEpoch(epoch, Stopwatch.Frequency);
                ulong identity = active.SourceIdentity;
                for (int i = 1; i <= 3; i++)
                {
                    await Task.Delay(2000);
                    CameraCaptureSession? next = null;
                    try
                    {
                        next = CameraCaptureSession.Start(device, format, Path.Combine(directory, $"camera-{i}.mp4"));
                        long boundary = Stopwatch.GetTimestamp(); next.SetEpoch(boundary, Stopwatch.Frequency);
                        if (next.SourceIdentity != identity) throw new IOException("Camera source reopened at checkpoint.");
                        await WaitForFrameAsync(next);
                        active.StopAt(boundary);
                        var status = active.GetStatus();
                        if (status.ErrorCode < 0 || status.FramesWritten == 0) throw new IOException(status.Message);
                        takes.Add(new { index = i - 1, epochQpc = epoch, boundaryQpc = boundary, sourceIdentity = identity, status });
                        active.Dispose(); active = next; next = null; epoch = boundary;
                    }
                    finally { next?.Dispose(); }
                }
                await Task.Delay(2000); long end = Stopwatch.GetTimestamp(); active.StopAt(end);
                takes.Add(new { index = 3, epochQpc = epoch, boundaryQpc = end, sourceIdentity = identity, status = active.GetStatus() });
                active.Dispose(); active = null;
                // Closing the final file must release the device so a later recording works.
                using var resumed = CameraCaptureSession.Start(device, format, Path.Combine(directory, "camera-resumed.mp4"));
                resumed.SetEpoch(Stopwatch.GetTimestamp(), Stopwatch.Frequency); await WaitForFrameAsync(resumed);
                await Task.Delay(500); resumed.StopAt(Stopwatch.GetTimestamp());
                if (resumed.SourceIdentity == identity) throw new IOException("Stopped camera source was retained unexpectedly.");
                await File.WriteAllTextAsync(Path.Combine(directory, "camera-continuity.json"), JsonSerializer.Serialize(new {
                    success = true, device = device.Name, format, qpcFrequency = Stopwatch.Frequency, takes, resumedSourceIdentity = resumed.SourceIdentity,
                    timestampUtc = DateTimeOffset.UtcNow
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("Camera checkpoints reused one source; final close/reopen passed."); return 0;
            }
            finally { active?.Dispose(); }
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "camera-continuity.json"), JsonSerializer.Serialize(new { success = false, error = error.ToString() }));
            Console.Error.WriteLine(error); return 1;
        }
    }
    private static async Task WaitForFrameAsync(CameraCaptureSession camera)
    {
        long start = Stopwatch.GetTimestamp();
        while (camera.GetStatus().FramesWritten == 0)
        {
            var status = camera.GetStatus(); if (status.ErrorCode < 0) throw new IOException(status.Message);
            if (Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(10)) throw new TimeoutException("Camera produced no frame.");
            await Task.Delay(10);
        }
    }
}
