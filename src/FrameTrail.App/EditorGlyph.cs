using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Shapes;

namespace FrameTrail.App;

// DogSC AppLineIcon's 18 point drawing grid. Keep editor icons in the same family as the recorder.
public sealed class EditorGlyph : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(EditorGlyph), new PropertyMetadata("Scene", Changed));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public EditorGlyph() { Width = Height = 16; IsHitTestVisible = false; Draw(); }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs _) => ((EditorGlyph)sender).Draw();
    private void Draw()
    {
        string data = Kind switch {
            "Scene" => "M4,3 L14,3 Q16,3 16,5 L16,13 Q16,15 14,15 L4,15 Q2,15 2,13 L2,5 Q2,3 4,3 M4,12 L7,8.5 L10,11 L12,8.5 L14,11 M13.3,6.6 A1.1,1.1 0 1 0 11.1,6.6 A1.1,1.1 0 1 0 13.3,6.6",
            "Opening" => "M4.5,3 L13.5,3 Q16,3 16,5.5 L16,12.5 Q16,15 13.5,15 L4.5,15 Q2,15 2,12.5 L2,5.5 Q2,3 4.5,3 M7.6,6.6 L11.8,9 L7.6,11.4 Z",
            "Zoom" => "M2.5,6.1 L2.5,4.1 Q2.5,2.5 4.1,2.5 L6.1,2.5 M11.9,2.5 L13.9,2.5 Q15.5,2.5 15.5,4.1 L15.5,6.1 M15.5,11.9 L15.5,13.9 Q15.5,15.5 13.9,15.5 L11.9,15.5 M6.1,15.5 L4.1,15.5 Q2.5,15.5 2.5,13.9 L2.5,11.9 M11.4,9 A2.4,2.4 0 1 0 6.6,9 A2.4,2.4 0 1 0 11.4,9",
            "Cursor" => "M7,2.5 L15.5,8.2 L11.9,8.7 L13.8,13 L11.6,14 L9.8,9.8 L7,12.4 Z M1.8,6.5 L4,6.5 M2.6,10 L4.4,10 M1.8,13.5 L5,13.5",
            "Camera" => "M3.5,4.5 L10,4.5 Q12,4.5 12,6.5 L12,11.5 Q12,13.5 10,13.5 L3.5,13.5 Q1.5,13.5 1.5,11.5 L1.5,6.5 Q1.5,4.5 3.5,4.5 M12,7 L16.5,4.5 L16.5,13.5 L12,11",
            "Speaker" => "M2,6 L5,6 L9,2.7 L9,15.3 L5,12 L2,12 Z M12,6 Q14.5,9 12,12 M14.5,3.5 Q18.5,9 14.5,14.5",
            "Folder" => "M2,6 L2,4 Q2,2.5 4,2.5 L7,2.5 L9,4.5 L14,4.5 Q16,4.5 16,6.5 L16,13.5 Q16,15.5 14,15.5 L4,15.5 Q2,15.5 2,13.5 Z M2,6 L12,6",
            "Undo" => "M7,3.8 L3.8,7 L7,10.2 M3.8,7 L11,7 C15.8,7 15.8,14.2 11,14.2 L6.5,14.2",
            "Redo" => "M11,3.8 L14.2,7 L11,10.2 M14.2,7 L7,7 C2.2,7 2.2,14.2 7,14.2 L11.5,14.2",
            "Share" => "M9,11 L9,2 M5.5,5.5 L9,2 L12.5,5.5 M5.5,8.5 L4.5,8.5 Q3,8.5 3,10 L3,14 Q3,15.5 4.5,15.5 L13.5,15.5 Q15,15.5 15,14 L15,10 Q15,8.5 13.5,8.5 L12.5,8.5",
            "Layers" => "M9,2.5 L16,6.5 L9,10.5 L2,6.5 Z M2,10.5 L9,14.5 L16,10.5",
            "Play" => "M5.5,3.5 L14,9 L5.5,14.5 Z", "Pause" => "M6,3.5 L6,14.5 M12,3.5 L12,14.5",
            "Previous" => "M3.5,3.5 L3.5,14.5 M14,3.5 L6,9 L14,14.5 Z", "Next" => "M14.5,3.5 L14.5,14.5 M4,3.5 L12,9 L4,14.5 Z",
            "Scissors" => "M6.8,4.5 A2.3,2.3 0 1 0 2.2,4.5 A2.3,2.3 0 1 0 6.8,4.5 M6.8,13.5 A2.3,2.3 0 1 0 2.2,13.5 A2.3,2.3 0 1 0 6.8,13.5 M6.3,6 L15,14.5 M6.3,12 L15,3.5",
            "Trash" => "M2.5,4.5 L15.5,4.5 M6.8,4.5 L7.4,2.2 L10.6,2.2 L11.2,4.5 M4,4.5 L4.9,14.4 L6.4,15.8 L11.6,15.8 L13.1,14.4 L14,4.5 M7.4,7.6 L7.6,12.6 M10.6,7.6 L10.4,12.6",
            "Waveform" => "M2,7.5 L2,10.5 M5.5,5 L5.5,13 M9,2.5 L9,15.5 M12.5,5.5 L12.5,12.5 M16,7.5 L16,10.5",
            "Film" => "M4,2.5 L14,2.5 Q16,2.5 16,4.5 L16,13.5 Q16,15.5 14,15.5 L4,15.5 Q2,15.5 2,13.5 L2,4.5 Q2,2.5 4,2.5 M5.5,2.5 L5.5,15.5 M12.5,2.5 L12.5,15.5 M2,6.5 L5.5,6.5 M2,11.5 L5.5,11.5 M12.5,6.5 L16,6.5 M12.5,11.5 L16,11.5",
            "Crop" => "M5,2 L5,13 L16,13 M2,5 L13,5 L13,16", "Layout" => "M4,3 L14,3 Q16,3 16,5 L16,13 Q16,15 14,15 L4,15 Q2,15 2,13 L2,5 Q2,3 4,3 M6,6 L12,6 Q13,6 13,7 L13,11 Q13,12 12,12 L6,12 Q5,12 5,11 L5,7 Q5,6 6,6",
            "Check" => "M5,9 L8,12 L13,6", "Chevron" => "M4,6.5 L9,11.5 L14,6.5", "Close" => "M4,4 L14,14 M14,4 L4,14",
            "Plus" => "M9,3.5 L9,14.5 M3.5,9 L14.5,9", "Minus" => "M3.5,9 L14.5,9",
            "Aspect" => "M4,3.5 L14,3.5 Q16.5,3.5 16.5,6 L16.5,12 Q16.5,14.5 14,14.5 L4,14.5 Q1.5,14.5 1.5,12 L1.5,6 Q1.5,3.5 4,3.5 M4.5,8.5 L4.5,6.5 L6.5,6.5 M13.5,9.5 L13.5,11.5 L11.5,11.5",
            "Sliders" => "M2,4 L6,4 M10,4 L16,4 M2,9 L10,9 M14,9 L16,9 M2,14 L4,14 M8,14 L16,14 M6,2 L6,6 L10,6 L10,2 Z M10,7 L10,11 L14,11 L14,7 Z M4,12 L4,16 L8,16 L8,12 Z",
            "Magnet" => "M3.5,2.5 L3.5,9 C3.5,17 14.5,17 14.5,9 L14.5,2.5 L11,2.5 L11,9 C11,12.2 7,12.2 7,9 L7,2.5 Z M3.5,5.5 L7,5.5 M11,5.5 L14.5,5.5",
            "Eye" => "M1.5,9 C5,2 13,2 16.5,9 C13,16 5,16 1.5,9 M11.3,9 A2.3,2.3 0 1 0 6.7,9 A2.3,2.3 0 1 0 11.3,9",
            "Fit" => "M2.5,3 L2.5,15 M15.5,3 L15.5,15 M5.5,6 L8.5,9 L5.5,12 M12.5,6 L9.5,9 L12.5,12",
            "Sun" => "M12.2,9 A3.2,3.2 0 1 0 5.8,9 A3.2,3.2 0 1 0 12.2,9 M9,1.6 L9,3.5 M9,14.5 L9,16.4 M1.6,9 L3.5,9 M14.5,9 L16.4,9 M3.8,3.8 L5.1,5.1 M12.9,12.9 L14.2,14.2 M3.8,14.2 L5.1,12.9 M12.9,5.1 L14.2,3.8",
            "Settings" => "M7,2 L11,2 L11.6,4.3 L13,5.1 L15.2,4.5 L17,7.5 L15.4,9 L15.4,10.4 L17,12 L15.2,15 L13,14.4 L11.6,15.2 L11,17 L7,17 L6.4,15.2 L5,14.4 L2.8,15 L1,12 L2.6,10.4 L2.6,9 L1,7.5 L2.8,4.5 L5,5.1 L6.4,4.3 Z M12,9.5 A3,3 0 1 0 6,9.5 A3,3 0 1 0 12,9.5",
            "Refresh" => "M15,5 L15,1.5 M15,5 L11.5,5 M15,5 A6.5,6.5 0 1 0 15.5,12",
            _ => "M3,3 L15,3 L15,15 L3,15 Z"
        };
        var path = (Microsoft.UI.Xaml.Shapes.Path)XamlReader.Load($"<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='{data}' StrokeThickness='1.45' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round' />");
        path.SetBinding(Shape.StrokeProperty, new Binding { Source = this, Path = new PropertyPath(nameof(Foreground)) });
        var canvas = new Canvas { Width = 18, Height = 18 }; canvas.Children.Add(path); Content = new Viewbox { Child = canvas };
    }
}
