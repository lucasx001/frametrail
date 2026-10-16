using FrameTrail.App;
using Xunit;

namespace FrameTrail.App.Tests;

public sealed class DiagnosticsTests
{
    [Fact] public void CrashReportExcludesMessagesPathsAndInnerExceptionSecrets()
    {
        Exception error;
        try { throw new IOException(@"C:\Users\private\secret-project\customer.wav token=private", new Exception("confidential notes")); }
        catch (Exception caught) { error = caught; }
        var report = SafeDiagnostics.Describe(error);
        Assert.Contains("System.IO.IOException", report);
        Assert.Contains(nameof(CrashReportExcludesMessagesPathsAndInnerExceptionSecrets), report);
        Assert.DoesNotContain("secret-project", report);
        Assert.DoesNotContain("customer.wav", report);
        Assert.DoesNotContain("confidential notes", report);
        Assert.DoesNotContain("token=", report);
    }
}
