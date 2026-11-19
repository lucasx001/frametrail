using FrameTrail.Domain;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace FrameTrail.App;

/// <summary>
/// Interactive video-player scrubber inspired by OpenScreen's PlaybackControls progress bar.
/// </summary>
internal sealed class PreviewScrubber : Grid
{
    private readonly Border track = new() { Height = 4, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly Border hoverTrack = new() { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Opacity = 0, IsHitTestVisible = false };
    private readonly Border fill = new() { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly ScaleTransform thumbScale = new() { ScaleX = 1, ScaleY = 1 };
    private readonly TranslateTransform thumbTranslate = new();
    private readonly Border thumb;
    private readonly InputSystemCursor handCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    private long positionTicks;
    private long durationTicks;
    private bool dragging, hovered;

    internal event Action<long>? SeekRequested;
    internal event Action<long?>? ScrubHover;

    internal long PositionTicks => positionTicks;
    internal long DurationTicks => durationTicks;

    internal PreviewScrubber()
    {
        Height = 28;
        MinWidth = 110;
        Background = new SolidColorBrush(Colors.Transparent);
        VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(this, "editor.preview.scrubber");
        AutomationProperties.SetName(this, AppText.Language == "en" ? "Playback progress" : "播放进度");

        var thumbGroup = new TransformGroup();
        thumbGroup.Children.Add(thumbScale);
        thumbGroup.Children.Add(thumbTranslate);

        thumb = new Border
        {
            Width = 12,
            Height = 12,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Colors.White),
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = thumbGroup,
            Translation = new System.Numerics.Vector3(0, 0, 4),
            Shadow = new ThemeShadow(),
            IsHitTestVisible = false
        };

        Children.Add(track);
        Children.Add(hoverTrack);
        Children.Add(fill);
        Children.Add(thumb);

        SizeChanged += (_, _) => Draw();
        ActualThemeChanged += (_, _) => Draw();
        PointerEntered += (_, _) => { hovered = true; ProtectedCursor = handCursor; UpdateVisualState(); };
        PointerExited += (_, _) => { hovered = false; if (!dragging) { hoverTrack.Opacity = 0; ScrubHover?.Invoke(null); } UpdateVisualState(); };
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => { dragging = false; UpdateVisualState(); };
    }

    internal void SetProgress(long position, long duration)
    {
        durationTicks = Math.Max(0, duration);
        if (!dragging) positionTicks = Math.Clamp(position, 0, durationTicks);
        Draw();
    }

    private double TrackWidth => Math.Max(0, ActualWidth - 12);

    private long TicksAt(double x)
    {
        if (durationTicks <= 0 || TrackWidth <= 0) return 0;
        double fraction = Math.Clamp((x - 6) / TrackWidth, 0, 1);
        return (long)Math.Round(fraction * Math.Max(0, durationTicks - 1));
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || durationTicks <= 0) return;
        dragging = true;
        CapturePointer(e.Pointer);
        UpdateVisualState();
        long ticks = TicksAt(point.Position.X);
        positionTicks = ticks;
        Draw();
        SeekRequested?.Invoke(ticks);
        ScrubHover?.Invoke(ticks);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (durationTicks <= 0) return;
        var point = e.GetCurrentPoint(this);
        double x = point.Position.X;
        long ticks = TicksAt(x);
        if (dragging && point.Properties.IsLeftButtonPressed)
        {
            positionTicks = ticks;
            Draw();
            SeekRequested?.Invoke(ticks);
            ScrubHover?.Invoke(ticks);
            e.Handled = true;
            return;
        }
        double hoverW = Math.Clamp(x - 6, 0, TrackWidth) + 6;
        hoverTrack.Width = hoverW;
        hoverTrack.Opacity = 0.2;
        ScrubHover?.Invoke(ticks);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        ReleasePointerCapture(e.Pointer);
        UpdateVisualState();
        var point = e.GetCurrentPoint(this);
        if (hovered && point.Position.X >= 0 && point.Position.X <= ActualWidth)
        {
            long ticks = TicksAt(point.Position.X);
            ScrubHover?.Invoke(ticks);
        }
        else
        {
            hoverTrack.Opacity = 0;
            ScrubHover?.Invoke(null);
        }
        e.Handled = true;
    }

    internal void SimulateHover(long? ticks)
    {
        hovered = ticks is not null;
        UpdateVisualState();
        if (ticks is long target)
        {
            long clamped = durationTicks > 0 ? Math.Clamp(target, 0, Math.Max(0, durationTicks - 1)) : 0;
            double fraction = durationTicks > 0 ? Math.Clamp((double)clamped / Math.Max(1, durationTicks - 1), 0, 1) : 0;
            hoverTrack.Width = fraction * TrackWidth + 6;
            hoverTrack.Opacity = 0.2;
            ScrubHover?.Invoke(clamped);
        }
        else
        {
            hoverTrack.Opacity = 0;
            ScrubHover?.Invoke(null);
        }
    }

    internal void SimulateSeek(long ticks)
    {
        if (durationTicks <= 0) return;
        long clamped = Math.Clamp(ticks, 0, Math.Max(0, durationTicks - 1));
        hovered = true;
        UpdateVisualState();
        positionTicks = clamped;
        Draw();
        SeekRequested?.Invoke(clamped);
        ScrubHover?.Invoke(clamped);
    }

    private void UpdateVisualState()
    {
        double s = dragging || hovered ? 1.2 : 1.0;
        thumbScale.ScaleX = s;
        thumbScale.ScaleY = s;
    }

    private void Draw()
    {
        double w = TrackWidth;
        var accent = RecorderTheme.Brush(this, "EditorAccent");
        track.Background = RecorderTheme.Brush(this, "EditorControlTrack");
        hoverTrack.Background = RecorderTheme.Brush(this, "EditorInk");
        fill.Background = accent;
        thumb.Background = RecorderTheme.Brush(this, "EditorSliderThumb");
        thumb.BorderBrush = accent;
        if (w <= 0) return;
        double fraction = durationTicks > 0 ? Math.Clamp((double)positionTicks / Math.Max(1, durationTicks - 1), 0, 1) : 0;
        double x = fraction * w;
        fill.Width = x + 6;
        thumbTranslate.X = x;
    }
}
