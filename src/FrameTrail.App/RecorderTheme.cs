using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FrameTrail.App;

internal static class RecorderTheme
{
    internal static Brush Brush(FrameworkElement element, string key)
    {
        foreach (var dictionary in Application.Current.Resources.MergedDictionaries)
        {
            var appearance = element.RequestedTheme == ElementTheme.Default ? element.ActualTheme : element.RequestedTheme;
            var theme = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast ? "HighContrast" : appearance == ElementTheme.Light ? "Light" : "Default";
            if (dictionary.ThemeDictionaries.TryGetValue(theme, out var value) && value is ResourceDictionary themed && themed.ContainsKey(key)) return (Brush)themed[key];
            if (dictionary.ContainsKey(key)) return (Brush)dictionary[key];
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }
}
