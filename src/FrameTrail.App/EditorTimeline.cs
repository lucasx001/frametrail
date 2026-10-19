using FrameTrail.Interop;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace FrameTrail.App;

// Only visible clips, ruler marks and waveform buckets become XAML elements.
internal sealed partial class EditorTimeline : ContentControl
{
    private readonly ScrollViewer scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Enabled };
    private readonly Canvas canvas = new() { Height = 148, Background = new SolidColorBrush(Colors.Transparent) };
    private ResolvedTimeline? timeline;
    private string? selected;
    private long playhead;
    private double scale = 80;
    private bool dragging;
    private string? dragKind;
    private ResolvedClip? dragClip;
    private ResolvedEffect? dragEffect;
    private long effectGrabOffset;
    private string? selectedVisual;
    private readonly Dictionary<long, BitmapImage> thumbnails = new();
    private float[]? waveform;
    private Dictionary<string, int> clipIndices = new();
    private ResolvedMarker[] markers = Array.Empty<ResolvedMarker>();
    private TimelineIntervalIndex<ResolvedEffect> effects = new([],e=>e.OutputStart,e=>e.OutputStart+e.OutputDuration);
    private Dictionary<string,int> effectOrdinals = new();
    private readonly Dictionary<string,Border> clipViews = new(), effectViews = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer hoverTimer;
    private long? pendingHover;
    private bool drawQueued;
    internal int DrawCount { get; private set; }
    internal event Action<long?>? Hover;
    private readonly Rectangle playLine = new() { Width = 1, Height = 134, Fill = Brush(255, 69, 59), IsHitTestVisible = false };
    private readonly Border playHandle = new() { Width = 8, Height = 15, CornerRadius = new CornerRadius(4), Background = Brush(255, 69, 59), IsHitTestVisible = false };
    internal event Action<long>? Seek;
    internal event Action<string>? SelectClip;
    internal event Action<string>? SelectEffect;
    internal event Action? BeginEdit;
    internal event Action<object>? PreviewEdit;
    internal event Action<bool>? EndEdit;
    internal event Action<IReadOnlyList<long>>? ThumbnailsNeeded;
    internal double PixelsPerSecond { get => scale; set { scale = Math.Clamp(value, 0.05, 1000); Draw(); } }
    internal bool EditingEnabled { get; set; } = true;
    internal bool ThumbnailsEnabled { get; set; } = true;
    internal bool SnappingEnabled { get; set; } = true;
    private bool waveformMode;
    internal bool WaveformMode { get => waveformMode; set { waveformMode = value; Draw(); } }
    internal void RefreshAppearance() => Draw();
    internal void SelectVisual(string? id) { selectedVisual = id; DescribeSelection(); Draw(); }
    internal int RenderedElementCount => canvas.Children.Count;
    internal EditorTimeline()
    {
        Content = scroll; HorizontalContentAlignment=HorizontalAlignment.Stretch;VerticalContentAlignment=VerticalAlignment.Stretch;IsTabStop=true; UseSystemFocusVisuals=true; scroll.Content = canvas; scroll.ViewChanged += (_, _) => Draw(); SizeChanged += (_, _) => Draw();
        hoverTimer=DispatcherQueue.CreateTimer();hoverTimer.Interval=TimeSpan.FromMilliseconds(100);hoverTimer.IsRepeating=false;
        hoverTimer.Tick+=(_,_)=>Hover?.Invoke(pendingHover);
        canvas.PointerExited+=(_,_)=>{HideHover();Hover?.Invoke(null);};
        canvas.PointerPressed += Pressed; canvas.PointerMoved += Moved; canvas.PointerReleased += Released;
        canvas.PointerCaptureLost += (_, _) => { if (dragging) Finish(false); };
        ActualThemeChanged += (_, _) => Draw();
    }
    internal void Set(ResolvedTimeline value, string? selection, long ticks) { timeline = value; selected = selection; playhead = ticks; clipIndices = value.Clips.Select((c, i) => (c.Id, i)).ToDictionary(k => k.Id, k => k.i); markers = value.Markers.OrderBy(m => m.Ticks).ToArray(); effects=new(value.Effects,e=>e.OutputStart,e=>e.OutputStart+e.OutputDuration); effectOrdinals=value.Effects.Select((e,i)=>(Key:e.Id+":"+e.OutputStart,Index:i)).ToDictionary(v=>v.Key,v=>v.Index); canvas.Height = value.Effects.Count > 0 ? 294 : 148; DescribeSelection(); Draw(); }
    internal void Position(long ticks) { playhead = ticks; Canvas.SetLeft(playLine, X(playhead)); Canvas.SetLeft(playHandle, X(playhead) - 3.5); }
    internal void SetWaveform(float[]? values) { waveform = values; Draw(); }
    private readonly HashSet<long> staleThumbnails = new();
    internal void ClearAssets() { waveform = null; thumbnails.Clear(); staleThumbnails.Clear(); Draw(); }
    internal void ClearThumbnails() { thumbnails.Clear(); staleThumbnails.Clear(); Draw(); }
    internal void InvalidateThumbnails() { staleThumbnails.UnionWith(thumbnails.Keys); Draw(); }
    internal void AddThumbnail(long ticks, string path)
    {
        if (thumbnails.Count >= 160 && !thumbnails.ContainsKey(ticks)) { var first = thumbnails.Keys.First(); thumbnails.Remove(first); staleThumbnails.Remove(first); }
        thumbnails[ticks] = new BitmapImage(new Uri(path)) { DecodePixelWidth = 384 }; staleThumbnails.Remove(ticks); Draw();
    }
    internal void CancelDrag() { if (dragging) { Finish(false); canvas.ReleasePointerCaptures(); } }
    internal void HideHover(){hoverTimer.Stop();pendingHover=null;}
    internal void Reveal(long ticks)=>scroll.ChangeView(Math.Max(0,X(ticks)-ActualWidth/2),null,null,true);
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
    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(255, r, g, b));
    private void At(UIElement element, double x, double y) { Canvas.SetLeft(element, x); Canvas.SetTop(element, y); canvas.Children.Add(element); }
    private void Draw()
    {
        if(drawQueued)return;drawQueued=true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,()=>{drawQueued=false;DrawNow();});
    }
    private void DrawNow()
    {
        DrawCount++;
        var requested = new List<long>();
        var usedClips=new HashSet<string>();var usedEffects=new HashSet<string>();
        canvas.Children.Clear(); if (timeline is null) return;
        canvas.Width = Math.Max(ActualWidth - 4, X(timeline.Duration) + 32); double left = scroll.HorizontalOffset, right = left + Math.Max(320, ActualWidth);
        double step = 1; while (step * EffectiveScale < 72) step *= 2; if (step * EffectiveScale > 180) step /= 2;
        double first = Math.Floor(Math.Max(0, (left - 16) / EffectiveScale) / step) * step;
        for (double s = first; 16 + s * EffectiveScale < right; s += step) {
            double x = 16 + s * EffectiveScale;
            At(new Rectangle { Width = 1, Height = 8, Fill = RecorderTheme.Brush(this, "EditorLine"), IsHitTestVisible = false }, x, 20);
            At(new TextBlock { Text = TimeSpan.FromSeconds(s).ToString(s >= 3600 ? @"h\:mm\:ss" : step < 1 ? @"m\:ss\.f" : @"m\:ss"), FontSize = 10, Foreground = RecorderTheme.Brush(this, "EditorTertiary"), IsHitTestVisible = false }, x + 4, 6);
            for (int i = 1; i < 4; i++) At(new Rectangle { Width = 0.5, Height = 5, Fill = RecorderTheme.Brush(this, "EditorLine"), IsHitTestVisible = false }, x + step * EffectiveScale * i / 4, 23);
        }
        double lastDenseClip = double.NegativeInfinity;
        foreach (var clip in VisibleClips(left, right)) {
            double start = X(clip.OutputStart), end = X(clip.OutputStart + clip.OutputDuration); double x = Math.Max(start, left), w = Math.Max(1, Math.Min(end, right) - x - 2);
            if (w < 3 && clip.Id != selected) { if (x < lastDenseClip + 3) continue; lastDenseClip = x; }
            var body = new Grid { Width = w, Height = 60, CornerRadius = new CornerRadius(6), Background = RecorderTheme.Brush(this, "EditorClip"), IsHitTestVisible = false };
            if (ThumbnailsEnabled && !waveformMode && w >= 6) {
                var strip = new Canvas { Width = w, Height = 46, VerticalAlignment = VerticalAlignment.Top };
                foreach (var tile in FilmstripSampling.Visible(clip.OutputStart, clip.OutputDuration, EffectiveScale, left, right)) {
                    bool exact = thumbnails.TryGetValue(tile.Ticks, out var picture);
                    // Fallbacks stay within this clip; they disappear as exact samples arrive.
                    picture ??= thumbnails.Where(t => t.Key >= clip.OutputStart && t.Key < clip.OutputStart + clip.OutputDuration)
                        .DefaultIfEmpty().MinBy(t => Math.Abs(t.Key - tile.Ticks)).Value;
                    if ((!exact || staleThumbnails.Contains(tile.Ticks)) && requested.Count < 128) requested.Add(tile.Ticks);
                    if (picture is not null) { var image = new Image { Source = picture, Width = tile.Width, Height = 46, Stretch = Stretch.UniformToFill }; Canvas.SetLeft(image, tile.Left); strip.Children.Add(image); }
                }
                body.Children.Add(strip);
            }
            if (w > 26) {
                string title = waveformMode ? L.T("S9D4973468893", clipIndices[clip.Id] + 1) : $"{clip.OutputDuration / 10_000_000.0:0.#}s";
                if (clip.Numerator != clip.Denominator) title += $" · {(double)clip.Numerator / clip.Denominator:0.##}×";
                body.Children.Add(new Border { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(160, 0, 0, 0)), CornerRadius = new CornerRadius(5), Padding = new Thickness(5, 2, 5, 2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(3), Child = new TextBlock { Text = title, FontSize = 10, Foreground = new SolidColorBrush(Colors.White), TextTrimming = TextTrimming.CharacterEllipsis } });
            }
            if(!clipViews.TryGetValue(clip.Id,out var clipView))clipViews[clip.Id]=clipView=new Border{Height=64,CornerRadius=new CornerRadius(8),Padding=new Thickness(1),IsHitTestVisible=false};
            usedClips.Add(clip.Id);clipView.Width=w;clipView.BorderThickness=new Thickness(clip.Id==selected?2:1);clipView.BorderBrush=clip.Id==selected?Brush(90,174,255):new SolidColorBrush(Windows.UI.Color.FromArgb(75,90,174,255));clipView.Child=body;
            At(clipView,x,32);
            if (clip.Id == selected) {
                if (start >= left) At(new Rectangle { Width = 3, Height = 16, RadiusX = 1.5, RadiusY = 1.5, Fill = Brush(90, 174, 255), IsHitTestVisible = false }, start + 3, 56);
                if (end <= right) At(new Rectangle { Width = 3, Height = 16, RadiusX = 1.5, RadiusY = 1.5, Fill = Brush(90, 174, 255), IsHitTestVisible = false }, end - 8, 56);
            }
        }
        At(new Rectangle { Width = Math.Max(1, right - left), Height = 0.5, Fill = RecorderTheme.Brush(this, "EditorLine"), IsHitTestVisible = false }, left, 101);
        if (waveform is null || waveform.Length == 0) At(new TextBlock { Text = waveform is null ? L.T("S0B2C11200068") : L.T("S923FBEB23043"), FontSize = 10, Foreground = RecorderTheme.Brush(this, "EditorTertiary"), IsHitTestVisible = false }, left + 16, 111);
        if (waveform is not null && waveform.Length > 0) {
            var line = new Polyline { Stroke = Brush(0, 143, 104), StrokeThickness = 1.5, IsHitTestVisible = false };
            for (double x = Math.Max(16, left); x < Math.Min(right, X(timeline.Duration)); x += 3) {
                int index = Math.Clamp((int)((x - 16) / Math.Max(1, X(timeline.Duration) - 16) * waveform.Length), 0, waveform.Length - 1); double amp = Math.Clamp(waveform[index], 0, 1) * 17;
                line.Points.Add(new Point(x, 122 - amp * 0.65)); line.Points.Add(new Point(x, 122 + amp * 0.65));
            }
            canvas.Children.Add(line);
            if (waveformMode) {
                var primary = new Polyline { Stroke = Brush(0, 143, 104), StrokeThickness = 1.5, IsHitTestVisible = false };
                foreach (var point in line.Points) primary.Points.Add(new Point(point.X, 65 + (point.Y - 122) * 1.6)); canvas.Children.Add(primary);
            }
        }
        int ml = 0, mh = markers.Length; while (ml < mh) { int m = (ml + mh) / 2; if (X(markers[m].Ticks) < left) ml = m + 1; else mh = m; }
        double lastMarker = double.NegativeInfinity;
        for (int i = ml; i < markers.Length && X(markers[i].Ticks) <= right; ++i) { double x = X(markers[i].Ticks); if (x - lastMarker < 4) continue; lastMarker = x; At(new TextBlock { Text = "◆", FontSize = 11, Foreground = Brush(235, 185, 95), IsHitTestVisible = false }, x - 5, 22); }
        var lastEffectX=new Dictionary<string,double>();
        foreach (var effect in effects.Query(T(left),T(right))) {
            double start = X(effect.OutputStart), end = X(effect.OutputStart + effect.OutputDuration), x = Math.Max(left, start), width = Math.Max(2, Math.Min(right, end) - x - 2);
            if(effect.Id!=selectedVisual&&lastEffectX.TryGetValue(effect.Kind,out var last)&&x-last<2)continue;
            lastEffectX[effect.Kind]=x;
            // A source effect can appear in multiple edited intervals.
            string viewKey=effect.Id+":"+effect.OutputStart;
            if(!effectViews.TryGetValue(viewKey,out var body))effectViews[viewKey]=body=new Border{Height=20,CornerRadius=new CornerRadius(6),BorderBrush=Brush(90,174,255),IsHitTestVisible=false};
            usedEffects.Add(viewKey);body.Width=width;body.Background=new SolidColorBrush(Windows.UI.Color.FromArgb(effect.Id==selectedVisual?(byte)190:(byte)65,90,174,255));body.BorderThickness=new Thickness(effect.Id==selectedVisual?2:1);body.Child=null;
            if(width > 38) body.Child = new TextBlock { Text = effect.Kind switch { "zoom" => L.T("SB762AEC769A3"), "tilt" => L.T("S17E14283A497"), "mask" => L.T("SAC7CE836857B"), "focus" => L.T("SED19BD5E2620"), "intro" => L.T("SFD0D4B7258C2"), _ => L.T("S622DA0460277") }, FontSize = 10, Margin = new Thickness(6,0,3,0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = RecorderTheme.Brush(this, "EditorInk") };
            At(body, x, EffectY(effect.Kind));
        }
        playLine.Height = canvas.Height - 14; At(playLine, X(playhead), 14); At(playHandle, X(playhead) - 3.5, 6);
        foreach(var id in clipViews.Keys.Where(id=>!usedClips.Contains(id)).ToArray())clipViews.Remove(id);
        foreach(var id in effectViews.Keys.Where(id=>!usedEffects.Contains(id)).ToArray())effectViews.Remove(id);
        ThumbnailsNeeded?.Invoke(requested);
    }
    private IEnumerable<ResolvedClip> VisibleClips(double left, double right)
    {
        if (timeline is null) yield break;
        int lo = 0, hi = timeline.Clips.Count;
        while (lo < hi) { int m = (lo + hi) / 2; var c = timeline.Clips[m]; if (X(c.OutputStart + c.OutputDuration) < left) lo = m + 1; else hi = m; }
        for (int i = lo; i < timeline.Clips.Count && X(timeline.Clips[i].OutputStart) <= right; ++i) yield return timeline.Clips[i];
    }
    private long Snap(long ticks)
    {
        if (timeline is null || !SnappingEnabled) return ticks; double tolerance = 8 / EffectiveScale * 10_000_000;
        int lo = 0, hi = timeline.Clips.Count; while (lo < hi) { int m = (lo + hi) / 2; if (timeline.Clips[m].OutputStart < ticks) lo = m + 1; else hi = m; }
        var candidates = new List<long> { playhead }; if (lo < timeline.Clips.Count) candidates.Add(timeline.Clips[lo].OutputStart); if (lo > 0) { var c = timeline.Clips[lo - 1]; candidates.Add(c.OutputStart); candidates.Add(c.OutputStart + c.OutputDuration); }
        lo = 0; hi = markers.Length; while (lo < hi) { int m = (lo + hi) / 2; if (markers[m].Ticks < ticks) lo = m + 1; else hi = m; } if (lo < markers.Length) candidates.Add(markers[lo].Ticks); if (lo > 0) candidates.Add(markers[lo - 1].Ticks);
        return candidates.OrderBy(t => Math.Abs(t - ticks)).FirstOrDefault(t => Math.Abs(t - ticks) <= tolerance, ticks);
    }
    private void Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (timeline is null) return; var point = e.GetCurrentPoint(canvas); if (!point.Properties.IsLeftButtonPressed) return;
        HideHover(); FocusTimeline(FocusState.Pointer);
        var p = point.Position; var ticks = T(p.X); var clip = VisibleClips(p.X,p.X).FirstOrDefault(c=>ticks<c.OutputStart+c.OutputDuration);
        var effect = effects.Query(ticks,ticks).Where(c => ticks < c.OutputStart + c.OutputDuration && p.Y >= EffectY(c.Kind) && p.Y <= EffectY(c.Kind) + 20).MaxBy(c=>effectOrdinals[c.Id+":"+c.OutputStart]);
        if(effect is not null) {
            bool already = selectedVisual == effect.Id; selectedVisual = effect.Id; SelectEffect?.Invoke(effect.Id);
            if(EditingEnabled && already) { dragEffect = effect; effectGrabOffset = ticks-effect.OutputStart; dragKind = Math.Abs(p.X-X(effect.OutputStart)) <= 10 ? "start" : Math.Abs(p.X-X(effect.OutputStart+effect.OutputDuration)) <= 10 ? "end" : "move"; BeginEdit?.Invoke(); dragging = true; }
            else { Seek?.Invoke(Math.Min(ticks, Math.Max(0, timeline.Duration-1))); dragKind = "seek"; }
            canvas.CapturePointer(e.Pointer); e.Handled = true; Draw(); return;
        }
        if (clip is not null && p.Y >= 32 && p.Y <= 96) {
            var prior = selected; SelectClip?.Invoke(clip.Id); selected = clip.Id; dragClip = clip;
            if (EditingEnabled && prior == clip.Id) {
                dragKind = Math.Abs(p.X - X(clip.OutputStart)) <= 12 ? "start" : Math.Abs(p.X - X(clip.OutputStart + clip.OutputDuration)) <= 12 ? "end" : "move";
                BeginEdit?.Invoke(); dragging = true;
            }
        }
        if (!dragging) { Seek?.Invoke(Math.Min(ticks, Math.Max(0, timeline.Duration - 1))); dragKind = "seek"; }
        canvas.CapturePointer(e.Pointer); e.Handled = true; Draw();
    }
    private void Moved(object sender, PointerRoutedEventArgs e)
    {
        if (timeline is null) return;
        if(!e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed){if(!dragging&&EditingEnabled){pendingHover=T(e.GetCurrentPoint(canvas).Position.X);if(!hoverTimer.IsRunning)hoverTimer.Start();}return;}
        var ticks = Snap(T(e.GetCurrentPoint(canvas).Position.X));
        if (!dragging) { if (dragKind == "seek") Seek?.Invoke(Math.Min(ticks, Math.Max(0, timeline.Duration - 1))); return; }
        if(dragEffect is not null) { PreviewEdit?.Invoke(new { op = "effectAtOutput", effectId = dragEffect.Id, outputTicks = dragKind == "move" ? ticks-effectGrabOffset : ticks, edge = dragKind }); e.Handled = true; return; }
        if (dragClip is null) return;
        object command;
        if (dragKind == "move") {
            var index = timeline.Clips.TakeWhile(c => ticks >= c.OutputStart + c.OutputDuration / 2).Count(); index = Math.Clamp(index, 0, timeline.Clips.Count - 1);
            command = new { op = "move", segmentId = dragClip.Id, index };
        } else command = new { op = "trimOutput", segmentId = dragClip.Id, outputTicks = ticks, edge = dragKind };
        PreviewEdit?.Invoke(command); e.Handled = true;
    }
    private void Released(object sender, PointerRoutedEventArgs e) { if (dragging) Finish(true); dragKind = null; canvas.ReleasePointerCapture(e.Pointer); e.Handled = true; }
    private void Finish(bool commit) { dragging = false; dragKind = null; dragClip = null; dragEffect = null; EndEdit?.Invoke(commit); }
    private static int EffectY(string kind) => kind switch { "zoom" => 148, "tilt" => 172, "intro" => 196, "mask" => 220, "focus" => 244, _ => 268 };
}

internal static class ReadOnlyListExtensions
{
    internal static int IndexOf<T>(this IReadOnlyList<T> list, T value) { for (int i = 0; i < list.Count; ++i) if (EqualityComparer<T>.Default.Equals(list[i], value)) return i; return -1; }
}
