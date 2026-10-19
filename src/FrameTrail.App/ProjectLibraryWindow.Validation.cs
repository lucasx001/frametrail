using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FrameTrail.App;

internal sealed partial class ProjectLibraryWindow
{
    internal int ProjectCount => entries.Count;
    internal bool HasError => notice.IsOpen && notice.Severity == InfoBarSeverity.Error;
    internal async Task ValidateListAsync(string directory, List<string> checks)
    {
        await Ready.WaitAsync(TimeSpan.FromSeconds(15));
        if (entries.Count != 3 || entries.Count(entry => entry.CanOpen) != 2) throw new IOException("Project list missed existing or incomplete projects.");
        var originalSize = AppWindow.Size; var originalTheme = root.RequestedTheme;
        foreach (bool compact in new[] { false, true }) {
            root.RequestedTheme = compact ? ElementTheme.Light : ElementTheme.Dark;
            if (compact) { double scale = RecorderWindowChrome.Scale(this); AppWindow.Resize(new((int)(520 * scale), (int)(520 * scale))); }
            await Task.Delay(150); root.UpdateLayout();
            var first = Row((ListViewItem)list.ContainerFromIndex(0));
            var open = (Button)first.Children[0]; var remove = (Button)first.Children[1];
            if (open.ActualWidth < 250 || remove.ActualWidth < 30 || open.ActualWidth + remove.ActualWidth > first.ActualWidth + 1)
                throw new IOException("Project row open and delete controls overlap or clip.");
            await MainWindow.SaveUiFrameAsync(root, Path.Combine(directory, compact ? "projects-compact-light.png" : "projects-dark.png"));
        }
        AppWindow.Resize(originalSize); root.RequestedTheme = originalTheme;
        search.Text = "项目 B"; await Task.Delay(250); root.UpdateLayout();
        if (list.Items.Count != 1) throw new IOException("Project name search did not filter rows.");
        search.Text = "no-such-project"; await Task.Delay(250); if (list.Items.Count != 0 || empty.Visibility != Visibility.Visible) throw new IOException("Search empty state was not shown.");
        search.Text = ""; await Task.Delay(250);
        checks.Add("The native project list shows existing and incomplete projects, filters names, shows an empty state, and fits dark and compact/light layouts with separate open/delete buttons.");
    }
    private Button EntryButton(string path, bool remove)
    {
        var entry = entries.Single(entry => string.Equals(entry.Directory, path, StringComparison.OrdinalIgnoreCase));
        string id = (remove ? "projects.delete." : "projects.open.") + EntryId(entry);
        list.ScrollIntoView(entry);root.UpdateLayout();
        if(list.ContainerFromItem(entry) is ListViewItem item)
            foreach(var button in Row(item).Children.OfType<Button>())if(AutomationProperties.GetAutomationId(button)==id)return button;
        throw new IOException("Project row button not found.");
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    internal void InvokeOpen(string path) => Invoke(EntryButton(path, false));
    internal async Task ValidateDeletionAsync(string path, bool confirm, string? screenshot = null)
    {
        Invoke(EntryButton(path, true));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (confirmation is null && DateTime.UtcNow < deadline) await Task.Delay(20);
        if (confirmation is not { } dialog || dialog.DefaultButton != ContentDialogButton.Close) throw new IOException("Project deletion did not default to cancellation.");
        await Task.Delay(150);
        if (screenshot is not null) await MainWindow.SaveUiFrameAsync(dialog, screenshot);
        Button? Find(DependencyObject parent)
        {
            if (parent is Button button && button.Content is string label && label == (confirm ? "移到回收站" : "取消")) return button;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) if (Find(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
            return null;
        }
        Invoke(Find(dialog) ?? throw new IOException("Confirmation action was not found."));
        deadline = DateTime.UtcNow.AddSeconds(15);
        do { await Task.Delay(30); } while ((confirming || working) && DateTime.UtcNow < deadline);
        if (confirming || working) throw new IOException("Deletion did not finish.");
    }
}
