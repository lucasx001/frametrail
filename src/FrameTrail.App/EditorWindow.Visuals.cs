using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Workspace;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    private readonly StackPanel motionSettings = new() { Spacing = 14, Visibility = Visibility.Collapsed };
    private readonly StackPanel openingSettings = new() { Spacing = 14, Visibility = Visibility.Collapsed };
    private readonly StackPanel mockupSettings = new() { Spacing = 14, Visibility = Visibility.Collapsed };
    private readonly ComboBox effectPicker = new() { DisplayMemberPath = "Label", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel effectProperties = new() { Spacing = 10 };
    private readonly StackPanel effectSelectedSection = new() { Spacing = 0, Visibility = Visibility.Collapsed };
    private readonly Border effectEmptyCard = new() { Visibility = Visibility.Visible };
    private readonly Border effectKindBadge = new() { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center };
    private readonly EditorGlyph effectKindGlyph = new() { Kind = "Zoom", Width = 14, Height = 14 };
    private readonly TextBlock effectCountLabel = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel springParamsPanel = new() { Spacing = 8 };
    private readonly List<(Button Button, double Depth)> zoomDepthChips = new();
    private readonly List<(Button Button, string Kind, EditorGlyph Glyph)> effectAddButtons = new();
    private readonly List<(Button Button, int Index)> introStyleChips = new();
    private readonly List<Border> visualDividers = new();
    private readonly List<(EditorGlyph Glyph, TextBlock Label, string StyleKey)> visualButtonContents = new();
    private Button? removeScreenOpeningButton, applyCameraOpeningButton, removeCameraOpeningButton;
    private Border? effectEmptyBadge, openingScreenBadge, openingCameraBadge;
    private EditorGlyph? effectEmptyGlyph;
    private TextBlock? openingScreenBadgeText, openingCameraBadgeText, openingCameraDescText;
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
    private readonly ComboBox transitionStyle = VisualChoice(L.T("TransitionStyleLabel"), L.T("TransitionStyleCrossfade"), L.T("TransitionStyleFadeBlack"), L.T("TransitionStyleFadeWhite"), L.T("TransitionStyleSlideLeft"), L.T("TransitionStyleSlideRight"), L.T("TransitionStyleWipeLeft"), L.T("TransitionStyleWipeRight"), L.T("TransitionStyleZoomIn"));
    private readonly EditorBarSlider transitionDuration = VisualBar(0.1, 3.0, 0.05, "s");
    private readonly NumberBox introDuration = VisualNumber(L.T("SD96399EC8B17"), 0, 5);
    private readonly EditorBarSlider cursorSmooth = VisualBar(0, 0.5, 0.01, "s");
    private readonly EditorBarSlider motionBlurSlider = VisualBar(0, 100, 5, "%");
    private readonly TextBox stickerText = new() { Header = L.T("S14B69BD6EEB0"), Text = L.T("SBF3B83E701D4"), PlaceholderText = L.T("SA582C47FD22C") };
    private readonly ComboBox stickerShape = VisualChoice(L.T("StickerShapeLabel"), L.T("StickerShapePill"), L.T("StickerShapeArrow"), L.T("StickerShapeRect"), L.T("StickerShapeCircle"), L.T("StickerShapeBadge"), L.T("StickerShapeKeycap"));
    private readonly ComboBox stickerColor = VisualChoice(
        L.T("StickerColorLabel"),
        AppText.Language == "en" ? "Blue (#38BDF8)" : "蓝色 (#38BDF8)",
        AppText.Language == "en" ? "Yellow (#FACC15)" : "黄色 (#FACC15)",
        AppText.Language == "en" ? "Red (#FB7185)" : "红色 (#FB7185)",
        AppText.Language == "en" ? "Green (#4ADE80)" : "绿色 (#4ADE80)",
        AppText.Language == "en" ? "Purple (#A855F7)" : "紫色 (#A855F7)");
    private readonly TextBlock visualAssetLabel = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
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
    private static readonly string[] introStyleLabels = { L.T("SF72B83EABA1C"), L.T("SF853A70B1204"), L.T("SB762AEC769A3"), L.T("SBA77DBC46AEE") };
    private static readonly string[] transitionStyles = { "crossfade", "fadeBlack", "fadeWhite", "slideLeft", "slideRight", "wipeLeft", "wipeRight", "zoomIn" };
    private static readonly string[] stickerShapes = { "pill", "arrow", "rect", "circle", "badge", "keycap" };
    private static readonly string[] stickerColors = { "#38BDF8", "#FACC15", "#FB7185", "#4ADE80", "#A855F7" };
    private static readonly double[] zoomPresetDepths = { 1.25, 1.5, 1.8, 2.2, 3.0 };
    private static string EffectName(string kind) => kind switch { "zoom" => L.T("SB762AEC769A3"), "tilt" => L.T("S17E14283A497"), "mask" => L.T("SAC7CE836857B"), "focus" => L.T("SED19BD5E2620"), "sticker" => L.T("S622DA0460277"), "intro" => L.T("SFD0D4B7258C2"), "transition" => L.T("TransitionName"), _ => kind };
    private static NumberBox VisualNumber(string label, double min, double max) => new() { Header = label, Minimum = min, Maximum = max, Value = 0, SmallChange = 0.1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
    private static EditorBarSlider VisualBar(double min, double max, double step, string suffix = "") => new() { Minimum = min, Maximum = max, StepFrequency = step, Value = min, Suffix = suffix };
    private static ComboBox VisualChoice(string label, params string[] items)
    { var box = new ComboBox { Header = label, HorizontalAlignment = HorizontalAlignment.Stretch }; foreach (var item in items) box.Items.Add(item); box.SelectedIndex = 0; return box; }
    private Border VisualTile(params UIElement[] children)
    { var content = new StackPanel { Spacing = 10 }; foreach (var child in children) content.Children.Add(child); return new Border { Style = (Style)Application.Current.Resources["EditorTile"], Child = content }; }
    private TextBlock VisualLabel(string text) => new() { Text = text, Style = (Style)Root.Resources["SectionLabel"] };
    private Button VisualButton(string text, Func<Task> action, string id, string styleKey = "EditorSecondaryButton", string? glyphKind = null)
    {
        var button = new Button { Style = (Style)Application.Current.Resources[styleKey], HorizontalAlignment = HorizontalAlignment.Stretch };
        if (glyphKind is not null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
            var glyph = new EditorGlyph { Kind = glyphKind, Width = 13, Height = 13 };
            row.Children.Add(glyph);
            var label = new TextBlock { Text = text, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            if (styleKey == "EditorPrimaryButton") label.FontFamily = (FontFamily)Application.Current.Resources["RecorderSemiboldFont"];
            row.Children.Add(label);
            visualButtonContents.Add((glyph, label, styleKey));
            ApplyVisualButtonContentTheme(glyph, label, styleKey);
            button.Content = row;
        }
        else
        {
            button.Content = text;
        }
        AutomationProperties.SetName(button, text); AutomationProperties.SetAutomationId(button, id);
        button.Click += async (_, _) => await PerformAsync(action); return button;
    }

    private (string GlyphKind, Brush BgBrush, Brush FgBrush) EffectKindStyle(string kind)
    {
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        return kind switch
        {
            "zoom" => ("Zoom", RecorderTheme.Brush(Root, "EditorAccentSoft"), RecorderTheme.Brush(Root, "EditorAccent")),
            "transition" => ("Transition", new SolidColorBrush(Windows.UI.Color.FromArgb(36, 168, 85, 247)), Hex(0xA855F7u)),
            "focus" => ("Sparkle", new SolidColorBrush(Windows.UI.Color.FromArgb(34, 255, 184, 48)), Hex(dark ? 0xFFB830u : 0xD97706u)),
            "mask" => ("Eye", new SolidColorBrush(Windows.UI.Color.FromArgb(34, 255, 107, 146)), Hex(0xFF6B92u)),
            "tilt" => ("Layout", new SolidColorBrush(Windows.UI.Color.FromArgb(34, 255, 133, 80)), Hex(0xFF8550u)),
            "sticker" => ("Scene", new SolidColorBrush(Windows.UI.Color.FromArgb(34, 181, 230, 85)), Hex(dark ? 0xB5E655u : 0x65A30Du)),
            "intro" => ("Opening", new SolidColorBrush(Windows.UI.Color.FromArgb(32, 61, 219, 176)), Hex(0x008F68u)),
            _ => ("Layers", RecorderTheme.Brush(Root, "EditorWell"), RecorderTheme.Brush(Root, "EditorSecondary"))
        };
    }

    private Border MakeVisualDivider()
    {
        var line = new Border { Height = 1, Background = RecorderTheme.Brush(Root, "EditorLine"), Margin = new Thickness(0, 2, 0, 2) };
        visualDividers.Add(line);
        return line;
    }

    private (Border Badge, TextBlock Text) CreateStatusBadge()
    {
        var label = new TextBlock { FontSize = 10.5, FontFamily = (FontFamily)Application.Current.Resources["RecorderSemiboldFont"], VerticalAlignment = VerticalAlignment.Center };
        var badge = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 2, 8, 2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Child = label };
        return (badge, label);
    }

    private void StyleStatusBadge(Border? badge, TextBlock? text, bool active)
    {
        if (badge is null || text is null) return;
        text.Text = active ? L.T("OpeningStatusActive") : L.T("OpeningStatusOff");
        if (active)
        {
            badge.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 0, 143, 104));
            text.Foreground = RecorderTheme.Brush(Root, "EditorSuccess");
        }
        else
        {
            badge.Background = RecorderTheme.Brush(Root, "EditorWell");
            text.Foreground = RecorderTheme.Brush(Root, "EditorTertiary");
        }
    }

    private void InitializeVisuals()
    {
        var host = (StackPanel)InspectorScroll.Content; host.Children.Add(motionSettings); host.Children.Add(openingSettings);
        PictureSettings.Children.Add(mockupSettings);
        AutomationProperties.SetName(effectPicker, L.T("S4EC66BC665A0"));
        AutomationProperties.SetAutomationId(effectPicker, "editor.effect.picker");

        // ── 1. Add at Playhead (3×2 OpenScreen-style compact action grid) ──
        var addSection = new StackPanel();
        addSection.Children.Add(VisualLabel(L.T("MotionAddSection")));
        var addGrid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        for (int c = 0; c < 3; c++) addGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var kinds = new[] { "zoom", "transition", "focus", "mask", "tilt", "sticker" };
        for (int i = 0; i < kinds.Length; i++)
        {
            if (i % 3 == 0) addGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var kind = kinds[i];
            var (glyphKind, _, fgBrush) = EffectKindStyle(kind);
            var glyph = new EditorGlyph { Kind = glyphKind, Width = 13, Height = 13, Foreground = fgBrush };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(glyph);
            content.Children.Add(new TextBlock { Text = EffectName(kind), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button
            {
                Style = (Style)Application.Current.Resources["EditorSecondaryButton"],
                Content = content,
                Height = 34,
                Padding = new Thickness(6, 0, 6, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            string fullLabel = L.T("AddEffect", EffectName(kind));
            AutomationProperties.SetName(button, fullLabel);
            AutomationProperties.SetAutomationId(button, "editor.effect.add." + kind);
            ToolTipService.SetToolTip(button, fullLabel);
            button.Click += async (_, _) => await PerformAsync(() => AddVisualAsync(kind));
            Grid.SetColumn(button, i % 3); Grid.SetRow(button, i / 3);
            addGrid.Children.Add(button);
            effectAddButtons.Add((button, kind, glyph));
        }
        addSection.Children.Add(new Border { Style = (Style)Application.Current.Resources["EditorTile"], Child = addGrid });
        motionSettings.Children.Add(addSection);

        // ── 2A. Empty State Card (when no effects exist) ──
        var emptyStack = new StackPanel { Spacing = 10, Margin = new Thickness(4, 14, 4, 14), HorizontalAlignment = HorizontalAlignment.Center };
        effectEmptyGlyph = new EditorGlyph { Kind = "Zoom", Width = 20, Height = 20, Foreground = RecorderTheme.Brush(Root, "EditorAccent") };
        effectEmptyBadge = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(22), Background = RecorderTheme.Brush(Root, "EditorAccentSoft"), HorizontalAlignment = HorizontalAlignment.Center, Child = effectEmptyGlyph };
        emptyStack.Children.Add(effectEmptyBadge);
        emptyStack.Children.Add(new TextBlock
        {
            Text = L.T("MotionEmptyTitle"),
            FontFamily = (FontFamily)Application.Current.Resources["RecorderSemiboldFont"],
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        emptyStack.Children.Add(new TextBlock
        {
            Text = L.T("MotionEmptyDesc"),
            Style = (Style)Root.Resources["SmallLabel"],
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 250
        });
        var emptyCta = VisualButton(L.T("MotionEmptyPrimaryCta"), () => AddVisualAsync("zoom"), "editor.effect.empty.addZoom", "EditorPrimaryButton", "Plus");
        emptyCta.HorizontalAlignment = HorizontalAlignment.Center;
        emptyCta.Height = 34;
        emptyCta.Padding = new Thickness(16, 0, 16, 0);
        emptyCta.CornerRadius = new CornerRadius(10);
        emptyCta.Margin = new Thickness(0, 4, 0, 0);
        emptyStack.Children.Add(emptyCta);
        effectEmptyCard.Style = (Style)Application.Current.Resources["EditorTile"];
        effectEmptyCard.Child = emptyStack;
        motionSettings.Children.Add(effectEmptyCard);

        // ── 2B. Selected Effect Inspector Card (contextual) ──
        var selectedHeader = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        selectedHeader.Children.Add(VisualLabel(L.T("MotionSelectedSection")));
        effectCountLabel.HorizontalAlignment = HorizontalAlignment.Right;
        effectCountLabel.Foreground = RecorderTheme.Brush(Root, "EditorTertiary");
        effectCountLabel.Margin = new Thickness(0, 0, 4, 6);
        selectedHeader.Children.Add(effectCountLabel);
        effectSelectedSection.Children.Add(selectedHeader);

        effectKindBadge.Child = effectKindGlyph;
        var selectorRow = new Grid { ColumnSpacing = 8 };
        selectorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        selectorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        selectorRow.Children.Add(effectKindBadge);
        Grid.SetColumn(effectPicker, 1);
        selectorRow.Children.Add(effectPicker);

        var selectedCardContent = new StackPanel { Spacing = 10 };
        selectedCardContent.Children.Add(selectorRow);
        selectedCardContent.Children.Add(MakeVisualDivider());
        selectedCardContent.Children.Add(effectProperties);
        effectSelectedSection.Children.Add(new Border { Style = (Style)Application.Current.Resources["EditorTile"], Child = selectedCardContent });
        motionSettings.Children.Add(effectSelectedSection);

        int fieldCount = 0;
        void RegisterControlAutomation(string label, FrameworkElement control)
        {
            fieldCount++;
            string key = control == effectStart ? "start" : control == effectEnd ? "end" : control == effectX ? "x" : control == effectY ? "y" : control == effectScale ? "scale" : label.Length > 0 ? label : control is Control c ? c.GetType().Name + "." + fieldCount : "field." + fieldCount;
            if (string.IsNullOrEmpty(AutomationProperties.GetAutomationId(control))) AutomationProperties.SetAutomationId(control, "editor.effect." + key);
            if (label.Length > 0) AutomationProperties.SetName(control, L.T("EffectField", label));
        }
        void Field(string label, FrameworkElement control, StackPanel target, params string[] supported)
        {
            var row = new StackPanel { Spacing = 4 };
            if (label.Length > 0) row.Children.Add(new TextBlock { Text = label, Style = (Style)Root.Resources["SmallLabel"] });
            row.Children.Add(control);
            target.Children.Add(row);
            visualVisibility[row] = supported;
            RegisterControlAutomation(label, control);
        }

        effectScale.DefaultValue = 1; effectOpacity.DefaultValue = 100; transitionDuration.DefaultValue = 0.6;
        effectAngleX.DefaultValue = 0; effectAngleY.DefaultValue = 0; stickerRotation.DefaultValue = 0;
        springFrequency.DefaultValue = MotionDefaults.SpringFrequency; springDamping.DefaultValue = MotionDefaults.SpringDamping;
        cursorSmooth.DefaultValue = 0; motionBlurSlider.DefaultValue = 0;
        AutomationProperties.SetAutomationId(motionBlurSlider, "editor.motion.blur");

        // Compact 2-column Time Range row (Start / End)
        var timeRangeRow = new Grid { ColumnSpacing = 8 };
        timeRangeRow.ColumnDefinitions.Add(new ColumnDefinition());
        timeRangeRow.ColumnDefinitions.Add(new ColumnDefinition());
        timeRangeRow.Children.Add(effectStart);
        Grid.SetColumn(effectEnd, 1);
        timeRangeRow.Children.Add(effectEnd);
        RegisterControlAutomation("", effectStart);
        RegisterControlAutomation("", effectEnd);
        effectProperties.Children.Add(timeRangeRow);
        visualVisibility[timeRangeRow] = ["zoom", "mask", "focus", "tilt", "sticker", "intro"];

        // Transition fields
        Field("", transitionStyle, effectProperties, "transition");
        Field(L.T("TransitionDurationLabel"), transitionDuration, effectProperties, "transition");

        // OpenScreen-style Zoom Depth Quick Presets + Fine Scale Slider
        var zoomDepthRow = new StackPanel { Spacing = 6 };
        zoomDepthRow.Children.Add(new TextBlock { Text = L.T("MotionZoomDepthLabel"), Style = (Style)Root.Resources["SmallLabel"] });
        var zoomChipGrid = new Grid { ColumnSpacing = 4 };
        for (int i = 0; i < zoomPresetDepths.Length; i++)
        {
            double depth = zoomPresetDepths[i];
            zoomChipGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var chip = new Button
            {
                Style = (Style)Application.Current.Resources["EditorChipButton"],
                Content = $"{depth:0.##}×",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(chip, $"{L.T("MotionZoomDepthLabel")} {depth:0.##}×");
            chip.Click += (_, _) =>
            {
                if (loadingVisuals || CurrentEffect is null) return;
                effectScale.Value = depth;
                UpdateZoomDepthChips();
            };
            Grid.SetColumn(chip, i);
            zoomChipGrid.Children.Add(chip);
            zoomDepthChips.Add((chip, depth));
        }
        zoomDepthRow.Children.Add(zoomChipGrid);
        zoomDepthRow.Children.Add(effectScale);
        RegisterControlAutomation(L.T("SB762AEC769A3"), effectScale);
        effectProperties.Children.Add(zoomDepthRow);
        visualVisibility[zoomDepthRow] = ["zoom"];
        effectScale.ValueChanged += (_, _) => UpdateZoomDepthChips();

        // Position & Size sliders
        Field("X", effectX, effectProperties, "zoom", "mask", "focus", "sticker");
        Field("Y", effectY, effectProperties, "zoom", "mask", "focus", "sticker");
        Field(L.T("S3B730DA0AD1A"), effectWidth, effectProperties, "mask", "focus", "sticker");
        Field(L.T("S968DD036CBB1"), effectHeight, effectProperties, "mask", "focus", "sticker");

        // Mask / Focus / Tilt / Sticker controls
        Field("", effectMode, effectProperties, "mask");
        Field(L.T("SA2846644A638"), effectStrength, effectProperties, "mask", "focus");
        Field(L.T("S04405E4A9FFD"), effectOpacity, effectProperties, "mask", "focus", "sticker");
        Field(L.T("SA4B0DE41E601"), effectAngleX, effectProperties, "tilt");
        Field(L.T("SD82466A62832"), effectAngleY, effectProperties, "tilt");
        Field("", stickerText, effectProperties, "sticker");
        Field("", stickerShape, effectProperties, "sticker");
        Field("", stickerColor, effectProperties, "sticker");
        Field(L.T("S68EB90FA809E"), stickerRotation, effectProperties, "sticker");
        Field("", effectAttachment, effectProperties, "mask", "focus", "sticker");

        // Sticker image / text toggle row
        var stickerAssetRow = new StackPanel { Spacing = 6 };
        visualAssetLabel.Foreground = RecorderTheme.Brush(Root, "EditorSecondary");
        stickerAssetRow.Children.Add(visualAssetLabel);
        var stickerBtns = new Grid { ColumnSpacing = 8 };
        stickerBtns.ColumnDefinitions.Add(new ColumnDefinition());
        stickerBtns.ColumnDefinitions.Add(new ColumnDefinition());
        var pickImgBtn = VisualButton(L.T("SA24E971FE950"), async () => { var path = await PickVisualAsync(false); if (path is not null && CurrentEffect is { } prior) { var p = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(prior.Parameters.GetRawText())!; p["path"] = JsonSerializer.SerializeToElement(path); await editor.UpsertEffectAsync(prior with { Parameters = JsonSerializer.SerializeToElement(p) }); } }, "editor.effect.image");
        var useTextBtn = VisualButton(L.T("S164BCC78FC98"), async () => { if (CurrentEffect is { } prior) { var p = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(prior.Parameters.GetRawText())!; p.Remove("path"); await editor.UpsertEffectAsync(prior with { Parameters = JsonSerializer.SerializeToElement(p) }); } }, "editor.effect.text");
        stickerBtns.Children.Add(pickImgBtn); Grid.SetColumn(useTextBtn, 1); stickerBtns.Children.Add(useTextBtn);
        stickerAssetRow.Children.Add(stickerBtns);
        effectProperties.Children.Add(stickerAssetRow);
        visualVisibility[stickerAssetRow] = ["sticker"];

        // Timing & Physics section (side-by-side Enter/Exit + conditional Spring controls)
        var physicsDivider = MakeVisualDivider();
        effectProperties.Children.Add(physicsDivider); visualVisibility[physicsDivider] = ["zoom", "tilt", "intro", "transition"];
        var physicsHeader = new TextBlock { Text = L.T("MotionSpringSection"), Style = (Style)Root.Resources["SectionLabel"], Margin = new Thickness(2, 2, 0, 0) };
        effectProperties.Children.Add(physicsHeader); visualVisibility[physicsHeader] = ["zoom", "tilt", "intro", "transition"];
        var physicsGroup = new StackPanel { Spacing = 10 };
        effectProperties.Children.Add(physicsGroup); visualVisibility[physicsGroup] = ["zoom", "tilt", "intro", "transition"];
        Field("", effectEasing, physicsGroup, "zoom", "tilt", "intro", "transition");

        var enterExitRow = new Grid { ColumnSpacing = 8 };
        enterExitRow.ColumnDefinitions.Add(new ColumnDefinition());
        enterExitRow.ColumnDefinitions.Add(new ColumnDefinition());
        enterExitRow.Children.Add(effectEnter);
        Grid.SetColumn(effectExit, 1);
        enterExitRow.Children.Add(effectExit);
        RegisterControlAutomation("", effectEnter);
        RegisterControlAutomation("", effectExit);
        physicsGroup.Children.Add(enterExitRow);
        visualVisibility[enterExitRow] = ["zoom", "tilt", "intro"];

        Field(L.T("SC19C240FA18F"), springFrequency, springParamsPanel, "zoom", "tilt", "intro");
        Field(L.T("SDE95E14AC3BD"), springDamping, springParamsPanel, "zoom", "tilt", "intro");
        physicsGroup.Children.Add(springParamsPanel);
        effectEasing.SelectionChanged += (_, _) => UpdateSpringVisibility();

        Field("", effectTarget, physicsGroup, "intro", "tilt");

        // Effect Actions Footer
        effectProperties.Children.Add(MakeVisualDivider());
        var actionsPanel = new StackPanel { Spacing = 8 };
        var duplicateEffectButton = VisualButton(L.T("MotionDuplicateToPlayhead"), DuplicateEffectToPlayheadAsync, "editor.effect.duplicateToPlayhead", "EditorSecondaryButton", "Copy");
        visualVisibility[duplicateEffectButton] = ["zoom", "mask", "focus", "tilt", "sticker", "intro"];
        actionsPanel.Children.Add(duplicateEffectButton);
        var deleteEffectButton = VisualButton(L.T("SEEB94B34ECA0"), async () => { if (selectedEffect is not null) await editor.DeleteEffectAsync(selectedEffect); }, "editor.effect.delete", "EditorDangerGhostButton", "Trash");
        actionsPanel.Children.Add(deleteEffectButton);
        effectProperties.Children.Add(actionsPanel);

        // ── 3. Global Motion Settings Card ──
        var globalMotionSection = new StackPanel();
        globalMotionSection.Children.Add(VisualLabel(L.T("MotionGlobalSection")));
        var motionBlurHint = new TextBlock { Text = L.T("MotionBlurHint"), Style = (Style)Root.Resources["SmallLabel"], TextWrapping = TextWrapping.Wrap };
        globalMotionSection.Children.Add(VisualTile(
            new TextBlock { Text = L.T("MotionBlurLabel"), Style = (Style)Root.Resources["SmallLabel"] },
            motionBlurSlider,
            motionBlurHint));
        motionSettings.Children.Add(globalMotionSection);
        motionBlurSlider.Committed += async (_, _) => { if (!loadingVisuals && ready) await PerformAsync(() => editor.ExecuteAsync(new { op = "settings", editor = (editor.Snapshot.Editor ?? new()) with { MotionBlur = Math.Clamp(motionBlurSlider.Value / 100.0, 0.0, 1.0) } })); };

        effectPicker.SelectionChanged += async (_, _) => { if (loadingVisuals) return; await SafeAsync(async () => { await FlushVisualChangesAsync(); selectedEffect = (effectPicker.SelectedItem as EffectEntry)?.Id; LoadSelectedVisual(); timeline.SelectVisual(selectedEffect); }); };
        timeline.SelectEffect += async id => {
            await SafeAsync(async () => {
                await FlushVisualChangesAsync();
                if (editor.Snapshot.Effects.FirstOrDefault(e => e.Id == id)?.Kind == "subtitle") {
                    selectedSubtitleId = id;
                    if (!selectedSubtitleIds.Contains(id)) { selectedSubtitleIds.Clear(); selectedSubtitleIds.Add(id); }
                    if (inspectorTab != 6) ShowInspector(6);
                    else RefreshSubtitleInspector();
                } else {
                    selectedEffect = id; ShowInspector(2); RefreshVisualInspector();
                }
            });
        };
        timeline.TransitionSeamClicked += async (fromId, toId) => {
            await PerformAsync(() => AddOrSelectTransitionBetweenAsync(fromId, toId));
        };

        // ── Opening (开场) Tab ──
        introDuration.Value = 0.7;
        var screenOpeningSection = new StackPanel();
        var screenHeader = new Grid();
        screenHeader.Children.Add(VisualLabel(L.T("SD50922830D9E")));
        (openingScreenBadge, openingScreenBadgeText) = CreateStatusBadge();
        openingScreenBadge.Margin = new Thickness(0, 0, 2, 6);
        screenHeader.Children.Add(openingScreenBadge);
        screenOpeningSection.Children.Add(screenHeader);

        var introChipGrid = new Grid { ColumnSpacing = 5 };
        for (int i = 0; i < introStyles.Length; i++)
        {
            int idx = i;
            introChipGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var chip = new Button
            {
                Style = (Style)Application.Current.Resources["EditorChipButton"],
                Content = introStyleLabels[i],
                Height = 30,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(chip, introStyleLabels[i]);
            chip.Click += (_, _) =>
            {
                introStyle.SelectedIndex = idx;
                UpdateIntroStyleChips();
            };
            Grid.SetColumn(chip, i);
            introChipGrid.Children.Add(chip);
            introStyleChips.Add((chip, i));
        }
        introStyle.SelectionChanged += (_, _) => UpdateIntroStyleChips();

        var applyScreenOpeningButton = VisualButton(L.T("SE1B2D5A70D59"), () => ApplyOpeningAsync(false), "editor.intro.apply", "EditorPrimaryButton", "Check");
        applyScreenOpeningButton.Height = 34;
        removeScreenOpeningButton = VisualButton(L.T("SBFA8C11031E5"), async () => { if (editor.Snapshot.Effects.Any(e => e.Id == "opening-screen")) await editor.DeleteEffectAsync("opening-screen"); }, "editor.intro.remove", "EditorDangerGhostButton", "Trash");
        removeScreenOpeningButton.Visibility = Visibility.Collapsed;

        screenOpeningSection.Children.Add(VisualTile(
            new TextBlock { Text = L.T("OpeningScreenDesc"), Style = (Style)Root.Resources["SmallLabel"], TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = L.T("S4E0E5D9CF7B9"), Style = (Style)Root.Resources["SmallLabel"] },
            introChipGrid,
            introDuration,
            applyScreenOpeningButton,
            removeScreenOpeningButton));
        openingSettings.Children.Add(screenOpeningSection);

        var cameraOpeningSection = new StackPanel();
        var cameraHeader = new Grid();
        cameraHeader.Children.Add(VisualLabel(L.T("OpeningCameraSection")));
        (openingCameraBadge, openingCameraBadgeText) = CreateStatusBadge();
        openingCameraBadge.Margin = new Thickness(0, 0, 2, 6);
        cameraHeader.Children.Add(openingCameraBadge);
        cameraOpeningSection.Children.Add(cameraHeader);

        openingCameraDescText = new TextBlock { Text = L.T("OpeningCameraDesc"), Style = (Style)Root.Resources["SmallLabel"], TextWrapping = TextWrapping.Wrap };
        applyCameraOpeningButton = VisualButton(L.T("SE87613AD8A19"), () => ApplyOpeningAsync(true), "editor.camera.animation", "EditorSecondaryButton", "Camera");
        removeCameraOpeningButton = VisualButton(L.T("OpeningCameraRemove"), async () => { if (editor.Snapshot.Effects.Any(e => e.Id == "opening-camera")) await editor.DeleteEffectAsync("opening-camera"); }, "editor.camera.animation.remove", "EditorDangerGhostButton", "Trash");
        removeCameraOpeningButton.Visibility = Visibility.Collapsed;
        cameraOpeningSection.Children.Add(VisualTile(openingCameraDescText, applyCameraOpeningButton, removeCameraOpeningButton));
        openingSettings.Children.Add(cameraOpeningSection);

        CursorSettings.Children.Add(VisualTile(new TextBlock { Text = L.T("SF6A8CEDF8C36"), Style = (Style)Root.Resources["SmallLabel"] }, cursorSmooth));
        InitializeCursor();
        cursorSmooth.Committed += async (_, _) => { if (!loadingVisuals && ready) await PerformAsync(() => editor.ExecuteAsync(new { op = "settings", editor = (editor.Snapshot.Editor ?? new()) with { CursorSmoothing = cursorSmooth.Value } })); };
        CameraSettings.Children.Add(VisualTile(cameraShape));
        cameraShape.SelectionChanged += async (_, _) => { if (!loadingVisuals && ready && !working) await PerformAsync(() => editor.ExecuteAsync(new { op = "settings", editor = (editor.Snapshot.Editor ?? new()) with { CameraShape = new[] { "rounded", "rectangle", "circle" }[Math.Max(0, cameraShape.SelectedIndex)] } })); };
        Root.ActualThemeChanged += (_, _) => RefreshVisualTheme();
        RefreshVisualTheme();
        InitializeMockups();
        InitializeWallpapers();
        InitializeLiveVisuals();
    }

    private void ApplyVisualButtonContentTheme(EditorGlyph glyph, TextBlock label, string styleKey)
    {
        if (styleKey == "EditorPrimaryButton")
        {
            var ink = RecorderTheme.Brush(Root, "EditorAccentInk");
            glyph.Foreground = ink;
            label.Foreground = ink;
        }
        else if (styleKey == "EditorDangerGhostButton")
        {
            var danger = RecorderTheme.Brush(Root, "EditorDanger");
            glyph.Foreground = danger;
            label.Foreground = danger;
        }
        else
        {
            glyph.Foreground = RecorderTheme.Brush(Root, "EditorSecondary");
            label.Foreground = RecorderTheme.Brush(Root, "EditorInk");
        }
    }

    private void RefreshVisualTheme()
    {
        var line = RecorderTheme.Brush(Root, "EditorLine");
        foreach (var d in visualDividers) d.Background = line;
        foreach (var (glyph, label, styleKey) in visualButtonContents) ApplyVisualButtonContentTheme(glyph, label, styleKey);
        foreach (var (_, kind, glyph) in effectAddButtons)
        {
            var (_, _, fg) = EffectKindStyle(kind);
            glyph.Foreground = fg;
        }
        if (effectEmptyBadge is not null) effectEmptyBadge.Background = RecorderTheme.Brush(Root, "EditorAccentSoft");
        if (effectEmptyGlyph is not null) effectEmptyGlyph.Foreground = RecorderTheme.Brush(Root, "EditorAccent");
        effectCountLabel.Foreground = RecorderTheme.Brush(Root, "EditorTertiary");
        visualAssetLabel.Foreground = RecorderTheme.Brush(Root, "EditorSecondary");
        UpdateSelectedEffectKindBadge();
        UpdateZoomDepthChips();
        UpdateIntroStyleChips();
        UpdateOpeningStatus();
    }

    private void UpdateSelectedEffectKindBadge()
    {
        var kind = CurrentEffect?.Kind ?? "zoom";
        var (glyphKind, bg, fg) = EffectKindStyle(kind);
        effectKindGlyph.Kind = glyphKind;
        effectKindGlyph.Foreground = fg;
        effectKindBadge.Background = bg;
    }

    private void UpdateZoomDepthChips()
    {
        var accent = RecorderTheme.Brush(Root, "EditorAccent");
        var accentInk = RecorderTheme.Brush(Root, "EditorAccentInk");
        var panel = RecorderTheme.Brush(Root, "EditorPanel");
        var line = RecorderTheme.Brush(Root, "EditorLine");
        var secondary = RecorderTheme.Brush(Root, "EditorSecondary");
        foreach (var (button, depth) in zoomDepthChips)
        {
            bool active = Math.Abs(effectScale.Value - depth) <= 0.03;
            button.Background = active ? accent : panel;
            button.Foreground = active ? accentInk : secondary;
            button.BorderBrush = active ? accent : line;
        }
    }

    private void UpdateIntroStyleChips()
    {
        var accent = RecorderTheme.Brush(Root, "EditorAccent");
        var accentInk = RecorderTheme.Brush(Root, "EditorAccentInk");
        var panel = RecorderTheme.Brush(Root, "EditorPanel");
        var line = RecorderTheme.Brush(Root, "EditorLine");
        var secondary = RecorderTheme.Brush(Root, "EditorSecondary");
        int selectedIdx = Math.Clamp(introStyle.SelectedIndex, 0, introStyles.Length - 1);
        foreach (var (button, idx) in introStyleChips)
        {
            bool active = idx == selectedIdx;
            button.Background = active ? accent : panel;
            button.Foreground = active ? accentInk : secondary;
            button.BorderBrush = active ? accent : line;
        }
    }

    private void UpdateSpringVisibility()
    {
        var kind = CurrentEffect?.Kind;
        bool supportsSpring = kind is "zoom" or "tilt" or "intro";
        bool isSpring = effectEasing.SelectedIndex == 1;
        springParamsPanel.Visibility = supportsSpring && isSpring ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateOpeningStatus()
    {
        bool hasScreenOpening = editor.Snapshot.Effects.Any(e => e.Id == "opening-screen");
        bool hasCameraOpening = editor.Snapshot.Effects.Any(e => e.Id == "opening-camera");
        bool hasCameraMedia = editor.Snapshot.Media.Any(m => m.Kind == MediaKind.Camera);
        StyleStatusBadge(openingScreenBadge, openingScreenBadgeText, hasScreenOpening);
        StyleStatusBadge(openingCameraBadge, openingCameraBadgeText, hasCameraOpening);
        if (removeScreenOpeningButton is not null) removeScreenOpeningButton.Visibility = hasScreenOpening ? Visibility.Visible : Visibility.Collapsed;
        if (applyCameraOpeningButton is not null) applyCameraOpeningButton.IsEnabled = hasCameraMedia;
        if (removeCameraOpeningButton is not null) removeCameraOpeningButton.Visibility = hasCameraOpening ? Visibility.Visible : Visibility.Collapsed;
        if (openingCameraDescText is not null) openingCameraDescText.Text = hasCameraMedia ? L.T("OpeningCameraDesc") : L.T("OpeningCameraUnavailable");
    }

    private long SourcePosition => mapper.MapOutputTime(position)?.Ticks ?? 0;
    private SourceEffect? CurrentEffect => editor.Snapshot.Effects.FirstOrDefault(e => e.Id == selectedEffect);
    private async Task DuplicateEffectToPlayheadAsync()
    {
        if (CurrentEffect is not { } current) return;
        await editor.PasteEffectAsync(current, SourcePosition);
        selectedEffect = editor.Snapshot.Effects.Last().Id;
    }
    private async Task PasteCopiedVisualAsync()
    {
        if (copiedEffect is null) return;
        await editor.PasteEffectAsync(copiedEffect, SourcePosition);
        selectedEffect = editor.Snapshot.Effects.Last().Id;
    }
    private async Task AddVisualAsync(string kind)
    {
        if (kind == "transition") {
            var clips = editor.Timeline.Clips;
            if (clips.Count < 2) {
                ShowNotice(L.T("TransitionName"), L.T("TransitionNeedTwoClips"), InfoBarSeverity.Informational);
                return;
            }
            int seamIdx = 0;
            int selIdx = selected is not null ? clips.IndexOf(clips.FirstOrDefault(c => c.Id == selected)!) : -1;
            if (selIdx >= 0 && selIdx + 1 < clips.Count) seamIdx = selIdx;
            else if (selIdx == clips.Count - 1) seamIdx = clips.Count - 2;
            else {
                long bestDist = long.MaxValue;
                for (int i = 0; i + 1 < clips.Count; i++) {
                    long dist = Math.Abs(clips[i + 1].OutputStart - position);
                    if (dist < bestDist) { bestDist = dist; seamIdx = i; }
                }
            }
            await AddOrSelectTransitionBetweenAsync(clips[seamIdx].Id, clips[seamIdx + 1].Id);
            return;
        }
        long start = SourcePosition, duration = Math.Min(20_000_000, VisualResources.Ticks(editor.Snapshot.SourceDuration) - start);
        var value = new SourceEffect { Id = "effect-" + Guid.NewGuid().ToString("N"), Kind = kind, SourceStart = MediaTime.FromTicks(start), SourceDuration = MediaTime.FromTicks(Math.Max(1, duration)), Parameters = JsonSerializer.SerializeToElement(new { origin = "manual", text = kind == "sticker" ? L.T("SBF3B83E701D4") : "" }) };
        selectedEffect = value.Id; await editor.UpsertEffectAsync(value);
    }
    private async Task AddOrSelectTransitionBetweenAsync(string fromSegmentId, string toSegmentId)
    {
        await FlushVisualChangesAsync();
        var existing = editor.Snapshot.Effects.FirstOrDefault(e =>
            e.Kind == "transition" &&
            StringValue(e.Parameters, "fromSegmentId", "") == fromSegmentId &&
            StringValue(e.Parameters, "toSegmentId", "") == toSegmentId);
        if (existing is not null) {
            selectedEffect = existing.Id;
            ShowInspector(2);
            RefreshVisualInspector();
            var resolved = editor.Timeline.Effects.FirstOrDefault(e => e.Id == existing.Id);
            if (resolved is not null) await SeekAsync(resolved.OutputStart + resolved.OutputDuration / 2);
            return;
        }
        var segs = editor.Snapshot.Segments;
        int fromIndex = -1;
        for (int i = 0; i + 1 < segs.Count; i++) {
            if (segs[i].Id == fromSegmentId && segs[i + 1].Id == toSegmentId) { fromIndex = i; break; }
        }
        if (fromIndex < 0) return;
        var cmd = AiAgentCommandBuilder.BuildTransitionCommand(editor.Snapshot, fromIndex + 1, "crossfade", 0.6, "cubic");
        await editor.ExecuteAsync(cmd);
        var created = editor.Snapshot.Effects.FirstOrDefault(e =>
            e.Kind == "transition" &&
            StringValue(e.Parameters, "fromSegmentId", "") == fromSegmentId &&
            StringValue(e.Parameters, "toSegmentId", "") == toSegmentId);
        selectedEffect = created?.Id;
        ShowInspector(2);
        RefreshVisualInspector();
        if (created is not null) {
            var resolved = editor.Timeline.Effects.FirstOrDefault(e => e.Id == created.Id);
            if (resolved is not null) await SeekAsync(resolved.OutputStart + resolved.OutputDuration / 2);
        }
    }
    private void RefreshVisualInspector()
    {
        loadingVisuals = true;
        try
        {
            var list = editor.Snapshot.Effects.Where(e => e.Kind != "subtitle").Select(e => new EffectEntry(e.Id, $"{EffectName(e.Kind)} · {VisualResources.Ticks(e.SourceStart) / 10_000_000.0:0.##}s")).ToArray();
            effectPicker.ItemsSource = list; effectPicker.SelectedItem = list.FirstOrDefault(e => e.Id == selectedEffect) ?? list.FirstOrDefault(); selectedEffect = (effectPicker.SelectedItem as EffectEntry)?.Id;
            var settings = editor.Snapshot.Editor ?? new(); cursorSmooth.Value = settings.CursorSmoothing; motionBlurSlider.Value = Math.Clamp(settings.MotionBlur * 100.0, 0, 100); cameraShape.SelectedIndex = Array.IndexOf(new[] { "rounded", "rectangle", "circle" }, settings.CameraShape);
            RefreshMockupInspector();
            var opening = editor.Snapshot.Effects.FirstOrDefault(e => e.Id == "opening-screen");
            if (opening is not null) { introStyle.SelectedIndex = Array.IndexOf(introStyles, StringValue(opening.Parameters, "style", "fade")); introDuration.Value = NumberValue(opening.Parameters, "enter", 0.7); }
        }
        finally { loadingVisuals = false; }
        LoadSelectedVisual();
        UpdateOpeningStatus();
        UpdateIntroStyleChips();
        timeline.SelectVisual(inspectorTab == 2 ? selectedEffect : inspectorTab == 6 ? selectedSubtitleId : null);
        UpdateBackdropTabs();
        QueueCanvasGeometry();
    }
    private static double NumberValue(JsonElement p, string key, double fallback) => p.TryGetProperty(key, out var value) && value.TryGetDouble(out var number) ? number : fallback;
    private static string StringValue(JsonElement p, string key, string fallback) => p.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : fallback;
    private void LoadSelectedVisual()
    {
        var effect = CurrentEffect;
        bool hasEffect = effect is not null;
        effectEmptyCard.Visibility = hasEffect ? Visibility.Collapsed : Visibility.Visible;
        effectSelectedSection.Visibility = hasEffect ? Visibility.Visible : Visibility.Collapsed;
        effectProperties.Visibility = hasEffect ? Visibility.Visible : Visibility.Collapsed;
        if (effect is null) return;
        loadingVisuals = true;
        try
        {
            if (effectPicker.ItemsSource is EffectEntry[] entries && entries.Length > 0)
            {
                int idx = Array.FindIndex(entries, e => e.Id == effect.Id);
                effectCountLabel.Text = idx >= 0 ? $"{idx + 1} / {entries.Length}" : $"{entries.Length}";
            }
            else
            {
                effectCountLabel.Text = "";
            }
            UpdateSelectedEffectKindBadge();
            foreach (var (control, kinds) in visualVisibility) control.Visibility = kinds.Length == 0 || kinds.Contains(effect.Kind) ? Visibility.Visible : Visibility.Collapsed;
            var totalSeconds = VisualResources.Ticks(editor.Snapshot.SourceDuration) / 10_000_000.0;
            effectStart.Maximum = Math.Max(0, totalSeconds - 0.0000001); effectEnd.Maximum = totalSeconds;
            var p = effect.Parameters; effectStart.Value = VisualResources.Ticks(effect.SourceStart) / 10_000_000.0; effectEnd.Value = (VisualResources.Ticks(effect.SourceStart) + VisualResources.Ticks(effect.SourceDuration)) / 10_000_000.0;
            effectScale.Value = NumberValue(p, "scale", 1.6); effectX.Value = NumberValue(p, effect.Kind == "zoom" ? "focusX" : "x", effect.Kind == "zoom" ? 0.5 : 0.25) * 100; effectY.Value = NumberValue(p, effect.Kind == "zoom" ? "focusY" : "y", effect.Kind == "zoom" ? 0.5 : 0.25) * 100;
            effectWidth.Value = NumberValue(p, "width", 0.5) * 100; effectHeight.Value = NumberValue(p, "height", 0.5) * 100; effectStrength.Value = NumberValue(p, "strength", 16); effectOpacity.Value = NumberValue(p, "opacity", 1) * 100;
            effectAngleX.Value = NumberValue(p, "angleX", 0); effectAngleY.Value = NumberValue(p, "angleY", 0); effectMode.SelectedIndex = Array.IndexOf(new[] { "solid", "pixelate", "blur" }, StringValue(p, "mode", "solid"));
            transitionStyle.SelectedIndex = Math.Max(0, Array.IndexOf(transitionStyles, StringValue(p, "style", "crossfade")));
            transitionDuration.Value = Math.Clamp(NumberValue(p, "duration", 0.6), 0.1, 3.0);
            effectEasing.SelectedIndex = Array.IndexOf(new[] { "cubic", "spring", "linear" }, StringValue(p, "easing", "cubic")); effectAttachment.SelectedIndex = StringValue(p, "attachment", "screen") == "canvas" ? 1 : 0; effectTarget.SelectedIndex = StringValue(p, "target", "screen") == "camera" ? 1 : 0;
            effectEnter.Value = NumberValue(p, "enter", effect.Kind is "zoom" or "tilt" or "intro" ? 0.7 : 0); effectExit.Value = NumberValue(p, "exit", effect.Kind is "zoom" or "tilt" ? 0.7 : 0);
            springFrequency.Value = NumberValue(p, "springFrequency", MotionDefaults.SpringFrequency); springDamping.Value = NumberValue(p, "damping", MotionDefaults.SpringDamping);
            stickerRotation.Value = NumberValue(p, "rotation", 0);
            stickerShape.SelectedIndex = Math.Max(0, Array.IndexOf(stickerShapes, StringValue(p, "shape", "pill")));
            stickerColor.SelectedIndex = Math.Max(0, Array.IndexOf(stickerColors, StringValue(p, "color", "#38BDF8").ToUpperInvariant()));
            stickerText.Text = StringValue(p, "text", L.T("SBF3B83E701D4")); visualAssetPath = StringValue(p, "path", ""); visualAssetLabel.Text = string.IsNullOrEmpty(visualAssetPath) ? L.T("SE2B3B077BDC5") : Path.GetFileName(visualAssetPath);
        }
        finally { loadingVisuals = false; }
        UpdateZoomDepthChips();
        UpdateSpringVisibility();
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
        if (prior.Kind == "transition") {
            Set("style", transitionStyles[Math.Clamp(transitionStyle.SelectedIndex, 0, transitionStyles.Length - 1)]);
            Set("duration", Math.Round(Math.Clamp(transitionDuration.Value, 0.1, 5.0), 2));
            return prior with { Parameters = JsonSerializer.SerializeToElement(p) };
        }
        if (prior.Kind == "zoom") { Set("scale", effectScale.Value); Set("focusX", effectX.Value / 100); Set("focusY", effectY.Value / 100); p.Remove("focusPoints"); }
        if (prior.Kind is "mask" or "focus" or "sticker") { double x = effectX.Value / 100, y = effectY.Value / 100; Set("x", x); Set("y", y); Set("width", Math.Min(effectWidth.Value / 100, 1 - x)); Set("height", Math.Min(effectHeight.Value / 100, 1 - y)); Set("opacity", effectOpacity.Value / 100); Set("attachment", effectAttachment.SelectedIndex == 1 ? "canvas" : "screen"); }
        if (prior.Kind is "mask" or "focus") Set("strength", effectStrength.Value);
        if (prior.Kind == "mask") Set("mode", new[] { "solid", "pixelate", "blur" }[Math.Max(0, effectMode.SelectedIndex)]);
        if (prior.Kind == "tilt") { Set("angleX", effectAngleX.Value); Set("angleY", effectAngleY.Value); }
        if (prior.Kind is "intro" or "tilt") Set("target", effectTarget.SelectedIndex == 1 ? "camera" : "screen");
        if (prior.Kind == "sticker") { Set("text", stickerText.Text); Set("rotation", stickerRotation.Value); Set("shape", stickerShapes[Math.Clamp(stickerShape.SelectedIndex, 0, stickerShapes.Length - 1)]); Set("color", stickerColors[Math.Clamp(stickerColor.SelectedIndex, 0, stickerColors.Length - 1)]); if (!string.IsNullOrEmpty(visualAssetPath)) Set("path", visualAssetPath); else p.Remove("path"); }
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
            bool active = button.Tag as string == kind; button.Background = active ? RecorderTheme.Brush(Root, "EditorAccent") : new SolidColorBrush(Colors.Transparent);
            button.Foreground = active ? RecorderTheme.Brush(Root, "EditorAccentInk") : RecorderTheme.Brush(Root, "EditorSecondary");
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
