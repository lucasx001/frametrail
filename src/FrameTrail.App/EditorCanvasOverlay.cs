using FrameTrail.Domain;
using FrameTrail.Interop;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace FrameTrail.App;

internal sealed class EditorCanvasOverlay : Canvas
{
    private IReadOnlyList<CanvasEditItem> items = [];
    private CanvasEditItem? active;
    private CanvasPlane? plane;
    private (double X,double Y) origin;
    private int corner;
    private double? activeGuideX;
    private double? activeGuideY;
    internal double CanvasAspect { get; set; } = 16.0 / 9.0;
    internal string? Selection { get; set; }
    internal bool Dragging => active is not null;
    internal event Action<string>? Selected;
    internal event Action? Begin;
    internal event Action<string,NormalizedRect>? Changed;
    internal event Action<bool>? Ended;
    internal event Action<string, Rect>? SubtitleInlineEditRequested;
    private readonly InputSystemCursor arrow=InputSystemCursor.Create(InputSystemCursorShape.Arrow), moveCursor=InputSystemCursor.Create(InputSystemCursorShape.SizeAll), diagonalA=InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast), diagonalB=InputSystemCursor.Create(InputSystemCursorShape.SizeNortheastSouthwest);
    internal EditorCanvasOverlay()
    {
        Background = new SolidColorBrush(Colors.Transparent);
        PointerPressed += Press; PointerMoved += Move; PointerReleased += (_, e) => { if(Dragging){Finish(true);ReleasePointerCapture(e.Pointer);e.Handled=true;} };
        PointerCaptureLost += (_,_) => Finish(false); PointerCanceled += (_,_)=>Finish(false);
        DoubleTapped += OnDoubleTapped;
        SizeChanged += (_,_)=>Draw();
    }
    internal void SetItems(IReadOnlyList<CanvasEditItem> value) { if(Dragging)return;items=value;IsHitTestVisible=value.Count>0;Draw(); }
    internal void Finish(bool commit) { if(active is null)return;active=null;plane=null;activeGuideX=null;activeGuideY=null;Ended?.Invoke(commit);ReleasePointerCaptures();Draw(); }
    private void Press(object sender,PointerRoutedEventArgs e)
    {
        if(!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Dragging)return;
        double x=e.GetCurrentPoint(this).Position.X/Math.Max(1,ActualWidth),y=e.GetCurrentPoint(this).Position.Y/Math.Max(1,ActualHeight);
        if (BeginGesture(x,y)) { CapturePointer(e.Pointer);e.Handled=true; }
    }
    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ActualWidth < 1 || ActualHeight < 1 || items.Count == 0) return;
        var pos = e.GetPosition(this);
        double x = pos.X / ActualWidth, y = pos.Y / ActualHeight;
        foreach (var item in items.Reverse().OrderByDescending(i => i.Id == Selection))
        {
            if (item.Kind != "subtitle") continue;
            try
            {
                var hit = new CanvasPlane(item.Quad).Inverse(x, y);
                if (hit.X < -0.05 || hit.Y < -0.05 || hit.X > 1.05 || hit.Y > 1.05) continue;
            }
            catch (ArgumentException) { continue; }

            if (Dragging) Finish(false);
            double minX = item.Quad.Min(p => p[0]) * ActualWidth;
            double maxX = item.Quad.Max(p => p[0]) * ActualWidth;
            double minY = item.Quad.Min(p => p[1]) * ActualHeight;
            double maxY = item.Quad.Max(p => p[1]) * ActualHeight;
            var bounds = new Rect(minX, minY, Math.Max(120, maxX - minX), Math.Max(36, maxY - minY));
            Selection = item.Id;
            SubtitleInlineEditRequested?.Invoke(item.Id, bounds);
            e.Handled = true;
            return;
        }
    }
    internal bool BeginGesture(double x,double y)
    {
        if (Dragging) return false;
        foreach(var item in items.Reverse().OrderByDescending(i=>i.Id==Selection)) {
            int handle = item.Kind == "subtitle" ? -1 : Array.FindIndex(item.Quad,q=>Math.Abs(q[0]-x)*ActualWidth<=10&&Math.Abs(q[1]-y)*ActualHeight<=10);
            (double X,double Y) hit;
            try { hit=new CanvasPlane(item.Quad).Inverse(x,y); } catch(ArgumentException) { continue; }
            if(handle<0&&(hit.X<0||hit.Y<0||hit.X>1||hit.Y>1))continue;
            Selection=item.Id;Selected?.Invoke(item.Id);active=item;plane=new(item.Base);origin=plane.Inverse(x,y);corner=handle;
            Begin?.Invoke();Draw();return Dragging;
        }
        return false;
    }
    private void Move(object sender,PointerRoutedEventArgs e)
    {
        if(active is null||plane is null){
            var point=e.GetCurrentPoint(this).Position;double x=point.X/Math.Max(1,ActualWidth),y=point.Y/Math.Max(1,ActualHeight);ProtectedCursor=arrow;
            foreach(var item in items.Reverse()) {
                int handle = item.Kind == "subtitle" ? -1 : Array.FindIndex(item.Quad,q=>Math.Abs(q[0]-x)*ActualWidth<=10&&Math.Abs(q[1]-y)*ActualHeight<=10);
                if(handle>=0){ProtectedCursor=handle%2==0?diagonalA:diagonalB;break;}
                try {var hit=new CanvasPlane(item.Quad).Inverse(x,y);if(hit.X>=0&&hit.X<=1&&hit.Y>=0&&hit.Y<=1){ProtectedCursor=moveCursor;break;}}catch(ArgumentException){}
            }
            return;
        }
        var p=e.GetCurrentPoint(this).Position;MoveGesture(p.X/Math.Max(1,ActualWidth),p.Y/Math.Max(1,ActualHeight));e.Handled=true;
    }
    internal void MoveGesture(double x,double y)
    {
        if(active is null||plane is null)return;
        var at=plane.Inverse(x,y);
        var rect=CanvasPlane.Drag(active.Rect,at.X-origin.X,at.Y-origin.Y,corner,active.Rotation,active.Aspect);
        if(active.Kind=="camera"&&corner>=0) {
            double ratio=active.Rect.Height/Math.Max(0.01,active.Rect.Width);
            double wFromH=rect.Height/ratio;
            double candidateW=Math.Abs(rect.Width-active.Rect.Width)>=Math.Abs(wFromH-active.Rect.Width)?rect.Width:wFromH;
            double maxW=Math.Min(0.60,0.95/ratio);
            double w=Math.Clamp(candidateW,0.05,maxW);
            double h=w*ratio;
            double rx=corner is 0 or 3?active.Rect.X+active.Rect.Width-w:active.Rect.X;
            double ry=corner is 0 or 1?active.Rect.Y+active.Rect.Height-h:active.Rect.Y;
            rect=new NormalizedRect{X=Math.Clamp(rx,0,1-w),Y=Math.Clamp(ry,0,1-h),Width=w,Height=h};
        }
        if (active.Kind == "subtitle") {
            var snapped = SubtitleCanvasSnap.Snap(rect, CanvasAspect);
            rect = snapped.Rect;
            activeGuideX = snapped.SnappedCenterX ? snapped.GuideX : null;
            activeGuideY = snapped.SnappedSafeY ? snapped.GuideY : null;
        } else {
            activeGuideX = null;
            activeGuideY = null;
        }
        Changed?.Invoke(active.Id,rect);
        // The quad is projected from the same native attachment homography.
        Draw(plane.Quad(rect,active.Rotation,active.Aspect));
    }
    private void Draw(double[][]? dragged=null)
    {
        Children.Clear();foreach(var item in items) {
            var q=active?.Id==item.Id&&dragged is not null?dragged:item.Quad;bool selected=item.Id==Selection||item.Kind=="crop";
            bool isSubtitle = item.Kind == "subtitle";
            var strokeColor = isSubtitle
                ? (selected ? ColorHelper.FromArgb(255, 56, 189, 248) : ColorHelper.FromArgb(150, 255, 255, 255))
                : (selected ? Colors.White : ColorHelper.FromArgb(140, 255, 255, 255));
            var polygon=new Polygon{Stroke=new SolidColorBrush(strokeColor),StrokeThickness=selected?2:1,Fill=new SolidColorBrush(ColorHelper.FromArgb(isSubtitle && selected ? (byte)22 : (byte)14,255,255,255)),IsHitTestVisible=false};
            foreach(var p in q)polygon.Points.Add(new Point(p[0]*ActualWidth,p[1]*ActualHeight));Children.Add(polygon);
            if(selected && !isSubtitle)foreach(var p in q){var dot=new Ellipse{Width=12,Height=12,Fill=new SolidColorBrush(Colors.White),Stroke=new SolidColorBrush(ColorHelper.FromArgb(255,24,24,27)),StrokeThickness=2,IsHitTestVisible=false};SetLeft(dot,p[0]*ActualWidth-6);SetTop(dot,p[1]*ActualHeight-6);Children.Add(dot);}
        }
        if (activeGuideX is double gx && ActualWidth > 0 && ActualHeight > 0) {
            var guide = new Line {
                X1 = gx * ActualWidth, Y1 = 0,
                X2 = gx * ActualWidth, Y2 = ActualHeight,
                Stroke = new SolidColorBrush(ColorHelper.FromArgb(245, 250, 204, 21)),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 5, 3 },
                IsHitTestVisible = false
            };
            Children.Add(guide);
        }
        if (activeGuideY is double gy && ActualWidth > 0 && ActualHeight > 0) {
            var guide = new Line {
                X1 = 0, Y1 = gy * ActualHeight,
                X2 = ActualWidth, Y2 = gy * ActualHeight,
                Stroke = new SolidColorBrush(ColorHelper.FromArgb(245, 250, 204, 21)),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 5, 3 },
                IsHitTestVisible = false
            };
            Children.Add(guide);
        }
    }
}
