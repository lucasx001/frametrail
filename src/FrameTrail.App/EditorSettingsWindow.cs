using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;

namespace FrameTrail.App;

// DogSC AppSettingsView's 560 DIP canvas, 56 DIP navigation capsule and 496 DIP page island.
internal sealed class EditorSettingsWindow : Window
{
    private readonly Grid root = new();
    private readonly StackPanel navigation = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly StackPanel page = new() { Spacing = 16 };
    private readonly string directory;
    private readonly Action<ElementTheme> setAppearance;
    private readonly EditorMediaCache? mediaCache;
    private readonly string[] names = ["常规", "编辑器", "录制", "权限", "关于"];
    private readonly string[] icons = ["Sliders", "Scene", "Opening", "Check", "Settings"];
    internal EditorSettingsWindow(string directory, Action<ElementTheme> setAppearance, EditorMediaCache? mediaCache = null)
    {
        this.directory = directory; this.setAppearance = setAppearance; this.mediaCache = mediaCache; Title = "FrameTrail · 设置";
        AppIcon.Apply(this);
        root.RequestedTheme = EditorAppearance.Current; root.Background = RecorderTheme.Brush(root, "EditorStage");
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(112) }); root.RowDefinitions.Add(new RowDefinition());
        var navSurface = new Border { Background = RecorderTheme.Brush(root, "EditorPanel"), CornerRadius = new CornerRadius(28), Padding = new Thickness(6), Height = 56, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = navigation };
        root.Children.Add(navSurface);
        var island = new Border { Background = RecorderTheme.Brush(root, "EditorPanel"), CornerRadius = new CornerRadius(20), Padding = new Thickness(22), Margin = new Thickness(32, 0, 32, 30), Child = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        Grid.SetRow(island, 1); root.Children.Add(island); Content = root;
        double scale = RecorderWindowChrome.Scale(this); var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)Math.Round(560 * scale), Math.Min((int)Math.Round(640 * scale), work.Height)));
        AppWindow.Move(new Windows.Graphics.PointInt32(work.X + (work.Width - AppWindow.Size.Width) / 2, work.Y + (work.Height - AppWindow.Size.Height) / 2));
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p) p.IsResizable = false;
        void ApplyTheme(ElementTheme value) { root.RequestedTheme = value; EditorAppearance.Save(value); setAppearance(value); root.Background = RecorderTheme.Brush(root, "EditorStage"); navSurface.Background = island.Background = RecorderTheme.Brush(root, "EditorPanel"); Select(1); }
        applyTheme = ApplyTheme; Select(0);
    }
    private readonly Action<ElementTheme> applyTheme;
    private TextBlock Text(string value, int size = 12, bool secondary = false) => new() { Text = value, FontSize = size, FontFamily = (FontFamily)Application.Current.Resources["RecorderFont"], Foreground = RecorderTheme.Brush(root, secondary ? "EditorSecondary" : "EditorInk"), TextWrapping = TextWrapping.Wrap };
    private void Select(int index)
    {
        navigation.Children.Clear();
        for (int i = 0; i < names.Length; i++) {
            int target = i; var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 }; row.Children.Add(new EditorGlyph { Kind = icons[i], Width = 17, Height = 17, Foreground = RecorderTheme.Brush(root, "EditorInk") }); if (i == index) row.Children.Add(Text(names[i], 13));
            var button = new Button { Style = (Style)Application.Current.Resources["EditorButton"], Content = row, Height = 44, MinWidth = 44, Padding = new Thickness(i == index ? 14 : 0, 0, i == index ? 16 : 0, 0), CornerRadius = new CornerRadius(22), Background = i == index ? RecorderTheme.Brush(root, "EditorHover") : new SolidColorBrush(Colors.Transparent) }; ToolTipService.SetToolTip(button, names[i]); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, names[i]); button.Click += (_, _) => Select(target); navigation.Children.Add(button);
        }
        page.Children.Clear(); page.Children.Add(Text(names[index], 26));
        if (index == 0) {
            page.Children.Add(Text("文件位置", 12, true)); var content = new StackPanel { Spacing = 12 }; content.Children.Add(Text("当前项目")); content.Children.Add(Text(directory, 11, true)); var reveal = Button("在资源管理器中显示"); reveal.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true }); content.Children.Add(reveal); page.Children.Add(Tile(content));
            page.Children.Add(Tile(Text("编辑会自动保存到项目包中，录制素材保存在项目的 media 文件夹。", 12, true)));
        } else if (index == 1) {
            if (mediaCache is not null) {
                var budget = new NumberBox { Header = "媒体缓存上限 (MiB)", Minimum = 128, Maximum = 16384, Value = mediaCache.Budget / (1024*1024) };
                var cacheStatus = Text("只清理当前工程可重建的缩略图与波形，保留源素材和使用中的缓存。",11,true);
                Task budgetUpdates=Task.CompletedTask;
                budget.ValueChanged += async (_, e) => {
                    if(!double.IsFinite(e.NewValue))return;
                    var previous=budgetUpdates;long next=(long)e.NewValue*1024*1024;
                    async Task SaveBudgetAsync(){await previous;try{await Task.Run(()=>mediaCache.Budget=next);}catch(Exception error){cacheStatus.Text=error.Message;}}
                    budgetUpdates=SaveBudgetAsync();await budgetUpdates;
                };
                var clear = Button("清理当前工程缓存"); clear.Click += async (_, _) => { clear.IsEnabled=false;try { await System.Threading.Tasks.Task.Run(mediaCache.ClearRebuildable);cacheStatus.Text="缓存已清理，正在使用的文件已保留。"; } catch(Exception error) { cacheStatus.Text=error.Message; } finally { clear.IsEnabled=true; } };
                var cachePanel=new StackPanel {Spacing=10};cachePanel.Children.Add(budget);cachePanel.Children.Add(clear);cachePanel.Children.Add(cacheStatus);page.Children.Add(Tile(cachePanel));
            }
            page.Children.Add(Text("外观", 12, true)); var row = new Grid { ColumnSpacing = 8 }; for (int i = 0; i < 3; i++) { var theme = new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Default }[i]; row.ColumnDefinitions.Add(new ColumnDefinition()); var button = Button(new[] { "浅色", "深色", "跟随系统" }[i]); button.Background = RecorderTheme.Brush(root, root.RequestedTheme == theme ? "EditorHover" : "EditorWell"); button.Click += (_, _) => applyTheme(theme); Grid.SetColumn(button, i); row.Children.Add(button); } page.Children.Add(Tile(row));
            page.Children.Add(Text("快捷操作", 12, true)); page.Children.Add(Tile(Text("空格  播放 / 暂停\n← / →  逐帧查看\nS  在播放头处分割\nCtrl+Z / Ctrl+Y  撤销 / 重做\nEsc  取消当前拖动", 12, true)));
        } else if (index == 2) {
            page.Children.Add(Tile(Text("从悬浮录制条选择屏幕、窗口或区域，并分别设置系统声音、麦克风与摄像头。", 13)));
            page.Children.Add(Tile(Text("帧率、保存位置与光标记录选项位于录制条的更多菜单。", 12, true)));
        } else if (index == 3) {
            page.Children.Add(Tile(Text("屏幕与窗口\n在录制条中选择需要录制的来源。\n\n摄像头与麦克风\n设备的可用状态显示在对应输入菜单中。", 13)));
            page.Children.Add(Text("这里不更改系统的设备权限。", 11, true));
        } else {
            page.Children.Add(Text("FrameTrail", 28)); page.Children.Add(Text("Windows 屏幕录制与剪辑", 13, true)); page.Children.Add(Tile(Text("界面参考 DogSC，沿用其 PuHuiTi 字体与界面设计。\n\nDogSC 相关设计与字体许可随应用附带。", 12, true)));
            page.Children.Add(Text($"版本 {typeof(App).Assembly.GetName().Version}", 12, true));
            var updateStatus = Text("更新由你手动安装；请先结束录制和导出，并关闭已保存的工程。", 12, true);
            var updates = Button("检查更新"); updates.Click += async (_, _) => {
                updates.IsEnabled = false;
                try {
                    var result = await Windows.ApplicationModel.Package.Current.CheckUpdateAvailabilityAsync();
                    updateStatus.Text = result.Availability switch {
                        Windows.ApplicationModel.PackageUpdateAvailability.Available or Windows.ApplicationModel.PackageUpdateAvailability.Required => "有新版本。请保存工程、结束媒体任务并退出 FrameTrail，再从原安装来源安装。",
                        Windows.ApplicationModel.PackageUpdateAvailability.NoUpdates => "当前没有可用更新。",
                        _ => "未配置可用的更新来源。请从原安装来源检查版本。"
                    };
                } catch { updateStatus.Text = "当前是开发版，或安装来源未提供更新检查。请从原安装来源检查版本。"; }
                finally { updates.IsEnabled = true; }
            }; page.Children.Add(updates); page.Children.Add(updateStatus);
            var diagnostics = Button("导出诊断信息"); diagnostics.Click += async (_, _) => {
                try {
                    var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "FrameTrail-diagnostics" };
                    picker.FileTypeChoices.Add("诊断信息", new[] { ".json" });
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                    var file = await picker.PickSaveFileAsync(); if (file is not null) await File.WriteAllTextAsync(file.Path, SafeDiagnostics.Export());
                } catch { updateStatus.Text = "无法写入诊断文件，请选择有写入权限的位置后重试。"; }
            }; page.Children.Add(diagnostics);
            foreach (var (label, file) in new[] { ("使用指南", "user-guide.md"), ("隐私说明", "privacy.md"), ("版本说明", "release-notes.md") }) {
                var document = Button(label); document.Click += (_, _) => Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Docs", file)) { UseShellExecute = true }); page.Children.Add(document);
            }
            var notices = Button("第三方许可"); notices.Click += (_, _) => Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md")) { UseShellExecute = true }); page.Children.Add(notices);
        }
    }
    private Button Button(string text) => new() { Content = text, Style = (Style)Application.Current.Resources["EditorButton"], HorizontalAlignment = HorizontalAlignment.Stretch, Background = RecorderTheme.Brush(root, "EditorWell") };
    private Border Tile(UIElement child) => new() { Style = (Style)Application.Current.Resources["EditorTile"], Child = child };
}
