using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;

namespace FrameTrail.App;

// A transparent HWND behind the anti-aliased XAML surface. DWM alpha setup
// follows WinUIEx's TransparentTintBackdrop approach; see THIRD-PARTY-NOTICES.
internal sealed class RecorderTransparentBackdrop : SystemBackdrop
{
    private readonly nint window;
    private readonly SubclassProcedure procedure;
    private Windows.UI.Composition.CompositionColorBrush? brush;
    private bool hooked;
    [ThreadStatic] private static Windows.UI.Composition.Compositor? compositor;
    [ThreadStatic] private static nint dispatcherController;
    private delegate nint SubclassProcedure(nint window, uint message, nuint wparam, nint lparam, nuint id, nuint data);
    [StructLayout(LayoutKind.Sequential)] private struct QueueOptions { public int Size, ThreadType, Apartment; }
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BlurBehind { public uint Flags; public int Enabled; public nint Region; public int Transition; }
    [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(QueueOptions options, out nint controller);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint window, SubclassProcedure procedure, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProcedure procedure, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wparam, nint lparam);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint window, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmEnableBlurBehindWindow(nint window, ref BlurBehind blur);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int index);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out RecorderWindowChrome.Rect rect);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref RecorderWindowChrome.Rect rect, nint brush);

    internal RecorderTransparentBackdrop(nint window) { this.window = window; procedure = Procedure; }
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        // WinUI can notify the hidden recorder with no connected target during a
        // theme change. The base implementation rejects that null target.
        if (target is not null) base.OnDefaultSystemBackdropConfigurationChanged(target, root);
    }
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        if (compositor is null) {
            if (Windows.System.DispatcherQueue.GetForCurrentThread() is null && dispatcherController == 0)
                Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(new QueueOptions { Size = Marshal.SizeOf<QueueOptions>(), ThreadType = 2, Apartment = 2 }, out dispatcherController));
            compositor = new Windows.UI.Composition.Compositor();
        }
        ConfigureAlpha(); brush = compositor.CreateColorBrush(Microsoft.UI.Colors.Transparent); target.SystemBackdrop = brush;
        hooked = SetWindowSubclass(window, procedure, 0x4654, 0);
    }
    private void ConfigureAlpha()
    {
        var margins = new Margins(); Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(window, ref margins));
        var region = CreateRectRgn(-2, -2, -1, -1);
        try { var blur = new BlurBehind { Flags = 3, Enabled = 1, Region = region }; Marshal.ThrowExceptionForHR(DwmEnableBlurBehindWindow(window, ref blur)); }
        finally { if (region != 0) DeleteObject(region); }
    }
    private nint Procedure(nint handle, uint message, nuint wparam, nint lparam, nuint id, nuint data)
    {
        // Zero alpha, rather than the default opaque black, under XAML's rounded edge.
        if (message == 0x14 && wparam != 0 && GetClientRect(handle, out var rect)) { FillRect((nint)wparam, ref rect, GetStockObject(4)); return 1; }
        if (message == 0x31E) ConfigureAlpha();
        return DefSubclassProc(handle, message, wparam, lparam);
    }
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        if (hooked) { RemoveWindowSubclass(window, procedure, 0x4654); hooked = false; }
        target.SystemBackdrop = null; brush?.Dispose(); brush = null;
        base.OnTargetDisconnected(target);
    }
}
