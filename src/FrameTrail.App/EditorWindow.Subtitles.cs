using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;
using FrameTrail.Workspace;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using VirtualKey = Windows.System.VirtualKey;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    private static readonly string[] SubtitleFontChoices =
        ["Alibaba PuHuiTi 3.0", "Microsoft YaHei UI", "Segoe UI", "Consolas"];
    private static readonly string[] SubtitleHighlightModes = ["none", "wordColor", "wordPill"];
    private static readonly string[] SubtitleAlignments = ["left", "center", "right"];
    private static readonly string[] SubtitleTextSwatches = ["#FFFFFF", "#FACC15", "#38BDF8", "#4ADE80", "#FB7185", "#09090B"];
    private static readonly string[] SubtitleOutlineSwatches = ["#09090B", "#18181B", "#1E3A8A", "#7C2D12", "#FFFFFF"];
    private static readonly string[] SubtitleBgSwatches = ["#09090B", "#18181B", "#1E293B", "#7C2D12", "#FFFFFF"];
    private static readonly string[] SubtitleHighlightSwatches = ["#FACC15", "#38BDF8", "#4ADE80", "#FB7185", "#F97316", "#A855F7"];

    private readonly AiSubtitleTranscriptionService subtitleService = new();
    private readonly StackPanel subtitleSettings = new() { Spacing = 14, Visibility = Visibility.Collapsed };

    // Top actions & ASR options
    private readonly Button subAddCueBtn = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Height = 36 };
    private readonly Button subImportBtn = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Height = 32 };
    private readonly Button subExportBtn = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Height = 32 };
    private readonly Button subAutoGenBtn = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Height = 34 };
    private readonly Button subOptionsToggleBtn = new() { Width = 34, Height = 34, Padding = new Thickness(0) };
    private readonly StackPanel subAsrOptionsPanel = new() { Spacing = 10, Visibility = Visibility.Collapsed };
    private readonly ComboBox subEngineCombo = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel subModelStatusPanel = new() { Spacing = 6 };
    private readonly TextBlock subModelStatusLabel = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly Button subModelDownloadBtn = new() { FontSize = 11, Height = 28, Padding = new Thickness(10, 0, 10, 0) };
    private readonly Button subModelDeleteBtn = new() { FontSize = 11, Height = 28, Padding = new Thickness(10, 0, 10, 0) };
    private readonly ComboBox subTrackCombo = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox subScopeCombo = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox subLanguageCombo = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox subVocabBox = new() { MaxLength = 240 };
    private readonly StackPanel subProgressPanel = new() { Spacing = 6, Visibility = Visibility.Collapsed };
    private readonly ProgressBar subProgressBar = new() { Minimum = 0, Maximum = 100, Height = 4 };
    private readonly TextBlock subProgressLabel = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly Button subCancelTaskBtn = new() { FontSize = 11, Height = 28, HorizontalAlignment = HorizontalAlignment.Right };

    // Sub-tab switch
    private readonly Button subTranscriptTabBtn = new() { Height = 32, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(6, 0, 6, 0) };
    private readonly Button subStyleTabBtn = new() { Height = 32, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(6, 0, 6, 0) };
    private readonly StackPanel subTranscriptPage = new() { Spacing = 12 };
    private readonly StackPanel subStylePage = new() { Spacing = 12, Visibility = Visibility.Collapsed };

    // Transcript sub-tab controls
    private readonly TextBox subFindBox = new() { PlaceholderText = L.T("SubFindPlaceholder") };
    private readonly TextBox subReplaceBox = new() { PlaceholderText = L.T("SubReplacePlaceholder") };
    private readonly Button subReplaceAllBtn = new() { Content = L.T("SubReplaceAll"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox subBilingualCheck = new() { Content = L.T("SubBilingualToggle") };
    private readonly Button subAiTranslateBtn = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Border subEmptyCard = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel subCueListPanel = new() { Spacing = 8 };

    // Style sub-tab controls
    private readonly Border subOverrideBanner = new() { Visibility = Visibility.Collapsed };
    private readonly Grid subPresetGrid = new() { ColumnSpacing = 8, RowSpacing = 8 };
    private readonly Dictionary<string, Button> subPresetButtons = new(StringComparer.Ordinal);
    private readonly ComboBox subFontCombo = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly EditorBarSlider subFontSize = VisualBar(16, 120, 1, "pt");
    private readonly EditorBarSlider subOutlineWidth = VisualBar(0, 12, 0.5);
    private readonly EditorBarSlider subBgOpacity = VisualBar(0, 100, 5, "%");
    private readonly EditorBarSlider subBgRadius = VisualBar(0, 32, 1, "px");
    private readonly ComboBox subHighlightMode = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly EditorBarSlider subPosY = VisualBar(10, 95, 1, "%");
    private readonly EditorBarSlider subMaxChars = VisualBar(8, 48, 1);
    private readonly EditorBarSlider subFade = VisualBar(0, 0.5, 0.02, "s");
    private readonly Grid subAlignGrid = new() { ColumnSpacing = 4 };
    private readonly Dictionary<string, Button> subAlignButtons = new(StringComparer.Ordinal);
    private readonly StackPanel subTextSwatchRow = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly StackPanel subOutlineSwatchRow = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly StackPanel subBgSwatchRow = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly StackPanel subHighlightSwatchRow = new() { Orientation = Orientation.Horizontal, Spacing = 6 };

    // State
    private readonly Dictionary<string, TextBox> subCueTextBoxes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> subCueCards = new(StringComparer.Ordinal);
    private readonly HashSet<string> selectedSubtitleIds = new(StringComparer.Ordinal);
    private string? selectedSubtitleId;
    private string? lastRenderedSubtitleSignature;
    private bool subtitleStylePage;
    private bool subtitleAsrOptionsExpanded;
    private bool subtitleBusy;
    private bool loadingSubtitles;
    private CancellationTokenSource? subtitleAsrCts;
    private string subActiveTextColor = "#FFFFFF";
    private string subActiveOutlineColor = "#09090B";
    private string subActiveBgColor = "#09090B";
    private string subActiveHighlightColor = "#FACC15";
    private string subActiveAlignment = "center";

    // Live style preview timers (33ms live preview + 400ms debounced commit)
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? subStylePreviewTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? subStyleCommitTimer;
    private SubtitleStyleSettings? pendingSubtitleStyleDraft;
    private bool subtitleStyleDirty;
    private Task subStylePreviewWork = Task.CompletedTask;

    // Inline canvas editor state
    private string? inlineEditingSubtitleId;
    private string inlineEditingOriginalText = "";
    private TextBox? inlineSubtitleTextBox;
    private bool committingInlineSubtitle;

    private bool HasSubtitles => editor.Snapshot.Effects.Any(e => e.Kind == "subtitle");

    private IReadOnlyList<SourceEffect> OrderedSubtitles =>
        editor.Snapshot.Effects
            .Where(e => e.Kind == "subtitle")
            .OrderBy(e => VisualResources.Ticks(e.SourceStart))
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToArray();

    private void InitializeSubtitles()
    {
        var host = (StackPanel)InspectorScroll.Content;
        host.Children.Add(subtitleSettings);

        timeline.SubtitleTextProvider = GetSubtitlePreviewText;
        timeline.SelectedEffectsChanged += ids =>
        {
            selectedSubtitleIds.Clear();
            foreach (var id in ids)
            {
                if (editor.Snapshot.Effects.Any(e => e.Id == id && e.Kind == "subtitle"))
                    selectedSubtitleIds.Add(id);
            }
            if (selectedSubtitleIds.Count > 0 && (selectedSubtitleId is null || !selectedSubtitleIds.Contains(selectedSubtitleId)))
                selectedSubtitleId = selectedSubtitleIds.First();
            UpdateSubtitleCardHighlights();
        };
        timeline.SubtitleDoubleClicked += id =>
        {
            SelectSubtitleById(id, openTab: true);
            FocusSubtitleCueEditor(id);
        };
        timeline.SubtitleContextActionRequested += async (id, action) =>
        {
            await SafeAsync(async () =>
            {
                switch (action)
                {
                    case "split":
                        await SplitSubtitleAtPlayheadAsync(id);
                        break;
                    case "mergeNext":
                        await MergeSubtitleWithNextAsync(id);
                        break;
                    case "resetStyle":
                        await ResetSubtitleOverrideAsync(id);
                        break;
                    case "delete":
                        if (!selectedSubtitleIds.Contains(id))
                        {
                            selectedSubtitleIds.Clear();
                            selectedSubtitleIds.Add(id);
                            selectedSubtitleId = id;
                        }
                        await DeleteSelectedSubtitlesAsync();
                        break;
                }
            });
        };

        BuildSubtitlesTopActionsCard();
        BuildSubtitlesSubTabSwitch();
        BuildSubtitlesTranscriptPage();
        BuildSubtitlesStylePage();

        subStylePreviewTimer = DispatcherQueue.CreateTimer();
        subStylePreviewTimer.Interval = TimeSpan.FromMilliseconds(33);
        subStylePreviewTimer.IsRepeating = false;
        subStylePreviewTimer.Tick += (_, _) =>
        {
            if (subStylePreviewWork.IsCompleted && pendingSubtitleStyleDraft is not null)
                subStylePreviewWork = RenderSubtitleStylePreviewLoopAsync();
        };

        subStyleCommitTimer = DispatcherQueue.CreateTimer();
        subStyleCommitTimer.Interval = TimeSpan.FromMilliseconds(400);
        subStyleCommitTimer.IsRepeating = false;
        subStyleCommitTimer.Tick += async (_, _) => await SafeAsync(FlushSubtitleStyleChangesAsync);

        UpdateSubtitleSubTabs();
        RefreshLocalModelStatus();
    }

    private void DisposeSubtitles()
    {
        try { subStylePreviewTimer?.Stop(); } catch { }
        try { subStyleCommitTimer?.Stop(); } catch { }
        try { subtitleAsrCts?.Cancel(); } catch { }
        try { subtitleAsrCts?.Dispose(); } catch { }
        subtitleAsrCts = null;
        DismissSubtitleInlineEditor(commit: false);
    }

    private string? GetSubtitlePreviewText(string effectId)
    {
        var eff = editor.Snapshot.Effects.FirstOrDefault(e => e.Id == effectId && e.Kind == "subtitle");
        if (eff is null) return null;
        var p = SubtitleCueParameters.FromEffect(eff);
        return p.Text;
    }

    private void BuildSubtitlesTopActionsCard()
    {
        // 1. Primary Card: Manual Subtitle Creation & SRT/VTT Import/Export (most prominent)
        var manualStack = new StackPanel { Spacing = 8 };

        var addContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        addContent.Children.Add(new EditorGlyph { Kind = "Plus", Width = 14, Height = 14, Foreground = RecorderTheme.Brush(Root, "EditorAccentInk") });
        addContent.Children.Add(new TextBlock
        {
            Text = $"{L.T("SubAddCue")} (Ctrl+T)",
            FontFamily = (FontFamily)Application.Current.Resources["RecorderSemiboldFont"],
            FontSize = 12.5,
            Foreground = RecorderTheme.Brush(Root, "EditorAccentInk")
        });
        subAddCueBtn.Style = (Style)Application.Current.Resources["EditorPrimaryButton"];
        subAddCueBtn.CornerRadius = new CornerRadius(12);
        subAddCueBtn.Content = addContent;
        ToolTipService.SetToolTip(subAddCueBtn, $"{L.T("SubAddCue")} (Ctrl+T)");
        AutomationProperties.SetName(subAddCueBtn, L.T("SubAddCue"));
        AutomationProperties.SetAutomationId(subAddCueBtn, "editor.subtitles.addCue");
        subAddCueBtn.Click += async (_, _) => await SafeAsync(AddSubtitleAtPlayheadAsync);
        manualStack.Children.Add(subAddCueBtn);

        var fileRow = new Grid { ColumnSpacing = 8 };
        fileRow.ColumnDefinitions.Add(new ColumnDefinition());
        fileRow.ColumnDefinitions.Add(new ColumnDefinition());

        subImportBtn.Content = new TextBlock { Text = L.T("SubImport"), FontSize = 11.5, Foreground = RecorderTheme.Brush(Root, "EditorInk"), TextTrimming = TextTrimming.CharacterEllipsis };
        subImportBtn.Background = RecorderTheme.Brush(Root, "EditorPanel");
        subImportBtn.BorderBrush = RecorderTheme.Brush(Root, "EditorLine");
        subImportBtn.BorderThickness = new Thickness(1);
        subImportBtn.CornerRadius = new CornerRadius(10);
        subImportBtn.Padding = new Thickness(8, 0, 8, 0);
        ToolTipService.SetToolTip(subImportBtn, L.T("SubImport"));
        AutomationProperties.SetAutomationId(subImportBtn, "editor.subtitles.import");
        subImportBtn.Click += async (_, _) => await SafeAsync(ImportSubtitlesAsync);
        fileRow.Children.Add(subImportBtn);

        subExportBtn.Content = new TextBlock { Text = L.T("SubExport"), FontSize = 11.5, Foreground = RecorderTheme.Brush(Root, "EditorInk"), TextTrimming = TextTrimming.CharacterEllipsis };
        subExportBtn.Background = RecorderTheme.Brush(Root, "EditorPanel");
        subExportBtn.BorderBrush = RecorderTheme.Brush(Root, "EditorLine");
        subExportBtn.BorderThickness = new Thickness(1);
        subExportBtn.CornerRadius = new CornerRadius(10);
        subExportBtn.Padding = new Thickness(8, 0, 8, 0);
        ToolTipService.SetToolTip(subExportBtn, L.T("SubExport"));
        AutomationProperties.SetAutomationId(subExportBtn, "editor.subtitles.export");
        subExportBtn.Click += async (_, _) => await SafeAsync(ExportSubtitlesFileAsync);
        Grid.SetColumn(subExportBtn, 1);
        fileRow.Children.Add(subExportBtn);

        manualStack.Children.Add(fileRow);
        subtitleSettings.Children.Add(new Border { Style = (Style)Application.Current.Resources["EditorTile"], Child = manualStack });

        // 2. Secondary Card: AI Speech-to-Text Enhancement (compact, options collapsed by default)
        var aiStack = new StackPanel { Spacing = 10 };

        var aiRow = new Grid { ColumnSpacing = 6 };
        aiRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        aiRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var genContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        genContent.Children.Add(new EditorGlyph { Kind = "Sparkle", Width = 14, Height = 14, Foreground = RecorderTheme.Brush(Root, "EditorInk") });
        genContent.Children.Add(new TextBlock
        {
            Text = L.T("SubAutoGenerate"),
            FontFamily = (FontFamily)Application.Current.Resources["RecorderSemiboldFont"],
            FontSize = 12,
            Foreground = RecorderTheme.Brush(Root, "EditorInk")
        });
        subAutoGenBtn.Background = RecorderTheme.Brush(Root, "EditorPanel");
        subAutoGenBtn.BorderBrush = RecorderTheme.Brush(Root, "EditorLine");
        subAutoGenBtn.BorderThickness = new Thickness(1);
        subAutoGenBtn.CornerRadius = new CornerRadius(11);
        subAutoGenBtn.Content = genContent;
        AutomationProperties.SetName(subAutoGenBtn, L.T("SubAutoGenerate"));
        AutomationProperties.SetAutomationId(subAutoGenBtn, "editor.subtitles.autoGenerate");
        subAutoGenBtn.Click += async (_, _) => await SafeAsync(RunAutoGenerateSubtitlesAsync);
        aiRow.Children.Add(subAutoGenBtn);

        subOptionsToggleBtn.Background = RecorderTheme.Brush(Root, "EditorPanel");
        subOptionsToggleBtn.BorderBrush = RecorderTheme.Brush(Root, "EditorLine");
        subOptionsToggleBtn.BorderThickness = new Thickness(1);
        subOptionsToggleBtn.CornerRadius = new CornerRadius(11);
        subOptionsToggleBtn.Content = new EditorGlyph { Kind = "Sliders", Width = 14, Height = 14, Foreground = RecorderTheme.Brush(Root, "EditorSecondary") };
        ToolTipService.SetToolTip(subOptionsToggleBtn, L.T("SubEngineLabel"));
        subOptionsToggleBtn.Click += (_, _) =>
        {
            subtitleAsrOptionsExpanded = !subtitleAsrOptionsExpanded;
            subAsrOptionsPanel.Visibility = subtitleAsrOptionsExpanded ? Visibility.Visible : Visibility.Collapsed;
            UpdateAsrOptionsToggleVisual();
        };
        Grid.SetColumn(subOptionsToggleBtn, 1);
        aiRow.Children.Add(subOptionsToggleBtn);
        aiStack.Children.Add(aiRow);

        // Collapsible ASR Options
        subEngineCombo.Header = L.T("SubEngineLabel");
        subEngineCombo.Items.Add(L.T("SubEngineLocalTurbo"));
        subEngineCombo.Items.Add(L.T("SubEngineLocalSmall"));
        subEngineCombo.Items.Add(L.T("SubEngineCloudOpenAi"));
        subEngineCombo.Items.Add(L.T("SubEngineCloudGroq"));
        subEngineCombo.SelectedIndex = 0;
        subEngineCombo.SelectionChanged += (_, _) => RefreshLocalModelStatus();
        subAsrOptionsPanel.Children.Add(subEngineCombo);

        // Local model status + download / delete buttons
        var modelBtnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        subModelDownloadBtn.Background = RecorderTheme.Brush(Root, "EditorPanel");
        subModelDownloadBtn.BorderBrush = RecorderTheme.Brush(Root, "EditorLine");
        subModelDownloadBtn.BorderThickness = new Thickness(1);
        subModelDownloadBtn.CornerRadius = new CornerRadius(8);
        subModelDownloadBtn.Click += async (_, _) => await SafeAsync(DownloadSelectedWhisperModelAsync);
        subModelDeleteBtn.Content = L.T("SubModelDelete");
        subModelDeleteBtn.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 0xE3, 0x5D, 0x62));
        subModelDeleteBtn.Click += (_, _) => DeleteSelectedWhisperModel();
        modelBtnRow.Children.Add(subModelDownloadBtn);
        modelBtnRow.Children.Add(subModelDeleteBtn);
        subModelStatusPanel.Children.Add(subModelStatusLabel);
        subModelStatusPanel.Children.Add(modelBtnRow);
        subAsrOptionsPanel.Children.Add(subModelStatusPanel);

        // Track + Scope row
        var trackScopeGrid = new Grid { ColumnSpacing = 8 };
        trackScopeGrid.ColumnDefinitions.Add(new ColumnDefinition());
        trackScopeGrid.ColumnDefinitions.Add(new ColumnDefinition());

        subTrackCombo.Header = L.T("SubTrackLabel");
        subTrackCombo.Items.Add(L.T("SubTrackAll"));
        subTrackCombo.Items.Add(L.T("SubTrackMic"));
        subTrackCombo.Items.Add(L.T("SubTrackSystem"));
        subTrackCombo.SelectedIndex = 0;
        trackScopeGrid.Children.Add(subTrackCombo);

        subScopeCombo.Header = L.T("SubScopeLabel");
        subScopeCombo.Items.Add(L.T("SubScopeAll"));
        subScopeCombo.Items.Add(L.T("SubScopeSelected"));
        subScopeCombo.SelectedIndex = 0;
        Grid.SetColumn(subScopeCombo, 1);
        trackScopeGrid.Children.Add(subScopeCombo);
        subAsrOptionsPanel.Children.Add(trackScopeGrid);

        // Language selector
        subLanguageCombo.Header = L.T("SubLanguageLabel");
        subLanguageCombo.Items.Add(L.T("SubLanguageAuto"));
        subLanguageCombo.Items.Add(L.T("SubLanguageZh"));
        subLanguageCombo.Items.Add(L.T("SubLanguageEn"));
        subLanguageCombo.Items.Add(L.T("SubLanguageJa"));
        subLanguageCombo.SelectedIndex = 0;
        subAsrOptionsPanel.Children.Add(subLanguageCombo);

        // Custom Vocabulary Prompt
        subVocabBox.Header = L.T("SubVocabPromptLabel");
        subVocabBox.PlaceholderText = L.T("SubVocabPromptPlaceholder");
        subAsrOptionsPanel.Children.Add(subVocabBox);

        aiStack.Children.Add(subAsrOptionsPanel);

        // Progress + cancel row
        subCancelTaskBtn.Content = L.T("S7EEFAAFF791F");
        subCancelTaskBtn.Click += (_, _) => subtitleAsrCts?.Cancel();
        subProgressPanel.Children.Add(subProgressBar);
        var progRow = new Grid { ColumnSpacing = 8 };
        progRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        progRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        subProgressLabel.VerticalAlignment = VerticalAlignment.Center;
        progRow.Children.Add(subProgressLabel);
        Grid.SetColumn(subCancelTaskBtn, 1);
        progRow.Children.Add(subCancelTaskBtn);
        subProgressPanel.Children.Add(progRow);
        aiStack.Children.Add(subProgressPanel);

        subtitleSettings.Children.Add(new Border { Style = (Style)Application.Current.Resources["EditorTile"], Child = aiStack });
    }

    private void UpdateAsrOptionsToggleVisual()
    {
        subOptionsToggleBtn.Background = subtitleAsrOptionsExpanded
            ? RecorderTheme.Brush(Root, "EditorAccent")
            : RecorderTheme.Brush(Root, "EditorPanel");
        if (subOptionsToggleBtn.Content is EditorGlyph glyph)
        {
            glyph.Foreground = subtitleAsrOptionsExpanded
                ? RecorderTheme.Brush(Root, "EditorAccentInk")
                : RecorderTheme.Brush(Root, "EditorSecondary");
        }
    }

    private void BuildSubtitlesSubTabSwitch()
    {
        var switchBorder = new Border
        {
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(2)
        };
        switchBorder.Background = RecorderTheme.Brush(Root, "EditorWell");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        subTranscriptTabBtn.Content = new TextBlock { Text = L.T("SubSectionTranscript"), FontSize = 12 };
        subTranscriptTabBtn.CornerRadius = new CornerRadius(16);
        subTranscriptTabBtn.Click += (_, _) => { subtitleStylePage = false; UpdateSubtitleSubTabs(); RebuildSubtitleCueListIfNeeded(); };
        grid.Children.Add(subTranscriptTabBtn);

        subStyleTabBtn.Content = new TextBlock { Text = L.T("SubSectionStyle"), FontSize = 12 };
        subStyleTabBtn.CornerRadius = new CornerRadius(16);
        subStyleTabBtn.Click += (_, _) => { subtitleStylePage = true; UpdateSubtitleSubTabs(); LoadSubtitleStyleControls(); };
        Grid.SetColumn(subStyleTabBtn, 1);
        grid.Children.Add(subStyleTabBtn);

        switchBorder.Child = grid;
        subtitleSettings.Children.Add(switchBorder);
        subtitleSettings.Children.Add(subTranscriptPage);
        subtitleSettings.Children.Add(subStylePage);
    }

    private void BuildSubtitlesTranscriptPage()
    {
        // Find & Replace tile
        var frStack = new StackPanel { Spacing = 8 };
        frStack.Children.Add(subFindBox);
        var replaceRow = new Grid { ColumnSpacing = 6 };
        replaceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        replaceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        replaceRow.Children.Add(subReplaceBox);
        subReplaceAllBtn.Padding = new Thickness(10, 0, 10, 0);
        subReplaceAllBtn.FontSize = 11.5;
        subReplaceAllBtn.Click += async (_, _) => await SafeAsync(ReplaceAllSubtitlesAsync);
        Grid.SetColumn(subReplaceAllBtn, 1);
        replaceRow.Children.Add(subReplaceAllBtn);
        frStack.Children.Add(replaceRow);
        subTranscriptPage.Children.Add(new Border { Style = (Style)Application.Current.Resources["EditorTile"], Child = frStack });

        // Bilingual toggle + AI translate tile
        var biStack = new StackPanel { Spacing = 8 };
        subBilingualCheck.Click += async (_, _) =>
        {
            if (loadingSubtitles || !ready || working) return;
            var style = CurrentSubtitleStyle with { BilingualEnabled = subBilingualCheck.IsChecked == true };
            await PerformAsync(() => editor.ExecuteAsync(AiAgentCommandBuilder.BuildSubtitleStyleCommand(editor.Snapshot, style)));
        };
        biStack.Children.Add(subBilingualCheck);

        var trContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        trContent.Children.Add(new EditorGlyph { Kind = "Sparkle", Width = 13, Height = 13 });
        trContent.Children.Add(new TextBlock { Text = L.T("SubAiTranslate"), FontSize = 12 });
        subAiTranslateBtn.Content = trContent;
        subAiTranslateBtn.Click += async (_, _) => await SafeAsync(TranslateSubtitlesWithAiAsync);
        biStack.Children.Add(subAiTranslateBtn);
        subTranscriptPage.Children.Add(new Border { Style = (Style)Application.Current.Resources["EditorTile"], Child = biStack });

        // Empty state card
        var emptyStack = new StackPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 10), HorizontalAlignment = HorizontalAlignment.Center };
        emptyStack.Children.Add(new EditorGlyph { Kind = "Subtitles", Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center });
        emptyStack.Children.Add(new TextBlock
        {
            Text = L.T("SubEmptyTitle"),
            FontFamily = (FontFamily)Application.Current.Resources["RecorderSemiboldFont"],
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        emptyStack.Children.Add(new TextBlock
        {
            Text = L.T("SubEmptyDesc"),
            Style = (Style)Root.Resources["SmallLabel"],
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center
        });
        subEmptyCard.Style = (Style)Application.Current.Resources["EditorTile"];
        subEmptyCard.Child = emptyStack;
        subTranscriptPage.Children.Add(subEmptyCard);

        // Cue list host
        subTranscriptPage.Children.Add(subCueListPanel);
    }

    private void BuildSubtitlesStylePage()
    {
        // Selected cue override banner
        var overrideStack = new StackPanel { Spacing = 8 };
        overrideStack.Children.Add(new TextBlock { Text = L.T("SubResetStyle"), Style = (Style)Root.Resources["SmallLabel"], TextWrapping = TextWrapping.Wrap });
        var overrideBtnRow = new Grid { ColumnSpacing = 6 };
        overrideBtnRow.ColumnDefinitions.Add(new ColumnDefinition());
        overrideBtnRow.ColumnDefinitions.Add(new ColumnDefinition());
        var resetCueBtn = new Button { Content = L.T("SubResetStyle"), HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11, Padding = new Thickness(6, 0, 6, 0) };
        resetCueBtn.Click += async (_, _) => { if (selectedSubtitleId is not null) await SafeAsync(() => ResetSubtitleOverrideAsync(selectedSubtitleId)); };
        var applyAllBtn = new Button { Content = L.T("SubApplyStyleAll"), HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11, Padding = new Thickness(6, 0, 6, 0) };
        applyAllBtn.Click += async (_, _) => await SafeAsync(ApplySelectedOverrideToAllAsync);
        overrideBtnRow.Children.Add(resetCueBtn);
        Grid.SetColumn(applyAllBtn, 1);
        overrideBtnRow.Children.Add(applyAllBtn);
        overrideStack.Children.Add(overrideBtnRow);
        subOverrideBanner.Style = (Style)Application.Current.Resources["EditorTile"];
        subOverrideBanner.Child = overrideStack;
        subStylePage.Children.Add(subOverrideBanner);

        // 4 One-click Preset cards
        subStylePage.Children.Add(VisualLabel(L.T("SubPresetLabel")));
        subPresetGrid.ColumnDefinitions.Add(new ColumnDefinition());
        subPresetGrid.ColumnDefinitions.Add(new ColumnDefinition());
        subPresetGrid.RowDefinitions.Add(new RowDefinition());
        subPresetGrid.RowDefinitions.Add(new RowDefinition());

        var presets = new[]
        {
            ("classicOutline", L.T("SubPresetClassic"), "#FFFFFF", "#00000000", "#09090B"),
            ("darkPill", L.T("SubPresetPill"), "#FFFFFF", "#C809090B", "#09090B"),
            ("karaokePop", L.T("SubPresetKaraoke"), "#FACC15", "#00000000", "#09090B"),
            ("bilingualClean", L.T("SubPresetBilingual"), "#FFFFFF", "#8C09090B", "#38BDF8")
        };
        for (int i = 0; i < presets.Length; i++)
        {
            var (presetKey, title, fgHex, bgHex, accentHex) = presets[i];
            var cardContent = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            var previewPill = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 3, 10, 3),
                Background = ParseHexBrush(bgHex, Colors.Transparent),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text = presetKey == "bilingualClean" ? "中 / En" : "Aa 字幕",
                    FontSize = 12,
                    FontFamily = (FontFamily)Application.Current.Resources["RecorderSemiboldFont"],
                    Foreground = ParseHexBrush(fgHex, Colors.White)
                }
            };
            cardContent.Children.Add(previewPill);
            cardContent.Children.Add(new TextBlock { Text = title, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var btn = new Button
            {
                Content = cardContent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 58,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(6, 6, 6, 6),
                Tag = presetKey
            };
            AutomationProperties.SetAutomationId(btn, "editor.subtitles.preset." + presetKey);
            btn.Click += async (_, _) => await SafeAsync(() => ApplySubtitlePresetAsync(presetKey));
            Grid.SetColumn(btn, i % 2);
            Grid.SetRow(btn, i / 2);
            subPresetGrid.Children.Add(btn);
            subPresetButtons[presetKey] = btn;
        }
        subStylePage.Children.Add(subPresetGrid);

        // Typography & Colors Card
        var typoStack = new StackPanel { Spacing = 10 };
        subFontCombo.Header = L.T("SubFontLabel");
        foreach (var font in SubtitleFontChoices) subFontCombo.Items.Add(font);
        subFontCombo.SelectedIndex = 0;
        subFontCombo.SelectionChanged += (_, _) => QueueSubtitleStyleChange(commitSoon: true);
        typoStack.Children.Add(subFontCombo);

        typoStack.Children.Add(SmallCaption(L.T("SubFontSizeLabel")));
        subFontSize.DefaultValue = 44;
        WireSubtitleStyleSlider(subFontSize);
        typoStack.Children.Add(subFontSize);

        typoStack.Children.Add(SmallCaption(L.T("SubTextColorLabel")));
        BuildSwatchRow(subTextSwatchRow, SubtitleTextSwatches, hex => { subActiveTextColor = hex; RefreshAllSwatchIndicators(); QueueSubtitleStyleChange(commitSoon: true); });
        typoStack.Children.Add(subTextSwatchRow);

        typoStack.Children.Add(SmallCaption(L.T("SubOutlineColorLabel")));
        BuildSwatchRow(subOutlineSwatchRow, SubtitleOutlineSwatches, hex => { subActiveOutlineColor = hex; RefreshAllSwatchIndicators(); QueueSubtitleStyleChange(commitSoon: true); });
        typoStack.Children.Add(subOutlineSwatchRow);

        typoStack.Children.Add(SmallCaption(L.T("SubOutlineWidthLabel")));
        subOutlineWidth.DefaultValue = 3.0;
        WireSubtitleStyleSlider(subOutlineWidth);
        typoStack.Children.Add(subOutlineWidth);

        subStylePage.Children.Add(new Border { Style = (Style)Application.Current.Resources["EditorTile"], Child = typoStack });

        // Background & Highlight Card
        var bgStack = new StackPanel { Spacing = 10 };
        bgStack.Children.Add(SmallCaption(L.T("SubBgColorLabel")));
        BuildSwatchRow(subBgSwatchRow, SubtitleBgSwatches, hex => { subActiveBgColor = hex; RefreshAllSwatchIndicators(); QueueSubtitleStyleChange(commitSoon: true); });
        bgStack.Children.Add(subBgSwatchRow);

        bgStack.Children.Add(SmallCaption(L.T("SubBgOpacityLabel")));
        subBgOpacity.DefaultValue = 0;
        WireSubtitleStyleSlider(subBgOpacity);
        bgStack.Children.Add(subBgOpacity);

        bgStack.Children.Add(SmallCaption(L.T("SubBgRadiusLabel")));
        subBgRadius.DefaultValue = 12;
        WireSubtitleStyleSlider(subBgRadius);
        bgStack.Children.Add(subBgRadius);

        subHighlightMode.Header = L.T("SubHighlightModeLabel");
        subHighlightMode.Items.Add(L.T("SubHighlightNone"));
        subHighlightMode.Items.Add(L.T("SubHighlightWordColor"));
        subHighlightMode.Items.Add(L.T("SubHighlightWordPill"));
        subHighlightMode.SelectedIndex = 0;
        subHighlightMode.SelectionChanged += (_, _) => QueueSubtitleStyleChange(commitSoon: true);
        bgStack.Children.Add(subHighlightMode);

        bgStack.Children.Add(SmallCaption(L.T("SubHighlightColorLabel")));
        BuildSwatchRow(subHighlightSwatchRow, SubtitleHighlightSwatches, hex => { subActiveHighlightColor = hex; RefreshAllSwatchIndicators(); QueueSubtitleStyleChange(commitSoon: true); });
        bgStack.Children.Add(subHighlightSwatchRow);

        subStylePage.Children.Add(new Border { Style = (Style)Application.Current.Resources["EditorTile"], Child = bgStack });

        // Layout & Timing Card
        var layoutStack = new StackPanel { Spacing = 10 };
        layoutStack.Children.Add(SmallCaption(L.T("SubPositionYLabel")));
        subPosY.DefaultValue = 88;
        WireSubtitleStyleSlider(subPosY);
        layoutStack.Children.Add(subPosY);

        layoutStack.Children.Add(SmallCaption(L.T("SubAlignmentLabel")));
        for (int i = 0; i < SubtitleAlignments.Length; i++)
        {
            string alignKey = SubtitleAlignments[i];
            subAlignGrid.ColumnDefinitions.Add(new ColumnDefinition());
            string label = alignKey switch { "left" => "L", "right" => "R", _ => "C" };
            var btn = new Button
            {
                Content = label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 30,
                CornerRadius = new CornerRadius(10),
                Tag = alignKey
            };
            btn.Click += (_, _) =>
            {
                subActiveAlignment = alignKey;
                UpdateAlignmentButtons();
                QueueSubtitleStyleChange(commitSoon: true);
            };
            Grid.SetColumn(btn, i);
            subAlignGrid.Children.Add(btn);
            subAlignButtons[alignKey] = btn;
        }
        layoutStack.Children.Add(subAlignGrid);

        layoutStack.Children.Add(SmallCaption(L.T("SubMaxCharsLabel")));
        subMaxChars.DefaultValue = 18;
        WireSubtitleStyleSlider(subMaxChars);
        layoutStack.Children.Add(subMaxChars);

        layoutStack.Children.Add(SmallCaption(L.T("SubFadeLabel")));
        subFade.DefaultValue = 0.12;
        WireSubtitleStyleSlider(subFade);
        layoutStack.Children.Add(subFade);

        layoutStack.Children.Add(new TextBlock { Text = L.T("SubCanvasEditHint"), Style = (Style)Root.Resources["SmallLabel"], TextWrapping = TextWrapping.Wrap });
        subStylePage.Children.Add(new Border { Style = (Style)Application.Current.Resources["EditorTile"], Child = layoutStack });
    }

    private TextBlock SmallCaption(string text) => new() { Text = text, Style = (Style)Root.Resources["SmallLabel"] };

    private void BuildSwatchRow(StackPanel row, string[] hexValues, Action<string> onSelected)
    {
        row.Children.Clear();
        foreach (var hex in hexValues)
        {
            string colorHex = hex;
            var btn = new Button
            {
                Width = 28,
                Height = 28,
                MinWidth = 28,
                MinHeight = 28,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                Background = ParseHexBrush(colorHex, Colors.White),
                BorderThickness = new Thickness(1),
                BorderBrush = RecorderTheme.Brush(Root, "EditorLine"),
                Tag = colorHex
            };
            ToolTipService.SetToolTip(btn, colorHex);
            btn.Click += (_, _) => onSelected(colorHex);
            row.Children.Add(btn);
        }
    }

    private void WireSubtitleStyleSlider(EditorBarSlider slider)
    {
        slider.ValueChanged += (_, _) => QueueSubtitleStyleChange(commitSoon: false);
        slider.InteractionEnded += commit =>
        {
            if (!commit)
            {
                CancelSubtitleStylePreview();
            }
            else if (subtitleStyleDirty)
            {
                subStyleCommitTimer?.Stop();
                if (subStyleCommitTimer is not null)
                {
                    subStyleCommitTimer.Interval = TimeSpan.FromMilliseconds(1);
                    subStyleCommitTimer.Start();
                }
            }
        };
    }

    private SubtitleStyleSettings CurrentSubtitleStyle =>
        editor.Snapshot.Editor?.SubtitleStyle ?? SubtitleStyleSettings.ClassicOutline();

    private void SyncSubtitlePositionSlider(double normalizedY)
    {
        bool prev = loadingSubtitles;
        loadingSubtitles = true;
        try { subPosY.Value = Math.Clamp(Math.Round(normalizedY * 100.0), 10, 95); }
        finally { loadingSubtitles = prev; }
    }

    private void SelectSubtitleById(string effectId, bool openTab)
    {
        selectedSubtitleId = effectId;
        selectedSubtitleIds.Clear();
        selectedSubtitleIds.Add(effectId);
        if (openTab && inspectorTab != 6)
            ShowInspector(6);
        else
            RefreshSubtitleInspector();
        timeline.SelectVisual(effectId);
        QueueCanvasGeometry();
    }

    private void FocusSubtitleCueEditor(string effectId)
    {
        subtitleStylePage = false;
        UpdateSubtitleSubTabs();
        RebuildSubtitleCueListIfNeeded(force: true);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (subCueTextBoxes.TryGetValue(effectId, out var box))
            {
                box.Focus(FocusState.Programmatic);
                box.SelectAll();
            }
        });
    }

    private void RefreshSubtitleInspector()
    {
        if (!ready) return;
        RefreshSubtitleTrackChoices();
        RefreshLocalModelStatus();
        LoadSubtitleStyleControls();
        UpdateSubtitleSubTabs();
        if (inspectorTab == 6)
            RebuildSubtitleCueListIfNeeded();
    }

    private void RefreshSubtitleTrackChoices()
    {
        bool hasAndroid = editor.Snapshot.Media.Any(m => m.Kind == MediaKind.AndroidAudio);
        int expectedCount = hasAndroid ? 4 : 3;
        if (subTrackCombo.Items.Count == expectedCount) return;
        int prev = subTrackCombo.SelectedIndex;
        subTrackCombo.Items.Clear();
        subTrackCombo.Items.Add(L.T("SubTrackAll"));
        subTrackCombo.Items.Add(L.T("SubTrackMic"));
        subTrackCombo.Items.Add(L.T("SubTrackSystem"));
        if (hasAndroid) subTrackCombo.Items.Add(L.T("S060AB6DF2ED4"));
        subTrackCombo.SelectedIndex = Math.Clamp(prev, 0, subTrackCombo.Items.Count - 1);
    }

    private void UpdateSubtitleTheme()
    {
        if (!ready) return;
        UpdateSubtitleSubTabs();
        RefreshAllSwatchIndicators();
        UpdateAlignmentButtons();
        UpdatePresetButtonHighlights();
        UpdateSubtitleCardHighlights();
        if (subAddCueBtn.Content is StackPanel addRow)
        {
            var accentInk = RecorderTheme.Brush(Root, "EditorAccentInk");
            foreach (var child in addRow.Children)
            {
                if (child is EditorGlyph g) g.Foreground = accentInk;
                else if (child is TextBlock t) t.Foreground = accentInk;
            }
        }
        var ink = RecorderTheme.Brush(Root, "EditorInk");
        var panel = RecorderTheme.Brush(Root, "EditorPanel");
        var line = RecorderTheme.Brush(Root, "EditorLine");
        foreach (var btn in new[] { subImportBtn, subExportBtn, subAutoGenBtn, subModelDownloadBtn })
        {
            btn.Background = panel;
            btn.BorderBrush = line;
            if (btn.Content is TextBlock tb) tb.Foreground = ink;
            else if (btn.Content is StackPanel sp)
            {
                foreach (var child in sp.Children)
                {
                    if (child is EditorGlyph g) g.Foreground = ink;
                    else if (child is TextBlock t) t.Foreground = ink;
                }
            }
        }
        UpdateAsrOptionsToggleVisual();
    }

    private void UpdateSubtitleSubTabs()
    {
        subTranscriptPage.Visibility = !subtitleStylePage ? Visibility.Visible : Visibility.Collapsed;
        subStylePage.Visibility = subtitleStylePage ? Visibility.Visible : Visibility.Collapsed;

        var accent = RecorderTheme.Brush(Root, "EditorAccent");
        var accentInk = RecorderTheme.Brush(Root, "EditorAccentInk");
        var secondary = RecorderTheme.Brush(Root, "EditorSecondary");

        void PaintTab(Button btn, bool active)
        {
            btn.Background = active ? accent : new SolidColorBrush(Colors.Transparent);
            btn.Foreground = active ? accentInk : secondary;
            if (btn.Content is TextBlock tb) tb.Foreground = active ? accentInk : secondary;
        }
        PaintTab(subTranscriptTabBtn, !subtitleStylePage);
        PaintTab(subStyleTabBtn, subtitleStylePage);
    }

    private void LoadSubtitleStyleControls()
    {
        loadingSubtitles = true;
        try
        {
            var style = CurrentSubtitleStyle;
            int fontIdx = Array.FindIndex(SubtitleFontChoices, f => string.Equals(f, style.FontFamily, StringComparison.OrdinalIgnoreCase));
            subFontCombo.SelectedIndex = fontIdx >= 0 ? fontIdx : 0;
            subFontSize.Value = Math.Clamp(style.FontSize, 16, 120);
            subOutlineWidth.Value = Math.Clamp(style.OutlineWidth, 0, 12);
            subBgOpacity.Value = Math.Clamp(Math.Round(style.BackgroundOpacity * 100.0), 0, 100);
            subBgRadius.Value = Math.Clamp(style.BackgroundRadius, 0, 32);
            int hlIdx = Array.IndexOf(SubtitleHighlightModes, style.HighlightMode);
            subHighlightMode.SelectedIndex = hlIdx >= 0 ? hlIdx : 0;
            subPosY.Value = Math.Clamp(Math.Round(style.PositionY * 100.0), 10, 95);
            subMaxChars.Value = Math.Clamp(style.MaxCharsPerLine, 8, 48);
            subFade.Value = Math.Clamp(style.FadeSeconds, 0, 0.5);
            subBilingualCheck.IsChecked = style.BilingualEnabled;

            subActiveTextColor = style.TextColor;
            subActiveOutlineColor = style.OutlineColor;
            subActiveBgColor = style.BackgroundColor;
            subActiveHighlightColor = style.HighlightColor;
            subActiveAlignment = style.Alignment;

            var selCue = selectedSubtitleId is not null
                ? editor.Snapshot.Effects.FirstOrDefault(e => e.Id == selectedSubtitleId && e.Kind == "subtitle")
                : null;
            bool hasOverride = selCue is not null && SubtitleCueParameters.FromEffect(selCue).StyleOverride is not null;
            subOverrideBanner.Visibility = hasOverride ? Visibility.Visible : Visibility.Collapsed;

            RefreshAllSwatchIndicators();
            UpdateAlignmentButtons();
            UpdatePresetButtonHighlights();
        }
        finally
        {
            loadingSubtitles = false;
        }
    }

    private void RefreshAllSwatchIndicators()
    {
        PaintSwatchRow(subTextSwatchRow, subActiveTextColor);
        PaintSwatchRow(subOutlineSwatchRow, subActiveOutlineColor);
        PaintSwatchRow(subBgSwatchRow, subActiveBgColor);
        PaintSwatchRow(subHighlightSwatchRow, subActiveHighlightColor);
    }

    private void PaintSwatchRow(StackPanel row, string activeHex)
    {
        var accent = RecorderTheme.Brush(Root, "EditorAccent");
        var line = RecorderTheme.Brush(Root, "EditorLine");
        foreach (var btn in row.Children.OfType<Button>())
        {
            string tag = btn.Tag as string ?? "";
            bool active = string.Equals(tag, activeHex, StringComparison.OrdinalIgnoreCase);
            btn.BorderThickness = new Thickness(active ? 2.5 : 1);
            btn.BorderBrush = active ? accent : line;
            if (active)
            {
                bool lightSwatch = string.Equals(tag, "#FFFFFF", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(tag, "#FACC15", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(tag, "#4ADE80", StringComparison.OrdinalIgnoreCase);
                btn.Content = new EditorGlyph
                {
                    Kind = "Check",
                    Width = 13,
                    Height = 13,
                    Foreground = new SolidColorBrush(lightSwatch ? ColorHelper.FromArgb(255, 24, 24, 27) : Colors.White)
                };
            }
            else
            {
                btn.Content = null;
            }
        }
    }

    private void UpdateAlignmentButtons()
    {
        var accent = RecorderTheme.Brush(Root, "EditorAccent");
        var accentInk = RecorderTheme.Brush(Root, "EditorAccentInk");
        var secondary = RecorderTheme.Brush(Root, "EditorSecondary");
        foreach (var (key, btn) in subAlignButtons)
        {
            bool active = string.Equals(key, subActiveAlignment, StringComparison.OrdinalIgnoreCase);
            btn.Background = active ? accent : new SolidColorBrush(Colors.Transparent);
            btn.Foreground = active ? accentInk : secondary;
        }
    }

    private void UpdatePresetButtonHighlights()
    {
        var activePreset = CurrentSubtitleStyle.Preset;
        var accent = RecorderTheme.Brush(Root, "EditorAccent");
        var line = RecorderTheme.Brush(Root, "EditorLine");
        foreach (var (key, btn) in subPresetButtons)
        {
            bool active = string.Equals(key, activePreset, StringComparison.OrdinalIgnoreCase);
            btn.BorderThickness = new Thickness(active ? 2 : 1);
            btn.BorderBrush = active ? accent : line;
        }
    }

    private SubtitleStyleSettings BuildDraftSubtitleStyleFromControls()
    {
        var existing = CurrentSubtitleStyle;
        string font = SubtitleFontChoices[Math.Clamp(subFontCombo.SelectedIndex, 0, SubtitleFontChoices.Length - 1)];
        string hlMode = SubtitleHighlightModes[Math.Clamp(subHighlightMode.SelectedIndex, 0, SubtitleHighlightModes.Length - 1)];
        return existing with
        {
            Preset = "custom",
            FontFamily = font,
            FontSize = Math.Clamp(subFontSize.Value, 16, 120),
            TextColor = subActiveTextColor,
            OutlineColor = subActiveOutlineColor,
            OutlineWidth = Math.Clamp(subOutlineWidth.Value, 0, 12),
            BackgroundColor = subActiveBgColor,
            BackgroundOpacity = Math.Clamp(subBgOpacity.Value / 100.0, 0, 1),
            BackgroundRadius = Math.Clamp(subBgRadius.Value, 0, 32),
            HighlightColor = subActiveHighlightColor,
            HighlightMode = hlMode,
            PositionY = Math.Clamp(subPosY.Value / 100.0, 0.10, 0.95),
            Alignment = subActiveAlignment,
            MaxCharsPerLine = Math.Clamp((int)Math.Round(subMaxChars.Value), 8, 48),
            FadeSeconds = Math.Clamp(Math.Round(subFade.Value, 2), 0, 0.5)
        };
    }

    private bool AnySubtitleStyleSliderDragging =>
        subFontSize.IsDragging || subOutlineWidth.IsDragging || subBgOpacity.IsDragging ||
        subBgRadius.IsDragging || subPosY.IsDragging || subMaxChars.IsDragging || subFade.IsDragging;

    private void QueueSubtitleStyleChange(bool commitSoon)
    {
        if (loadingSubtitles || !ready || working || closing || editor.IsExporting) return;
        Pause();
        pendingSubtitleStyleDraft = BuildDraftSubtitleStyleFromControls();
        subtitleStyleDirty = true;
        preview.InvalidateRequests();
        if (subStylePreviewWork.IsCompleted && !subStylePreviewTimer!.IsRunning)
            subStylePreviewTimer.Start();

        subStyleCommitTimer?.Stop();
        if (!AnySubtitleStyleSliderDragging && subStyleCommitTimer is not null)
        {
            subStyleCommitTimer.Interval = TimeSpan.FromMilliseconds(commitSoon ? 120 : 400);
            subStyleCommitTimer.Start();
        }
    }

    private async Task RenderSubtitleStylePreviewLoopAsync()
    {
        try
        {
            while (pendingSubtitleStyleDraft is { } style && !closing)
            {
                pendingSubtitleStyleDraft = null;
                var draft = editor.Snapshot with
                {
                    Editor = (editor.Snapshot.Editor ?? new EditorSettings()) with { SubtitleStyle = style }
                };
                await preview.UpdateProjectAsync(CanvasDisplay(draft), position);
                await Task.Delay(33);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { SafeDiagnostics.Record(error); }
    }

    private async Task FlushSubtitleStyleChangesAsync()
    {
        subStylePreviewTimer?.Stop();
        subStyleCommitTimer?.Stop();
        if (!subtitleStyleDirty) return;
        subtitleStyleDirty = false;
        pendingSubtitleStyleDraft = null;
        await subStylePreviewWork;
        var style = BuildDraftSubtitleStyleFromControls();
        await PerformAsync(() => editor.ExecuteAsync(AiAgentCommandBuilder.BuildSubtitleStyleCommand(editor.Snapshot, style)));
    }

    private async void CancelSubtitleStylePreview()
    {
        subStylePreviewTimer?.Stop();
        subStyleCommitTimer?.Stop();
        pendingSubtitleStyleDraft = null;
        subtitleStyleDirty = false;
        await subStylePreviewWork;
        LoadSubtitleStyleControls();
        try { await preview.UpdateProjectAsync(CanvasDisplay(editor.Snapshot), position); }
        catch { }
    }

    private async Task ApplySubtitlePresetAsync(string presetKey)
    {
        if (working || editor.IsExporting) return;
        subStylePreviewTimer?.Stop();
        subStyleCommitTimer?.Stop();
        subtitleStyleDirty = false;
        pendingSubtitleStyleDraft = null;

        var style = SubtitleStyleSettings.ForPreset(presetKey);
        double aspect = (double)editor.Snapshot.Canvas.Width / Math.Max(1u, editor.Snapshot.Canvas.Height);
        if (aspect < SubtitleCanvasSnap.PortraitAspectCutoff)
            style = style with { PositionY = Math.Min(style.PositionY, 0.78) };

        await PerformAsync(() => editor.ExecuteAsync(AiAgentCommandBuilder.BuildSubtitleStyleCommand(editor.Snapshot, style)));
        LoadSubtitleStyleControls();
    }

    // ─────────────────────────── ASR & Whisper Model Controls ───────────────────────────

    private (string Engine, string ModelId) SelectedAsrEngine() => subEngineCombo.SelectedIndex switch
    {
        0 => ("whisper-local", WhisperModelManager.LargeTurboModelId),
        1 => ("whisper-local", WhisperModelManager.SmallModelId),
        2 => ("openai-whisper", "whisper-1"),
        3 => ("groq-whisper", "whisper-large-v3-turbo"),
        _ => ("whisper-local", WhisperModelManager.LargeTurboModelId)
    };

    private void RefreshLocalModelStatus()
    {
        var (engine, modelId) = SelectedAsrEngine();
        bool isLocal = engine == "whisper-local";
        subModelStatusPanel.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;
        if (!isLocal) return;

        bool installed = subtitleService.ModelManager.IsModelInstalled(modelId);
        var desc = subtitleService.ModelManager.GetDescriptor(modelId);
        string sizeLabel = $"{desc.ExpectedBytes / (1024.0 * 1024.0):0} MB";
        subModelStatusLabel.Text = installed
            ? L.T("SubModelInstalled")
            : L.T("SubModelNotInstalled", sizeLabel);
        subModelStatusLabel.Foreground = installed
            ? RecorderTheme.Brush(Root, "EditorSuccess")
            : RecorderTheme.Brush(Root, "EditorSecondary");
        subModelDownloadBtn.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        subModelDownloadBtn.Content = AppText.Language == "en" ? $"Download ({sizeLabel})" : $"下载模型 ({sizeLabel})";
        subModelDownloadBtn.IsEnabled = !subtitleBusy;
        subModelDeleteBtn.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        subModelDeleteBtn.IsEnabled = !subtitleBusy;
    }

    private void SetSubtitleBusy(bool busy, string? statusText = null, double progressPercent = 0)
    {
        subtitleBusy = busy;
        subAutoGenBtn.IsEnabled = !busy;
        subAiTranslateBtn.IsEnabled = !busy;
        subModelDownloadBtn.IsEnabled = !busy;
        subModelDeleteBtn.IsEnabled = !busy;
        subProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            subProgressBar.IsIndeterminate = progressPercent <= 0.1;
            subProgressBar.Value = Math.Clamp(progressPercent, 0, 100);
            if (!string.IsNullOrEmpty(statusText)) subProgressLabel.Text = statusText;
        }
        RefreshLocalModelStatus();
    }

    private async Task DownloadSelectedWhisperModelAsync()
    {
        if (subtitleBusy || working) return;
        var (engine, modelId) = SelectedAsrEngine();
        if (engine != "whisper-local") return;

        subtitleAsrCts?.Dispose();
        subtitleAsrCts = new CancellationTokenSource();
        var ct = subtitleAsrCts.Token;
        SetSubtitleBusy(true, L.T("SubModelDownloading", 0), 0);

        try
        {
            var progress = new Progress<WhisperDownloadProgress>(p =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    subProgressBar.IsIndeterminate = false;
                    subProgressBar.Value = p.Percent;
                    subProgressLabel.Text = L.T("SubModelDownloading", p.Percent);
                });
            });
            await subtitleService.ModelManager.EnsureModelAsync(modelId, progress, ct);
            ShowNotice(L.T("SubTabTitle"), L.T("SubModelInstalled"), InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            ShowNotice(L.T("SubTabTitle"), L.T("AiCancelled"));
        }
        catch (Exception ex)
        {
            ShowNotice(L.T("SubTabTitle"), ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetSubtitleBusy(false);
        }
    }

    private void DeleteSelectedWhisperModel()
    {
        if (subtitleBusy) return;
        var (engine, modelId) = SelectedAsrEngine();
        if (engine != "whisper-local") return;
        try
        {
            subtitleService.ModelManager.DeleteModel(modelId);
            RefreshLocalModelStatus();
        }
        catch (Exception ex)
        {
            ShowNotice(L.T("SubTabTitle"), ex.Message, InfoBarSeverity.Warning);
        }
    }

    private async Task RunAutoGenerateSubtitlesAsync()
    {
        if (subtitleBusy || working || editor.IsExporting) return;
        await FlushSubtitleStyleChangesAsync();
        await FlushVisualChangesAsync();

        var (engine, modelId) = SelectedAsrEngine();
        string trackFilter = subTrackCombo.SelectedIndex switch
        {
            1 => "microphone",
            2 => "systemAudio",
            3 => "androidAudio",
            _ => "all"
        };
        IReadOnlyList<string>? clipIds = subScopeCombo.SelectedIndex == 1 && !string.IsNullOrEmpty(selected)
            ? [selected]
            : null;
        string language = subLanguageCombo.SelectedIndex switch
        {
            1 => "zh",
            2 => "en",
            3 => "ja",
            _ => "auto"
        };
        int maxChars = CurrentSubtitleStyle.MaxCharsPerLine;
        var request = new SubtitleTranscriptionRequest(
            Engine: engine,
            ModelId: modelId,
            Language: language,
            TrackFilter: trackFilter,
            ClipIds: clipIds,
            VocabularyPrompt: string.IsNullOrWhiteSpace(subVocabBox.Text) ? null : subVocabBox.Text.Trim(),
            MaxCharsPerLine: maxChars);

        subtitleAsrCts?.Dispose();
        subtitleAsrCts = new CancellationTokenSource();
        var ct = subtitleAsrCts.Token;
        SetSubtitleBusy(true, L.T("SubGenerating", 0), 0);

        try
        {
            var snapshotCopy = editor.Snapshot;
            var projectDir = editor.DirectoryPath;
            var progress = new Progress<SubtitleTranscriptionProgress>(p =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    subProgressBar.IsIndeterminate = false;
                    subProgressBar.Value = Math.Clamp(p.Percent, 0, 100);
                    subProgressLabel.Text = p.Stage == "downloading"
                        ? L.T("SubModelDownloading", p.Percent)
                        : L.T("SubGenerating", p.Percent);
                });
            });

            var result = await Task.Run(
                () => subtitleService.TranscribeProjectAsync(projectDir, snapshotCopy, request, progress, ct),
                ct);

            if (result.Cues.Count == 0)
            {
                ShowNotice(L.T("SubTabTitle"), L.T("SubEmptyTitle"), InfoBarSeverity.Informational);
                return;
            }

            await PerformAsync(() => editor.ExecuteAsync(result.ReplaceCommand));
            selectedSubtitleId = result.Cues[0].Id;
            selectedSubtitleIds.Clear();
            selectedSubtitleIds.Add(selectedSubtitleId);
            subtitleAsrOptionsExpanded = false;
            subAsrOptionsPanel.Visibility = Visibility.Collapsed;
            subtitleStylePage = false;
            RefreshSubtitleInspector();
            timeline.SelectVisual(selectedSubtitleId);
            ShowNotice(L.T("SubTabTitle"), $"{result.Cues.Count} · {result.EngineUsed}", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            ShowNotice(L.T("SubTabTitle"), L.T("AiCancelled"));
        }
        catch (Exception ex)
        {
            ShowNotice(L.T("SubTabTitle"), ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetSubtitleBusy(false);
        }
    }

    // ─────────────────────────── Cue List, CapCut Editing & Actions ───────────────────────────

    private void RebuildSubtitleCueListIfNeeded(bool force = false)
    {
        var cues = OrderedSubtitles;
        bool bilingual = CurrentSubtitleStyle.BilingualEnabled;
        var sigBuilder = new StringBuilder(cues.Count * 48);
        sigBuilder.Append(bilingual ? "B1|" : "B0|");
        foreach (var c in cues)
        {
            var p = SubtitleCueParameters.FromEffect(c);
            sigBuilder.Append(c.Id).Append(':')
                .Append(VisualResources.Ticks(c.SourceStart)).Append(':')
                .Append(VisualResources.Ticks(c.SourceDuration)).Append(':')
                .Append(p.Text).Append('|')
                .Append(p.SecondaryText ?? "").Append(';');
        }
        string sig = sigBuilder.ToString();
        if (!force && string.Equals(sig, lastRenderedSubtitleSignature, StringComparison.Ordinal))
        {
            UpdateSubtitleCardHighlights();
            return;
        }
        lastRenderedSubtitleSignature = sig;

        subCueListPanel.Children.Clear();
        subCueTextBoxes.Clear();
        subCueCards.Clear();

        subEmptyCard.Visibility = cues.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (cues.Count == 0) return;

        var wellBrush = RecorderTheme.Brush(Root, "EditorWell");
        var lineBrush = RecorderTheme.Brush(Root, "EditorLine");
        var secondaryBrush = RecorderTheme.Brush(Root, "EditorSecondary");

        for (int i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            int cueIndex = i;
            string cueId = cue.Id;
            var cueParams = SubtitleCueParameters.FromEffect(cue);
            long srcStart = VisualResources.Ticks(cue.SourceStart);
            long srcEnd = srcStart + VisualResources.Ticks(cue.SourceDuration);

            var cardBody = new StackPanel { Spacing = 6 };

            // Header row: timestamp pill + merge/split/delete icon buttons
            var header = new Grid { ColumnSpacing = 4 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var timeBtn = new Button
            {
                Content = $"{SubtitleCueMath.FormatCueTime(srcStart)} – {SubtitleCueMath.FormatCueTime(srcEnd)}",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10.5,
                Height = 24,
                MinHeight = 24,
                Padding = new Thickness(8, 0, 8, 0),
                CornerRadius = new CornerRadius(12),
                Background = wellBrush,
                Foreground = secondaryBrush,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            timeBtn.Click += async (_, _) => await SafeAsync(() => SeekAndSelectSubtitleAsync(cueId));
            header.Children.Add(timeBtn);

            var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            if (cueIndex > 0)
            {
                var mergeBtn = new Button
                {
                    Content = "↑",
                    FontSize = 11,
                    Width = 24,
                    Height = 24,
                    MinWidth = 24,
                    MinHeight = 24,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(8)
                };
                ToolTipService.SetToolTip(mergeBtn, L.T("SubMergePrev"));
                mergeBtn.Click += async (_, _) => await SafeAsync(() => MergeCueWithPreviousAsync(cueId));
                actionRow.Children.Add(mergeBtn);
            }

            var delBtn = new Button
            {
                Content = new EditorGlyph { Kind = "Trash", Width = 12, Height = 12, Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 0xE3, 0x5D, 0x62)) },
                Width = 24,
                Height = 24,
                MinWidth = 24,
                MinHeight = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(8)
            };
            ToolTipService.SetToolTip(delBtn, L.T("SubDeleteCue"));
            delBtn.Click += async (_, _) => await SafeAsync(async () =>
            {
                await PerformAsync(() => editor.DeleteEffectAsync(cueId));
                if (selectedSubtitleId == cueId) selectedSubtitleId = null;
                selectedSubtitleIds.Remove(cueId);
            });
            actionRow.Children.Add(delBtn);

            Grid.SetColumn(actionRow, 1);
            header.Children.Add(actionRow);
            cardBody.Children.Add(header);

            // Primary cue TextBox with CapCut Enter-split & Backspace-merge
            var primaryBox = new TextBox
            {
                Text = cueParams.Text ?? "",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 6, 8, 6)
            };
            primaryBox.GotFocus += (_, _) =>
            {
                if (selectedSubtitleId != cueId)
                {
                    selectedSubtitleId = cueId;
                    selectedSubtitleIds.Clear();
                    selectedSubtitleIds.Add(cueId);
                    UpdateSubtitleCardHighlights();
                    timeline.SelectVisual(cueId);
                }
            };
            primaryBox.PreviewKeyDown += async (sender, args) =>
            {
                if (sender is not TextBox tb) return;
                bool shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
                bool ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
                if (args.Key == VirtualKey.Enter && !shift && !ctrl)
                {
                    int caret = tb.SelectionStart;
                    string currentText = tb.Text ?? "";
                    if (caret > 0 && caret < currentText.Length)
                    {
                        args.Handled = true;
                        await SafeAsync(() => SplitCueByCaretAsync(cueId, currentText, caret));
                        return;
                    }
                    args.Handled = true;
                    await SafeAsync(() => CommitCueTextEditAsync(cueId, currentText, isSecondary: false));
                }
                else if (args.Key == VirtualKey.Back && tb.SelectionStart == 0 && tb.SelectionLength == 0 && cueIndex > 0)
                {
                    args.Handled = true;
                    await SafeAsync(() => MergeCueWithPreviousAsync(cueId));
                }
            };
            primaryBox.LostFocus += async (sender, _) =>
            {
                if (sender is TextBox tb)
                    await SafeAsync(() => CommitCueTextEditAsync(cueId, tb.Text ?? "", isSecondary: false));
            };
            cardBody.Children.Add(primaryBox);
            subCueTextBoxes[cueId] = primaryBox;

            // Optional secondary text box in bilingual mode
            if (bilingual || !string.IsNullOrWhiteSpace(cueParams.SecondaryText))
            {
                var secondaryBox = new TextBox
                {
                    Text = cueParams.SecondaryText ?? "",
                    PlaceholderText = L.T("SubBilingualToggle"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(8, 5, 8, 5)
                };
                secondaryBox.LostFocus += async (sender, _) =>
                {
                    if (sender is TextBox tb)
                        await SafeAsync(() => CommitCueTextEditAsync(cueId, tb.Text ?? "", isSecondary: true));
                };
                secondaryBox.PreviewKeyDown += async (sender, args) =>
                {
                    if (args.Key == VirtualKey.Enter && sender is TextBox tb)
                    {
                        args.Handled = true;
                        await SafeAsync(() => CommitCueTextEditAsync(cueId, tb.Text ?? "", isSecondary: true));
                    }
                };
                cardBody.Children.Add(secondaryBox);
            }

            var card = new Border
            {
                Style = (Style)Application.Current.Resources["EditorTile"],
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                BorderBrush = lineBrush,
                Child = cardBody
            };
            subCueCards[cueId] = card;
            subCueListPanel.Children.Add(card);
        }

        UpdateSubtitleCardHighlights();
    }

    private void UpdateSubtitleCardHighlights()
    {
        var accent = RecorderTheme.Brush(Root, "EditorAccent");
        var line = RecorderTheme.Brush(Root, "EditorLine");
        foreach (var (id, card) in subCueCards)
        {
            bool active = id == selectedSubtitleId || selectedSubtitleIds.Contains(id);
            card.BorderBrush = active ? accent : line;
            card.BorderThickness = new Thickness(active ? 2 : 1);
        }
    }

    private async Task SeekAndSelectSubtitleAsync(string cueId)
    {
        selectedSubtitleId = cueId;
        selectedSubtitleIds.Clear();
        selectedSubtitleIds.Add(cueId);
        UpdateSubtitleCardHighlights();
        timeline.SelectVisual(cueId);

        var resolved = editor.Timeline.Effects.FirstOrDefault(e => e.Id == cueId);
        if (resolved is not null)
        {
            await SeekAsync(resolved.OutputStart + Math.Min(resolved.OutputDuration / 2, 1_000_000L));
        }
        else if (editor.Snapshot.Effects.FirstOrDefault(e => e.Id == cueId) is { } eff)
        {
            var mapped = mapper.MapSourceTime(VisualResources.Ticks(eff.SourceStart));
            if (mapped is not null) await SeekAsync(mapped.Ticks);
        }
    }

    private async Task CommitCueTextEditAsync(string cueId, string newText, bool isSecondary)
    {
        if (working || closing || editor.IsExporting) return;
        var cue = editor.Snapshot.Effects.FirstOrDefault(e => e.Id == cueId && e.Kind == "subtitle");
        if (cue is null) return;
        var prior = SubtitleCueParameters.FromEffect(cue);
        string trimmed = newText.Trim();
        if (!isSecondary)
        {
            if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, prior.Text, StringComparison.Ordinal))
                return;
            var nextParams = prior with { Text = trimmed, Words = null };
            await PerformAsync(() => editor.UpsertEffectAsync(cue with { Parameters = nextParams.ToJsonElement() }));
        }
        else
        {
            string? nextSec = string.IsNullOrEmpty(trimmed) ? null : trimmed;
            if (string.Equals(nextSec, prior.SecondaryText, StringComparison.Ordinal))
                return;
            var nextParams = prior with { SecondaryText = nextSec };
            await PerformAsync(() => editor.UpsertEffectAsync(cue with { Parameters = nextParams.ToJsonElement() }));
        }
    }

    private async Task SplitCueByCaretAsync(string cueId, string editedText, int caretIndex)
    {
        if (working || editor.IsExporting) return;
        var cues = OrderedSubtitles.ToList();
        int idx = cues.FindIndex(c => c.Id == cueId);
        if (idx < 0) return;

        var split = SubtitleCueMath.SplitByCaret(cues[idx], editedText, caretIndex);
        if (split is null) return;

        cues[idx] = split.Value.First;
        cues.Insert(idx + 1, split.Value.Second);
        var cmd = AiAgentCommandBuilder.BuildSubtitlesReplaceCommand(cues);
        await PerformAsync(() => editor.ExecuteAsync(cmd));
        selectedSubtitleId = split.Value.Second.Id;
        selectedSubtitleIds.Clear();
        selectedSubtitleIds.Add(selectedSubtitleId);
        FocusSubtitleCueEditor(selectedSubtitleId);
    }

    private async Task MergeCueWithPreviousAsync(string cueId)
    {
        if (working || editor.IsExporting) return;
        var cues = OrderedSubtitles.ToList();
        int idx = cues.FindIndex(c => c.Id == cueId);
        if (idx <= 0) return;

        var merged = SubtitleCueMath.MergeCues(cues[idx - 1], cues[idx]);
        cues[idx - 1] = merged;
        cues.RemoveAt(idx);
        var cmd = AiAgentCommandBuilder.BuildSubtitlesReplaceCommand(cues);
        await PerformAsync(() => editor.ExecuteAsync(cmd));
        selectedSubtitleId = merged.Id;
        selectedSubtitleIds.Clear();
        selectedSubtitleIds.Add(merged.Id);
        FocusSubtitleCueEditor(merged.Id);
    }

    private async Task MergeSubtitleWithNextAsync(string cueId)
    {
        if (working || editor.IsExporting) return;
        var cues = OrderedSubtitles.ToList();
        int idx = cues.FindIndex(c => c.Id == cueId);
        if (idx < 0 || idx + 1 >= cues.Count) return;

        var merged = SubtitleCueMath.MergeCues(cues[idx], cues[idx + 1]);
        cues[idx] = merged;
        cues.RemoveAt(idx + 1);
        var cmd = AiAgentCommandBuilder.BuildSubtitlesReplaceCommand(cues);
        await PerformAsync(() => editor.ExecuteAsync(cmd));
        selectedSubtitleId = merged.Id;
        selectedSubtitleIds.Clear();
        selectedSubtitleIds.Add(merged.Id);
        RefreshSubtitleInspector();
        timeline.SelectVisual(merged.Id);
    }

    private async Task<bool> SplitSubtitleAtPlayheadAsync(string? targetId)
    {
        if (working || editor.IsExporting) return false;
        long srcTicks = SourcePosition;
        var cues = OrderedSubtitles.ToList();
        int idx = -1;
        if (!string.IsNullOrEmpty(targetId))
        {
            idx = cues.FindIndex(c => c.Id == targetId);
            if (idx >= 0)
            {
                long st = VisualResources.Ticks(cues[idx].SourceStart);
                long en = st + VisualResources.Ticks(cues[idx].SourceDuration);
                if (srcTicks <= st || srcTicks >= en) idx = -1;
            }
        }
        if (idx < 0)
        {
            idx = cues.FindIndex(c =>
            {
                long st = VisualResources.Ticks(c.SourceStart);
                long en = st + VisualResources.Ticks(c.SourceDuration);
                return srcTicks > st && srcTicks < en;
            });
        }
        if (idx < 0) return false;

        var split = SubtitleCueMath.SplitAtSourceTime(cues[idx], srcTicks);
        if (split is null) return false;

        cues[idx] = split.Value.First;
        cues.Insert(idx + 1, split.Value.Second);
        var cmd = AiAgentCommandBuilder.BuildSubtitlesReplaceCommand(cues);
        await PerformAsync(() => editor.ExecuteAsync(cmd));
        selectedSubtitleId = split.Value.Second.Id;
        selectedSubtitleIds.Clear();
        selectedSubtitleIds.Add(selectedSubtitleId);
        RefreshSubtitleInspector();
        timeline.SelectVisual(selectedSubtitleId);
        return true;
    }

    private async Task ResetSubtitleOverrideAsync(string cueId)
    {
        var cue = editor.Snapshot.Effects.FirstOrDefault(e => e.Id == cueId && e.Kind == "subtitle");
        if (cue is null) return;
        var prior = SubtitleCueParameters.FromEffect(cue);
        if (prior.StyleOverride is null) return;
        var nextParams = prior with { StyleOverride = null };
        await PerformAsync(() => editor.UpsertEffectAsync(cue with { Parameters = nextParams.ToJsonElement() }));
        LoadSubtitleStyleControls();
    }

    private async Task ApplySelectedOverrideToAllAsync()
    {
        if (selectedSubtitleId is null) return;
        var cue = editor.Snapshot.Effects.FirstOrDefault(e => e.Id == selectedSubtitleId && e.Kind == "subtitle");
        if (cue is null) return;
        var ov = SubtitleCueParameters.FromEffect(cue).StyleOverride;
        if (ov is null) return;

        var nextStyle = CurrentSubtitleStyle with
        {
            Preset = "custom",
            PositionX = ov.PositionX ?? CurrentSubtitleStyle.PositionX,
            PositionY = ov.PositionY ?? CurrentSubtitleStyle.PositionY,
            FontSize = ov.FontSize ?? CurrentSubtitleStyle.FontSize,
            TextColor = ov.TextColor ?? CurrentSubtitleStyle.TextColor,
            OutlineColor = ov.OutlineColor ?? CurrentSubtitleStyle.OutlineColor,
            BackgroundColor = ov.BackgroundColor ?? CurrentSubtitleStyle.BackgroundColor,
            HighlightColor = ov.HighlightColor ?? CurrentSubtitleStyle.HighlightColor,
            Alignment = ov.Alignment ?? CurrentSubtitleStyle.Alignment
        };

        var clearedCues = OrderedSubtitles
            .Select(c =>
            {
                var p = SubtitleCueParameters.FromEffect(c);
                return p.StyleOverride is null ? c : c with { Parameters = (p with { StyleOverride = null }).ToJsonElement() };
            })
            .ToArray();

        var cmd = AiAgentCommandBuilder.BuildSubtitlesReplaceCommand(clearedCues, subtitleStyle: nextStyle);
        await PerformAsync(() => editor.ExecuteAsync(cmd));
        LoadSubtitleStyleControls();
    }

    private async Task AddSubtitleAtPlayheadAsync()
    {
        if (working || editor.IsExporting) return;
        long totalSrc = VisualResources.Ticks(editor.Snapshot.SourceDuration);
        if (totalSrc <= 0) return;

        long start = Math.Clamp(SourcePosition, 0, Math.Max(0, totalSrc - 1));
        long duration = Math.Min(20_000_000L, totalSrc - start);
        if (duration <= 0) return;

        string id = "sub-" + Guid.NewGuid().ToString("N")[..12];
        var cueParams = new SubtitleCueParameters { Text = L.T("SubAddCue") };
        var effect = new SourceEffect
        {
            Id = id,
            Kind = "subtitle",
            SourceStart = MediaTime.FromTicks(start),
            SourceDuration = MediaTime.FromTicks(duration),
            Parameters = cueParams.ToJsonElement()
        };

        await PerformAsync(() => editor.UpsertEffectAsync(effect));
        selectedSubtitleId = id;
        selectedSubtitleIds.Clear();
        selectedSubtitleIds.Add(id);
        if (inspectorTab != 6) ShowInspector(6);
        FocusSubtitleCueEditor(id);
        timeline.SelectVisual(id);
    }

    private async Task DeleteSelectedSubtitlesAsync()
    {
        if (working || editor.IsExporting) return;
        var toDelete = new HashSet<string>(selectedSubtitleIds, StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(selectedSubtitleId)) toDelete.Add(selectedSubtitleId);
        if (toDelete.Count == 0) return;

        if (toDelete.Count == 1)
        {
            string onlyId = toDelete.First();
            await PerformAsync(() => editor.DeleteEffectAsync(onlyId));
        }
        else
        {
            var remaining = OrderedSubtitles.Where(c => !toDelete.Contains(c.Id)).ToArray();
            var cmd = AiAgentCommandBuilder.BuildSubtitlesReplaceCommand(remaining);
            await PerformAsync(() => editor.ExecuteAsync(cmd));
        }

        selectedSubtitleIds.Clear();
        selectedSubtitleId = OrderedSubtitles.FirstOrDefault()?.Id;
        RefreshSubtitleInspector();
        timeline.SelectVisual(selectedSubtitleId);
    }

    private async Task ReplaceAllSubtitlesAsync()
    {
        string find = subFindBox.Text ?? "";
        string replace = subReplaceBox.Text ?? "";
        if (string.IsNullOrEmpty(find)) return;

        var (updated, count) = SubtitleCueMath.FindAndReplace(OrderedSubtitles, find, replace);
        if (count == 0)
        {
            ShowNotice(L.T("SubTabTitle"), $"{L.T("SubReplaceAll")}: 0");
            return;
        }
        var cmd = AiAgentCommandBuilder.BuildSubtitlesReplaceCommand(updated);
        await PerformAsync(() => editor.ExecuteAsync(cmd));
        ShowNotice(L.T("SubTabTitle"), $"{L.T("SubReplaceAll")}: {count}", InfoBarSeverity.Success);
    }

    private async Task ImportSubtitlesAsync()
    {
        if (working || editor.IsExporting) return;
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".srt");
        picker.FileTypeFilter.Add(".vtt");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        string content = await File.ReadAllTextAsync(file.Path, Encoding.UTF8);
        long totalSrc = VisualResources.Ticks(editor.Snapshot.SourceDuration);
        var imported = SrtVttCodec.ImportToSourceEffects(content, editor.Timeline, totalSrc);
        if (imported.Count == 0)
        {
            ShowNotice(L.T("SubImport"), L.T("SubEmptyTitle"), InfoBarSeverity.Warning);
            return;
        }

        bool hasBilingual = imported.Any(c => !string.IsNullOrWhiteSpace(SubtitleCueParameters.FromEffect(c).SecondaryText));
        SubtitleStyleSettings? styleUpdate = hasBilingual ? CurrentSubtitleStyle with { BilingualEnabled = true } : null;
        var cmd = AiAgentCommandBuilder.BuildSubtitlesReplaceCommand(imported, subtitleStyle: styleUpdate);
        await PerformAsync(() => editor.ExecuteAsync(cmd));
        selectedSubtitleId = imported[0].Id;
        selectedSubtitleIds.Clear();
        selectedSubtitleIds.Add(selectedSubtitleId);
        RefreshSubtitleInspector();
        timeline.SelectVisual(selectedSubtitleId);
        ShowNotice(L.T("SubImport"), $"{imported.Count}", InfoBarSeverity.Success);
    }

    private async Task ExportSubtitlesFileAsync()
    {
        if (!HasSubtitles)
        {
            ShowNotice(L.T("SubExport"), L.T("SubEmptyTitle"), InfoBarSeverity.Warning);
            return;
        }
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = ProjectTitle.Text };
        picker.FileTypeChoices.Add("SubRip Subtitle (.srt)", new[] { ".srt" });
        picker.FileTypeChoices.Add("WebVTT Subtitle (.vtt)", new[] { ".vtt" });
        picker.FileTypeChoices.Add("Plain Text (.txt)", new[] { ".txt" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        var ext = Path.GetExtension(file.Path).ToLowerInvariant();
        var fmt = ext switch
        {
            ".vtt" => SubtitleExportFormat.Vtt,
            ".txt" => SubtitleExportFormat.Txt,
            _ => SubtitleExportFormat.Srt
        };
        string output = SrtVttCodec.Export(editor.Snapshot, editor.Timeline, fmt);
        await File.WriteAllTextAsync(file.Path, output, new UTF8Encoding(false));
        ShowNotice(L.T("SubExport"), L.T("S1640C24FF7BA") + file.Path, InfoBarSeverity.Success);
    }

    // ─────────────────────────── AI Bilingual Translation ───────────────────────────

    private async Task TranslateSubtitlesWithAiAsync()
    {
        if (subtitleBusy || working || editor.IsExporting) return;
        var cues = OrderedSubtitles.ToArray();
        if (cues.Length == 0)
        {
            ShowNotice(L.T("SubAiTranslate"), L.T("SubEmptyTitle"), InfoBarSeverity.Informational);
            return;
        }

        var settings = aiCredentials.Load();
        string providerKey = (settings.ActiveProvider ?? "openai").Trim().ToLowerInvariant();
        string? apiKey = aiCredentials.GetApiKey(providerKey)
            ?? aiCredentials.GetApiKey("openai")
            ?? aiCredentials.GetApiKey("deepseek")
            ?? aiCredentials.GetApiKey("anthropic");

        bool isOllama = providerKey == "ollama";
        if (!isOllama && string.IsNullOrWhiteSpace(apiKey))
        {
            ShowNotice(L.T("SubAiTranslate"), L.T("AiApiKeyPlaceholder"), InfoBarSeverity.Warning);
            return;
        }

        string chosenProvider = !string.IsNullOrWhiteSpace(aiCredentials.GetApiKey(providerKey))
            ? providerKey
            : !string.IsNullOrWhiteSpace(aiCredentials.GetApiKey("openai")) ? "openai"
            : !string.IsNullOrWhiteSpace(aiCredentials.GetApiKey("deepseek")) ? "deepseek"
            : !string.IsNullOrWhiteSpace(aiCredentials.GetApiKey("anthropic")) ? "anthropic"
            : providerKey;

        bool sourceIsCjk = cues.Count(c => ContainsCjkChar(SubtitleCueParameters.FromEffect(c).Text)) >= cues.Length / 2.0;
        string targetLanguage = sourceIsCjk ? "natural concise English" : "natural concise Simplified Chinese (简体中文)";

        subtitleAsrCts?.Dispose();
        subtitleAsrCts = new CancellationTokenSource();
        var ct = subtitleAsrCts.Token;
        SetSubtitleBusy(true, L.T("SubTranslating"), 10);

        try
        {
            var updated = new List<SourceEffect>(cues.Length);
            const int batchSize = 30;
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };

            for (int offset = 0; offset < cues.Length; offset += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batch = cues.Skip(offset).Take(batchSize).ToArray();
                var texts = batch.Select(c => SubtitleCueParameters.FromEffect(c).Text ?? "").ToArray();
                double pct = 10.0 + 80.0 * offset / cues.Length;
                DispatcherQueue.TryEnqueue(() => { subProgressBar.IsIndeterminate = false; subProgressBar.Value = pct; });

                var translated = await RequestBatchTranslationAsync(http, chosenProvider, settings, apiKey ?? "", texts, targetLanguage, ct);
                for (int j = 0; j < batch.Length; j++)
                {
                    var p = SubtitleCueParameters.FromEffect(batch[j]);
                    string? sec = j < translated.Length && !string.IsNullOrWhiteSpace(translated[j])
                        ? translated[j].Trim()
                        : p.SecondaryText;
                    updated.Add(batch[j] with { Parameters = (p with { SecondaryText = sec }).ToJsonElement() });
                }
            }

            var nextStyle = CurrentSubtitleStyle with { BilingualEnabled = true };
            var cmd = AiAgentCommandBuilder.BuildSubtitlesReplaceCommand(updated, subtitleStyle: nextStyle);
            await PerformAsync(() => editor.ExecuteAsync(cmd));
            RefreshSubtitleInspector();
            ShowNotice(L.T("SubAiTranslate"), $"{updated.Count}", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            ShowNotice(L.T("SubAiTranslate"), L.T("AiCancelled"));
        }
        catch (Exception ex)
        {
            ShowNotice(L.T("SubAiTranslate"), ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetSubtitleBusy(false);
        }
    }

    private static async Task<string[]> RequestBatchTranslationAsync(
        HttpClient http,
        string provider,
        AiUserSettings settings,
        string apiKey,
        string[] sourceLines,
        string targetLanguage,
        CancellationToken ct)
    {
        string userPrompt = "Translate each subtitle string in this JSON array into " + targetLanguage +
            ". Return ONLY a valid JSON array of translated strings of the EXACT same length, with no markdown fences or commentary.\n" +
            JsonSerializer.Serialize(sourceLines);

        settings.ProviderBaseUrls.TryGetValue(provider, out var configuredBase);
        settings.ProviderModels.TryGetValue(provider, out var configuredModel);
        string baseUrl = !string.IsNullOrWhiteSpace(configuredBase) ? configuredBase.Trim().TrimEnd('/') : settings.CustomBaseUrl?.Trim().TrimEnd('/') ?? "";

        string rawContent;
        if (provider == "anthropic")
        {
            string endpoint = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.anthropic.com/v1/messages" : (baseUrl.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) ? baseUrl : baseUrl + "/v1/messages");
            string model = !string.IsNullOrWhiteSpace(configuredModel) ? configuredModel : "claude-3-5-sonnet-latest";
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            req.Content = new StringContent(JsonSerializer.Serialize(new
            {
                model,
                max_tokens = 4096,
                messages = new[] { new { role = "user", content = userPrompt } }
            }), Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Translation API failed ({(int)resp.StatusCode}): {body}");
            using var doc = JsonDocument.Parse(body);
            rawContent = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "[]";
        }
        else
        {
            string defaultEndpoint = provider switch
            {
                "deepseek" => "https://api.deepseek.com/v1/chat/completions",
                "ollama" => "http://127.0.0.1:11434/v1/chat/completions",
                _ => "https://api.openai.com/v1/chat/completions"
            };
            string endpoint = string.IsNullOrWhiteSpace(baseUrl)
                ? defaultEndpoint
                : baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? baseUrl
                : baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? baseUrl + "/chat/completions"
                : baseUrl + "/v1/chat/completions";
            string model = !string.IsNullOrWhiteSpace(configuredModel)
                ? configuredModel
                : provider switch { "deepseek" => "deepseek-flash", "ollama" => "qwen2.5:7b", _ => "gpt-4o-mini" };
            if (provider == "deepseek" &&
                (model.Equals("deepseek-chat", StringComparison.OrdinalIgnoreCase) ||
                 model.Equals("deepseek-reasoner", StringComparison.OrdinalIgnoreCase)))
            {
                model = "deepseek-flash";
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            if (!string.IsNullOrWhiteSpace(apiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(new
            {
                model,
                temperature = 0.2,
                messages = new[]
                {
                    new { role = "system", content = "You are a precise video subtitle translator. Respond strictly with a JSON array of strings." },
                    new { role = "user", content = userPrompt }
                }
            }), Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Translation API failed ({(int)resp.StatusCode}): {body}");
            using var doc = JsonDocument.Parse(body);
            rawContent = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "[]";
        }

        return ParseJsonStringArray(rawContent, sourceLines.Length);
    }

    private static string[] ParseJsonStringArray(string raw, int expectedLength)
    {
        string trimmed = raw.Trim();
        int start = trimmed.IndexOf('[');
        int end = trimmed.LastIndexOf(']');
        if (start >= 0 && end > start)
            trimmed = trimmed[start..(end + 1)];
        var parsed = JsonSerializer.Deserialize<string[]>(trimmed) ?? Array.Empty<string>();
        if (parsed.Length == expectedLength) return parsed;
        var padded = new string[expectedLength];
        for (int i = 0; i < expectedLength; i++)
            padded[i] = i < parsed.Length ? parsed[i] : "";
        return padded;
    }

    private static bool ContainsCjkChar(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (char c in text)
        {
            if ((c >= '\u4E00' && c <= '\u9FFF') || (c >= '\u3400' && c <= '\u4DBF'))
                return true;
        }
        return false;
    }

    // ─────────────────────────── Inline Canvas Subtitle Editor ───────────────────────────

    private void ShowSubtitleInlineEditor(string effectId, Rect screenBounds)
    {
        if (working || playing || editor.IsExporting || closing) return;
        var cue = editor.Snapshot.Effects.FirstOrDefault(e => e.Id == effectId && e.Kind == "subtitle");
        if (cue is null) return;

        DismissSubtitleInlineEditor(commit: false);
        SelectSubtitleById(effectId, openTab: false);

        var cueParams = SubtitleCueParameters.FromEffect(cue);
        inlineEditingSubtitleId = effectId;
        inlineEditingOriginalText = cueParams.Text ?? "";

        double hostW = Math.Max(200, PreviewMount.ActualWidth);
        double hostH = Math.Max(100, PreviewMount.ActualHeight);
        double boxW = Math.Clamp(Math.Max(screenBounds.Width + 32, 240), 200, Math.Max(200, hostW - 24));
        double boxH = Math.Clamp(Math.Max(screenBounds.Height + 12, 44), 40, Math.Max(44, hostH - 24));
        double left = Math.Clamp(screenBounds.X + (screenBounds.Width - boxW) * 0.5, 12, Math.Max(12, hostW - boxW - 12));
        double top = Math.Clamp(screenBounds.Y + (screenBounds.Height - boxH) * 0.5, 12, Math.Max(12, hostH - boxH - 12));

        var tb = new TextBox
        {
            Text = inlineEditingOriginalText,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            FontSize = 14,
            Width = boxW,
            MinHeight = boxH,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 56, 189, 248)),
            Background = new SolidColorBrush(ColorHelper.FromArgb(235, 17, 17, 19)),
            Foreground = new SolidColorBrush(Colors.White),
            Padding = new Thickness(12, 8, 12, 8)
        };
        tb.PreviewKeyDown += (sender, e) =>
        {
            bool ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            bool shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                DismissSubtitleInlineEditor(commit: false);
            }
            else if (e.Key == VirtualKey.Enter && (ctrl || !shift))
            {
                e.Handled = true;
                DismissSubtitleInlineEditor(commit: true);
            }
        };
        tb.LostFocus += (_, _) => DismissSubtitleInlineEditor(commit: true);

        inlineSubtitleTextBox = tb;
        subtitleInlineHost.Children.Clear();
        Canvas.SetLeft(tb, left);
        Canvas.SetTop(tb, top);
        subtitleInlineHost.Children.Add(tb);
        subtitleInlineHost.Visibility = Visibility.Visible;

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            tb.Focus(FocusState.Programmatic);
            tb.SelectAll();
        });
    }

    private void DismissSubtitleInlineEditor(bool commit)
    {
        if (committingInlineSubtitle || inlineEditingSubtitleId is null) return;
        committingInlineSubtitle = true;
        string cueId = inlineEditingSubtitleId;
        string edited = inlineSubtitleTextBox?.Text ?? inlineEditingOriginalText;
        string original = inlineEditingOriginalText;
        inlineEditingSubtitleId = null;
        inlineSubtitleTextBox = null;
        subtitleInlineHost.Children.Clear();
        subtitleInlineHost.Visibility = Visibility.Collapsed;
        committingInlineSubtitle = false;

        if (commit && !string.IsNullOrWhiteSpace(edited) && !string.Equals(edited.Trim(), original.Trim(), StringComparison.Ordinal))
        {
            _ = SafeAsync(() => CommitCueTextEditAsync(cueId, edited, isSecondary: false));
        }
    }

    private static SolidColorBrush ParseHexBrush(string hex, Windows.UI.Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex[0] != '#')
            return new SolidColorBrush(fallback);
        try
        {
            string digits = hex[1..];
            if (digits.Length == 6)
            {
                byte r = Convert.ToByte(digits[0..2], 16);
                byte g = Convert.ToByte(digits[2..4], 16);
                byte b = Convert.ToByte(digits[4..6], 16);
                return new SolidColorBrush(ColorHelper.FromArgb(255, r, g, b));
            }
            if (digits.Length == 8)
            {
                byte a = Convert.ToByte(digits[0..2], 16);
                byte r = Convert.ToByte(digits[2..4], 16);
                byte g = Convert.ToByte(digits[4..6], 16);
                byte b = Convert.ToByte(digits[6..8], 16);
                return new SolidColorBrush(ColorHelper.FromArgb(a, r, g, b));
            }
        }
        catch { }
        return new SolidColorBrush(fallback);
    }
}
