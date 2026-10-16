using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace FrameTrail.App;

/// <summary>One 18-unit pictogram family, matching the reference recorder's grid.</summary>
public sealed class RecorderGlyph : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(RecorderGlyph), new PropertyMetadata("Display", Changed));
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(nameof(IsOn), typeof(bool), typeof(RecorderGlyph), new PropertyMetadata(true, Changed));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public bool IsOn { get => (bool)GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }
    public RecorderGlyph() { Width = Height = 18; IsHitTestVisible = false; Loaded += (_, _) => Draw(); ActualThemeChanged += (_, _) => Draw(); }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs _) => ((RecorderGlyph)sender).Draw();
    private void Draw()
    {
        var (data, filled) = Kind switch
        {
            "Phone" => ("M5,1 L13,1 Q15,1 15,3 L15,15 Q15,17 13,17 L5,17 Q3,17 3,15 L3,3 Q3,1 5,1 M7,3 L11,3 M8,15 L10,15", false),
            "Window" => ("M4,3 L14,3 Q16.5,3 16.5,5.5 L16.5,12.5 Q16.5,15 14,15 L4,15 Q1.5,15 1.5,12.5 L1.5,5.5 Q1.5,3 4,3 M4.6,6.2 L7.4,6.2", false),
            "Region" => ("M2,6.4 L2,4.4 Q2,2 4.4,2 L6.4,2 M11.6,2 L13.6,2 Q16,2 16,4.4 L16,6.4 M16,11.6 L16,13.6 Q16,16 13.6,16 L11.6,16 M6.4,16 L4.4,16 Q2,16 2,13.6 L2,11.6", false),
            "Microphone" => ("M9,1 Q6,1 6,4 L6,9 Q6,12 9,12 Q12,12 12,9 L12,4 Q12,1 9,1 M4,8 L4,9 Q4,14 9,14 Q14,14 14,9 L14,8 M9,14 L9,17 M6,17 L12,17", false),
            "Camera" => ("M3,4 L10,4 Q12,4 12,6 L12,12 Q12,14 10,14 L3,14 Q1,14 1,12 L1,6 Q1,4 3,4 M13,7 L17,4.5 L17,13.5 L13,11 Z", true),
            "Speaker" => ("M1.5,7 L5,7 L9.5,3 L9.5,15 L5,11 L1.5,11 Z M12,6 Q15,9 12,12 M14.5,3.5 Q20,9 14.5,14.5", false),
            "More" => ("M3,8 A1,1 0 1 0 3,10 A1,1 0 1 0 3,8 M9,8 A1,1 0 1 0 9,10 A1,1 0 1 0 9,8 M15,8 A1,1 0 1 0 15,10 A1,1 0 1 0 15,8", true),
            "Marker" => ("M5,2 L13,2 L13,16 L9,12 L5,16 Z", true),
            "Memo" => ("M3,4 L15,4 M3,7.5 L15,7.5 M3,11 L15,11 M3,14.5 L11,14.5", false),
            "Pause" => ("M4,3 L7,3 L7,15 L4,15 Z M11,3 L14,3 L14,15 L11,15 Z", true),
            "Play" => ("M5,2.5 L15,9 L5,15.5 Z", true),
            "Folder" => ("M2,4 Q2,2.5 3.5,2.5 L7,2.5 L9,5 L15,5 Q17,5 17,7 L17,13 Q17,15 15,15 L3,15 Q1,15 1,13 L1,6 Q1,4 2,4 Z", false),
            "Close" => ("M4,4 L14,14 M14,4 L4,14", false),
            "Arrow" => ("M3,9 L15,9 M10,4 L15,9 L10,14", false),
            "Check" => ("M3,9 L7,13 L15,5", false),
            "Power" => ("M9,1.5 L9,9 M5,3.5 A6.5,6.5 0 1 0 13,3.5", false),
            "Grip" => ("M6.5,3 A1,1 0 1 0 6.5,5 A1,1 0 1 0 6.5,3 M11.5,3 A1,1 0 1 0 11.5,5 A1,1 0 1 0 11.5,3 M6.5,8 A1,1 0 1 0 6.5,10 A1,1 0 1 0 6.5,8 M11.5,8 A1,1 0 1 0 11.5,10 A1,1 0 1 0 11.5,8 M6.5,13 A1,1 0 1 0 6.5,15 A1,1 0 1 0 6.5,13 M11.5,13 A1,1 0 1 0 11.5,15 A1,1 0 1 0 11.5,13", true),
            _ => ("M4,2.5 L14,2.5 Q16.5,2.5 16.5,5 L16.5,10 Q16.5,12.5 14,12.5 L4,12.5 Q1.5,12.5 1.5,10 L1.5,5 Q1.5,2.5 4,2.5 M6,15.6 L12,15.6", false)
        };
        Brush ink = new SolidColorBrush(ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black) { Opacity = IsOn ? 0.96 : 0.36 };
        var path = (Microsoft.UI.Xaml.Shapes.Path)XamlReader.Load($"<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='{data}' StrokeThickness='1.6' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round' />");
        if (filled) path.Fill = ink; else path.Stroke = ink;
        var canvas = new Canvas { Width = 18, Height = 18 }; canvas.Children.Add(path);
        if (!IsOn)
        {
            canvas.Children.Add(new Line { X1 = 2, Y1 = 2, X2 = 16, Y2 = 16, Stroke = new SolidColorBrush(ActualTheme == ElementTheme.Dark ? Windows.UI.Color.FromArgb(255, 16, 17, 19) : Windows.UI.Color.FromArgb(255, 253, 253, 254)), StrokeThickness = 4 });
            canvas.Children.Add(new Line { X1 = 2, Y1 = 2, X2 = 16, Y2 = 16, Stroke = ink, StrokeThickness = 1.5 });
        }
        Content = new Viewbox { Child = canvas };
    }
}
