using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace FrameTrail.App;

internal static class RecorderWindowChrome
{
    private static readonly Dictionary<nint, (int Width, int Height, int Diameter)> contours = new();
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref uint value, uint size);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(nint region, int x, int y);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }

    internal static double Scale(Window window) => Math.Max(1, GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window))) / 96.0;
    internal static object Diagnostics(Window window)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window); GetWindowRect(handle, out var outer); GetClientRect(handle, out var client);
        return new { windowWidth = outer.Right - outer.Left, windowHeight = outer.Bottom - outer.Top, clientWidth = client.Right, clientHeight = client.Bottom, scale = Scale(window), style = $"0x{GetWindowLongPtr(handle, -16).ToInt64():X8}", extendedStyle = $"0x{GetWindowLongPtr(handle, -20).ToInt64():X8}" };
    }
    internal static void Configure(Window window, double width, double height, double radius, bool center = false)
    {
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false; presenter.IsMaximizable = false; presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(false, false);
        }
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        // WASDK 1.8 can retain WS_DLGFRAME and WS_EX_WINDOWEDGE even when the
        // presenter reports no border: a 3 px frame on each side at 115 DPI.
        // Remove the actual non-client styles and recalculate the client area.
        long style = GetWindowLongPtr(handle, -16).ToInt64();
        long extendedStyle = GetWindowLongPtr(handle, -20).ToInt64();
        SetWindowLongPtr(handle, -16, (nint)((style & ~0x00CF0000L) | 0x80000000L)); // borderless popup; preserve visibility/clipping
        SetWindowLongPtr(handle, -20, (nint)(extendedStyle & ~0x00020301L)); // dialog/window/client/static edges
        if (!SetWindowPos(handle, 0, 0, 0, 0, 0, 0x0037)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        // The XAML capsule and the region below own the contour. A second DWM
        // contour would add a rectangular border around that rounded surface.
        uint noRound = 1, noBorder = 0xFFFFFFFE;
        DwmSetWindowAttribute(handle, 33, ref noRound, sizeof(uint));
        DwmSetWindowAttribute(handle, 34, ref noBorder, sizeof(uint));
        window.SystemBackdrop = new RecorderTransparentBackdrop(handle);
        window.Closed += (_, _) => contours.Remove(handle);
        Resize(window, width, height, radius, center);
    }
    internal static bool HasMatchingClientBounds(Window window)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window); GetWindowRect(handle, out var outer); GetClientRect(handle, out var client);
        return outer.Right - outer.Left == client.Right && outer.Bottom - outer.Top == client.Bottom;
    }
    internal static int CountClippedPixels(Window window, int width, int height, byte[] bgra)
    {
        var region = CreateRectRgn(0, 0, 0, 0); int clipped = 0;
        try {
            if (GetWindowRgn(WinRT.Interop.WindowNative.GetWindowHandle(window), region) == 0) throw new InvalidOperationException("Window input region missing.");
            var size = window.AppWindow.Size;
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                if (bgra[(y * width + x) * 4 + 3] > 1 && !PtInRegion(region, x * size.Width / width, y * size.Height / height)) clipped++;
        } finally { DeleteObject(region); }
        return clipped;
    }
    internal static void Resize(Window window, double width, double height, double radius, bool center = false)
    {
        var scale = Scale(window); var app = window.AppWindow;
        int w = (int)Math.Ceiling(width * scale), h = (int)Math.Ceiling(height * scale);
        var work = WorkArea(window); var current = app.Position; var size = app.Size;
        app.ResizeClient(new SizeInt32(w, h));
        var outer = app.Size;
        int x = center ? (work.Left + work.Right - outer.Width) / 2 : current.X + (size.Width - outer.Width) / 2;
        int y = center ? (work.Top + work.Bottom - outer.Height) / 2 : current.Y + (size.Height - outer.Height) / 2;
        app.Move(new PointInt32(Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - outer.Width)), Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - outer.Height))));
        Round(window, radius);
    }
    internal static void Round(Window window, double radius)
    {
        var size = window.AppWindow.Size; int diameter = (int)Math.Ceiling(radius * 2 * Scale(window));
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var contour = (size.Width, size.Height, diameter);
        if (contours.TryGetValue(handle, out var current) && current == contour) return;
        // Setting a region itself emits a window-position notification. Cache
        // before applying it so AppWindow.Changed cannot create a feedback loop.
        contours[handle] = contour;
        // This region is only the input fence. Keep it outside XAML's visible
        // anti-aliased curve: an integer GDI region must not cut the 1 DIP stroke.
        const int guard = 2;
        var region = CreateRoundRectRgn(-guard, -guard, size.Width + guard + 1, size.Height + guard + 1, diameter + guard * 2, diameter + guard * 2);
        if (region != 0 && SetWindowRgn(handle, region, true) == 0) { contours.Remove(handle); DeleteObject(region); }
        // On success the window owns the region; deleting it would invalidate the contour.
    }
    internal static Rect WorkArea(Window window)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(MonitorFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(window), 2), ref info); return info.Work;
    }
    internal static void Drag(Window window, PointerRoutedEventArgs args)
    {
        if (!args.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        args.Handled = true; ReleaseCapture(); SendMessage(WinRT.Interop.WindowNative.GetWindowHandle(window), 0xA1, 2, 0);
    }
}
