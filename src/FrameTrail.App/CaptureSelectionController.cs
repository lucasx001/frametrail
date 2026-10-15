using FrameTrail.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.System;

namespace FrameTrail.App;

/// <summary>Transient, local framing UI. The layered surface never supplies recording pixels.</summary>
internal sealed class CaptureSelectionController : IDisposable
{
    private readonly int mode;
    private readonly IReadOnlyList<CaptureSource> displays, windows;
    private readonly Action<CaptureSource, CaptureRegion?> confirmed;
    private readonly Action cancelled;
    private readonly List<SelectionShade> shades = new();
    private readonly SelectionActionWindow card;
    private CaptureSource? hovered, selected, dragDisplay;
    private CaptureRegion? region;
    private int startX, startY;
    private bool dragging, disposed;
    private long lastDraw;
    internal CaptureRegion? SelectedRegion => region;
    internal FrameworkElement Surface => (FrameworkElement)card.Content;

    internal CaptureSelectionController(Window owner, int mode, IReadOnlyList<CaptureSource> sources,
        Action<CaptureSource, CaptureRegion?> confirmed, Action cancelled)
    {
        this.mode = mode; this.confirmed = confirmed; this.cancelled = cancelled;
        displays = sources.Where(s => s.Kind == CaptureSourceKind.Monitor).ToArray();
        windows = sources.Where(s => s.Kind == CaptureSourceKind.Window && !SelectionShade.IsOwnWindow(s.Handle)).ToArray();
        if (displays.Count == 0) throw new InvalidOperationException("未发现可选择的显示器，请刷新来源。");
        if (mode == 1 && windows.Count == 0) throw new InvalidOperationException("没有可录制的应用窗口。");
        card = new SelectionActionWindow(Action, Cancel);
        card.Closed += (_, _) => { if (!disposed) Cancel(); };
        try { foreach (var display in displays) shades.Add(new SelectionShade(display, Pointer, Cancel, Action)); }
        catch { Dispose(); throw; }
        hovered = displays[0];
    }
    internal void Show()
    {
        foreach (var shade in shades) shade.Show();
        card.Activate();
        if (SelectionShade.Cursor() is var point) hovered = DisplayAt(point.X, point.Y) ?? displays[0];
        UpdateCard(); Redraw(force: true);
    }
    private CaptureSource? DisplayAt(int x, int y) => displays.FirstOrDefault(s => x >= s.X && y >= s.Y && x < s.X + s.Width && y < s.Y + s.Height);
    internal void Pointer(uint message, int x, int y)
    {
        if (disposed) return;
        var display = DisplayAt(x, y); if (display is null) return;
        if (mode == 2)
        {
            if (message == 0x201) { dragDisplay = display; selected = null; dragging = true; startX = x; startY = y; region = null; }
            if (dragging && dragDisplay is not null)
            {
                var d = dragDisplay;
                x = Math.Clamp(x, d.X, d.X + d.Width); y = Math.Clamp(y, d.Y, d.Y + d.Height);
                int left = Math.Min(x, startX) - d.X, top = Math.Min(y, startY) - d.Y;
                int width = Math.Abs(x - startX) & ~1, height = Math.Abs(y - startY) & ~1;
                region = width >= 2 && height >= 2 ? new(left, top, width, height) : null;
                hovered = d;
            }
            if (message == 0x202) { dragging = false; if (region is not null) selected = dragDisplay; }
        }
        else
        {
            if (selected is not null && message == 0x200) return;
            hovered = mode == 0 ? display : windows.FirstOrDefault(w => SelectionShade.Contains(w, x, y));
            if (message == 0x201) selected = hovered;
        }
        UpdateCard(); Redraw(message != 0x200);
    }
    private void UpdateCard()
    {
        var source = selected ?? hovered;
        var display = mode == 1 && source is not null ? DisplayAt(source.X, source.Y) ?? displays[0] : source ?? displays[0];
        if (mode == 2)
        {
            card.SetText(region is CaptureRegion r ? $"{r.Width} × {r.Height}" : "拖动框选录制区域",
                region is not null ? "仅录制框内画面" : "在一个显示器内拖动 · Esc 取消",
                "开始录制", selected is not null && region is not null && selected.IsSdr, true);
        }
        else if (source is null)
            card.SetText("选择一个窗口", "移动鼠标选择窗口，单击确认", "开始录制", false, false);
        else
        {
            string title = mode == 0 ? $"显示器 {displays.ToList().FindIndex(d => d.Handle == source.Handle) + 1}" : source.Name;
            card.SetText(title, source.IsSdr ? $"{source.Width} × {source.Height}" + (selected is null ? " · 单击选择" : " · 已选择") : "当前仅支持 SDR 显示器",
                selected is null ? (mode == 0 ? "选择此显示器" : "选择此窗口") : "开始录制", source.IsSdr, selected is not null);
        }
        card.PositionOn(display);
    }
    private void Redraw(bool force)
    {
        long now = Environment.TickCount64; if (!force && now - lastDraw < 30) return; lastDraw = now;
        var source = selected ?? hovered;
        RecorderWindowChrome.Rect? frame = source is null ? null : SelectionShade.Bounds(source);
        if (mode == 2)
            frame = region is CaptureRegion r && dragDisplay is CaptureSource d ? new() { Left = d.X + r.X, Top = d.Y + r.Y, Right = d.X + r.X + r.Width, Bottom = d.Y + r.Y + r.Height } : null;
        foreach (var shade in shades) shade.Paint(frame, selected is not null);
    }
    private void Action()
    {
        if (disposed) return;
        if (selected is null && mode != 2) { selected = hovered; UpdateCard(); Redraw(true); return; }
        if (selected is null || !selected.IsSdr || mode == 2 && region is null) return;
        var source = selected; var crop = region; Dispose(); confirmed(source, crop);
    }
    private void Cancel() { if (disposed) return; Dispose(); cancelled(); }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        foreach (var shade in shades) shade.Dispose(); shades.Clear(); card.Close();
    }
}

internal sealed class SelectionActionWindow : Window
{
    private readonly TextBlock title = new() { FontSize = 14, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock detail = new() { FontSize = 11, Opacity = 0.56 };
    private readonly Button start;
    private readonly Action cancel;
    private readonly Grid root;
    private nuint displayHandle;
    internal SelectionActionWindow(Action action, Action cancel)
    {
        this.cancel = cancel; Title = "FrameTrail · 选择录制来源";
        title.FontFamily = (FontFamily)Application.Current.Resources["RecorderSemiboldFont"];
        var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 16, 0) };
        labels.Children.Add(title); labels.Children.Add(detail);
        var dismiss = new Button { Content = "取消  esc", Style = (Style)Application.Current.Resources["RecorderPillButton"], Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        dismiss.Click += (_, _) => cancel();
        start = new Button { Content = "开始录制", Style = (Style)Application.Current.Resources["RecorderPillButton"], MinWidth = 144, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
        start.Click += (_, _) => action();
        var row = new Grid { ColumnSpacing = 6, Padding = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(144) });
        row.Children.Add(labels); Grid.SetColumn(dismiss, 1); row.Children.Add(dismiss); Grid.SetColumn(start, 2); row.Children.Add(start);
        root = new Grid { CornerRadius = new CornerRadius(30) }; root.Children.Add(row); Content = root;
        SetTheme(); root.ActualThemeChanged += (_, _) => SetTheme();
        root.KeyDown += (_, args) => { if (args.Key == VirtualKey.Escape) { cancel(); args.Handled = true; } else if (args.Key == VirtualKey.Enter && start.IsEnabled) { action(); args.Handled = true; } };
        RecorderWindowChrome.Configure(this, 490, 60, 30);
        try { RecordingUtilities.ExcludeWindow((nuint)WinRT.Interop.WindowNative.GetWindowHandle(this), true); } catch { }
    }
    private void SetTheme()
    {
        var dark = root.ActualTheme == ElementTheme.Dark;
        root.Background = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(255, 16, 17, 19) : Windows.UI.Color.FromArgb(255, 253, 253, 254));
        title.Foreground = detail.Foreground = new SolidColorBrush(dark ? Microsoft.UI.Colors.White : Windows.UI.Color.FromArgb(255, 26, 26, 26));
    }
    internal void SetText(string name, string dimensions, string action, bool enabled, bool records)
    {
        title.Text = name; detail.Text = dimensions; start.Content = action; start.IsEnabled = enabled;
        start.Background = records ? (Brush)Application.Current.Resources["RecorderRed"] : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 40, 40, 42));
    }
    internal void PositionOn(CaptureSource display)
    {
        if (displayHandle == display.Handle) return; displayHandle = display.Handle;
        var size = AppWindow.Size; var scale = RecorderWindowChrome.Scale(this);
        AppWindow.Move(new PointInt32(display.X + (display.Width - size.Width) / 2, display.Y + display.Height - size.Height - (int)(132 * scale)));
    }
}

/// <summary>Premultiplied-alpha selection shade and viewfinder, without taking a desktop screenshot.</summary>
internal sealed class SelectionShade : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WndProc(nint window, uint message, nuint wparam, nint lparam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass { public uint Size, Style; public WndProc Procedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName; public nint SmallIcon; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int XPels, YPels; public uint Used, Important; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Size { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct Blend { public byte Operation, Flags, Alpha, Format; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint extended, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern nint DefWindowProc(nint window, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint SetCapture(nint window);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out RecorderWindowChrome.Rect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(nint window, nint dc, ref Point position, ref Size size, nint source, ref Point origin, uint key, ref Blend blend, uint flags);
    [DllImport("user32.dll")] private static extern nint LoadCursor(nint instance, nint name);

    private readonly CaptureSource display;
    private readonly Action<uint, int, int> pointer;
    private readonly Action cancel, start;
    private readonly WndProc procedure;
    private readonly string className = "FrameTrail.Selection." + Guid.NewGuid().ToString("N");
    private readonly nint window, dc, bitmap, bits, previous;
    private readonly int[] pixels;
    private bool disposed;
    internal static Point Cursor() { GetCursorPos(out var point); return point; }
    internal static bool IsOwnWindow(nuint handle) { GetWindowThreadProcessId((nint)handle, out var process); return process == Environment.ProcessId; }
    internal static RecorderWindowChrome.Rect Bounds(CaptureSource source)
    {
        if (source.Kind == CaptureSourceKind.Window && GetWindowRect((nint)source.Handle, out var rect)) return rect;
        return new() { Left = source.X, Top = source.Y, Right = source.X + source.Width, Bottom = source.Y + source.Height };
    }
    internal static bool Contains(CaptureSource source, int x, int y) { var r = Bounds(source); return x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom; }
    internal SelectionShade(CaptureSource display, Action<uint, int, int> pointer, Action cancel, Action start)
    {
        this.display = display; this.pointer = pointer; this.cancel = cancel; this.start = start; procedure = Procedure;
        if (display.Width <= 0 || display.Height <= 0 || (long)display.Width * display.Height > 40_000_000) throw new InvalidOperationException("此显示器尺寸超出选择器支持范围。");
        var wc = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = procedure, Instance = GetModuleHandle(null), ClassName = className, Cursor = LoadCursor(0, 32515) };
        if (RegisterClassEx(ref wc) == 0) throw new System.ComponentModel.Win32Exception();
        window = CreateWindowEx(0x00080088, className, "FrameTrail · 录制范围", 0x80000000, display.X, display.Y, display.Width, display.Height, 0, 0, wc.Instance, 0);
        dc = CreateCompatibleDC(0); var info = new BitmapInfo { Size = 40, Width = display.Width, Height = -display.Height, Planes = 1, Bits = 32 };
        bitmap = CreateDIBSection(dc, ref info, 0, out bits, 0, 0); previous = SelectObject(dc, bitmap);
        if (window == 0 || dc == 0 || bitmap == 0 || bits == 0) { Dispose(); throw new System.ComponentModel.Win32Exception(); }
        pixels = new int[checked(display.Width * display.Height)];
        try { RecordingUtilities.ExcludeWindow((nuint)window, true); } catch { }
    }
    internal void Show() { ShowWindow(window, 4); Paint(null, false); }
    private nint Procedure(nint target, uint message, nuint wparam, nint lparam)
    {
        if (message == 0x21) return 3; // The action card keeps keyboard focus while framing.
        if (message is 0x200 or 0x201 or 0x202)
        {
            var point = Cursor(); if (message == 0x201) SetCapture(target); if (message == 0x202) ReleaseCapture();
            pointer(message, point.X, point.Y); return 0;
        }
        if (message == 0x100 && wparam == 27) { cancel(); return 0; }
        if (message == 0x100 && wparam == 13) { start(); return 0; }
        return DefWindowProc(target, message, wparam, lparam);
    }
    internal void Paint(RecorderWindowChrome.Rect? selection, bool locked)
    {
        if (disposed) return;
        Array.Fill(pixels, unchecked((int)0x42000000));
        if (selection is RecorderWindowChrome.Rect rect)
        {
            int l = rect.Left - display.X, t = rect.Top - display.Y, r = rect.Right - display.X, b = rect.Bottom - display.Y;
            Fill(l, t, r, b, unchecked((int)0x14000000));
            int inset = 20, arm = locked ? 54 : 38, stroke = locked ? 3 : 2;
            if (rect.Left == display.X && rect.Top == display.Y && rect.Right == display.X + display.Width && rect.Bottom == display.Y + display.Height)
            { l += inset; t += inset; r -= inset; b -= inset; }
            foreach (var (x, y, dx, dy) in new[] { (l, t, 1, 1), (r, t, -1, 1), (r, b, -1, -1), (l, b, 1, -1) })
            {
                Fill(Math.Min(x, x + arm * dx), Math.Min(y, y + stroke * dy), Math.Max(x, x + arm * dx), Math.Max(y, y + stroke * dy), -1);
                Fill(Math.Min(x, x + stroke * dx), Math.Min(y, y + arm * dy), Math.Max(x, x + stroke * dx), Math.Max(y, y + arm * dy), -1);
            }
        }
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        var position = new Point { X = display.X, Y = display.Y }; var size = new Size { Width = display.Width, Height = display.Height }; var origin = new Point(); var blend = new Blend { Alpha = 255, Format = 1 };
        var screen = GetDC(0);
        try { UpdateLayeredWindow(window, screen, ref position, ref size, dc, ref origin, 0, ref blend, 2); }
        finally { ReleaseDC(0, screen); }
    }
    private void Fill(int left, int top, int right, int bottom, int color)
    {
        left = Math.Clamp(left, 0, display.Width); right = Math.Clamp(right, 0, display.Width);
        top = Math.Clamp(top, 0, display.Height); bottom = Math.Clamp(bottom, 0, display.Height);
        if (right <= left || bottom <= top) return;
        for (int y = top; y < bottom; y++) Array.Fill(pixels, color, y * display.Width + left, right - left);
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        ReleaseCapture(); if (window != 0) DestroyWindow(window);
        if (dc != 0) SelectObject(dc, previous); if (bitmap != 0) DeleteObject(bitmap); if (dc != 0) DeleteDC(dc);
        UnregisterClass(className, GetModuleHandle(null));
    }
}
