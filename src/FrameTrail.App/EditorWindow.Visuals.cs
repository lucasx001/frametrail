using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Workspace;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    private readonly StackPanel motionSettings = new() { Spacing = 16, Visibility = Visibility.Collapsed };
    private readonly StackPanel openingSettings = new() { Spacing = 16, Visibility = Visibility.Collapsed };
    private readonly StackPanel mockupSettings = new() { Spacing = 16, Visibility = Visibility.Collapsed };
    private readonly ComboBox effectPicker = new() { Header = "效果片段", DisplayMemberPath = "Label", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel effectProperties = new() { Spacing = 10 };
    private readonly NumberBox effectStart = VisualNumber("源入点（秒）", 0, 604800), effectEnd = VisualNumber("源出点（秒）", 0, 604800);
    private readonly EditorBarSlider effectScale = VisualBar(1, 4, 0.05, "×"), effectX = VisualBar(0, 99, 1, "%"), effectY = VisualBar(0, 99, 1, "%");
    private readonly EditorBarSlider effectWidth = VisualBar(1, 100, 1, "%"), effectHeight = VisualBar(1, 100, 1, "%"), effectStrength = VisualBar(1, 64, 1), effectOpacity = VisualBar(0, 100, 1, "%");
    private readonly EditorBarSlider effectAngleX = VisualBar(-35, 35, 1, "°"), effectAngleY = VisualBar(-35, 35, 1, "°");
    private readonly EditorBarSlider stickerRotation = VisualBar(-180, 180, 1, "°");
    private readonly EditorBarSlider springFrequency = VisualBar(1, 12, 0.5), springDamping = VisualBar(0.2, 2, 0.05);
    private readonly NumberBox effectEnter = VisualNumber("进入（秒）", 0, 5), effectExit = VisualNumber("退出（秒）", 0, 5);
    private readonly ComboBox effectMode = VisualChoice("遮挡方式", "遮挡", "像素化", "柔化"), effectEasing = VisualChoice("运动曲线", "平滑", "弹簧", "线性");
    private readonly ComboBox effectAttachment = VisualChoice("附着位置", "跟随画面", "固定画布"), effectTarget = VisualChoice("动画对象", "屏幕", "摄像头");
    private readonly ComboBox introStyle = VisualChoice("开场方式", "淡入", "上移", "缩放", "滑入"), cameraShape = VisualChoice("摄像头形状", "圆角", "矩形", "圆形");
    private readonly NumberBox introDuration = VisualNumber("进入时长（秒）", 0, 5);
    private readonly EditorBarSlider cursorSmooth = VisualBar(0, 0.5, 0.01, "s");
    private readonly TextBox stickerText = new() { Header = "文字", Text = "重点", PlaceholderText = "输入贴图文字" };
    private readonly TextBlock visualAssetLabel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<FrameworkElement, string[]> visualVisibility = new();
    private string? selectedEffect, visualAssetPath;
    private SourceEffect? copiedEffect;
    private bool loadingVisuals;
    private sealed class EffectEntry(string id, string label) : System.ComponentModel.INotifyPropertyChanged
    {
        public string Id { get; } = id;
        private string label = label;
        public string Label { get => label; set { if (label == value) return; label = value; PropertyChanged?.Invoke(this, new(nameof(Label))); } }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
    private static readonly string[] introStyles = { "fade", "rise", "scale", "slide" };
    private static string EffectName(string kind) => kind switch { "zoom" => "缩放", "tilt" => "透视", "mask" => "遮挡", "focus" => "聚焦", "sticker" => "贴图", "intro" => "开场", _ => kind };
    private static NumberBox VisualNumber(string label, double min, double max) => new() { Header = label, Minimum = min, Maximum = max, Value = 0, SmallChange = 0.1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
    private static EditorBarSlider VisualBar(double min, double max, double step, string suffix = "") => new() { Minimum = min, Maximum = max, StepFrequency = step, Value = min, Suffix = suffix };
    private static ComboBox VisualChoice(string label, params string[] items)
    { var box = new ComboBox { Header = label, HorizontalAlignment = HorizontalAlignment.Stretch }; foreach (var item in items) box.Items.Add(item); box.SelectedIndex = 0; return box; }
    private Border VisualTile(params UIElement[] children)
    { var content = new StackPanel { Spacing = 10 }; foreach (var child in children) content.Children.Add(child); return new Border { Style = (Style)Application.Current.Resources["EditorTile"], Child = content }; }
    private TextBlock VisualLabel(string text) => new() { Text = text, Style = (Style)Root.Resources["SectionLabel"] };
    private Button VisualButton(string text, Func<Task> action, string id)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(button, text); AutomationProperties.SetAutomationId(button, id);
        button.Click += async (_, _) => await PerformAsync(action); return button;
    }
    private void InitializeVisuals()
    {
        var host = (StackPanel)InspectorScroll.Content; host.Children.Add(motionSettings); host.Children.Add(openingSettings);
        PictureSettings.Children.Add(mockupSettings);
        AutomationProperties.SetAutomationId(effectPicker, "editor.effect.picker");
        motionSettings.Children.Add(VisualLabel("运镜与效果"));
        var add = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        add.ColumnDefinitions.Add(new()); add.ColumnDefinitions.Add(new());
        var kinds = new[] { "zoom", "mask", "focus", "tilt", "sticker" };
        for (int i = 0; i < kinds.Length; i++) { if (i % 2 == 0) add.RowDefinitions.Add(new()); var kind = kinds[i]; var button = VisualButton("添加" + EffectName(kind), () => AddVisualAsync(kind), "editor.effect.add." + kind); Grid.SetColumn(button, i % 2); Grid.SetRow(button, i / 2); add.Children.Add(button); }
        motionSettings.Children.Add(add); motionSettings.Children.Add(effectPicker); motionSettings.Children.Add(VisualTile(effectProperties));
        void Field(string label, FrameworkElement control, params string[] supported)
        {
            var row = new StackPanel { Spacing = 6 }; if (label.Length > 0) row.Children.Add(new TextBlock { Text = label, Style = (Style)Root.Resources["SmallLabel"] }); row.Children.Add(control); effectProperties.Children.Add(row); visualVisibility[row] = supported;
            string key = control == effectStart ? "start" : control == effectEnd ? "end" : control == effectX ? "x" : control == effectY ? "y" : control == effectScale ? "scale" : label.Length > 0 ? label : control is Control c ? c.GetType().Name + "." + effectProperties.Children.Count : "field." + effectProperties.Children.Count;
            if (string.IsNullOrEmpty(AutomationProperties.GetAutomationId(control))) AutomationProperties.SetAutomationId(control, "editor.effect." + key);
            if (label.Length > 0) AutomationProperties.SetName(control, "效果" + label);
        }
        Field("", effectStart); Field("", effectEnd); Field("缩放", effectScale, "zoom");
        Field("X", effectX, "zoom", "mask", "focus", "sticker"); Field("Y", effectY, "zoom", "mask", "focus", "sticker");
        Field("宽度", effectWidth, "mask", "focus", "sticker"); Field("高度", effectHeight, "mask", "focus", "sticker");
        Field("", effectMode, "mask"); Field("强度", effectStrength, "mask", "focus"); Field("不透明度", effectOpacity, "mask", "focus", "sticker");
        Field("X 轴倾斜", effectAngleX, "tilt"); Field("Y 轴倾斜", effectAngleY, "tilt");
        Field("旋转", stickerRotation, "sticker");
        Field("", effectAttachment, "mask", "focus", "sticker"); Field("", effectEasing, "zoom", "tilt", "intro");
        Field("弹簧频率", springFrequency, "zoom", "tilt", "intro"); Field("弹簧阻尼", springDamping, "zoom", "tilt", "intro");
        Field("", effectEnter, "zoom", "tilt", "intro"); Field("", effectExit, "zoom", "tilt", "intro"); Field("", effectTarget, "intro", "tilt");
        Field("", stickerText, "sticker"); Field("", visualAssetLabel, "sticker");
        Field("", VisualButton("选择贴图图片", async () => { var path = await PickVisualAsync(false); if (path is not null && CurrentEffect is { } prior) { var p = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(prior.Parameters.GetRawText())!; p["path"] = JsonSerializer.SerializeToElement(path); await editor.UpsertEffectAsync(prior with { Parameters = JsonSerializer.SerializeToElement(p) }); } }, "editor.effect.image"), "sticker");
        Field("", VisualButton("移除图片，使用文字", async () => { if (CurrentEffect is { } prior) { var p = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(prior.Parameters.GetRawText())!; p.Remove("path"); await editor.UpsertEffectAsync(prior with { Parameters = JsonSerializer.SerializeToElement(p) }); } }, "editor.effect.text"), "sticker");
        effectProperties.Children.Add(VisualButton("复制效果", () => { copiedEffect = CurrentEffect; ShowNotice("效果已复制", "可粘贴到播放头所在位置。"); return Task.CompletedTask; }, "editor.effect.copy"));
        motionSettings.Children.Add(VisualButton("粘贴到播放头", PasteCopiedVisualAsync, "editor.effect.paste"));
        effectProperties.Children.Add(VisualButton("删除效果", async () => { if (selectedEffect is not null) await editor.DeleteEffectAsync(selectedEffect); }, "editor.effect.delete"));
        effectPicker.SelectionChanged += async (_, _) => { if (loadingVisuals) return; await SafeAsync(async () => { await FlushVisualChangesAsync(); selectedEffect = (effectPicker.SelectedItem as EffectEntry)?.Id; LoadSelectedVisual(); timeline.SelectVisual(selectedEffect); }); };
        timeline.SelectEffect += async id => { await SafeAsync(async () => { await FlushVisualChangesAsync(); selectedEffect = id; ShowInspector(2); RefreshVisualInspector(); }); };
        openingSettings.Children.Add(VisualLabel("屏幕开场")); introDuration.Value = 0.7;
        openingSettings.Children.Add(VisualTile(introStyle, introDuration, VisualButton("应用开场动画", () => ApplyOpeningAsync(false), "editor.intro.apply"), VisualButton("移除开场动画", async () => { if (editor.Snapshot.Effects.Any(e => e.Id == "opening-screen")) await editor.DeleteEffectAsync("opening-screen"); }, "editor.intro.remove")));
        openingSettings.Children.Add(VisualTile(VisualButton("添加摄像头出入场", () => ApplyOpeningAsync(true), "editor.camera.animation")));
        CursorSettings.Children.Add(VisualTile(VisualLabel("光标平滑"), cursorSmooth));
        InitializeCursor();
        cursorSmooth.Committed += async (_, _) => { if (!loadingVisuals && ready) await PerformAsync(() => editor.ExecuteAsync(new { op = "settings", editor = (editor.Snapshot.Editor ?? new()) with { CursorSmoothing = cursorSmooth.Value } })); };
        CameraSettings.Children.Add(VisualTile(cameraShape));
        cameraShape.SelectionChanged += async (_, _) => { if (!loadingVisuals && ready && !working) await PerformAsync(() => editor.ExecuteAsync(new { op = "settings", editor = (editor.Snapshot.Editor ?? new()) with { CameraShape = new[] { "rounded", "rectangle", "circle" }[Math.Max(0, cameraShape.SelectedIndex)] } })); };
        InitializeMockups();
        BackgroundSettings.Children.Add(VisualTile(VisualButton("导入背景图片", async () => { var path = await PickVisualAsync(false); if (path is not null) await SetBackdropAsync("image", path); }, "editor.background.image"), VisualButton("导入循环视频", async () => { var path = await PickVisualAsync(true); if (path is not null) await SetBackdropAsync("video", path); }, "editor.background.video"), VisualButton("使用渐变背景", () => SetBackdropAsync("gradient"), "editor.background.gradient")));
        InitializeLiveVisuals();
    }
    private long SourcePosition => mapper.MapOutputTime(position)?.Ticks ?? 0;
    private SourceEffect? CurrentEffect => editor.Snapshot.Effects.FirstOrDefault(e => e.Id == selectedEffect);
    private async Task PasteCopiedVisualAsync()
    {
        if (copiedEffect is null) return;
        await editor.PasteEffectAsync(copiedEffect, SourcePosition);
        selectedEffect = editor.Snapshot.Effects.Last().Id;
    }
    private async Task AddVisualAsync(string kind)
    {
        long start = SourcePosition, duration = Math.Min(20_000_000, VisualResources.Ticks(editor.Snapshot.SourceDuration) - start);
        var value = new SourceEffect { Id = "effect-" + Guid.NewGuid().ToString("N"), Kind = kind, SourceStart = MediaTime.FromTicks(start), SourceDuration = MediaTime.FromTicks(Math.Max(1, duration)), Parameters = JsonSerializer.SerializeToElement(new { origin = "manual", text = kind == "sticker" ? "重点" : "" }) };
        selectedEffect = value.Id; await editor.UpsertEffectAsync(value);
    }
    private void RefreshVisualInspector()
    {
        loadingVisuals = true;
        try
        {
            var list = editor.Snapshot.Effects.Select(e => new EffectEntry(e.Id, $"{EffectName(e.Kind)} · {VisualResources.Ticks(e.SourceStart) / 10_000_000.0:0.##}s")).ToArray();
            effectPicker.ItemsSource = list; effectPicker.SelectedItem = list.FirstOrDefault(e => e.Id == selectedEffect) ?? list.FirstOrDefault(); selectedEffect = (effectPicker.SelectedItem as EffectEntry)?.Id;
            var settings = editor.Snapshot.Editor ?? new(); cursorSmooth.Value = settings.CursorSmoothing; cameraShape.SelectedIndex = Array.IndexOf(new[] { "rounded", "rectangle", "circle" }, settings.CameraShape);
            RefreshMockupInspector();
            var opening = editor.Snapshot.Effects.FirstOrDefault(e => e.Id == "opening-screen");
            if (opening is not null) { introStyle.SelectedIndex = Array.IndexOf(introStyles, StringValue(opening.Parameters, "style", "fade")); introDuration.Value = NumberValue(opening.Parameters, "enter", 0.7); }
        }
        finally { loadingVisuals = false; }
        LoadSelectedVisual(); timeline.SelectVisual(selectedEffect); UpdateBackdropTabs();
    }
    private static double NumberValue(JsonElement p, string key, double fallback) => p.TryGetProperty(key, out var value) && value.TryGetDouble(out var number) ? number : fallback;
    private static string StringValue(JsonElement p, string key, string fallback) => p.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : fallback;
    private void LoadSelectedVisual()
    {
        var effect = CurrentEffect; effectProperties.Visibility = effect is null ? Visibility.Collapsed : Visibility.Visible; if (effect is null) return;
        loadingVisuals = true;
        try
        {
            foreach (var (control, kinds) in visualVisibility) control.Visibility = kinds.Length == 0 || kinds.Contains(effect.Kind) ? Visibility.Visible : Visibility.Collapsed;
            var totalSeconds = VisualResources.Ticks(editor.Snapshot.SourceDuration) / 10_000_000.0;
            effectStart.Maximum = Math.Max(0, totalSeconds - 0.0000001); effectEnd.Maximum = totalSeconds;
            var p = effect.Parameters; effectStart.Value = VisualResources.Ticks(effect.SourceStart) / 10_000_000.0; effectEnd.Value = (VisualResources.Ticks(effect.SourceStart) + VisualResources.Ticks(effect.SourceDuration)) / 10_000_000.0;
            effectScale.Value = NumberValue(p, "scale", 1.6); effectX.Value = NumberValue(p, effect.Kind == "zoom" ? "focusX" : "x", effect.Kind == "zoom" ? 0.5 : 0.25) * 100; effectY.Value = NumberValue(p, effect.Kind == "zoom" ? "focusY" : "y", effect.Kind == "zoom" ? 0.5 : 0.25) * 100;
            effectWidth.Value = NumberValue(p, "width", 0.5) * 100; effectHeight.Value = NumberValue(p, "height", 0.5) * 100; effectStrength.Value = NumberValue(p, "strength", 16); effectOpacity.Value = NumberValue(p, "opacity", 1) * 100;
            effectAngleX.Value = NumberValue(p, "angleX", 0); effectAngleY.Value = NumberValue(p, "angleY", 0); effectMode.SelectedIndex = Array.IndexOf(new[] { "solid", "pixelate", "blur" }, StringValue(p, "mode", "solid"));
            effectEasing.SelectedIndex = Array.IndexOf(new[] { "cubic", "spring", "linear" }, StringValue(p, "easing", "cubic")); effectAttachment.SelectedIndex = StringValue(p, "attachment", "screen") == "canvas" ? 1 : 0; effectTarget.SelectedIndex = StringValue(p, "target", "screen") == "camera" ? 1 : 0;
            effectEnter.Value = NumberValue(p, "enter", effect.Kind is "zoom" or "tilt" or "intro" ? 0.7 : 0); effectExit.Value = NumberValue(p, "exit", effect.Kind is "zoom" or "tilt" ? 0.7 : 0);
            springFrequency.Value = NumberValue(p, "springFrequency", MotionDefaults.SpringFrequency); springDamping.Value = NumberValue(p, "damping", MotionDefaults.SpringDamping);
            stickerRotation.Value = NumberValue(p, "rotation", 0);
            stickerText.Text = StringValue(p, "text", "重点"); visualAssetPath = StringValue(p, "path", ""); visualAssetLabel.Text = string.IsNullOrEmpty(visualAssetPath) ? "可使用文字或导入图片" : Path.GetFileName(visualAssetPath);
        }
        finally { loadingVisuals = false; }
    }
    private SourceEffect? SelectedVisualDraft()
    {
        var prior = CurrentEffect; if (prior is null) return null;
        if (!TryVisualNumber(effectStart, out var startSeconds) || !TryVisualNumber(effectEnd, out var endSeconds) || !TryVisualNumber(effectEnter, out var enter) || !TryVisualNumber(effectExit, out var exit)) return null;
        if (endSeconds <= startSeconds || endSeconds > VisualResources.Ticks(editor.Snapshot.SourceDuration) / 10_000_000.0) return null;
        long total = VisualResources.Ticks(editor.Snapshot.SourceDuration), start = Math.Clamp((long)(startSeconds * 10_000_000), 0, total - 1), end = Math.Clamp((long)(endSeconds * 10_000_000), start + 1, total);
        var p = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(prior.Parameters.GetRawText())!;
        void Set(string name, object value) => p[name] = JsonSerializer.SerializeToElement(value);
        Set("origin", "edited"); Set("enter", enter); Set("exit", exit); Set("easing", new[] { "cubic", "spring", "linear" }[Math.Max(0, effectEasing.SelectedIndex)]);
        Set("springFrequency", springFrequency.Value); Set("damping", springDamping.Value);
        if (prior.Kind == "zoom") { Set("scale", effectScale.Value); Set("focusX", effectX.Value / 100); Set("focusY", effectY.Value / 100); p.Remove("focusPoints"); }
        if (prior.Kind is "mask" or "focus" or "sticker") { double x = effectX.Value / 100, y = effectY.Value / 100; Set("x", x); Set("y", y); Set("width", Math.Min(effectWidth.Value / 100, 1 - x)); Set("height", Math.Min(effectHeight.Value / 100, 1 - y)); Set("opacity", effectOpacity.Value / 100); Set("attachment", effectAttachment.SelectedIndex == 1 ? "canvas" : "screen"); }
        if (prior.Kind is "mask" or "focus") Set("strength", effectStrength.Value);
        if (prior.Kind == "mask") Set("mode", new[] { "solid", "pixelate", "blur" }[Math.Max(0, effectMode.SelectedIndex)]);
        if (prior.Kind == "tilt") { Set("angleX", effectAngleX.Value); Set("angleY", effectAngleY.Value); }
        if (prior.Kind is "intro" or "tilt") Set("target", effectTarget.SelectedIndex == 1 ? "camera" : "screen");
        if (prior.Kind == "sticker") { Set("text", stickerText.Text); Set("rotation", stickerRotation.Value); if (!string.IsNullOrEmpty(visualAssetPath)) Set("path", visualAssetPath); else p.Remove("path"); }
        return prior with { SourceStart = MediaTime.FromTicks(start), SourceDuration = MediaTime.FromTicks(end - start), Parameters = JsonSerializer.SerializeToElement(p) };
    }
    private Task ApplyOpeningAsync(bool camera)
    {
        var media = editor.Snapshot.Media.Where(m => m.Kind == MediaKind.Camera).OrderBy(m => VisualResources.Ticks(m.StartOffset)).ToArray();
        if (camera && media.Length == 0) throw new InvalidOperationException("当前工程没有摄像头素材。");
        long start = camera ? Math.Max(0, VisualResources.Ticks(media[0].StartOffset)) : 0;
        long end = camera ? Math.Min(VisualResources.Ticks(editor.Snapshot.SourceDuration), media.Max(m => VisualResources.Ticks(m.StartOffset) + VisualResources.Ticks(m.Duration))) : VisualResources.Ticks(editor.Snapshot.SourceDuration);
        return editor.UpsertEffectAsync(new() { Id = camera ? "opening-camera" : "opening-screen", Kind = "intro", SourceStart = MediaTime.FromTicks(start), SourceDuration = MediaTime.FromTicks(end - start), Parameters = JsonSerializer.SerializeToElement(new { target = camera ? "camera" : "screen", style = introStyles[Math.Max(0, introStyle.SelectedIndex)], enter = introDuration.Value, exit = camera ? 0.4 : 0, origin = "manual" }) });
    }
    private async Task<string?> PickVisualAsync(bool video)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker(); foreach (var extension in video ? new[] { ".mp4", ".mov", ".m4v" } : new[] { ".png", ".jpg", ".jpeg", ".bmp" }) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this)); var file = await picker.PickSingleFileAsync(); return file is null ? null : await editor.ImportVisualAsync(file.Path, video);
    }
    private Task SetBackdropAsync(string kind, string path = "") => editor.ExecuteAsync(new { op = "settings", editor = (editor.Snapshot.Editor ?? new()) with { Backdrop = new() { Kind = kind, Path = path } } });
    private async void Backdrop_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string kind) return;
        await PerformAsync(async () => { string path = ""; if (kind is "image" or "video") { var chosen = await PickVisualAsync(kind == "video"); if (chosen is null) return; path = chosen; } await SetBackdropAsync(kind, path); });
    }
    private void UpdateBackdropTabs()
    {
        string kind = editor.Snapshot.Editor?.Backdrop?.Kind ?? "solid";
        foreach (var button in BackgroundSourceTabs.Children.OfType<Button>()) {
            bool active = button.Tag as string == kind; button.Background = active ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["EditorBlue"] : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.Foreground = active ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["EditorBlueInk"] : RecorderTheme.Brush(Root, "EditorSecondary");
        }
    }
    private void ScenePreset_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout(); foreach (var (title, preset) in new[] { ("简洁", "clean"), ("演示工作室", "studio"), ("设备展示", "device") }) { var item = new MenuFlyoutItem { Text = title }; item.Click += async (_, _) => await PerformAsync(() => editor.ApplyPresetAsync(preset)); menu.Items.Add(item); }
        menu.Items.Add(new MenuFlyoutSeparator()); var manage = new MenuFlyoutItem { Text = "管理场景预设…" }; manage.Click += ManagePresets_Click; menu.Items.Add(manage);
        var save = new MenuFlyoutItem { Text = "保存到当前工程（兼容旧版）" }; save.Click += async (_, _) => await SafeAsync(editor.SaveVisualPresetAsync); menu.Items.Add(save);
        var load = new MenuFlyoutItem { Text = "应用已保存预设", IsEnabled = File.Exists(Path.Combine(editor.DirectoryPath, "assets", "presets", "saved.json")) }; load.Click += async (_, _) => await PerformAsync(editor.LoadVisualPresetAsync); menu.Items.Add(load); menu.ShowAt((FrameworkElement)sender);
    }
    private void FrameMockup_Click(object sender, RoutedEventArgs e) { ShowInspector(0); mockupPage = true; UpdateFrameNavigation(); _ = SafeAsync(async () => { await FlushVisualChangesAsync(); QueueMockupPoster(); }); }
}
