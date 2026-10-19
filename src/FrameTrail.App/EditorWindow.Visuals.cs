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
    private readonly ComboBox effectPicker = new() { Header = L.T("S4EC66BC665A0"), DisplayMemberPath = "Label", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel effectProperties = new() { Spacing = 10 };
    private readonly NumberBox effectStart = VisualNumber(L.T("S12610375F070"), 0, 604800), effectEnd = VisualNumber(L.T("S91692C46BF2D"), 0, 604800);
    private readonly EditorBarSlider effectScale = VisualBar(1, 4, 0.05, "×"), effectX = VisualBar(0, 99, 1, "%"), effectY = VisualBar(0, 99, 1, "%");
    private readonly EditorBarSlider effectWidth = VisualBar(1, 100, 1, "%"), effectHeight = VisualBar(1, 100, 1, "%"), effectStrength = VisualBar(1, 64, 1), effectOpacity = VisualBar(0, 100, 1, "%");
    private readonly EditorBarSlider effectAngleX = VisualBar(-35, 35, 1, "°"), effectAngleY = VisualBar(-35, 35, 1, "°");
    private readonly EditorBarSlider stickerRotation = VisualBar(-180, 180, 1, "°");
    private readonly EditorBarSlider springFrequency = VisualBar(1, 12, 0.5), springDamping = VisualBar(0.2, 2, 0.05);
    private readonly NumberBox effectEnter = VisualNumber(L.T("SB41BC22DF7C7"), 0, 5), effectExit = VisualNumber(L.T("SC96C1091A3A2"), 0, 5);
    private readonly ComboBox effectMode = VisualChoice(L.T("S38AAA0E9F139"), L.T("SAC7CE836857B"), L.T("SD34D1D9BA175"), L.T("S2C12D695A8EB")), effectEasing = VisualChoice(L.T("S5FB5B3244BDB"), L.T("SB3275D61C8F8"), L.T("SDF1A091F6DC1"), L.T("S078E4CAEDA5C"));
    private readonly ComboBox effectAttachment = VisualChoice(L.T("S24BF8AAD101C"), L.T("S54E1C4791059"), L.T("S4CB51E9E232E")), effectTarget = VisualChoice(L.T("SE0770CB96BEA"), L.T("S04D4ABA0AB5F"), L.T("SFAC308FBABB8"));
    private readonly ComboBox introStyle = VisualChoice(L.T("S4E0E5D9CF7B9"), L.T("SF72B83EABA1C"), L.T("SF853A70B1204"), L.T("SB762AEC769A3"), L.T("SBA77DBC46AEE")), cameraShape = VisualChoice(L.T("S2319A4DDA44C"), L.T("S22C4DB09D8DC"), L.T("S88B75A101595"), L.T("S81639A8A9BCE"));
    private readonly NumberBox introDuration = VisualNumber(L.T("SD96399EC8B17"), 0, 5);
    private readonly EditorBarSlider cursorSmooth = VisualBar(0, 0.5, 0.01, "s");
    private readonly TextBox stickerText = new() { Header = L.T("S14B69BD6EEB0"), Text = L.T("SBF3B83E701D4"), PlaceholderText = L.T("SA582C47FD22C") };
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
    private static string EffectName(string kind) => kind switch { "zoom" => L.T("SB762AEC769A3"), "tilt" => L.T("S17E14283A497"), "mask" => L.T("SAC7CE836857B"), "focus" => L.T("SED19BD5E2620"), "sticker" => L.T("S622DA0460277"), "intro" => L.T("SFD0D4B7258C2"), _ => kind };
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
        motionSettings.Children.Add(VisualLabel(L.T("S3D0C6B294146")));
        var add = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        add.ColumnDefinitions.Add(new()); add.ColumnDefinitions.Add(new());
        var kinds = new[] { "zoom", "mask", "focus", "tilt", "sticker" };
        for (int i = 0; i < kinds.Length; i++) { if (i % 2 == 0) add.RowDefinitions.Add(new()); var kind = kinds[i]; var button = VisualButton(L.T("AddEffect", EffectName(kind)), () => AddVisualAsync(kind), "editor.effect.add." + kind); Grid.SetColumn(button, i % 2); Grid.SetRow(button, i / 2); add.Children.Add(button); }
        motionSettings.Children.Add(add); motionSettings.Children.Add(effectPicker); motionSettings.Children.Add(VisualTile(effectProperties));
        void Field(string label, FrameworkElement control, params string[] supported)
        {
            var row = new StackPanel { Spacing = 6 }; if (label.Length > 0) row.Children.Add(new TextBlock { Text = label, Style = (Style)Root.Resources["SmallLabel"] }); row.Children.Add(control); effectProperties.Children.Add(row); visualVisibility[row] = supported;
            string key = control == effectStart ? "start" : control == effectEnd ? "end" : control == effectX ? "x" : control == effectY ? "y" : control == effectScale ? "scale" : label.Length > 0 ? label : control is Control c ? c.GetType().Name + "." + effectProperties.Children.Count : "field." + effectProperties.Children.Count;
            if (string.IsNullOrEmpty(AutomationProperties.GetAutomationId(control))) AutomationProperties.SetAutomationId(control, "editor.effect." + key);
            if (label.Length > 0) AutomationProperties.SetName(control, L.T("EffectField", label));
        }
        Field("", effectStart); Field("", effectEnd); Field(L.T("SB762AEC769A3"), effectScale, "zoom");
        Field("X", effectX, "zoom", "mask", "focus", "sticker"); Field("Y", effectY, "zoom", "mask", "focus", "sticker");
        Field(L.T("S3B730DA0AD1A"), effectWidth, "mask", "focus", "sticker"); Field(L.T("S968DD036CBB1"), effectHeight, "mask", "focus", "sticker");
        Field("", effectMode, "mask"); Field(L.T("SA2846644A638"), effectStrength, "mask", "focus"); Field(L.T("S04405E4A9FFD"), effectOpacity, "mask", "focus", "sticker");
        Field(L.T("SA4B0DE41E601"), effectAngleX, "tilt"); Field(L.T("SD82466A62832"), effectAngleY, "tilt");
        Field(L.T("S68EB90FA809E"), stickerRotation, "sticker");
        Field("", effectAttachment, "mask", "focus", "sticker"); Field("", effectEasing, "zoom", "tilt", "intro");
        Field(L.T("SC19C240FA18F"), springFrequency, "zoom", "tilt", "intro"); Field(L.T("SDE95E14AC3BD"), springDamping, "zoom", "tilt", "intro");
        Field("", effectEnter, "zoom", "tilt", "intro"); Field("", effectExit, "zoom", "tilt", "intro"); Field("", effectTarget, "intro", "tilt");
        Field("", stickerText, "sticker"); Field("", visualAssetLabel, "sticker");
        Field("", VisualButton(L.T("SA24E971FE950"), async () => { var path = await PickVisualAsync(false); if (path is not null && CurrentEffect is { } prior) { var p = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(prior.Parameters.GetRawText())!; p["path"] = JsonSerializer.SerializeToElement(path); await editor.UpsertEffectAsync(prior with { Parameters = JsonSerializer.SerializeToElement(p) }); } }, "editor.effect.image"), "sticker");
        Field("", VisualButton(L.T("S164BCC78FC98"), async () => { if (CurrentEffect is { } prior) { var p = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(prior.Parameters.GetRawText())!; p.Remove("path"); await editor.UpsertEffectAsync(prior with { Parameters = JsonSerializer.SerializeToElement(p) }); } }, "editor.effect.text"), "sticker");
        effectProperties.Children.Add(VisualButton(L.T("S4D269086BCC1"), () => { copiedEffect = CurrentEffect; ShowNotice(L.T("S61639C86E5FF"), L.T("S91952A32E4CC")); return Task.CompletedTask; }, "editor.effect.copy"));
        motionSettings.Children.Add(VisualButton(L.T("SF87CEC6C5646"), PasteCopiedVisualAsync, "editor.effect.paste"));
        effectProperties.Children.Add(VisualButton(L.T("S5FC2029B8F1F"), PasteAttributesAsync, "editor.effect.pasteAttributes"));
        effectProperties.Children.Add(VisualButton(L.T("SEEB94B34ECA0"), async () => { if (selectedEffect is not null) await editor.DeleteEffectAsync(selectedEffect); }, "editor.effect.delete"));
        effectPicker.SelectionChanged += async (_, _) => { if (loadingVisuals) return; await SafeAsync(async () => { await FlushVisualChangesAsync(); selectedEffect = (effectPicker.SelectedItem as EffectEntry)?.Id; LoadSelectedVisual(); timeline.SelectVisual(selectedEffect); }); };
        timeline.SelectEffect += async id => { await SafeAsync(async () => { await FlushVisualChangesAsync(); selectedEffect = id; ShowInspector(2); RefreshVisualInspector(); }); };
        openingSettings.Children.Add(VisualLabel(L.T("SD50922830D9E"))); introDuration.Value = 0.7;
        openingSettings.Children.Add(VisualTile(introStyle, introDuration, VisualButton(L.T("SE1B2D5A70D59"), () => ApplyOpeningAsync(false), "editor.intro.apply"), VisualButton(L.T("SBFA8C11031E5"), async () => { if (editor.Snapshot.Effects.Any(e => e.Id == "opening-screen")) await editor.DeleteEffectAsync("opening-screen"); }, "editor.intro.remove")));
        openingSettings.Children.Add(VisualTile(VisualButton(L.T("SE87613AD8A19"), () => ApplyOpeningAsync(true), "editor.camera.animation")));
        CursorSettings.Children.Add(VisualTile(VisualLabel(L.T("SF6A8CEDF8C36")), cursorSmooth));
        InitializeCursor();
        cursorSmooth.Committed += async (_, _) => { if (!loadingVisuals && ready) await PerformAsync(() => editor.ExecuteAsync(new { op = "settings", editor = (editor.Snapshot.Editor ?? new()) with { CursorSmoothing = cursorSmooth.Value } })); };
        CameraSettings.Children.Add(VisualTile(cameraShape));
        cameraShape.SelectionChanged += async (_, _) => { if (!loadingVisuals && ready && !working) await PerformAsync(() => editor.ExecuteAsync(new { op = "settings", editor = (editor.Snapshot.Editor ?? new()) with { CameraShape = new[] { "rounded", "rectangle", "circle" }[Math.Max(0, cameraShape.SelectedIndex)] } })); };
        InitializeMockups();
        InitializeWallpapers();
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
        var value = new SourceEffect { Id = "effect-" + Guid.NewGuid().ToString("N"), Kind = kind, SourceStart = MediaTime.FromTicks(start), SourceDuration = MediaTime.FromTicks(Math.Max(1, duration)), Parameters = JsonSerializer.SerializeToElement(new { origin = "manual", text = kind == "sticker" ? L.T("SBF3B83E701D4") : "" }) };
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
        LoadSelectedVisual(); timeline.SelectVisual(inspectorTab==2?selectedEffect:null); UpdateBackdropTabs(); QueueCanvasGeometry();
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
            stickerText.Text = StringValue(p, "text", L.T("SBF3B83E701D4")); visualAssetPath = StringValue(p, "path", ""); visualAssetLabel.Text = string.IsNullOrEmpty(visualAssetPath) ? L.T("SE2B3B077BDC5") : Path.GetFileName(visualAssetPath);
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
        if (camera && media.Length == 0) throw new InvalidOperationException(L.T("S6BA9D4034385"));
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
        if (kind == "image") { UpdateBackdropTabs("image"); return; }
        await PerformAsync(async () => { string path = ""; if (kind is "image" or "video") { var chosen = await PickVisualAsync(kind == "video"); if (chosen is null) return; path = chosen; } await SetBackdropAsync(kind, path); });
    }
    private void UpdateBackdropTabs(string? page = null)
    {
        string kind = page ?? editor.Snapshot.Editor?.Backdrop?.Kind ?? "solid";
        foreach (var button in BackgroundSourceTabs.Children.OfType<Button>()) {
            bool active = button.Tag as string == kind; button.Background = active ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["EditorBlue"] : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.Foreground = active ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["EditorBlueInk"] : RecorderTheme.Brush(Root, "EditorSecondary");
        }
        WallpaperPicker.Visibility = kind == "image" ? Visibility.Visible : Visibility.Collapsed;
        BackgroundColors.Visibility = kind is "solid" or "gradient" ? Visibility.Visible : Visibility.Collapsed;
        ImportBackgroundVideoButton.Visibility = kind == "video" ? Visibility.Visible : Visibility.Collapsed;
        RefreshWallpaperSelection();
    }
    private void ScenePreset_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout(); foreach (var (title, preset) in new[] { (L.T("SAA55960E4B02"), "clean"), (L.T("S36ACA0C4EB8C"), "studio"), (L.T("SEE2AF30CF05B"), "device") }) { var item = new MenuFlyoutItem { Text = title }; item.Click += async (_, _) => await PerformAsync(() => editor.ApplyPresetAsync(preset)); menu.Items.Add(item); }
        menu.Items.Add(new MenuFlyoutSeparator()); var manage = new MenuFlyoutItem { Text = L.T("S2A9148FF8A0F") }; manage.Click += ManagePresets_Click; menu.Items.Add(manage);
        var save = new MenuFlyoutItem { Text = L.T("S4021CDFD0E72") }; save.Click += async (_, _) => await SafeAsync(editor.SaveVisualPresetAsync); menu.Items.Add(save);
        var load = new MenuFlyoutItem { Text = L.T("S6B8C76468E4E"), IsEnabled = File.Exists(Path.Combine(editor.DirectoryPath, "assets", "presets", "saved.json")) }; load.Click += async (_, _) => await PerformAsync(editor.LoadVisualPresetAsync); menu.Items.Add(load); menu.ShowAt((FrameworkElement)sender);
    }
    private void FrameMockup_Click(object sender, RoutedEventArgs e) { ShowInspector(0); mockupPage = true; UpdateFrameNavigation(); _ = SafeAsync(async () => { await FlushVisualChangesAsync(); QueueMockupPoster(); }); }
}
