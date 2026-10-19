using System.Diagnostics;
using FrameTrail.Domain;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;

namespace FrameTrail.App;

// Port of DogSC EditorMockupTurntable / EditorMockupCard (MIT).
public sealed class EditorMockupTurntable : UserControl
{
    private const double CardWidth = 142, PointsPerCard = 78;
    private readonly Canvas ring = new() { Height = 118, Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Grid stage = new() { Height = 118 };
    private readonly TextBlock name = new() { FontSize = 14.5, Height = 20, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel dots = new() { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly List<(Button Button, Border Surface, Image Image, Image Glow, Border Shade)> cards = new();
    private readonly List<Button> arrows = new();
    private readonly DispatcherTimer animation = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch clock = new();
    private string selected = "none";
    private double rotation, origin, target, pointerX, pointerRotation, pointerTime, velocity, lastPointerX;
    private bool held, moved;
    internal event Action<string>? SelectionRequested;
    internal string SelectedStyle => selected;
    internal double RingRotation => rotation;
    public EditorMockupTurntable()
    {
        IsTabStop = true; HorizontalContentAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(this, L.T("SB26B975CA6A5")); AutomationProperties.SetAutomationId(this, "editor.frame.turntable");
        name.FontFamily = (FontFamily)Application.Current.Resources["RecorderSemiboldFont"];
        var content = new StackPanel { Spacing = 10 };
        stage.Children.Add(ring);
        foreach (var style in FrameStyles.All) {
            var entry = CreateCard(style); cards.Add(entry); ring.Children.Add(entry.Button);
            dots.Children.Add(new Border { Height = 5, Width = 5, CornerRadius = new CornerRadius(2.5) });
        }
        var arrows = new Grid { Margin = new Thickness(2, 0, 2, 0), IsHitTestVisible = true };
        arrows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); arrows.ColumnDefinitions.Add(new ColumnDefinition()); arrows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = Arrow(L.T("S765F08441443"), -1); var right = Arrow(L.T("S79D7AA580A2E"), 1); Grid.SetColumn(right, 2); arrows.Children.Add(left); arrows.Children.Add(right); stage.Children.Add(arrows);
        // Only the buttons intercept input; empty space still reaches the ring.
        arrows.Background = null;
        content.Children.Add(stage);
        var legend = new StackPanel { Spacing = 8 }; legend.Children.Add(name); legend.Children.Add(dots); content.Children.Add(legend); Content = content;
        stage.SizeChanged += (_, _) => Draw(); ActualThemeChanged += (_, _) => Draw();
        stage.AddHandler(PointerPressedEvent, new PointerEventHandler(BeginDrag), true);
        stage.AddHandler(PointerMovedEvent, new PointerEventHandler(Drag), true);
        stage.AddHandler(PointerReleasedEvent, new PointerEventHandler(EndDrag), true);
        stage.AddHandler(PointerCanceledEvent, new PointerEventHandler((_, _) => { if (held) { held = false; Settle(Math.Round(rotation), false); } }), true);
        animation.Tick += (_, _) => {
            double t = clock.Elapsed.TotalSeconds, omega = Math.PI * 2 / 0.62, damping = 0.74, frequency = omega * Math.Sqrt(1 - damping * damping);
            rotation = target + (origin - target) * Math.Exp(-damping * omega * t) * (Math.Cos(frequency * t) + damping * omega / frequency * Math.Sin(frequency * t));
            if (t > 1 || Math.Abs(rotation - target) < 0.0005 && t > 0.3) { rotation = target; animation.Stop(); }
            Draw();
        };
        Unloaded += (_, _) => animation.Stop(); Loaded += (_, _) => Draw();
    }
    internal void SetPoster(ImageSource? image) { foreach (var card in cards) card.Image.Source = image; }
    internal void SetSelected(string value)
    {
        value = FrameStyles.Normalize(value); if (!FrameStyles.All.Contains(value)) value = "none";
        selected = value; var index = FrameStyles.All.ToList().IndexOf(value);
        if (!held && Math.Abs(Wrapped(index - rotation)) > 0.01) Settle(rotation + Wrapped(index - rotation), false);
        Draw();
    }
    internal void Step(int direction) => Settle(Math.Round(animation.IsEnabled ? target : rotation) + direction, true);
    private static double Wrapped(double value) => value - FrameStyles.All.Count * Math.Round(value / FrameStyles.All.Count);
    private void Settle(double value, bool notify)
    {
        animation.Stop(); origin = rotation; target = value;
        int index = ((int)Math.Round(value) % FrameStyles.All.Count + FrameStyles.All.Count) % FrameStyles.All.Count;
        if (notify && selected != FrameStyles.All[index]) { selected = FrameStyles.All[index]; SelectionRequested?.Invoke(selected); }
        if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled && Math.Abs(origin - target) > 0.001) { clock.Restart(); animation.Start(); }
        else rotation = target;
        Draw();
    }
    private Button Arrow(string label, int direction)
    {
        var chevron = new Polyline { Points = direction < 0 ? new PointCollection { new(8.5, 2), new(3.5, 7), new(8.5, 12) } : new PointCollection { new(3.5, 2), new(8.5, 7), new(3.5, 12) }, StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round, Width = 13, Height = 14 };
        var button = new Button { Style = (Style)Application.Current.Resources["EditorButton"], Width = 30, Height = 30, Padding = new Thickness(0), CornerRadius = new CornerRadius(15), VerticalAlignment = VerticalAlignment.Center,
            Content = chevron, Tag = "arrow" };
        arrows.Add(button);
        AutomationProperties.SetName(button, label); AutomationProperties.SetAutomationId(button, direction < 0 ? "editor.frame.previous" : "editor.frame.next");
        button.Click += (_, _) => Step(direction); return button;
    }
    private (Button Button, Border Surface, Image Image, Image Glow, Border Shade) CreateCard(string style)
    {
        bool dark = style.EndsWith("Dark", StringComparison.Ordinal), device = style == "devicePhone", browser = FrameStyles.IsBrowser(style);
        var poster = new Image { Stretch = Stretch.UniformToFill };
        var screen = new Border { Background = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1), GradientStops = { new GradientStop { Color = Color(0x8C9EC7), Offset = 0 }, new GradientStop { Color = Color(0xC7A8BD), Offset = 1 } } }, Child = poster };
        FrameworkElement frame;
        if (style == "none") { screen.Width = CardWidth * 0.66; screen.Height = screen.Width / 1.6; screen.CornerRadius = new CornerRadius(5); frame = screen; }
        else if (device) {
            var width = CardWidth * 0.62; var bezel = Math.Max(width * 0.045, 3); screen.Height = width / 1.6; screen.CornerRadius = new CornerRadius(4);
            frame = new Border { Width = width, Padding = new Thickness(bezel), CornerRadius = new CornerRadius(bezel * 3.2), BorderThickness = new Thickness(0.8),
                BorderBrush = Gradient(0xB8B8B8, 0x525252), Background = Gradient(0x292929, 0x141414), Child = screen };
        } else {
            double width = CardWidth * 0.7, bar = Math.Max(width * 0.075, 9);
            var header = new Grid { Height = bar, Background = new SolidColorBrush(Color(dark ? 0x2B2B2B : 0xF6F6F6)), Padding = new Thickness(bar * 0.45, 0, bar * 0.45, 0) };
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var colour in new[] { 0xFF5F57, 0xFFBD2E, 0x28C840 }) controls.Children.Add(new Ellipse { Width = bar * 0.38, Height = bar * 0.38, Fill = new SolidColorBrush(Color(colour)) });
            header.Children.Add(controls);
            if (browser) header.Children.Add(new Border { Width = width * 0.42, Height = bar * 0.5, CornerRadius = new CornerRadius(bar * 0.25), HorizontalAlignment = HorizontalAlignment.Center, Background = new SolidColorBrush(Color(dark ? 0x444444 : 0xE7E7E7)), Margin = new Thickness(12, 0, 0, 0) });
            var shell = new StackPanel(); shell.Children.Add(header); screen.Height = width / 1.6; shell.Children.Add(screen);
            frame = new Border { Width = width, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(0.5), BorderBrush = new SolidColorBrush(Color(dark ? 0x555555 : 0xCCCCCC)), Child = shell };
        }
        frame.HorizontalAlignment = HorizontalAlignment.Center; frame.VerticalAlignment = VerticalAlignment.Center;
        var plate = new Grid();
        var glow = new Image { Source = new SvgImageSource(new Uri("ms-appx:///Assets/MockupStudioGlow.svg")), Stretch = Stretch.Fill, IsHitTestVisible = false };
        plate.Children.Add(glow);
        var shadowHost = new Grid { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        plate.Children.Add(shadowHost); plate.Children.Add(frame);
        frame.Loaded += (_, _) => {
            var compositor = ElementCompositionPreview.GetElementVisual(frame).Compositor;
            var shadow = compositor.CreateDropShadow(); shadow.Color = Colors.Black; shadow.Opacity = style == "none" ? 0.2f : device ? 0.25f : 0.22f;
            shadow.BlurRadius = style == "none" ? 8 : 9; shadow.Offset = new System.Numerics.Vector3(0, 4, 0);
            var sprite = compositor.CreateSpriteVisual(); sprite.Size = new System.Numerics.Vector2((float)frame.ActualWidth, (float)frame.ActualHeight); sprite.Shadow = shadow;
            shadowHost.Width = frame.ActualWidth; shadowHost.Height = frame.ActualHeight; ElementCompositionPreview.SetElementChildVisual(shadowHost, sprite);
        };
        frame.RenderTransformOrigin = new Point(0.5, 0.5);
        var shade = new Border { Background = new SolidColorBrush(Colors.Black), IsHitTestVisible = false, CornerRadius = new CornerRadius(12) }; plate.Children.Add(shade);
        var surface = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(0.5), Child = plate };
        var button = new Button { Style = (Style)Application.Current.Resources["EditorButton"], Width = CardWidth, Height = CardWidth / 1.45, Padding = new Thickness(0), CornerRadius = new CornerRadius(14), Content = surface,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, RenderTransformOrigin = new Point(0.5, 0.5), IsTabStop = false, Tag = style };
        AutomationProperties.SetName(button, FrameStyles.Name(style)); AutomationProperties.SetAutomationId(button, "editor.frame." + style);
        button.Click += (_, _) => Settle(rotation + Wrapped(FrameStyles.All.ToList().IndexOf(style) - rotation), true);
        button.PointerEntered += (_, _) => { if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) frame.RenderTransform = new ScaleTransform { ScaleX = 1.035, ScaleY = 1.035 }; };
        button.PointerExited += (_, _) => frame.RenderTransform = null;
        ToolTipService.SetToolTip(button, L.T("S5968C228E6BC"));
        return (button, surface, poster, glow, shade);
    }
    private static Windows.UI.Color Color(int colour) => Windows.UI.Color.FromArgb(255, (byte)(colour >> 16), (byte)(colour >> 8), (byte)colour);
    private static LinearGradientBrush Gradient(int top, int bottom) => new() { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1), GradientStops = { new GradientStop { Color = Color(top), Offset = 0 }, new GradientStop { Color = Color(bottom), Offset = 1 } } };
    private void Draw()
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        foreach (var arrow in arrows) {
            arrow.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(18, dark ? (byte)255 : (byte)0, dark ? (byte)255 : (byte)0, dark ? (byte)255 : (byte)0));
            ((Polyline)arrow.Content).Stroke = RecorderTheme.Brush(this, "EditorSecondary");
        }
        for (int i = 0; i < cards.Count; i++) {
            var card = cards[i]; double offset = Wrapped(i - rotation), angle = offset * 360 / cards.Count, radians = angle * Math.PI / 180, facing = Math.Cos(radians), scale = 0.72 + 0.28 * Math.Max(facing, 0);
            Canvas.SetLeft(card.Button, (stage.ActualWidth - CardWidth) / 2 + Math.Sin(radians) * CardWidth * 0.98 * 0.78); Canvas.SetTop(card.Button, (118 - CardWidth / 1.45) / 2);
            Canvas.SetZIndex(card.Button, (int)(facing * 1000)); card.Button.RenderTransform = new ScaleTransform { ScaleX = scale, ScaleY = scale };
            card.Button.Projection = new PlaneProjection { RotationY = -angle * 0.8 };
            card.Button.Opacity = Math.Clamp(facing * 1.25 + 0.05, 0, 1); card.Button.IsHitTestVisible = facing > 0.2;
            bool front = Math.Abs(offset) < 0.5 && FrameStyles.All[i] == selected;
            card.Surface.BorderBrush = front ? (Brush)Application.Current.Resources["EditorBlue"] : RecorderTheme.Brush(this, "EditorLine");
            card.Surface.BorderThickness = new Thickness(front ? 2 : 0.5); card.Surface.Background = Gradient(dark ? 0x222428 : 0xF1F2F5, dark ? 0x17181B : 0xE2E4E8);
            card.Glow.Opacity = dark ? 0.06 : 0.8; card.Shade.Opacity = 0.25 * (1 - Math.Max(facing, 0));
            var dot = (Border)dots.Children[i]; dot.Width = FrameStyles.All[i] == selected ? 18 : 5; dot.Background = FrameStyles.All[i] == selected ? (Brush)Application.Current.Resources["EditorBlue"] : RecorderTheme.Brush(this, "EditorLine");
        }
        name.Text = FrameStyles.Name(selected); name.Foreground = RecorderTheme.Brush(this, "EditorInk");
    }
    private void BeginDrag(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(stage).Properties.IsLeftButtonPressed || e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Pen) return;
        for (var node = e.OriginalSource as DependencyObject; node is not null && node != stage; node = VisualTreeHelper.GetParent(node)) if (node is Button { Tag: "arrow" }) return;
        animation.Stop(); pointerX = lastPointerX = e.GetCurrentPoint(stage).Position.X; pointerRotation = rotation; pointerTime = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        velocity = 0; moved = false; held = stage.CapturePointer(e.Pointer); Focus(FocusState.Pointer);
        e.Handled = held;
    }
    private void Drag(object sender, PointerRoutedEventArgs e)
    {
        if (!held) return; double x = e.GetCurrentPoint(stage).Position.X, now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (Math.Abs(x - pointerX) > 3) moved = true;
        if (now - pointerTime > 0.008) { velocity = (x - lastPointerX) / (now - pointerTime); lastPointerX = x; pointerTime = now; }
        if (moved) { rotation = pointerRotation - (x - pointerX) / PointsPerCard; Draw(); e.Handled = true; }
    }
    private void EndDrag(object sender, PointerRoutedEventArgs e)
    {
        if (!held) return; held = false; stage.ReleasePointerCapture(e.Pointer);
        if (moved) { Settle(Math.Round(rotation - Math.Clamp(velocity * 0.18 * 0.55 / PointsPerCard, -3, 3)), true); e.Handled = true; }
        else { var x = e.GetCurrentPoint(stage).Position.X; int step = Math.Abs(x - stage.ActualWidth / 2) < CardWidth * 0.32 ? 0 : x < stage.ActualWidth / 2 ? -1 : 1; if (step != 0) Step(step); }
    }
    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Left or VirtualKey.Right) { Step(e.Key == VirtualKey.Left ? -1 : 1); e.Handled = true; return; }
        base.OnKeyDown(e);
    }
}
