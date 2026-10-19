using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace FrameTrail.App;

internal sealed partial class GettingStartedWindow
{
    internal XamlRoot GuideRoot => root.XamlRoot;
    internal void InvokeStart() => ((IInvokeProvider)new ButtonAutomationPeer(start).GetPattern(PatternInterface.Invoke)).Invoke();

    internal async Task<object> ValidateLayoutAsync(string screenshot)
    {
        root.UpdateLayout();
        var point = start.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point());
        if (root.ActualHeight < 200 || start.ActualHeight < 30 || point.X < 0 || point.Y < 0 ||
            point.X + start.ActualWidth > root.ActualWidth + 1 || point.Y + start.ActualHeight > root.ActualHeight + 1)
            throw new IOException("Getting-started action is clipped by its window.");
        await MainWindow.SaveUiFrameAsync(root, screenshot);
        return new { width = root.ActualWidth, height = root.ActualHeight, buttonX = point.X, buttonY = point.Y, buttonWidth = start.ActualWidth, buttonHeight = start.ActualHeight };
    }
}
