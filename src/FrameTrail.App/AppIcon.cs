using Microsoft.UI.Xaml;

namespace FrameTrail.App;

internal static class AppIcon
{
    internal static void Apply(Window window) => window.AppWindow.SetIcon(
        Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", "FrameTrail.Icon.v1.ico"));
}
