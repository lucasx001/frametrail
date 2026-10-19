using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace FrameTrail.App;

internal static class UiAccessibility
{
    internal static void NameControls(DependencyObject root)
    {
        if(root is ButtonBase button && string.IsNullOrEmpty(AutomationProperties.GetName(button))) {
            string? name=ToolTipService.GetToolTip(button) as string;
            if(string.IsNullOrEmpty(name))name=button.Content as string;
            if(string.IsNullOrEmpty(name)&&button.Content is DependencyObject content)name=Text(content);
            if(!string.IsNullOrWhiteSpace(name))AutomationProperties.SetName(button,name);
        }
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)NameControls(VisualTreeHelper.GetChild(root,i));
    }
    private static string? Text(DependencyObject value)
    {
        if(value is TextBlock text&&!string.IsNullOrWhiteSpace(text.Text))return text.Text;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(value);i++)if(Text(VisualTreeHelper.GetChild(value,i)) is { } label)return label;
        return null;
    }
}
