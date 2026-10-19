using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;

namespace FrameTrail.App;

// DogSC AppSettingsView's 560 DIP canvas, 56 DIP navigation capsule and 496 DIP page island.
internal sealed partial class EditorSettingsWindow : Window
{
    private readonly Grid root = new();
    private readonly StackPanel navigation = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly StackPanel page = new() { Spacing = 16 };
    private readonly string directory;
    private readonly Action<ElementTheme> setAppearance;
    private readonly EditorMediaCache? mediaCache;
    private readonly string[] names = [L.T("S40FAE00B7C6D"), L.T("S3B7F5965BDBF"), L.T("S4B6EF790032F"), L.T("S978CBCA6265D"), L.T("S52D25A9E30BA")];
    private readonly string[] icons = ["Sliders", "Scene", "Opening", "Check", "Settings"];
    internal EditorSettingsWindow(string directory, Action<ElementTheme> setAppearance, EditorMediaCache? mediaCache = null)
    {
        this.directory = directory; this.setAppearance = setAppearance; this.mediaCache = mediaCache; Title = L.T("S888F02152174");
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
        Closed+=(_,_)=>{updateClosed=true;updateLifetime.Cancel();};
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
            var language=new ComboBox{Header=L.T("S3D13868593AE"),HorizontalAlignment=HorizontalAlignment.Stretch,ItemsSource=new[]{L.T("S217CFE7DB1E3"),L.T("S9937E365AAF3"),"English"}};
            language.SelectedIndex=FrameTrail.Domain.AppText.Preference switch{"zh-Hans"=>1,"en"=>2,_=>0};
            language.SelectionChanged+=(_,_)=>{try{FrameTrail.Domain.AppText.SavePreference(new[]{"system","zh-Hans","en"}[language.SelectedIndex]);}catch(Exception error){page.Children.Add(Text(error.Message));}};
            page.Children.Add(language);page.Children.Add(Text(L.T("S1CF8288F24FE"),11,true));
            page.Children.Add(Text(L.T("S18A461562395"), 12, true)); var content = new StackPanel { Spacing = 12 }; content.Children.Add(Text(L.T("SBF962B2B913F"))); content.Children.Add(Text(directory, 11, true)); var reveal = Button(L.T("S9E3FC11213B9")); reveal.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true }); content.Children.Add(reveal); page.Children.Add(Tile(content));
            page.Children.Add(Tile(Text(L.T("S21E709788CF5"), 12, true)));
        } else if (index == 1) {
            if (mediaCache is not null) {
                var budget = new NumberBox { Header = L.T("SD222057F23B7"), Minimum = 128, Maximum = 16384, Value = mediaCache.Budget / (1024*1024) };
                var cacheStatus = Text(L.T("SAD171618E732"),11,true);
                Task budgetUpdates=Task.CompletedTask;
                budget.ValueChanged += async (_, e) => {
                    if(!double.IsFinite(e.NewValue))return;
                    var previous=budgetUpdates;long next=(long)e.NewValue*1024*1024;
                    async Task SaveBudgetAsync(){await previous;try{await Task.Run(()=>mediaCache.Budget=next);}catch(Exception error){cacheStatus.Text=error.Message;}}
                    budgetUpdates=SaveBudgetAsync();await budgetUpdates;
                };
                var clear = Button(L.T("SC6ABBA0F95CC")); clear.Click += async (_, _) => { clear.IsEnabled=false;try { await System.Threading.Tasks.Task.Run(mediaCache.ClearRebuildable);cacheStatus.Text=L.T("S84354BD74567"); } catch(Exception error) { cacheStatus.Text=error.Message; } finally { clear.IsEnabled=true; } };
                var cachePanel=new StackPanel {Spacing=10};cachePanel.Children.Add(budget);cachePanel.Children.Add(clear);cachePanel.Children.Add(cacheStatus);page.Children.Add(Tile(cachePanel));
            }
            page.Children.Add(Text(L.T("S86A63F23A076"), 12, true)); var row = new Grid { ColumnSpacing = 8 }; for (int i = 0; i < 3; i++) { var theme = new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Default }[i]; row.ColumnDefinitions.Add(new ColumnDefinition()); var button = Button(new[] { L.T("SAA0819DFC4D8"), L.T("SA6B75D068032"), L.T("S217CFE7DB1E3") }[i]); button.Background = RecorderTheme.Brush(root, root.RequestedTheme == theme ? "EditorHover" : "EditorWell"); button.Click += (_, _) => applyTheme(theme); Grid.SetColumn(button, i); row.Children.Add(button); } page.Children.Add(Tile(row));
            page.Children.Add(Text(L.T("S899432D83D98"), 12, true)); foreach(var help in EditorShortcuts.Help)page.Children.Add(Tile(Text(help,12,true)));
        } else if (index == 2) {
            var guide=Button(L.T("S0BBB77BE9877"));guide.Click+=async(_,_)=>await GettingStarted.ShowAsync(this,true);page.Children.Add(guide);
            page.Children.Add(Tile(Text(L.T("S873445EE20A5"), 13)));
            page.Children.Add(Tile(Text(L.T("SACCDB9647538"), 12, true)));
        } else if (index == 3) {
            foreach(var (label,uri) in new[]{(L.T("SA81867B44DD3"),"ms-settings:privacy-microphone"),(L.T("SB4763A13DAEA"),"ms-settings:privacy-webcam"),(L.T("S9D5909F99594"),"ms-settings:sound")}){
                var link=new HyperlinkButton{Content=label,NavigateUri=new Uri(uri)};page.Children.Add(link);
            }
            page.Children.Add(Text(L.T("S9C71311A0FED"),12,true));
            page.Children.Add(Tile(Text(L.T("S36114ADF26A8"), 13)));
            page.Children.Add(Text(L.T("S8A84E71EDB09"), 11, true));
        } else {
            page.Children.Add(Text("FrameTrail", 28)); page.Children.Add(Text(L.T("SAD37EFDE9DC7"), 13, true)); page.Children.Add(Tile(Text(L.T("S5A415D14D7EE"), 12, true)));
            page.Children.Add(Text(L.T("SCE0744E60649", typeof(App).Assembly.GetName().Version), 12, true));
            AddUpdateControls();
            var updateStatus=Text("",12,true);page.Children.Add(updateStatus);
            var diagnostics = Button(L.T("SDCCC0CB87D8A")); diagnostics.Click += async (_, _) => {
                try {
                    var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "FrameTrail-diagnostics" };
                    picker.FileTypeChoices.Add(L.T("S9167EE8B67DB"), new[] { ".json" });
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                    var file = await picker.PickSaveFileAsync(); if (file is not null) await File.WriteAllTextAsync(file.Path, SafeDiagnostics.Export());
                } catch { updateStatus.Text = L.T("SB3B6460D4056"); }
            }; page.Children.Add(diagnostics);
            foreach (var (label, file) in new[] { (L.T("SB6E77060DB53"), "user-guide.md"), (L.T("S9D06D61EE41F"), "privacy.md"), (L.T("SA596C619E4B8"), "release-notes.md") }) {
                var document = Button(label); string localizedFile=FrameTrail.Domain.AppText.Language=="en"?file.Replace(".md",".en.md"):file;
                document.Click += (_, _) => Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Docs", localizedFile)) { UseShellExecute = true }); page.Children.Add(document);
            }
            var notices = Button(L.T("S2D6D1145E673")); notices.Click += (_, _) => Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md")) { UseShellExecute = true }); page.Children.Add(notices);
        }
    }
    private Button Button(string text) => new() { Content = text, Style = (Style)Application.Current.Resources["EditorButton"], HorizontalAlignment = HorizontalAlignment.Stretch, Background = RecorderTheme.Brush(root, "EditorWell") };
    private Border Tile(UIElement child) => new() { Style = (Style)Application.Current.Resources["EditorTile"], Child = child };
}
