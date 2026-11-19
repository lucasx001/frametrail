using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Input;
using Windows.System;

namespace FrameTrail.App;

public sealed class NoWheelSlider : Slider
{
    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        // Do not adjust slider values on mouse wheel hover.
    }
}

// shadcn/ui's track, circular thumb and focus ring, retaining Slider's
// RangeValue accessibility contract and one save at the end of a pointer drag.
public sealed class EditorBarSlider : Slider
{
    private FrameworkElement? trackArea, track;
    private Border? range, thumb;
    private TranslateTransform? thumbPosition;
    private TextBlock? valueLabel;
    private bool held, thumbHovered, userInteracting;
    private double dragStart, dragOffset, lastPointerX, dragRawValue, lastKnownMin, lastKnownMax;
    internal bool IsDragging => held;
    public event EventHandler? Committed;
    internal event Action<bool>? InteractionEnded;
    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(nameof(Suffix), typeof(string), typeof(EditorBarSlider), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(EditorBarSlider), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty InkProperty = DependencyProperty.Register(nameof(Ink), typeof(Brush), typeof(EditorBarSlider), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty DefaultValueProperty = DependencyProperty.Register(nameof(DefaultValue), typeof(double), typeof(EditorBarSlider), new PropertyMetadata(double.NaN));
    public string Suffix { get => (string)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Brush Ink { get => (Brush)GetValue(InkProperty); set => SetValue(InkProperty, value); }
    public double DefaultValue { get => (double)GetValue(DefaultValueProperty); set => SetValue(DefaultValueProperty, value); }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((EditorBarSlider)d).Draw();
    public EditorBarSlider()
    {
        Height = MinHeight = 32; IsThumbToolTipEnabled = false; UseSystemFocusVisuals = false;
        Template = (ControlTemplate)XamlReader.Load("""
          <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Slider">
            <Grid x:Name="SliderRoot" Background="Transparent" ColumnSpacing="12">
              <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="48"/></Grid.ColumnDefinitions>
              <VisualStateManager.VisualStateGroups>
                <VisualStateGroup x:Name="ThumbFeedback">
                  <VisualStateGroup.Transitions><VisualTransition GeneratedDuration="0:0:0.15"><VisualTransition.GeneratedEasingFunction><CubicEase EasingMode="EaseOut"/></VisualTransition.GeneratedEasingFunction></VisualTransition></VisualStateGroup.Transitions>
                  <VisualState x:Name="ThumbQuiet"/>
                  <VisualState x:Name="ThumbHighlighted"><VisualState.Setters><Setter Target="ThumbRing.Opacity" Value="0.3"/></VisualState.Setters></VisualState>
                </VisualStateGroup>
              </VisualStateManager.VisualStateGroups>
              <Grid x:Name="TrackArea" Background="Transparent" Margin="4,0">
                <Grid x:Name="Track" Height="6" Margin="8,0" VerticalAlignment="Center">
                  <Border Background="{ThemeResource EditorControlTrack}" CornerRadius="3"/>
                  <Border x:Name="Range" HorizontalAlignment="Left" CornerRadius="3"/>
                </Grid>
                <Grid Width="16" Height="16" HorizontalAlignment="Left" VerticalAlignment="Center" IsHitTestVisible="False">
                  <Grid.RenderTransform><TranslateTransform x:Name="ThumbPosition"/></Grid.RenderTransform>
                  <Border x:Name="ThumbRing" Margin="-4" BorderThickness="4" CornerRadius="12" Opacity="0"/>
                  <Border x:Name="Thumb" Background="{ThemeResource EditorSliderThumb}" BorderThickness="1.5" CornerRadius="8"><Border.Shadow><ThemeShadow/></Border.Shadow></Border>
                </Grid>
              </Grid>
              <TextBlock x:Name="ValueLabel" Grid.Column="1" FontFamily="{StaticResource RecorderFont}" FontSize="12" VerticalAlignment="Center" HorizontalAlignment="Right" IsHitTestVisible="False"/>
            </Grid>
          </ControlTemplate>
          """);
        lastKnownMin = Minimum; lastKnownMax = Maximum;
        ValueChanged += (_, e) => {
            if (!userInteracting && !held && double.IsNaN(DefaultValue) && Math.Abs(Minimum - lastKnownMin) < 1e-9 && Math.Abs(Maximum - lastKnownMax) < 1e-9)
                DefaultValue = e.NewValue;
            Draw();
            if (!held) Committed?.Invoke(this, EventArgs.Empty);
        };
        SizeChanged += (_, _) => Draw(); ActualThemeChanged += (_, _) => Draw();
        RegisterPropertyChangedCallback(MinimumProperty, (_, _) => { lastKnownMin = Minimum; Draw(); });
        RegisterPropertyChangedCallback(MaximumProperty, (_, _) => { lastKnownMax = Maximum; Draw(); });
        RegisterPropertyChangedCallback(StepFrequencyProperty, (_, _) => Draw());
        PointerCaptureLost += (_, _) => FinishDrag(true);
        IsEnabledChanged += (_, _) => { Opacity = IsEnabled ? 1 : 0.5; if (!IsEnabled) FinishDrag(true); UpdateFeedback(); };
    }
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        trackArea = GetTemplateChild("TrackArea") as FrameworkElement; track = GetTemplateChild("Track") as FrameworkElement;
        range = GetTemplateChild("Range") as Border; thumb = GetTemplateChild("Thumb") as Border;
        thumbPosition = GetTemplateChild("ThumbPosition") as TranslateTransform; valueLabel = GetTemplateChild("ValueLabel") as TextBlock;
        if (trackArea is not null) trackArea.SizeChanged += (_, _) => Draw();
        Draw(); UpdateFeedback(false);
    }
    private void Draw()
    {
        if (range is null || thumb is null || thumbPosition is null || valueLabel is null || track is null) return;
        double fraction = Math.Clamp((Value - Minimum) / Math.Max(double.Epsilon, Maximum - Minimum), 0, 1);
        if (FlowDirection == FlowDirection.RightToLeft) fraction = 1 - fraction;
        double x = fraction * track.ActualWidth;
        range.Width = FlowDirection == FlowDirection.RightToLeft ? track.ActualWidth - x : x;
        range.HorizontalAlignment = FlowDirection == FlowDirection.RightToLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        thumbPosition.X = x;
        var accent = Accent ?? RecorderTheme.Brush(this, "EditorAccent");
        range.Background = thumb.BorderBrush = accent;
        thumb.Background = RecorderTheme.Brush(this, "EditorSliderThumb");
        if (GetTemplateChild("ThumbRing") is Border ring) ring.BorderBrush = accent;
        string format = StepFrequency >= 1 && Math.Abs(Value - Math.Round(Value)) <= 0.001 ? "0" : "0.##";
        valueLabel.Text = Value.ToString(format, System.Globalization.CultureInfo.CurrentCulture) + Suffix;
        valueLabel.Foreground = Ink ?? RecorderTheme.Brush(this, "EditorSecondary");
    }
    private void UpdateFeedback(bool animate = true) => VisualStateManager.GoToState(this,
        IsEnabled && (held || thumbHovered || FocusState == FocusState.Keyboard) ? "ThumbHighlighted" : "ThumbQuiet", animate && SwitchMotion.AnimationsEnabled);
    protected override void OnGotFocus(RoutedEventArgs e) { base.OnGotFocus(e); UpdateFeedback(); }
    protected override void OnLostFocus(RoutedEventArgs e) { base.OnLostFocus(e); UpdateFeedback(); }
    private void UpdateHover(PointerRoutedEventArgs e)
    {
        if (trackArea is null || thumbPosition is null) return;
        var point = e.GetCurrentPoint(trackArea).Position;
        thumbHovered = Math.Abs(point.X - thumbPosition.X - 8) <= 12 && Math.Abs(point.Y - trackArea.ActualHeight / 2) <= 12;
        UpdateFeedback();
    }
    protected override void OnPointerEntered(PointerRoutedEventArgs e) { base.OnPointerEntered(e); UpdateHover(e); }
    protected override void OnPointerExited(PointerRoutedEventArgs e) { base.OnPointerExited(e); thumbHovered = false; UpdateFeedback(); }
    private static bool Shift => InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    private void ChangeAt(double x)
    {
        double fraction = Math.Clamp((x - 8) / Math.Max(1, track?.ActualWidth ?? 0), 0, 1);
        if (FlowDirection == FlowDirection.RightToLeft) fraction = 1 - fraction;
        Value = Snap(Minimum + fraction * (Maximum - Minimum));
    }
    private double Snap(double value, double? stepOverride = null)
    {
        double step = Math.Max(0.0001, stepOverride ?? StepFrequency);
        return Math.Round(Math.Clamp(Minimum + Math.Round((value - Minimum) / step) * step, Minimum, Maximum), 6);
    }
    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (!IsEnabled || trackArea is null || (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)) return;
        var point = e.GetCurrentPoint(trackArea).Position;
        if (point.X < 0 || point.X > trackArea.ActualWidth) return;
        Focus(FocusState.Pointer);
        if (!CapturePointer(e.Pointer)) return;
        double fromCenter = point.X - (thumbPosition?.X ?? 0) - 8;
        dragOffset = Math.Abs(fromCenter) <= 8 ? fromCenter : 0;
        dragStart = Value; held = true;
        if (!Shift) ChangeAt(point.X - dragOffset);
        dragRawValue = Value; lastPointerX = point.X;
        UpdateFeedback(); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (held && trackArea is not null) {
            var point = e.GetCurrentPoint(trackArea).Position;
            if (Shift) {
                double dx = point.X - lastPointerX;
                if (FlowDirection == FlowDirection.RightToLeft) dx = -dx;
                dragRawValue = Math.Clamp(dragRawValue + (dx / Math.Max(1, track?.ActualWidth ?? 0)) * (Maximum - Minimum) * 0.2, Minimum, Maximum);
                Value = Snap(dragRawValue, StepFrequency * 0.2);
            } else {
                ChangeAt(point.X - dragOffset);
                dragRawValue = Value;
            }
            lastPointerX = point.X;
            e.Handled = true;
        }
        else UpdateHover(e);
    }
    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (!held) return;
        FinishDrag(true); ReleasePointerCapture(e.Pointer); UpdateHover(e); e.Handled = true;
    }
    protected override void OnPointerCanceled(PointerRoutedEventArgs e) { if (held) { FinishDrag(false); ReleasePointerCapture(e.Pointer); e.Handled = true; } }
    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        // Do not adjust slider values on mouse wheel hover; let parent ScrollViewers scroll normally.
    }
    protected override void OnDoubleTapped(DoubleTappedRoutedEventArgs e)
    {
        base.OnDoubleTapped(e);
        if (!IsEnabled || !double.IsFinite(DefaultValue) || Math.Abs(Value - DefaultValue) <= 0.0001) return;
        double target = Snap(DefaultValue);
        double prior = Value;
        held = false;
        userInteracting = true;
        try { Value = target; }
        finally { userInteracting = false; }
        if (Value == prior) Committed?.Invoke(this, EventArgs.Empty);
        InteractionEnded?.Invoke(true);
        e.Handled = true;
    }
    private void FinishDrag(bool commit)
    {
        if (!held) return;
        bool changed = Value != dragStart;
        if (!commit) Value = dragStart;
        held = false; UpdateFeedback();
        if (commit && changed) Committed?.Invoke(this, EventArgs.Empty);
        InteractionEnded?.Invoke(commit);
    }
    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (!IsEnabled) return;
        if (e.Key == VirtualKey.Escape && held) { FinishDrag(false); ReleasePointerCaptures(); e.Handled = true; return; }
        if (e.Key is VirtualKey.Left or VirtualKey.Down or VirtualKey.Right or VirtualKey.Up or VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown) {
            double direction = e.Key is VirtualKey.Left or VirtualKey.Down or VirtualKey.PageDown ? -1 : 1;
            if (FlowDirection == FlowDirection.RightToLeft && e.Key is VirtualKey.Left or VirtualKey.Right) direction *= -1;
            double step = Shift ? StepFrequency * 0.2 : StepFrequency;
            double increment = e.Key is VirtualKey.PageUp or VirtualKey.PageDown ? Math.Max(step, (Maximum - Minimum) / 10) : step;
            userInteracting = true;
            try { Value = e.Key switch { VirtualKey.Home => Minimum, VirtualKey.End => Maximum, _ => Snap(Value + direction * increment, step) }; }
            finally { userInteracting = false; }
            e.Handled = true;
        } else base.OnKeyDown(e);
    }
}
