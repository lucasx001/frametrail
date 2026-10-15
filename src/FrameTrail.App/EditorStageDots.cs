using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace FrameTrail.App;

public sealed class EditorStageDots : Canvas
{
    public EditorStageDots() { IsHitTestVisible = false; SizeChanged += (_, _) => Draw(); ActualThemeChanged += (_, _) => Draw(); }
    private void Draw()
    {
        Children.Clear(); var ink = RecorderTheme.Brush(this, "EditorDot");
        for (double y = 10; y < ActualHeight; y += 18) for (double x = 10; x < ActualWidth; x += 18) {
            var dot = new Ellipse { Width = 1.1, Height = 1.1, Fill = ink }; SetLeft(dot, x); SetTop(dot, y); Children.Add(dot);
        }
    }
}
