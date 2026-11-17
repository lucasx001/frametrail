using System.Runtime.InteropServices;
using FrameTrail.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;

namespace FrameTrail.App;

/// <summary>
/// Floating center-screen countdown overlay inspired by OpenScreen's CountdownOverlay (160x160 circle, 80px bold tabular numeral).
/// </summary>
internal sealed class CountdownOverlayWindow : Window
{
    private const int OverlayDiameter = 160;
    private readonly Border circle;
    private readonly TextBlock mainNumber;
    private readonly nint hwnd;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    internal FrameworkElement Surface => circle;
    internal string CurrentText => mainNumber.Text;

    internal CountdownOverlayWindow(bool excludeFromCapture, Action onCancel, CaptureSource? targetSource = null)
    {
        Title = "FrameTrail Countdown";
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        mainNumber = new TextBlock
        {
            Text = "3",
            FontFamily = new FontFamily("Segoe UI Variable Display"),
            FontSize = 80,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, -4, 0, 0),
            IsHitTestVisible = false
        };

        circle = new Border
        {
            Width = OverlayDiameter,
            Height = OverlayDiameter,
            CornerRadius = new CornerRadius(OverlayDiameter / 2.0),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0x10, 0x11, 0x13)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = mainNumber
        };
        AutomationProperties.SetAutomationId(circle, "recorder.countdown.overlay");
        AutomationProperties.SetName(circle, "Recording Countdown");

        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, args) =>
        {
            args.Handled = true;
            onCancel();
        };
        circle.KeyboardAccelerators.Add(escape);
        circle.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Escape)
            {
                args.Handled = true;
                onCancel();
            }
        };

        Content = circle;
        RecorderWindowChrome.Configure(this, OverlayDiameter, OverlayDiameter, OverlayDiameter / 2.0, center: true);

        // Make the overlay click-through (WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW) so it never blocks the user's cursor
        long exStyle = GetWindowLongPtr(hwnd, -20).ToInt64();
        SetWindowLongPtr(hwnd, -20, (nint)(exStyle | 0x00000020L | 0x00000080L));

        if (excludeFromCapture)
        {
            try { RecordingUtilities.ExcludeWindow((nuint)hwnd, true); }
            catch { }
        }

        if (targetSource is { Width: > 0, Height: > 0 })
        {
            var size = AppWindow.Size;
            int centerX = targetSource.X + (targetSource.Width - size.Width) / 2;
            int centerY = targetSource.Y + (targetSource.Height - size.Height) / 2;
            AppWindow.Move(new PointInt32(centerX, centerY));
        }
    }

    internal void SetSeconds(int seconds)
    {
        string text = seconds.ToString();
        mainNumber.Text = text;
        AutomationProperties.SetName(circle, $"Recording Countdown {text}");
    }
}
