using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace FrameTrail.App;

// Deliberately omit exception messages, file paths, command lines and user data.
internal static class SafeDiagnostics
{
    internal static string Describe(Exception error)
    {
        var failures = new List<object>();
        for (int i = 0; i < 4 && error is not null; i++, error = error.InnerException!) {
            var methods = new StackTrace(error, false).GetFrames()?.Take(32)
                .Select(frame => frame.GetMethod()).Where(method => method is not null)
                .Select(method => method!.DeclaringType?.FullName + "." + method.Name).ToArray();
            failures.Add(new { type = error.GetType().FullName, hresult = $"0x{error.HResult:X8}", methods });
        }
        return JsonSerializer.Serialize(new { timestampUtc = DateTimeOffset.UtcNow,
            version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
            osVersion = Environment.OSVersion.Version.ToString(), failures }, new JsonSerializerOptions { WriteIndented = true });
    }
    internal static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "crashes");
    internal static void Record(Exception error)
    {
        try {
            Directory.CreateDirectory(DirectoryPath);
            // A distinct extension excludes older raw crash logs from diagnostic exports.
            File.WriteAllText(Path.Combine(DirectoryPath, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.diagnostic.json"), Describe(error));
            foreach (var old in new DirectoryInfo(DirectoryPath).EnumerateFiles("*.diagnostic.json").OrderByDescending(f => f.LastWriteTimeUtc).Skip(10)) old.Delete();
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    internal static string Export()
    {
        var reports = Directory.Exists(DirectoryPath) ? new DirectoryInfo(DirectoryPath).EnumerateFiles("*.diagnostic.json")
            .Where(file => file.Length <= 64 * 1024).OrderByDescending(file => file.LastWriteTimeUtc).Take(10)
            .Select(file => File.ReadAllText(file.FullName)).ToArray() : [];
        return JsonSerializer.Serialize(new { version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
            osVersion = Environment.OSVersion.Version.ToString(), reports }, new JsonSerializerOptions { WriteIndented = true });
    }
}
