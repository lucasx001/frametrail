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
    internal string? Selection { get; set; }
    internal bool Dragging => active is not null;
    internal event Action<string>? Selected;
    internal event Action? Begin;
    internal event Action<string,NormalizedRect>? Changed;
    internal event Action<bool>? Ended;
    private readonly InputSystemCursor arrow=InputSystemCursor.Create(InputSystemCursorShape.Arrow), moveCursor=InputSystemCursor.Create(InputSystemCursorShape.SizeAll), diagonalA=InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast), diagonalB=InputSystemCursor.Create(InputSystemCursorShape.SizeNortheastSouthwest);
    internal EditorCanvasOverlay()
    {
        Background = new SolidColorBrush(Colors.Transparent);
        PointerPressed += Press; PointerMoved += Move; PointerReleased += (_, e) => { if(Dragging){Finish(true);ReleasePointerCapture(e.Pointer);e.Handled=true;} };
        PointerCaptureLost += (_,_) => Finish(false); PointerCanceled += (_,_)=>Finish(false);
        SizeChanged += (_,_)=>Draw();
    }
    internal void SetItems(IReadOnlyList<CanvasEditItem> value) { if(Dragging)return;items=value;IsHitTestVisible=value.Count>0;Draw(); }
    internal void Finish(bool commit) { if(active is null)return;active=null;plane=null;Ended?.Invoke(commit);ReleasePointerCaptures(); }
    private void Press(object sender,PointerRoutedEventArgs e)
    {
        if(!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Dragging)return;
        double x=e.GetCurrentPoint(this).Position.X/Math.Max(1,ActualWidth),y=e.GetCurrentPoint(this).Position.Y/Math.Max(1,ActualHeight);
        if (BeginGesture(x,y)) { CapturePointer(e.Pointer);e.Handled=true; }
    }
    internal bool BeginGesture(double x,double y)
    {
        if (Dragging) return false;
        foreach(var item in items.Reverse().OrderByDescending(i=>i.Id==Selection)) {
            int handle=Array.FindIndex(item.Quad,q=>Math.Abs(q[0]-x)*ActualWidth<=10&&Math.Abs(q[1]-y)*ActualHeight<=10);
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
                int handle=Array.FindIndex(item.Quad,q=>Math.Abs(q[0]-x)*ActualWidth<=10&&Math.Abs(q[1]-y)*ActualHeight<=10);
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
        Changed?.Invoke(active.Id,rect);
        // The quad is projected from the same native attachment homography.
        Draw(plane.Quad(rect,active.Rotation,active.Aspect));
    }
    private void Draw(double[][]? dragged=null)
    {
        Children.Clear();foreach(var item in items) {
            var q=active?.Id==item.Id&&dragged is not null?dragged:item.Quad;bool selected=item.Id==Selection||item.Kind=="crop";
            var polygon=new Polygon{Stroke=new SolidColorBrush(selected?ColorHelper.FromArgb(255,90,174,255):ColorHelper.FromArgb(160,255,255,255)),StrokeThickness=selected?2:1,Fill=new SolidColorBrush(ColorHelper.FromArgb(8,90,174,255)),IsHitTestVisible=false};
            foreach(var p in q)polygon.Points.Add(new Point(p[0]*ActualWidth,p[1]*ActualHeight));Children.Add(polygon);
            if(selected)foreach(var p in q){var dot=new Ellipse{Width=12,Height=12,Fill=new SolidColorBrush(Colors.White),Stroke=new SolidColorBrush(ColorHelper.FromArgb(255,90,174,255)),StrokeThickness=2,IsHitTestVisible=false};SetLeft(dot,p[0]*ActualWidth-6);SetTop(dot,p[1]*ActualHeight-6);Children.Add(dot);}
        }
    }
}
