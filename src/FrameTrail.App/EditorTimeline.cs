using System.Runtime.InteropServices.WindowsRuntime;
using FrameTrail.Interop;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.System;

namespace FrameTrail.App;

// Only visible clips, ruler marks and waveform buckets become XAML elements.
internal sealed partial class EditorTimeline : ContentControl
{
    private static readonly string[] CanonicalEffectOrder = ["zoom", "tilt", "intro", "mask", "focus", "sticker"];
    private readonly ScrollViewer scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Enabled };
    private readonly Grid scrollSurface = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Canvas canvas = new() { Height = 148, Background = new SolidColorBrush(Colors.Transparent), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly Canvas rulerLayer = new() { IsHitTestVisible = false };
    private readonly Canvas clipsLayer = new() { IsHitTestVisible = false };
    private readonly Canvas overlayLayer = new() { IsHitTestVisible = false };
    private readonly InputSystemCursor arrowCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow), moveCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll), resizeHorizontalCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    private ResolvedTimeline? timeline;
    private string? selected;
    private long playhead;
    private double scale = 80, dragScale = 80;
    private bool dragging, dragPending;
    private Point pressPoint;
    private long pressTicks, dragInitialDuration;
    private string? dragKind;
    private ResolvedClip? dragClip;
    private ResolvedClip[]? dragInitialClips;
    private ResolvedEffect? dragEffect;
    private long effectGrabOffset;
    private string? selectedVisual;
    private readonly Dictionary<long, BitmapImage> thumbnails = new();
    private readonly LinkedList<long> thumbnailLru = new();
    private readonly Dictionary<long, LinkedListNode<long>> thumbnailNodes = new();
    private int thumbnailEpoch;
    private float[]? waveform;
    private Dictionary<string, int> clipIndices = new();
    private ResolvedMarker[] markers = Array.Empty<ResolvedMarker>();
    private TimelineIntervalIndex<ResolvedEffect> effects = new([],e=>e.OutputStart,e=>e.OutputStart+e.OutputDuration);
    private Dictionary<string,int> effectOrdinals = new();
    private readonly Dictionary<string, int> effectRows = new();
    private static Dictionary<string, int>? lastEffectRows;
    private readonly Dictionary<string, ClipVisual> clipViews = new();
    private readonly Dictionary<string, Border> effectViews = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer hoverTimer;
    private long? hoverTicks, pendingHover;
    private bool hoverNotified;
    private bool drawQueued;
    internal int DrawCount { get; private set; }
    internal event Action<long?>? Hover;
    private readonly Rectangle playLine = new() { Width = 1, Height = 134, Fill = Brush(255, 69, 59), IsHitTestVisible = false };
    private readonly Border playHandle = new() { Width = 8, Height = 15, CornerRadius = new CornerRadius(4), Background = Brush(255, 69, 59), IsHitTestVisible = false };
    private readonly Line hoverLine = new() { Width = 2, X1 = 1, Y1 = 0, X2 = 1, Y2 = 126, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly TextBlock hoverLabel = new() { FontFamily = new FontFamily("Consolas"), FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, IsHitTestVisible = false };
    private readonly Border hoverBadge = new() { CornerRadius = new CornerRadius(5), Padding = new Thickness(5, 1, 5, 1), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    internal long? HoverTicks => hoverTicks;
    internal bool IsMoveCursorActive => ProtectedCursor == moveCursor;
    internal bool IsHoverIndicatorVisible => hoverLine.Visibility == Visibility.Visible && hoverBadge.Visibility == Visibility.Visible;
    internal event Action<long>? Seek;
    internal event Action<string>? SelectClip;
    internal event Action<string>? ClipDoubleClicked;
    internal event Action<string>? SelectEffect;
    internal event Action<double>? ZoomChanged;
    internal event Action? BeginEdit;
    internal event Action<object>? PreviewEdit;
    internal event Action<bool>? EndEdit;
    internal event Action<IReadOnlyList<long>>? ThumbnailsNeeded;
    internal double PixelsPerSecond { get => scale; set { double next = Math.Clamp(value, 0.05, 1000); if (Math.Abs(scale - next) < 0.00001) return; scale = next; Draw(); } }
    internal bool EditingEnabled { get; set; } = true;
    internal bool ThumbnailsEnabled { get; set; } = true;
    internal bool SnappingEnabled { get; set; } = true;
    private bool waveformMode;
    internal bool WaveformMode { get => waveformMode; set { waveformMode = value; Draw(); } }
    internal long FrameTicks { get; set; } = 333_333;
    internal void RefreshAppearance() => Draw();
    internal void SelectVisual(string? id) { selectedVisual = id; DescribeSelection(); Draw(); }
    internal int RenderedElementCount => rulerLayer.Children.Count + clipsLayer.Children.Count + overlayLayer.Children.Count;
    internal EditorTimeline()
    {
        Content = scroll; HorizontalContentAlignment=HorizontalAlignment.Stretch;VerticalContentAlignment=VerticalAlignment.Stretch;IsTabStop=true; UseSystemFocusVisuals=true;
        canvas.Children.Add(rulerLayer);
        canvas.Children.Add(clipsLayer);
        canvas.Children.Add(overlayLayer);
        scrollSurface.Children.Add(canvas); scroll.Content = scrollSurface;
        Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(hoverLabel, FontNumeralAlignment.Tabular);
        hoverBadge.Child = hoverLabel;
        scroll.ViewChanged += (_, _) => Draw(); SizeChanged += (_, _) => Draw();
        hoverTimer = DispatcherQueue.CreateTimer(); hoverTimer.Interval = TimeSpan.FromMilliseconds(50); hoverTimer.IsRepeating = false;
        hoverTimer.Tick += (_, _) => { if (pendingHover is long t) { hoverNotified = true; Hover?.Invoke(t); } };
        canvas.PointerExited += (_, _) => { HideHover(); ProtectedCursor = arrowCursor; };
        canvas.PointerPressed += Pressed; canvas.PointerMoved += Moved; canvas.PointerReleased += Released;
        canvas.DoubleTapped += OnCanvasDoubleTapped;
        scrollSurface.PointerWheelChanged += WheelChanged;
        canvas.PointerCaptureLost += (_, _) => { ClearDragState(); if (dragging) Finish(false); ProtectedCursor = arrowCursor; };
        ActualThemeChanged += (_, _) => Draw();
    }
    internal void Set(ResolvedTimeline value, string? selection, long ticks)
    {
        timeline = value; selected = selection; playhead = ticks;
        clipIndices = value.Clips.Select((c, i) => (c.Id, i)).ToDictionary(k => k.Id, k => k.i);
        markers = value.Markers.OrderBy(m => m.Ticks).ToArray();
        effects = new(value.Effects, e => e.OutputStart, e => e.OutputStart + e.OutputDuration);
        effectOrdinals = value.Effects.Select((e, i) => (Key: e.Id + ":" + e.OutputStart, Index: i)).ToDictionary(v => v.Key, v => v.Index);
        var presentKinds = value.Effects.Select(e => e.Kind).ToHashSet();
        var orderedKinds = CanonicalEffectOrder.Where(presentKinds.Contains)
            .Concat(presentKinds.Where(k => !CanonicalEffectOrder.Contains(k)).OrderBy(k => k))
            .ToList();
        effectRows.Clear();
        for (int i = 0; i < orderedKinds.Count; i++) effectRows[orderedKinds[i]] = 148 + i * 24;
        lastEffectRows = new Dictionary<string, int>(effectRows);
        canvas.Height = value.Effects.Count > 0 ? 148 + orderedKinds.Count * 24 + 6 : 148;
        DescribeSelection();
        if (dragging) DrawNow(); else Draw();
    }
    internal void Position(long ticks) { playhead = ticks; Canvas.SetLeft(playLine, X(playhead)); Canvas.SetLeft(playHandle, X(playhead) - 3.5); }
    internal void SetWaveform(float[]? values) { waveform = values; Draw(); }
    private readonly HashSet<long> staleThumbnails = new(), loadingThumbnails = new();
    internal void ClearAssets() { waveform = null; thumbnailEpoch++; thumbnails.Clear(); thumbnailLru.Clear(); thumbnailNodes.Clear(); staleThumbnails.Clear(); loadingThumbnails.Clear(); Draw(); }
    internal void ClearThumbnails() { thumbnailEpoch++; thumbnails.Clear(); thumbnailLru.Clear(); thumbnailNodes.Clear(); staleThumbnails.Clear(); loadingThumbnails.Clear(); Draw(); }
    internal void InvalidateThumbnails() { staleThumbnails.UnionWith(thumbnails.Keys); loadingThumbnails.Clear(); Draw(); }
    internal async void AddThumbnail(long ticks, string path)
    {
        if ((thumbnails.ContainsKey(ticks) && !staleThumbnails.Contains(ticks)) || !loadingThumbnails.Add(ticks)) return;
        int epoch = thumbnailEpoch;
        try {
            byte[] bytes = await File.ReadAllBytesAsync(path);
            if (epoch != thumbnailEpoch || timeline is null) return;
            var bitmap = new BitmapImage { DecodePixelWidth = 384 };
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            if (epoch != thumbnailEpoch || timeline is null) return;
            StoreThumbnail(ticks, bitmap);
            staleThumbnails.Remove(ticks);
            Draw();
        } catch { }
        finally { loadingThumbnails.Remove(ticks); }
    }
    private void StoreThumbnail(long ticks, BitmapImage bitmap)
    {
        if (thumbnailNodes.TryGetValue(ticks, out var existingNode)) {
            thumbnailLru.Remove(existingNode);
            thumbnailNodes[ticks] = thumbnailLru.AddLast(ticks);
        } else {
            while (thumbnails.Count >= 600 && thumbnailLru.First is { } oldest) {
                thumbnailLru.RemoveFirst();
                thumbnailNodes.Remove(oldest.Value);
                thumbnails.Remove(oldest.Value);
                staleThumbnails.Remove(oldest.Value);
            }
            thumbnailNodes[ticks] = thumbnailLru.AddLast(ticks);
        }
        thumbnails[ticks] = bitmap;
    }
    private void TouchThumbnail(long ticks)
    {
        if (!thumbnailNodes.TryGetValue(ticks, out var node) || ReferenceEquals(node, thumbnailLru.Last)) return;
        thumbnailLru.Remove(node);
        thumbnailNodes[ticks] = thumbnailLru.AddLast(ticks);
    }
    private void ClearDragState() { dragPending = false; dragClip = null; dragInitialClips = null; dragEffect = null; }
    internal void CancelDrag() { ClearDragState(); if (dragging) { Finish(false); canvas.ReleasePointerCaptures(); } ProtectedCursor = arrowCursor; }
    internal void HideHover() => HideHover(notify: true);
    internal void HideHoverIndicator() => HideHover(notify: false);
    private void HideHover(bool notify)
    {
        hoverTimer.Stop(); pendingHover = null; hoverTicks = null;
        hoverLine.Visibility = Visibility.Collapsed; hoverBadge.Visibility = Visibility.Collapsed;
        if (notify && hoverNotified) { hoverNotified = false; Hover?.Invoke(null); }
        else hoverNotified = false;
    }
    internal void ShowHoverIndicator(long ticks)
    {
        if (timeline is null || timeline.Duration <= 0) { HideHover(); return; }
        hoverTicks = Math.Clamp(ticks, 0, timeline.Duration);
        if (!overlayLayer.Children.Contains(hoverLine)) overlayLayer.Children.Add(hoverLine);
        if (!overlayLayer.Children.Contains(hoverBadge)) overlayLayer.Children.Add(hoverBadge);
        PositionHoverIndicator();
    }
    private void PositionHoverIndicator()
    {
        if (hoverTicks is not long ticks || timeline is null) { HideHover(); return; }
        double hx = X(ticks);
        var ink = RecorderTheme.Brush(this, "EditorInk");
        var panel = RecorderTheme.Brush(this, "EditorPanel");
        hoverLine.Stroke = ink;
        hoverLine.Opacity = 0.8;
        hoverLine.Y2 = Math.Max(0, canvas.Height - 22);
        Canvas.SetLeft(hoverLine, hx - 1);
        Canvas.SetTop(hoverLine, 20);
        hoverLine.Visibility = Visibility.Visible;

        hoverLabel.Text = FormatHoverTime(ticks);
        hoverLabel.Foreground = panel;
        hoverBadge.Background = ink;
        hoverBadge.Visibility = Visibility.Visible;
        hoverBadge.Measure(new Size(240, 40));
        double badgeW = hoverBadge.DesiredSize.Width > 0 ? hoverBadge.DesiredSize.Width : hoverBadge.ActualWidth;
        double viewportW = ActualWidth > 0 ? ActualWidth : canvas.Width;
        double minLeft = scroll.HorizontalOffset + 4;
        double maxLeft = Math.Max(minLeft, scroll.HorizontalOffset + viewportW - badgeW - 8);
        Canvas.SetLeft(hoverBadge, Math.Clamp(hx - badgeW / 2, minLeft, maxLeft));
        Canvas.SetTop(hoverBadge, 1);
    }
    private static string FormatHoverTime(long ticks) => TimeSpan.FromTicks(ticks).ToString(ticks >= TimeSpan.TicksPerHour ? @"h\:mm\:ss\.ff" : @"mm\:ss\.ff");
    internal double HorizontalScrollOffset => scroll.HorizontalOffset;
    internal double ViewportWidth => scroll.ViewportWidth > 0 ? scroll.ViewportWidth : ActualWidth;
    internal double PlayheadX => X(playhead);
    internal void Reveal(long ticks) => scroll.ChangeView(Math.Max(0, X(ticks) - ViewportWidth / 2), null, null, true);
    internal void RevealIfOutside(long ticks)
    {
        double x = X(ticks), left = scroll.HorizontalOffset, width = ViewportWidth;
        if (width <= 0) return;
        if (x < left + 16 || x > left + width - 16) Reveal(ticks);
    }
    internal void FollowPlayhead(long ticks)
    {
        double width = ViewportWidth;
        if (width <= 0) return;
        double x = X(ticks), left = scroll.HorizontalOffset;
        if (x >= left + 8 && x <= left + width - 24) return;
        double maxOffset = Math.Max(0, scroll.ScrollableWidth > 0 ? scroll.ScrollableWidth : canvas.Width - width);
        double target = Math.Clamp(x - width * 0.15, 0, maxOffset);
        scroll.ChangeView(target, null, null, true);
    }
    internal bool FocusTimeline(FocusState state)=>Focus(state);
    internal bool HasFocus {
        get {for(var current=XamlRoot is null?null:FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;current is not null;current=VisualTreeHelper.GetParent(current))if(current==this)return true;return false;}
    }
    private void DescribeSelection()
    {
        var effect=timeline?.Effects.FirstOrDefault(e=>e.Id==selectedVisual);
        string value=effect is not null ? $"{EffectLabel(effect.Kind)} · {TimeSpan.FromTicks(effect.OutputStart):g}" : selected is not null&&clipIndices.TryGetValue(selected,out var index)?L.T("SB0B17D80E7A4", index+1, timeline!.Clips.Count):L.T("SF1241A97B082");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this,value);
        if(Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(this) is { } peer) peer.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }
    private double X(long ticks) => 16 + ticks / 10_000_000.0 * EffectiveScale;
    private static string EffectLabel(string kind)=>kind switch{"zoom"=>L.T("SB762AEC769A3"),"tilt"=>L.T("S17E14283A497"),"mask"=>L.T("SAC7CE836857B"),"focus"=>L.T("SED19BD5E2620"),"intro"=>L.T("SFD0D4B7258C2"),_=>L.T("S622DA0460277")};
    private double EffectiveScale => timeline is null ? scale : Math.Min(scale, 8_000_000.0 / Math.Max(1, timeline.Duration / 10_000_000.0));
    private long T(double x) => timeline is null ? 0 : Math.Clamp((long)Math.Round((x - 16) / EffectiveScale * 10_000_000), 0, timeline.Duration);
    private long RawTicks(double x, double? scaleOverride = null) => timeline is null ? 0 : (long)Math.Round((x - 16) / (scaleOverride ?? EffectiveScale) * 10_000_000);
    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(255, r, g, b));
    private static void At(Canvas target, UIElement element, double x, double y) { Canvas.SetLeft(element, x); Canvas.SetTop(element, y); target.Children.Add(element); }
    private void Draw()
    {
        if(drawQueued)return;drawQueued=true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,()=>{drawQueued=false;DrawNow();});
    }
    private void DrawNow()
    {
        DrawCount++;
        var requested = new List<long>();
        var prefetch = new List<long>();
        var seenRequested = new HashSet<long>();
        var usedClips = new HashSet<string>();
        var usedEffects = new HashSet<string>();
        rulerLayer.Children.Clear();
        overlayLayer.Children.Clear();
        if (timeline is null) { clipsLayer.Children.Clear(); clipViews.Clear(); return; }
        long spanTicks = dragging ? Math.Max(timeline.Duration, dragInitialDuration) : timeline.Duration;
        canvas.Width = Math.Max(ActualWidth - 4, X(spanTicks) + 32);
        rulerLayer.Width = clipsLayer.Width = overlayLayer.Width = canvas.Width;
        rulerLayer.Height = clipsLayer.Height = overlayLayer.Height = canvas.Height;
        double left = scroll.HorizontalOffset, right = left + Math.Max(320, ActualWidth);
        double step = 1; while (step * EffectiveScale < 72) step *= 2; if (step * EffectiveScale > 180) step /= 2;
        double first = Math.Floor(Math.Max(0, (left - 16) / EffectiveScale) / step) * step;
        for (double s = first; 16 + s * EffectiveScale < right; s += step) {
            double x = 16 + s * EffectiveScale;
            At(rulerLayer, new Rectangle { Width = 1, Height = 8, Fill = RecorderTheme.Brush(this, "EditorLine"), IsHitTestVisible = false }, x, 20);
            At(rulerLayer, new TextBlock { Text = TimeSpan.FromSeconds(s).ToString(s >= 3600 ? @"h\:mm\:ss" : step < 1 ? @"m\:ss\.f" : @"m\:ss"), FontSize = 10, Foreground = RecorderTheme.Brush(this, "EditorTertiary"), IsHitTestVisible = false }, x + 4, 6);
            for (int i = 1; i < 4; i++) At(rulerLayer, new Rectangle { Width = 0.5, Height = 5, Fill = RecorderTheme.Brush(this, "EditorLine"), IsHitTestVisible = false }, x + step * EffectiveScale * i / 4, 23);
        }
        double lastDenseClip = double.NegativeInfinity;
        var clipBg = RecorderTheme.Brush(this, "EditorClip");
        var selectedBorder = Brush(90, 174, 255);
        var idleBorder = new SolidColorBrush(Windows.UI.Color.FromArgb(75, 90, 174, 255));
        foreach (var clip in VisibleClips(left, right)) {
            double start = X(clip.OutputStart), end = X(clip.OutputStart + clip.OutputDuration); double x = Math.Max(start, left), w = Math.Max(1, Math.Min(end, right) - x - 2);
            if (w < 3 && clip.Id != selected) { if (x < lastDenseClip + 3) continue; lastDenseClip = x; }
            usedClips.Add(clip.Id);
            if (!clipViews.TryGetValue(clip.Id, out var visual)) {
                visual = new ClipVisual();
                clipViews[clip.Id] = visual;
                clipsLayer.Children.Add(visual.Root);
            } else if (visual.Root.Parent is null) {
                clipsLayer.Children.Add(visual.Root);
            }
            visual.Root.Width = w;
            visual.Root.BorderThickness = new Thickness(clip.Id == selected ? 2 : 1);
            visual.Root.BorderBrush = clip.Id == selected ? selectedBorder : idleBorder;
            visual.Body.Width = w;
            visual.Body.Background = clipBg;
            Canvas.SetLeft(visual.Root, x);
            Canvas.SetTop(visual.Root, 32);

            if (ThumbnailsEnabled && !waveformMode && w >= 6) {
                visual.Strip.Visibility = Visibility.Visible;
                visual.Strip.Width = w;
                var tiles = FilmstripSampling.Visible(clip.OutputStart, clip.OutputDuration, EffectiveScale, left, right);
                int imgIndex = 0;
                for (int ti = 0; ti < tiles.Count; ti++) {
                    var tile = tiles[ti];
                    bool exact = thumbnails.TryGetValue(tile.Ticks, out var picture);
                    if (exact) TouchThumbnail(tile.Ticks);
                    else {
                        picture = thumbnails.Where(t => t.Key >= clip.OutputStart && t.Key < clip.OutputStart + clip.OutputDuration)
                            .DefaultIfEmpty().MinBy(t => Math.Abs(t.Key - tile.Ticks)).Value;
                    }
                    if ((!exact || staleThumbnails.Contains(tile.Ticks)) && !loadingThumbnails.Contains(tile.Ticks) && seenRequested.Add(tile.Ticks) && requested.Count < 128)
                        requested.Add(tile.Ticks);
                    if (picture is not null) {
                        Image image;
                        if (imgIndex < visual.Images.Count) {
                            image = visual.Images[imgIndex];
                        } else {
                            image = new Image { Height = 46, Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
                            visual.Images.Add(image);
                            visual.Strip.Children.Add(image);
                        }
                        imgIndex++;
                        image.Visibility = Visibility.Visible;
                        if (Math.Abs(image.Width - tile.Width) > 0.01) image.Width = tile.Width;
                        if (Math.Abs(Canvas.GetLeft(image) - tile.Left) > 0.01) Canvas.SetLeft(image, tile.Left);
                        if (!ReferenceEquals(image.Source, picture)) image.Source = picture;
                    }
                }
                for (int hi = imgIndex; hi < visual.Images.Count; hi++)
                    visual.Images[hi].Visibility = Visibility.Collapsed;

                if (EffectiveScale >= 200) {
                    foreach (long frameTicks in FilmstripSampling.PrefetchFrames(clip.OutputStart, clip.OutputDuration, EffectiveScale, left, right, FrameTicks)) {
                        if (requested.Count + prefetch.Count >= 240) break;
                        bool has = thumbnails.ContainsKey(frameTicks) && !staleThumbnails.Contains(frameTicks);
                        if (!has && !loadingThumbnails.Contains(frameTicks) && seenRequested.Add(frameTicks)) prefetch.Add(frameTicks);
                    }
                }
            } else {
                visual.Strip.Visibility = Visibility.Collapsed;
            }

            if (w > 26) {
                string title = waveformMode ? L.T("S9D4973468893", clipIndices[clip.Id] + 1) : $"{clip.OutputDuration / 10_000_000.0:0.#}s";
                if (clip.Numerator != clip.Denominator) title += $" · {(double)clip.Numerator / clip.Denominator:0.##}×";
                visual.BadgeText.Text = title;
                visual.Badge.Visibility = Visibility.Visible;
            } else {
                visual.Badge.Visibility = Visibility.Collapsed;
            }

            if (clip.Id == selected) {
                if (start >= left) At(overlayLayer, new Rectangle { Width = 3, Height = 16, RadiusX = 1.5, RadiusY = 1.5, Fill = selectedBorder, IsHitTestVisible = false }, start + 3, 56);
                if (end <= right) At(overlayLayer, new Rectangle { Width = 3, Height = 16, RadiusX = 1.5, RadiusY = 1.5, Fill = selectedBorder, IsHitTestVisible = false }, end - 8, 56);
            }
        }
        At(overlayLayer, new Rectangle { Width = Math.Max(1, right - left), Height = 0.5, Fill = RecorderTheme.Brush(this, "EditorLine"), IsHitTestVisible = false }, left, 101);
        if (waveform is null || waveform.Length == 0) At(overlayLayer, new TextBlock { Text = waveform is null ? L.T("S0B2C11200068") : L.T("S923FBEB23043"), FontSize = 10, Foreground = RecorderTheme.Brush(this, "EditorTertiary"), IsHitTestVisible = false }, left + 16, 111);
        if (waveform is not null && waveform.Length > 0) {
            var line = new Polyline { Stroke = Brush(0, 143, 104), StrokeThickness = 1.5, IsHitTestVisible = false };
            for (double x = Math.Max(16, left); x < Math.Min(right, X(timeline.Duration)); x += 3) {
                int index = Math.Clamp((int)((x - 16) / Math.Max(1, X(timeline.Duration) - 16) * waveform.Length), 0, waveform.Length - 1); double amp = Math.Clamp(waveform[index], 0, 1) * 17;
                line.Points.Add(new Point(x, 122 - amp * 0.65)); line.Points.Add(new Point(x, 122 + amp * 0.65));
            }
            overlayLayer.Children.Add(line);
            if (waveformMode) {
                var primary = new Polyline { Stroke = Brush(0, 143, 104), StrokeThickness = 1.5, IsHitTestVisible = false };
                foreach (var point in line.Points) primary.Points.Add(new Point(point.X, 65 + (point.Y - 122) * 1.6)); overlayLayer.Children.Add(primary);
            }
        }
        int ml = 0, mh = markers.Length; while (ml < mh) { int m = (ml + mh) / 2; if (X(markers[m].Ticks) < left) ml = m + 1; else mh = m; }
        double lastMarker = double.NegativeInfinity;
        for (int i = ml; i < markers.Length && X(markers[i].Ticks) <= right; ++i) { double x = X(markers[i].Ticks); if (x - lastMarker < 4) continue; lastMarker = x; At(overlayLayer, new TextBlock { Text = "◆", FontSize = 11, Foreground = Brush(235, 185, 95), IsHitTestVisible = false }, x - 5, 22); }
        var lastEffectX=new Dictionary<string,double>();
        foreach (var effect in effects.Query(T(left),T(right))) {
            double start = X(effect.OutputStart), end = X(effect.OutputStart + effect.OutputDuration), x = Math.Max(left, start), width = Math.Max(2, Math.Min(right, end) - x - 2);
            if(effect.Id!=selectedVisual&&lastEffectX.TryGetValue(effect.Kind,out var last)&&x-last<2)continue;
            lastEffectX[effect.Kind]=x;
            // A source effect can appear in multiple edited intervals.
            string viewKey=effect.Id+":"+effect.OutputStart;
            if(!effectViews.TryGetValue(viewKey,out var body))effectViews[viewKey]=body=new Border{Height=20,CornerRadius=new CornerRadius(6),IsHitTestVisible=false};
            var (r, g, b) = EffectColor(effect.Kind);
            usedEffects.Add(viewKey);body.Width=width;body.BorderBrush=Brush(r,g,b);body.Background=new SolidColorBrush(Windows.UI.Color.FromArgb(effect.Id==selectedVisual?(byte)190:(byte)65,r,g,b));body.BorderThickness=new Thickness(effect.Id==selectedVisual?2:1);body.Child=null;
            if(width > 38) body.Child = new TextBlock { Text = effect.Kind switch { "zoom" => L.T("SB762AEC769A3"), "tilt" => L.T("S17E14283A497"), "mask" => L.T("SAC7CE836857B"), "focus" => L.T("SED19BD5E2620"), "intro" => L.T("SFD0D4B7258C2"), _ => L.T("S622DA0460277") }, FontSize = 10, Margin = new Thickness(6,0,3,0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = effect.Id == selectedVisual ? Brush(20, 22, 26) : RecorderTheme.Brush(this, "EditorInk") };
            At(overlayLayer, body, x, InstanceEffectY(effect.Kind));
        }
        playLine.Height = canvas.Height - 14; At(overlayLayer, playLine, X(playhead), 14); At(overlayLayer, playHandle, X(playhead) - 3.5, 6);
        foreach (var pair in clipViews)
            pair.Value.Root.Opacity = dragging && dragKind == "move" && dragClip is not null && pair.Key == dragClip.Id ? 0.72 : 1;
        if (dragging && dragKind == "move" && dragClip is not null && timeline.Clips.FirstOrDefault(c => c.Id == dragClip.Id) is { } movedClip)
            At(overlayLayer, new Rectangle { Width = 3, Height = 64, RadiusX = 1.5, RadiusY = 1.5, Fill = selectedBorder, IsHitTestVisible = false }, X(movedClip.OutputStart) - 1.5, 32);
        if (hoverTicks is not null) { overlayLayer.Children.Add(hoverLine); overlayLayer.Children.Add(hoverBadge); PositionHoverIndicator(); }
        foreach (var id in clipViews.Keys.Where(id => !usedClips.Contains(id)).ToArray()) {
            clipsLayer.Children.Remove(clipViews[id].Root);
            clipViews.Remove(id);
        }
        foreach(var id in effectViews.Keys.Where(id=>!usedEffects.Contains(id)).ToArray())effectViews.Remove(id);
        if (prefetch.Count > 0) requested.AddRange(prefetch);
        ThumbnailsNeeded?.Invoke(requested);
    }

    private sealed class ClipVisual
    {
        internal readonly Border Root = new() { Height = 64, CornerRadius = new CornerRadius(8), Padding = new Thickness(1), IsHitTestVisible = false };
        internal readonly Grid Body = new() { Height = 60, CornerRadius = new CornerRadius(6), IsHitTestVisible = false };
        internal readonly Canvas Strip = new() { Height = 46, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        internal readonly List<Image> Images = new();
        internal readonly Border Badge = new() { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(160, 0, 0, 0)), CornerRadius = new CornerRadius(5), Padding = new Thickness(5, 2, 5, 2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(3), IsHitTestVisible = false };
        internal readonly TextBlock BadgeText = new() { FontSize = 10, Foreground = new SolidColorBrush(Colors.White), TextTrimming = TextTrimming.CharacterEllipsis };
        internal ClipVisual()
        {
            Badge.Child = BadgeText;
            Body.Children.Add(Strip);
            Body.Children.Add(Badge);
            Root.Child = Body;
        }
    }
    private IEnumerable<ResolvedClip> VisibleClips(double left, double right)
    {
        if (timeline is null) yield break;
        int lo = 0, hi = timeline.Clips.Count;
        while (lo < hi) { int m = (lo + hi) / 2; var c = timeline.Clips[m]; if (X(c.OutputStart + c.OutputDuration) < left) lo = m + 1; else hi = m; }
        for (int i = lo; i < timeline.Clips.Count && X(timeline.Clips[i].OutputStart) <= right; ++i) yield return timeline.Clips[i];
    }
    private long Snap(long ticks, string? excludeClipId = null)
    {
        if (timeline is null || !SnappingEnabled) return ticks; double tolerance = 8 / (dragging ? dragScale : EffectiveScale) * 10_000_000;
        var candidates = new List<long> { playhead };
        foreach (var c in timeline.Clips) {
            if (c.Id == excludeClipId) continue;
            if (Math.Abs(c.OutputStart - ticks) <= tolerance) candidates.Add(c.OutputStart);
            long end = c.OutputStart + c.OutputDuration;
            if (Math.Abs(end - ticks) <= tolerance) candidates.Add(end);
        }
        int lo = 0, hi = markers.Length; while (lo < hi) { int m = (lo + hi) / 2; if (markers[m].Ticks < ticks) lo = m + 1; else hi = m; } if (lo < markers.Length) candidates.Add(markers[lo].Ticks); if (lo > 0) candidates.Add(markers[lo - 1].Ticks);
        return candidates.OrderBy(t => Math.Abs(t - ticks)).FirstOrDefault(t => Math.Abs(t - ticks) <= tolerance, ticks);
    }
    private (ResolvedEffect Effect, string Kind)? HitTestEffect(Point p)
    {
        if (timeline is null) return null;
        long leftTicks = T(Math.Max(16, p.X - 10)), rightTicks = T(p.X + 10);
        var effect = effects.Query(leftTicks, rightTicks)
            .Where(c => p.Y >= InstanceEffectY(c.Kind) && p.Y <= InstanceEffectY(c.Kind) + 20)
            .Where(c => p.X >= X(c.OutputStart) - 10 && p.X <= X(c.OutputStart + c.OutputDuration) + 10)
            .MaxBy(c => effectOrdinals[c.Id + ":" + c.OutputStart]);
        if (effect is null) return null;
        double startX = X(effect.OutputStart), endX = X(effect.OutputStart + effect.OutputDuration);
        double edgeTol = Math.Min(10, Math.Max(4, (endX - startX) / 3));
        bool nearStart = Math.Abs(p.X - startX) <= (p.X < startX ? 10 : edgeTol);
        bool nearEnd = Math.Abs(p.X - endX) <= (p.X > endX ? 10 : edgeTol);
        if (nearStart && nearEnd) return (effect, Math.Abs(p.X - startX) <= Math.Abs(p.X - endX) ? "start" : "end");
        return (effect, nearStart ? "start" : nearEnd ? "end" : "move");
    }
    private (ResolvedClip Clip, string Kind)? HitTestClip(Point p)
    {
        if (timeline is null || p.Y < 32 || p.Y > 96) return null;
        var candidates = VisibleClips(p.X - 12, p.X + 12).ToArray();
        ResolvedClip? bestEdgeClip = null; string? bestEdgeKind = null; double bestDist = double.MaxValue;
        foreach (var c in candidates) {
            double startX = X(c.OutputStart), endX = X(c.OutputStart + c.OutputDuration);
            double edgeTol = Math.Min(12, Math.Max(4, (endX - startX) / 3));
            double dStart = Math.Abs(p.X - startX), dEnd = Math.Abs(p.X - endX);
            if (dStart <= (p.X < startX ? 12 : edgeTol) && dStart < bestDist) { bestDist = dStart; bestEdgeClip = c; bestEdgeKind = "start"; }
            if (dEnd <= (p.X > endX ? 12 : edgeTol) && dEnd < bestDist) { bestDist = dEnd; bestEdgeClip = c; bestEdgeKind = "end"; }
        }
        if (bestEdgeClip is not null && bestEdgeKind is not null) return (bestEdgeClip, bestEdgeKind);
        var inside = candidates.FirstOrDefault(c => p.X >= X(c.OutputStart) && p.X <= X(c.OutputStart + c.OutputDuration));
        if (inside is null) return null;
        return (inside, timeline.Clips.Count > 1 ? "move" : "seek");
    }
    private void Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (timeline is null) return; var point = e.GetCurrentPoint(canvas); if (!point.Properties.IsLeftButtonPressed) return;
        HideHover(notify: false); FocusTimeline(FocusState.Pointer);
        var p = point.Position; var ticks = T(p.X);
        if (HitTestEffect(p) is { } effectHit) {
            var effect = effectHit.Effect;
            selectedVisual = effect.Id; SelectEffect?.Invoke(effect.Id);
            if (EditingEnabled) {
                dragEffect = effect; dragClip = null; dragInitialClips = null; dragScale = EffectiveScale; dragInitialDuration = timeline.Duration;
                effectGrabOffset = ticks - effect.OutputStart;
                dragKind = effectHit.Kind; dragPending = true; pressPoint = p; pressTicks = ticks;
                ProtectedCursor = dragKind == "move" ? moveCursor : resizeHorizontalCursor;
            }
            else { Seek?.Invoke(Math.Min(ticks, Math.Max(0, timeline.Duration - 1))); dragKind = "seek"; ProtectedCursor = arrowCursor; }
            canvas.CapturePointer(e.Pointer); e.Handled = true; Draw(); return;
        }
        if (HitTestClip(p) is { } clipHit) {
            var clip = clipHit.Clip;
            SelectClip?.Invoke(clip.Id); selected = clip.Id;
            if (EditingEnabled && clipHit.Kind != "seek") {
                dragClip = clip; dragEffect = null; dragInitialClips = timeline.Clips.ToArray(); dragScale = EffectiveScale; dragInitialDuration = timeline.Duration;
                dragKind = clipHit.Kind; dragPending = true; pressPoint = p; pressTicks = ticks;
                ProtectedCursor = dragKind == "move" ? moveCursor : resizeHorizontalCursor;
            } else {
                dragClip = null; dragEffect = null; dragInitialClips = null;
                Seek?.Invoke(Math.Min(ticks, Math.Max(0, timeline.Duration - 1))); dragKind = "seek"; ProtectedCursor = arrowCursor;
            }
            canvas.CapturePointer(e.Pointer); e.Handled = true; Draw(); return;
        }
        Seek?.Invoke(Math.Min(ticks, Math.Max(0, timeline.Duration - 1))); dragKind = "seek"; ProtectedCursor = arrowCursor;
        canvas.CapturePointer(e.Pointer); e.Handled = true; Draw();
    }
    private void Moved(object sender, PointerRoutedEventArgs e)
    {
        if (timeline is null) return;
        var point = e.GetCurrentPoint(canvas);
        var p = point.Position;
        if(!point.Properties.IsLeftButtonPressed){
            if(!dragging && !dragPending){
                if (p.X >= 8 && p.X <= X(timeline.Duration) + 8) {
                    var hoverT = T(p.X);
                    ShowHoverIndicator(hoverT);
                    if (EditingEnabled) {
                        pendingHover = hoverT;
                        if (!hoverTimer.IsRunning) hoverTimer.Start();
                    }
                }
                else HideHover();
                UpdateHoverCursor(p);
            }
            return;
        }
        if (dragPending && !dragging) {
            if (Math.Abs(p.X - pressPoint.X) < 3 && Math.Abs(p.Y - pressPoint.Y) < 3) return;
            if (!EditingEnabled) { ClearDragState(); dragKind = "seek"; ProtectedCursor = arrowCursor; return; }
            dragPending = false; dragging = true; BeginEdit?.Invoke();
        }
        if (!dragging) {
            var seekTicks = Snap(T(p.X));
            if (dragKind == "seek") Seek?.Invoke(Math.Min(seekTicks, Math.Max(0, timeline.Duration - 1)));
            return;
        }
        var rawTicks = RawTicks(p.X, dragScale);
        ProtectedCursor = dragKind == "move" ? moveCursor : resizeHorizontalCursor;
        if (dragEffect is not null) {
            var effectTicks = Snap(Math.Clamp(rawTicks, 0, timeline.Duration));
            PreviewEdit?.Invoke(new { op = "effectAtOutput", effectId = dragEffect.Id, outputTicks = dragKind == "move" ? effectTicks - effectGrabOffset : effectTicks, edge = dragKind });
            e.Handled = true; return;
        }
        if (dragClip is null) return;
        object command;
        if (dragKind == "move" && dragInitialClips is { Length: > 1 }) {
            int initialIndex = Array.FindIndex(dragInitialClips, c => c.Id == dragClip.Id);
            if (initialIndex < 0) return;
            long delta = rawTicks - pressTicks;
            int index = initialIndex;
            if (delta >= 0) {
                long leading = dragClip.OutputStart + dragClip.OutputDuration + delta;
                for (int i = initialIndex + 1; i < dragInitialClips.Length; i++)
                    if (leading >= dragInitialClips[i].OutputStart + dragInitialClips[i].OutputDuration / 2) index = i;
            } else {
                long leading = dragClip.OutputStart + delta;
                for (int i = initialIndex - 1; i >= 0; i--)
                    if (leading <= dragInitialClips[i].OutputStart + dragInitialClips[i].OutputDuration / 2) index = i;
            }
            command = new { op = "move", segmentId = dragClip.Id, index };
        } else {
            var trimTicks = Snap(rawTicks, dragClip.Id);
            command = new { op = "trimOutput", segmentId = dragClip.Id, outputTicks = trimTicks, edge = dragKind };
        }
        PreviewEdit?.Invoke(command); e.Handled = true;
    }
    private void Released(object sender, PointerRoutedEventArgs e)
    {
        if (dragPending) {
            ClearDragState(); dragKind = null;
            if (timeline is not null) Seek?.Invoke(Math.Min(pressTicks, Math.Max(0, timeline.Duration - 1)));
        } else if (dragging) {
            Finish(true);
        }
        dragKind = null; canvas.ReleasePointerCapture(e.Pointer);
        var p = e.GetCurrentPoint(canvas).Position;
        UpdateHoverCursor(p);
        if (timeline is not null && p.X >= 8 && p.X <= X(timeline.Duration) + 8) ShowHoverIndicator(T(p.X));
        e.Handled = true;
    }
    private void Finish(bool commit) { dragging = false; ClearDragState(); dragKind = null; ProtectedCursor = arrowCursor; EndEdit?.Invoke(commit); }
    private void OnCanvasDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (timeline is null) return;
        var p = e.GetPosition(canvas);
        if (p.Y < 32 || p.Y > 96) return;
        var ticks = T(p.X);
        var clip = VisibleClips(p.X, p.X).FirstOrDefault(c => ticks < c.OutputStart + c.OutputDuration);
        if (clip is null) return;
        selected = clip.Id;
        ClipDoubleClicked?.Invoke(clip.Id);
        e.Handled = true;
        Draw();
    }
    private void WheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (timeline is null) return;
        bool ctrl = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (!ctrl) return;
        int delta = e.GetCurrentPoint(canvas).Properties.MouseWheelDelta;
        if (delta == 0) return;
        e.Handled = true;
        double factor = delta > 0 ? 1.25 : 0.8;
        double next = Math.Clamp(scale * factor, 0.05, 1000);
        if (Math.Abs(next - scale) < 0.0001) return;
        PixelsPerSecond = next;
        ZoomChanged?.Invoke(next);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => {
            scroll.UpdateLayout();
            Reveal(playhead);
        });
    }
    internal void SimulateHoverAt(long ticks)
    {
        if (timeline is null) return;
        double x = X(Math.Clamp(ticks, 0, timeline.Duration));
        var point = new Point(x, 64);
        ShowHoverIndicator(T(point.X));
        UpdateHoverCursor(point);
    }
    private void UpdateHoverCursor(Point p)
    {
        if (timeline is null || !EditingEnabled) { ProtectedCursor = arrowCursor; return; }
        if (HitTestEffect(p) is { } effectHit) {
            ProtectedCursor = effectHit.Kind is "start" or "end" ? resizeHorizontalCursor : arrowCursor;
            return;
        }
        if (HitTestClip(p) is { } clipHit) {
            ProtectedCursor = clipHit.Kind is "start" or "end" ? resizeHorizontalCursor : arrowCursor;
            return;
        }
        ProtectedCursor = arrowCursor;
    }
    private int InstanceEffectY(string kind) => effectRows.TryGetValue(kind, out int y) ? y : DefaultEffectY(kind);
    private static int EffectY(string kind) => lastEffectRows is not null && lastEffectRows.TryGetValue(kind, out int y) ? y : DefaultEffectY(kind);
    private static int DefaultEffectY(string kind) => kind switch { "zoom" => 148, "tilt" => 172, "intro" => 196, "mask" => 220, "focus" => 244, _ => 268 };
    private static (byte R, byte G, byte B) EffectColor(string kind) => kind switch {
        "zoom" => (90, 174, 255),
        "focus" => (255, 184, 48),
        "mask" => (255, 107, 146),
        "tilt" => (255, 133, 80),
        "intro" => (61, 219, 176),
        "sticker" => (181, 230, 85),
        _ => (90, 174, 255)
    };
}

internal static class ReadOnlyListExtensions
{
    internal static int IndexOf<T>(this IReadOnlyList<T> list, T value) { for (int i = 0; i < list.Count; ++i) if (EqualityComparer<T>.Default.Equals(list[i], value)) return i; return -1; }
}
