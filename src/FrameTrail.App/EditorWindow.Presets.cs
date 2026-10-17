using FrameTrail.Workspace;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    private VisualPresetLibrary? presetLibrary;
    private bool showingPresets;
    private ContentDialog? activePresetDialog;
    private async void ManagePresets_Click(object sender, RoutedEventArgs e) => await SafeAsync(ManagePresetsAsync);
    private async Task ManagePresetsAsync()
    {
        if (showingPresets || showingExport || working || editor.IsExporting) return;
        Pause(); await FlushVisualChangesAsync(); await FlushPictureInputsAsync();
        presetLibrary ??= new VisualPresetLibrary();
        var list = new ListView { DisplayMemberPath = "Name", SelectionMode = ListViewSelectionMode.Single, MaxHeight = 210, MinHeight = 80 };
        var name = new TextBox { Header = "预设名称", MaxLength = 60, PlaceholderText = "例如：课程演示" };
        name.Resources["TextControlBorderBrushFocused"] = Application.Current.Resources["EditorBlue"];
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var save = new Button { Content = "保存当前画面" }; var rename = new Button { Content = "重命名" }; var delete = new Button { Content = "删除" };
        var defaults = new Button { Content = "设为录制默认" }; var resetDefault = new Button { Content = "取消默认" };
        foreach (var button in new[] { save, rename, delete, defaults, resetDefault }) { button.Style = (Style)Application.Current.Resources["EditorButton"]; button.Background = RecorderTheme.Brush(Root, "EditorWell"); button.Padding = new Thickness(12, 0, 12, 0); }
        list.Resources["ListViewItemSelectionIndicatorBrush"] = Application.Current.Resources["EditorBlue"];
        AutomationProperties.SetAutomationId(rename, "editor.presets.rename"); AutomationProperties.SetAutomationId(delete, "editor.presets.delete");
        AutomationProperties.SetAutomationId(defaults, "editor.presets.default"); AutomationProperties.SetAutomationId(resetDefault, "editor.presets.clearDefault");
        buttons.Children.Add(save); buttons.Children.Add(rename); buttons.Children.Add(delete);
        var defaultRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; defaultRow.Children.Add(defaults); defaultRow.Children.Add(resetDefault);
        var content = new StackPanel { Spacing = 12, MinWidth = 350 }; content.Children.Add(list); content.Children.Add(name); content.Children.Add(buttons); content.Children.Add(defaultRow); content.Children.Add(status);
        AutomationProperties.SetAutomationId(list, "editor.presets.list"); AutomationProperties.SetAutomationId(name, "editor.presets.name"); AutomationProperties.SetAutomationId(save, "editor.presets.save");
        var dialog = new ContentDialog { Title = "场景预设", Content = content, PrimaryButtonText = "应用所选预设", CloseButtonText = "关闭", DefaultButton = ContentDialogButton.None, XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme };
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["EditorPrimaryButton"];
        dialog.CloseButtonStyle = (Style)Application.Current.Resources["EditorButton"];
        bool busy = false;
        void Update() { bool selected = list.SelectedItem is VisualPreset; rename.IsEnabled = delete.IsEnabled = defaults.IsEnabled = selected && !busy; save.IsEnabled = resetDefault.IsEnabled = !busy; dialog.IsPrimaryButtonEnabled = selected && !busy; }
        async Task Refresh(string? id = null) {
            var catalog = await presetLibrary.ListAsync(); list.ItemsSource = catalog.Presets;
            list.SelectedItem = catalog.Presets.FirstOrDefault(p => p.Id == id);
            var selectedDefault = catalog.Presets.FirstOrDefault(p => p.Id == catalog.DefaultId);
            status.Text = (selectedDefault is null ? "未设置录制默认预设。" : "新录制默认使用：“" + selectedDefault.Name + "”。") + (catalog.Warnings.Count > 0 ? $"\n{catalog.Warnings.Count} 个损坏预设未载入。" : ""); Update();
        }
        async Task Action(Func<Task<string?>> action) {
            if (busy) return; busy = true; Update();
            try { await Refresh(await action()); } catch (Exception error) { status.Text = error.Message; }
            finally { busy = false; Update(); }
        }
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is VisualPreset p) name.Text = p.Name; Update(); };
        save.Click += async (_, _) => await Action(async () => (await presetLibrary.SaveAsync(name.Text, editor.Snapshot, editor.DirectoryPath)).Id);
        rename.Click += async (_, _) => await Action(async () => { var p = (VisualPreset)list.SelectedItem; await presetLibrary.RenameAsync(p.Id, name.Text); return p.Id; });
        delete.Click += async (_, _) => await Action(async () => { var p = (VisualPreset)list.SelectedItem; await presetLibrary.DeleteAsync(p.Id); return null; });
        defaults.Click += async (_, _) => await Action(async () => { var p = (VisualPreset)list.SelectedItem; await presetLibrary.SetDefaultAsync(p.Id); return p.Id; });
        resetDefault.Click += async (_, _) => await Action(async () => { var id = (list.SelectedItem as VisualPreset)?.Id; await presetLibrary.SetDefaultAsync(null); return id; });
        dialog.Closing += (_, args) => { if (busy) args.Cancel = true; };
        showingPresets = true;
        try {
            await Refresh();
            activePresetDialog = dialog;
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && list.SelectedItem is VisualPreset p) await PerformAsync(() => editor.ApplyLibraryPresetAsync(presetLibrary, p.Id));
        } finally { activePresetDialog = null; showingPresets = false; }
    }
}
