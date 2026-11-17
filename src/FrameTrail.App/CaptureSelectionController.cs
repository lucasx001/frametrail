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
    private enum RegionDragKind { None, New, Move, Resize }
    private const int EdgeTolerance = 12;
    private readonly int mode;
    private readonly IReadOnlyList<CaptureSource> displays, windows;
    private readonly Action<CaptureSource, CaptureRegion?> confirmed;
    private readonly Action cancelled;
    private readonly List<SelectionShade> shades = new();
    private readonly SelectionActionWindow card;
    private CaptureSource? hovered, selected, dragDisplay;
    private CaptureRegion? region;
    private CaptureRegion anchorRegion;
    private RegionDragKind dragKind;
    private bool resizeLeft, resizeTop, resizeRight, resizeBottom;
    private int startX, startY;
    private bool dragging, disposed;
    private long lastDraw;
    internal CaptureRegion? SelectedRegion => region;
    internal FrameworkElement Surface => (FrameworkElement)card.Content;
    internal bool UsesCrosshair => shades.Count > 0 && shades.All(s => s.UsesCrosshair);

    internal CaptureSelectionController(Window owner, int mode, IReadOnlyList<CaptureSource> sources,
        Action<CaptureSource, CaptureRegion?> confirmed, Action cancelled)
    {
        this.mode = mode; this.confirmed = confirmed; this.cancelled = cancelled;
        displays = sources.Where(s => s.Kind == CaptureSourceKind.Monitor).ToArray();
        windows = sources.Where(s => s.Kind == CaptureSourceKind.Window && !SelectionShade.IsOwnWindow(s.Handle)).ToArray();
        if (displays.Count == 0) throw new InvalidOperationException(L.T("SF040584A9226"));
        if (mode == 1 && windows.Count == 0) throw new InvalidOperationException(L.T("S52EF37E77698"));
        card = new SelectionActionWindow(Action, Cancel, mode == 2, Apply16By9Preset, Apply1080pPreset, NudgeRegion);
        card.Closed += (_, _) => { if (!disposed) Cancel(); };
        try { foreach (var display in displays) shades.Add(new SelectionShade(display, mode == 2, Pointer, Cancel, Action, NudgeRegion)); }
        catch { Dispose(); throw; }
        hovered = displays[0];
    }
    internal void Show()
    {
        foreach (var shade in shades) shade.Show();
        card.Activate();
        if (SelectionShade.Cursor() is var point) hovered = mode == 1 ? WindowAt(point.X, point.Y) : DisplayAt(point.X, point.Y) ?? displays[0];
        UpdateCard(); Redraw(force: true);
    }
    private CaptureSource? DisplayAt(int x, int y) => displays.FirstOrDefault(s => x >= s.X && y >= s.Y && x < s.X + s.Width && y < s.Y + s.Height);
    private CaptureSource? WindowAt(int x, int y) => windows.FirstOrDefault(w => SelectionShade.Contains(w, x, y));
    internal void Pointer(uint message, int x, int y)
    {
        if (disposed) return;
        var display = DisplayAt(x, y); if (display is null) return;
        if (mode == 2)
        {
            if (message == 0x201)
            {
                if (!dragging && region is CaptureRegion current && dragDisplay is not null && display.Handle == dragDisplay.Handle)
                {
                    int rLeft = dragDisplay.X + current.X, rTop = dragDisplay.Y + current.Y;
                    int rRight = rLeft + current.Width, rBottom = rTop + current.Height;
                    bool nearLeft = Math.Abs(x - rLeft) <= EdgeTolerance && y >= rTop - EdgeTolerance && y <= rBottom + EdgeTolerance;
                    bool nearRight = Math.Abs(x - rRight) <= EdgeTolerance && y >= rTop - EdgeTolerance && y <= rBottom + EdgeTolerance;
                    bool nearTop = Math.Abs(y - rTop) <= EdgeTolerance && x >= rLeft - EdgeTolerance && x <= rRight + EdgeTolerance;
                    bool nearBottom = Math.Abs(y - rBottom) <= EdgeTolerance && x >= rLeft - EdgeTolerance && x <= rRight + EdgeTolerance;
                    if (nearLeft && nearRight) { if (Math.Abs(x - rLeft) <= Math.Abs(x - rRight)) nearRight = false; else nearLeft = false; }
                    if (nearTop && nearBottom) { if (Math.Abs(y - rTop) <= Math.Abs(y - rBottom)) nearBottom = false; else nearTop = false; }
                    if (nearLeft || nearRight || nearTop || nearBottom)
                    {
                        dragKind = RegionDragKind.Resize;
                        resizeLeft = nearLeft; resizeRight = nearRight; resizeTop = nearTop; resizeBottom = nearBottom;
                        anchorRegion = current; startX = x; startY = y; dragging = true; selected = null;
                    }
                    else if (x > rLeft && x < rRight && y > rTop && y < rBottom)
                    {
                        dragKind = RegionDragKind.Move;
                        anchorRegion = current; startX = x; startY = y; dragging = true; selected = null;
                    }
                    else
                    {
                        dragKind = RegionDragKind.New;
                        dragDisplay = display; selected = null; dragging = true; startX = x; startY = y; region = null;
                    }
                }
                else
                {
                    dragKind = RegionDragKind.New;
                    dragDisplay = display; selected = null; dragging = true; startX = x; startY = y; region = null;
                }
            }
            if (dragging && dragDisplay is not null)
            {
                var d = dragDisplay;
                if (dragKind == RegionDragKind.Move)
                {
                    int maxLeft = Math.Max(0, d.Width - anchorRegion.Width);
                    int maxTop = Math.Max(0, d.Height - anchorRegion.Height);
                    region = anchorRegion with
                    {
                        X = Math.Clamp(anchorRegion.X + (x - startX), 0, maxLeft),
                        Y = Math.Clamp(anchorRegion.Y + (y - startY), 0, maxTop)
                    };
                }
                else if (dragKind == RegionDragKind.Resize)
                {
                    int left = anchorRegion.X, top = anchorRegion.Y;
                    int right = anchorRegion.X + anchorRegion.Width, bottom = anchorRegion.Y + anchorRegion.Height;
                    int dx = x - startX, dy = y - startY;
                    if (resizeLeft) left = Math.Clamp(anchorRegion.X + dx, 0, right - 2);
                    if (resizeRight) right = Math.Clamp(anchorRegion.X + anchorRegion.Width + dx, left + 2, d.Width);
                    if (resizeTop) top = Math.Clamp(anchorRegion.Y + dy, 0, bottom - 2);
                    if (resizeBottom) bottom = Math.Clamp(anchorRegion.Y + anchorRegion.Height + dy, top + 2, d.Height);
                    int width = Math.Max(2, (right - left) & ~1);
                    int height = Math.Max(2, (bottom - top) & ~1);
                    if (resizeLeft && !resizeRight) left = right - width;
                    if (resizeTop && !resizeBottom) top = bottom - height;
                    left = Math.Clamp(left, 0, Math.Max(0, d.Width - width));
                    top = Math.Clamp(top, 0, Math.Max(0, d.Height - height));
                    region = new(left, top, width, height);
                }
                else
                {
                    x = Math.Clamp(x, d.X, d.X + d.Width); y = Math.Clamp(y, d.Y, d.Y + d.Height);
                    int left = Math.Min(x, startX) - d.X, top = Math.Min(y, startY) - d.Y;
                    int width = Math.Abs(x - startX) & ~1, height = Math.Abs(y - startY) & ~1;
                    region = width >= 2 && height >= 2 ? new(left, top, width, height) : null;
                }
                hovered = d;
            }
            if (message == 0x202) { dragging = false; dragKind = RegionDragKind.None; if (region is not null) selected = dragDisplay; }
        }
        else
        {
            hovered = mode == 0 ? display : WindowAt(x, y);
            if (message == 0x201) selected = hovered;
            if (message == 0x202)
            {
                if (selected is not null && selected.Handle == hovered?.Handle) { Action(); return; }
                selected = null;
            }
        }
        UpdateCard(); Redraw(message != 0x200);
    }
    private void UpdateCard()
    {
        var source = selected ?? hovered;
        var display = mode == 1 && source is not null ? DisplayAt(source.X, source.Y) ?? displays[0] : source ?? displays[0];
        if (mode == 2)
        {
            card.SetText(region is CaptureRegion r ? $"{r.Width} × {r.Height}" : L.T("S98CE7CDC67C8"),
                region is not null ? L.T("S9E142FABF4D7") : L.T("SB4CE719E445B"),
                L.T("S415D8F361894"), selected is not null && region is not null && selected.IsSdr, true);
        }
        else if (source is null)
            card.SetText(L.T("S96343D09DCB4"), L.T("S5F86210B104F"), L.T("S415D8F361894"), false, false);
        else
        {
            string title = mode == 0 ? L.T("S00081E19E6F4", displays.ToList().FindIndex(d => d.Handle == source.Handle) + 1) : source.Name;
            card.SetText(title, source.IsSdr ? L.T("S5DB4056F1023", source.Width, source.Height) : L.T("S71047DD9B33E"),
                L.T("S415D8F361894"), source.IsSdr, selected is not null);
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
    private CaptureSource ActiveRegionDisplay => dragDisplay ?? selected ?? hovered ?? displays[0];
    private void SetRegionCentered(CaptureSource d, int width, int height, int centerX, int centerY)
    {
        width = Math.Clamp(width & ~1, 2, d.Width & ~1);
        height = Math.Clamp(height & ~1, 2, d.Height & ~1);
        if (width < 2 || height < 2) return;
        int left = Math.Clamp(centerX - width / 2, 0, Math.Max(0, d.Width - width));
        int top = Math.Clamp(centerY - height / 2, 0, Math.Max(0, d.Height - height));
        dragDisplay = d; selected = d; hovered = d;
        region = new(left, top, width, height);
        UpdateCard(); Redraw(force: true);
    }
    private void Apply16By9Preset()
    {
        if (disposed || mode != 2) return;
        var d = ActiveRegionDisplay;
        int maxW = d.Width & ~1, maxH = d.Height & ~1;
        int targetW = region is CaptureRegion r ? Math.Max(64, r.Width & ~1) : (int)(maxW * 0.75) & ~1;
        targetW = Math.Clamp(targetW, 2, maxW);
        int targetH = ((int)Math.Round(targetW * 9.0 / 16.0)) & ~1;
        if (targetH > maxH) { targetH = maxH; targetW = ((int)Math.Round(targetH * 16.0 / 9.0)) & ~1; }
        int cx = region is CaptureRegion cur ? cur.X + cur.Width / 2 : d.Width / 2;
        int cy = region is CaptureRegion cur2 ? cur2.Y + cur2.Height / 2 : d.Height / 2;
        SetRegionCentered(d, targetW, Math.Max(2, targetH), cx, cy);
    }
    private void Apply1080pPreset()
    {
        if (disposed || mode != 2) return;
        var d = ActiveRegionDisplay;
        int maxW = d.Width & ~1, maxH = d.Height & ~1;
        int targetW = Math.Min(1920, maxW);
        int targetH = Math.Min(1080, maxH);
        if (targetW < 1920 || targetH < 1080)
        {
            targetH = ((int)Math.Round(targetW * 9.0 / 16.0)) & ~1;
            if (targetH > maxH) { targetH = maxH; targetW = ((int)Math.Round(targetH * 16.0 / 9.0)) & ~1; }
        }
        int cx = region is CaptureRegion cur ? cur.X + cur.Width / 2 : d.Width / 2;
        int cy = region is CaptureRegion cur2 ? cur2.Y + cur2.Height / 2 : d.Height / 2;
        SetRegionCentered(d, targetW, Math.Max(2, targetH), cx, cy);
    }
    private void NudgeRegion(int dx, int dy, bool resize)
    {
        if (disposed || mode != 2 || region is not CaptureRegion r) return;
        var d = ActiveRegionDisplay;
        if (resize)
        {
            int width = Math.Clamp((r.Width + dx) & ~1, 2, Math.Max(2, (d.Width - r.X) & ~1));
            int height = Math.Clamp((r.Height + dy) & ~1, 2, Math.Max(2, (d.Height - r.Y) & ~1));
            region = r with { Width = width, Height = height };
        }
        else
        {
            int left = Math.Clamp(r.X + dx, 0, Math.Max(0, d.Width - r.Width));
            int top = Math.Clamp(r.Y + dy, 0, Math.Max(0, d.Height - r.Height));
            region = r with { X = left, Y = top };
        }
        dragDisplay = d; selected = d; hovered = d;
        UpdateCard(); Redraw(force: true);
    }
    private void Action()
    {
        if (disposed) return;
        if (selected is null && mode != 2) selected = hovered;
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
    private readonly Button? preset16By9, preset1080p;
    private readonly Action cancel;
    private readonly Grid root;
    private nuint displayHandle;
    internal SelectionActionWindow(Action action, Action cancel, bool regionPresets, Action apply16By9, Action apply1080p, Action<int, int, bool> nudge)
    {
        this.cancel = cancel; Title = L.T("SD42ECADDD44C");
        title.FontFamily = (FontFamily)Application.Current.Resources["RecorderSemiboldFont"];
        var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 12, 0) };
        labels.Children.Add(title); labels.Children.Add(detail);
        var dismiss = new Button { Content = L.T("SFCB63229A829"), Style = (Style)Application.Current.Resources["RecorderPillButton"], Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        dismiss.Click += (_, _) => cancel();
        start = new Button { Content = L.T("S415D8F361894"), Style = (Style)Application.Current.Resources["RecorderPillButton"], MinWidth = 144, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
        start.Click += (_, _) => action();
        var row = new Grid { ColumnSpacing = 6, Padding = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        if (regionPresets)
        {
            var pillStyle = (Style)Application.Current.Resources["RecorderPillButton"];
            preset16By9 = new Button { Content = "16:9", Style = pillStyle, Height = 32, MinWidth = 46, Padding = new Thickness(10, 0, 10, 0), FontSize = 11 };
            preset16By9.Click += (_, _) => apply16By9();
            preset1080p = new Button { Content = "1080p", Style = pillStyle, Height = 32, MinWidth = 54, Padding = new Thickness(10, 0, 10, 0), FontSize = 11 };
            preset1080p.Click += (_, _) => apply1080p();
            var presets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            presets.Children.Add(preset16By9); presets.Children.Add(preset1080p);
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(144) });
            row.Children.Add(labels);
            Grid.SetColumn(presets, 1); row.Children.Add(presets);
            Grid.SetColumn(dismiss, 2); row.Children.Add(dismiss);
            Grid.SetColumn(start, 3); row.Children.Add(start);
        }
        else
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(144) });
            row.Children.Add(labels);
            Grid.SetColumn(dismiss, 1); row.Children.Add(dismiss);
            Grid.SetColumn(start, 2); row.Children.Add(start);
        }
        root = new Grid { CornerRadius = new CornerRadius(30) }; root.Children.Add(row); Content = root;
        SetTheme(); root.ActualThemeChanged += (_, _) => SetTheme();
        root.PreviewKeyDown += (_, args) =>
        {
            if (regionPresets && args.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
            {
                bool shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
                int dx = args.Key == VirtualKey.Left ? -2 : args.Key == VirtualKey.Right ? 2 : 0;
                int dy = args.Key == VirtualKey.Up ? -2 : args.Key == VirtualKey.Down ? 2 : 0;
                nudge(dx, dy, shift);
                args.Handled = true;
            }
        };
        root.KeyDown += (_, args) => { if (args.Key == VirtualKey.Escape) { cancel(); args.Handled = true; } else if (args.Key == VirtualKey.Enter && start.IsEnabled) { action(); args.Handled = true; } };
        RecorderWindowChrome.Configure(this, regionPresets ? 600 : 490, 60, 30);
        try { RecordingUtilities.ExcludeWindow((nuint)WinRT.Interop.WindowNative.GetWindowHandle(this), true); } catch { }
    }
    private void SetTheme()
    {
        var dark = root.ActualTheme == ElementTheme.Dark;
        root.Background = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(255, 16, 17, 19) : Windows.UI.Color.FromArgb(255, 253, 253, 254));
        var ink = new SolidColorBrush(dark ? Microsoft.UI.Colors.White : Windows.UI.Color.FromArgb(255, 26, 26, 26));
        title.Foreground = detail.Foreground = ink;
        if (preset16By9 is not null) preset16By9.Foreground = ink;
        if (preset1080p is not null) preset1080p.Foreground = ink;
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
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] private static extern nint GetClassLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern short GetKeyState(int virtualKey);

    private readonly CaptureSource display;
    private readonly Action<uint, int, int> pointer;
    private readonly Action cancel, start;
    private readonly Action<int, int, bool> nudge;
    private readonly WndProc procedure;
    private readonly string className = "FrameTrail.Selection." + Guid.NewGuid().ToString("N");
    private readonly nint window, dc, bitmap, bits, previous;
    private readonly int[] pixels;
    private bool disposed;
    internal bool UsesCrosshair => GetClassLongPtr(window, -12) == LoadCursor(0, 32515);
    internal static Point Cursor() { GetCursorPos(out var point); return point; }
    internal static bool IsOwnWindow(nuint handle) { GetWindowThreadProcessId((nint)handle, out var process); return process == Environment.ProcessId; }
    internal static RecorderWindowChrome.Rect Bounds(CaptureSource source)
    {
        if (source.Kind == CaptureSourceKind.Window && GetWindowRect((nint)source.Handle, out var rect)) return rect;
        return new() { Left = source.X, Top = source.Y, Right = source.X + source.Width, Bottom = source.Y + source.Height };
    }
    internal static bool Contains(CaptureSource source, int x, int y) { var r = Bounds(source); return x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom; }
    internal SelectionShade(CaptureSource display, bool regionSelection, Action<uint, int, int> pointer, Action cancel, Action start, Action<int, int, bool> nudge)
    {
        this.display = display; this.pointer = pointer; this.cancel = cancel; this.start = start; this.nudge = nudge; procedure = Procedure;
        if (display.Width <= 0 || display.Height <= 0 || (long)display.Width * display.Height > 40_000_000) throw new InvalidOperationException(L.T("S88BC5457B43F"));
        var wc = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = procedure, Instance = GetModuleHandle(null), ClassName = className, Cursor = LoadCursor(0, regionSelection ? 32515 : 32512) };
        if (RegisterClassEx(ref wc) == 0) throw new System.ComponentModel.Win32Exception();
        window = CreateWindowEx(0x00080088, className, L.T("S329410902635"), 0x80000000, display.X, display.Y, display.Width, display.Height, 0, 0, wc.Instance, 0);
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
        if (message == 0x100 && wparam is 37 or 38 or 39 or 40)
        {
            bool shift = (GetKeyState(0x10) & 0x8000) != 0;
            int dx = wparam == 37 ? -2 : wparam == 39 ? 2 : 0;
            int dy = wparam == 38 ? -2 : wparam == 40 ? 2 : 0;
            nudge(dx, dy, shift);
            return 0;
        }
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
