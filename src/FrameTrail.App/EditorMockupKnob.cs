using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace FrameTrail.App;

// DogSC EditorTransactionalKnob: 52 DIP dial, 270-degree ring, editable readout (MIT).
public sealed class EditorMockupKnob : Slider
{
    private Canvas? dial;
    private Path? track, fill;
    private Border? pointer;
    private TextBox? readout;
    private TextBlock? caption;
    private bool held, editing, updating;
    private double startValue, previousAngle;
    private readonly DispatcherTimer wheel = new() { Interval = TimeSpan.FromMilliseconds(180) };
    internal bool IsDragging => held || wheel.IsEnabled || editing;
    internal event Action<bool>? InteractionEnded;
    public string Label { get; set; } = "";
    public string Suffix { get; set; } = "×";
    public EditorMockupKnob()
    {
        Height = 124; MinWidth = 70; IsThumbToolTipEnabled = false; UseSystemFocusVisuals = true;
        Template = (ControlTemplate)XamlReader.Load("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Slider">
          <Grid RowSpacing="4"><Grid.RowDefinitions><RowDefinition Height="70"/><RowDefinition Height="24"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
            <Canvas x:Name="Dial" Width="72" Height="70" HorizontalAlignment="Center" Background="Transparent">
              <Path x:Name="Track" StrokeThickness="3.5" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
              <Path x:Name="Fill" Stroke="{StaticResource EditorBlue}" StrokeThickness="3.5" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
              <Ellipse Width="52" Height="52" Canvas.Left="10" Canvas.Top="9" Fill="{ThemeResource EditorRaised}" Stroke="{ThemeResource EditorLine}" StrokeThickness="0.75"/>
              <Border x:Name="Pointer" Width="3" Height="11.44" CornerRadius="1.5" Background="{StaticResource EditorBlue}" Canvas.Left="34.5" Canvas.Top="14" RenderTransformOrigin="0.5,1.8356643"/>
            </Canvas>
            <TextBox x:Name="Readout" Grid.Row="1" Width="76" Height="24" MinHeight="24" FontSize="12" Padding="2,0" BorderThickness="0" CornerRadius="8" TextAlignment="Center" HorizontalAlignment="Center" Background="Transparent" Foreground="{ThemeResource EditorInk}"/>
            <TextBlock x:Name="Caption" Grid.Row="2" FontSize="12" Foreground="{ThemeResource EditorSecondary}" TextAlignment="Center" TextWrapping="Wrap" HorizontalAlignment="Stretch"/>
          </Grid>
        </ControlTemplate>
        """);
        ValueChanged += (_, _) => Draw();
        ActualThemeChanged += (_, _) => Draw();
        wheel.Tick += (_, _) => { wheel.Stop(); InteractionEnded?.Invoke(true); };
        Unloaded += (_, _) => { if (held || wheel.IsEnabled) { held = false; wheel.Stop(); InteractionEnded?.Invoke(true); } };
    }
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate(); dial = GetTemplateChild("Dial") as Canvas; track = GetTemplateChild("Track") as Path; fill = GetTemplateChild("Fill") as Path;
        pointer = GetTemplateChild("Pointer") as Border; caption = GetTemplateChild("Caption") as TextBlock; readout = GetTemplateChild("Readout") as TextBox;
        if (readout is not null) {
            readout.GotFocus += (_, _) => { editing = true; startValue = Value; readout.Text = Value.ToString("0.##", CultureInfo.CurrentCulture); readout.SelectAll(); };
            readout.TextChanged += (_, _) => {
                if (!updating && editing && double.TryParse(readout.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value))
                    Value = Math.Clamp(value, Minimum, Maximum);
            };
            readout.LostFocus += (_, _) => { if (editing) { editing = false; Draw(); InteractionEnded?.Invoke(true); } };
            readout.KeyDown += (_, e) => {
                if (e.Key == VirtualKey.Enter) { editing = false; Draw(); Focus(FocusState.Programmatic); InteractionEnded?.Invoke(true); e.Handled = true; }
                else if (e.Key == VirtualKey.Escape) { editing = false; Value = startValue; Draw(); Focus(FocusState.Programmatic); InteractionEnded?.Invoke(false); e.Handled = true; }
            };
            ToolTipService.SetToolTip(readout, L.T("S7B16A6DAD97C"));
        }
        Draw();
    }
    private static PathGeometry Arc(double fraction)
    {
        Point At(double f) { double angle = (-135 + 270 * f) * Math.PI / 180; return new(36 + 32 * Math.Sin(angle), 35 - 32 * Math.Cos(angle)); }
        var figure = new PathFigure { StartPoint = At(0), IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment { Point = At(Math.Max(0.0001, fraction)), Size = new Size(32, 32), IsLargeArc = fraction > 2d / 3, SweepDirection = SweepDirection.Clockwise });
        var geometry = new PathGeometry(); geometry.Figures.Add(figure); return geometry;
    }
    private void Draw()
    {
        if (track is null || fill is null || pointer is null || readout is null || caption is null) return;
        double fraction = (Value - Minimum) / Math.Max(Maximum - Minimum, 0.0001);
        track.Data = Arc(1); track.Stroke = RecorderTheme.Brush(this, "EditorLine"); fill.Data = Arc(fraction); fill.Opacity = fraction > 0.004 ? 1 : 0;
        pointer.RenderTransform = new RotateTransform { Angle = -135 + 270 * fraction };
        caption.Text = Label;
        if (!editing) { updating = true; readout.Text = Value.ToString(Suffix == "×" ? "0.00" : "0", CultureInfo.CurrentCulture) + Suffix; updating = false; }
    }
    private static bool Shift => InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (!IsEnabled || dial is null || e.GetCurrentPoint(this).Position.Y > 70 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { base.OnPointerPressed(e); return; }
        if (editing) Focus(FocusState.Pointer);
        var point = e.GetCurrentPoint(dial).Position; startValue = Value; previousAngle = Math.Atan2(point.X - 36, 35 - point.Y);
        held = CapturePointer(e.Pointer); Focus(FocusState.Pointer); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (!held || dial is null) return;
        var point = e.GetCurrentPoint(dial).Position;
        if (Math.Abs(point.X - 36) + Math.Abs(point.Y - 35) < 8) return;
        double angle = Math.Atan2(point.X - 36, 35 - point.Y), delta = angle - previousAngle;
        while (delta > Math.PI) delta -= Math.PI * 2; while (delta < -Math.PI) delta += Math.PI * 2;
        previousAngle = angle; Value = Math.Clamp(Value + delta / (Math.PI * 1.5) * (Maximum - Minimum) * (Shift ? 0.15 : 1), Minimum, Maximum); e.Handled = true;
    }
    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (!held) return; held = false; ReleasePointerCapture(e.Pointer); InteractionEnded?.Invoke(true); e.Handled = true;
    }
    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        if (held) { held = false; InteractionEnded?.Invoke(true); }
    }
    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        if (!IsEnabled) return; wheel.Start(); Value = Math.Clamp(Value + Math.Sign(e.GetCurrentPoint(this).Properties.MouseWheelDelta) * StepFrequency * (Shift ? 0.1 : 1), Minimum, Maximum);
        wheel.Stop(); wheel.Start(); e.Handled = true;
    }
    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (editing) return;
        if (e.Key == VirtualKey.Escape && held) { held = false; ReleasePointerCaptures(); Value = startValue; InteractionEnded?.Invoke(false); e.Handled = true; return; }
        if (e.Key is VirtualKey.Left or VirtualKey.Down or VirtualKey.Right or VirtualKey.Up or VirtualKey.Home or VirtualKey.End) {
            Value = e.Key == VirtualKey.Home ? Minimum : e.Key == VirtualKey.End ? Maximum : Math.Clamp(Value + (e.Key is VirtualKey.Right or VirtualKey.Up ? 1 : -1) * StepFrequency * (Shift ? 10 : 1), Minimum, Maximum);
            InteractionEnded?.Invoke(true); e.Handled = true; return;
        }
        base.OnKeyDown(e);
    }
}
