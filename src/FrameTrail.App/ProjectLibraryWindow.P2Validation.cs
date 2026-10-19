using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FrameTrail.App;

internal sealed partial class ProjectLibraryWindow
{
    internal async Task<object> ValidateStressAsync(string directory)
    {
        await Ready;var watch=Stopwatch.StartNew();int maximum=0;var samples=new List<long>();
        foreach(int index in new[]{0,99,50,entries.Count-1,entries.Count/2,0}) {
            var step=Stopwatch.StartNew();list.ScrollIntoView(entries[index]);root.UpdateLayout();await Task.Delay(45);root.UpdateLayout();
            if(list.ContainerFromItem(entries[index]) is not ListViewItem item)throw new IOException("Virtual row was not realized.");
            var row=Row(item);if(row.ActualWidth<300||row.ActualHeight<80)throw new IOException("Virtual row was not arranged visibly.");
            var button=(Button)row.Children[0];if(AutomationProperties.GetAutomationId(button)!="projects.open."+EntryId(entries[index]))throw new IOException("Recycled row opened the wrong item.");
            int realized=Enumerable.Range(0,list.Items.Count).Count(i=>list.ContainerFromIndex(i) is not null);maximum=Math.Max(maximum,realized);samples.Add(step.ElapsedMilliseconds);
        }
        for(int i=0;i<30;i++)search.Text="Project"+i;search.Text="Project0999";await Task.Delay(250);root.UpdateLayout();
        if(list.Items.Count!=1||list.Items[0] is not ProjectCatalogEntry {Name:"Project0999"})throw new IOException("Debounced search retained an old result.");
        await MainWindow.SaveUiFrameAsync(root,Path.Combine(directory,"projects-filtered.png"));
        search.Text="";await Task.Delay(250);root.UpdateLayout();
        await MainWindow.SaveUiFrameAsync(root,Path.Combine(directory,"projects-thousand.png"));
        if(maximum>=100)throw new IOException("Project list did not virtualize its containers.");
        return new{projectCount=entries.Count,maximumRealizedRows=maximum,elapsedMilliseconds=watch.ElapsedMilliseconds,scrollMilliseconds=samples,privateBytes=Process.GetCurrentProcess().PrivateMemorySize64};
    }
}
