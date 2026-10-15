using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace FrameTrail.App;

// Shares template transitions between CheckBox and ToggleButton without
// changing their keyboard, click, toggle accessibility or checked events.
public sealed class SwitchMotion : DependencyObject
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SwitchMotion), new PropertyMetadata(false, EnabledChanged));
    private static readonly DependencyProperty HoveredProperty = DependencyProperty.RegisterAttached("Hovered", typeof(bool), typeof(SwitchMotion), new PropertyMetadata(false));
    private static readonly Windows.UI.ViewManagement.UISettings settings = new();
    internal static bool AnimationsEnabled => settings.AnimationsEnabled;
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    private static void EnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ToggleButton button) return;
        if ((bool)args.NewValue) {
            button.Loaded += Loaded; button.SizeChanged += Layout; button.Checked += Changed; button.Unchecked += Changed; button.Indeterminate += Changed;
            button.GotFocus += Changed; button.LostFocus += Changed; button.IsEnabledChanged += AvailabilityChanged;
            button.PointerEntered += Entered; button.PointerExited += Exited;
            Update(button, false);
        } else {
            button.Loaded -= Loaded; button.SizeChanged -= Layout; button.Checked -= Changed; button.Unchecked -= Changed; button.Indeterminate -= Changed;
            button.GotFocus -= Changed; button.LostFocus -= Changed; button.IsEnabledChanged -= AvailabilityChanged;
            button.PointerEntered -= Entered; button.PointerExited -= Exited;
        }
    }
    private static void Loaded(object sender, RoutedEventArgs args) => Update((ToggleButton)sender, false);
    private static void Layout(object sender, SizeChangedEventArgs args) => Update((ToggleButton)sender, false);
    private static void Changed(object sender, RoutedEventArgs args) => Update((ToggleButton)sender, true);
    private static void AvailabilityChanged(object sender, DependencyPropertyChangedEventArgs args) => Update((ToggleButton)sender, false);
    private static void Entered(object sender, PointerRoutedEventArgs args) { var button = (ToggleButton)sender; button.SetValue(HoveredProperty, true); Update(button, true); }
    private static void Exited(object sender, PointerRoutedEventArgs args) { var button = (ToggleButton)sender; button.SetValue(HoveredProperty, false); Update(button, true); }
    private static void Update(ToggleButton button, bool animate)
    {
        if (!button.IsLoaded) return;
        // Collapsed inspector pages may receive checked values before their
        // templates exist. Synchronize again when first measured/displayed.
        button.ApplyTemplate();
        bool transitions = animate && AnimationsEnabled;
        VisualStateManager.GoToState(button, button.IsChecked == true ? "SwitchOn" : "SwitchOff", transitions);
        VisualStateManager.GoToState(button, button.IsEnabled ? "SwitchEnabled" : "SwitchDisabled", false);
        bool highlight = button.IsEnabled && ((bool)button.GetValue(HoveredProperty) || button.FocusState == FocusState.Keyboard);
        VisualStateManager.GoToState(button, highlight ? "SwitchHighlighted" : "SwitchQuiet", transitions);
    }
}
