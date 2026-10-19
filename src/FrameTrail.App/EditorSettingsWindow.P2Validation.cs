using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace FrameTrail.App;

internal sealed partial class EditorSettingsWindow
{
    internal async Task ValidateP2Async(string directory)
    {
        for(int i=0;i<5;i++) {Select(i);await Task.Delay(60);root.UpdateLayout();await MainWindow.SaveUiFrameAsync(root,Path.Combine(directory,$"settings-{FrameTrail.Domain.AppText.Language}-{i}.png"));}
        var check=page.Children.OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)=="settings.update.check");
        ((IInvokeProvider)new ButtonAutomationPeer(check).GetPattern(PatternInterface.Invoke)).Invoke();
        var deadline=DateTime.UtcNow.AddSeconds(20);await Task.Delay(100);
        while(!check.IsEnabled&&DateTime.UtcNow<deadline)await Task.Delay(50);
        var status=page.Children.OfType<TextBlock>().Single(b=>AutomationProperties.GetAutomationId(b)=="settings.update.status");
        bool confirmed=System.Text.RegularExpressions.Regex.IsMatch(status.Text,@"v\d+\.\d+\.\d+");
        bool officialLink=page.Children.OfType<StackPanel>().SelectMany(p=>p.Children.OfType<Button>()).Any(b=>b.Tag is Uri uri&&ReleaseUpdateClient.Official(uri));
        if(!check.IsEnabled||!officialLink)throw new IOException("Update check did not preserve the official fallback action: "+status.Text);
        await File.WriteAllTextAsync(Path.Combine(directory,"update-check.json"),System.Text.Json.JsonSerializer.Serialize(new{confirmedOnline=confirmed,status=status.Text,officialLink}));
        await File.WriteAllTextAsync(Path.Combine(directory,"update-status.txt"),status.Text);
        await MainWindow.SaveUiFrameAsync(root,Path.Combine(directory,"update-result.png"));
        var showing=GettingStarted.ShowAsync(this,true,Path.Combine(directory,"getting-started-state"));await Task.Delay(120);
        var guide=GettingStarted.Active??throw new IOException("Getting-started window was not shown.");
        await guide.ValidateLayoutAsync(Path.Combine(directory,"getting-started-"+FrameTrail.Domain.AppText.Language+".png"));guide.Close();await showing;
    }
}
