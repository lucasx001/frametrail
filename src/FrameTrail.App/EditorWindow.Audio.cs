using FrameTrail.Domain;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    private readonly CheckBox micNoiseReductionCheck = new() { Content = L.T("AudioNoiseReductionLabel") };
    private readonly CheckBox audioNormalizeCheck = new() { Content = L.T("AudioNormalizeLabel") };
    private readonly CheckBox audioDuckingCheck = new() { Content = L.T("AudioDuckingLabel") };
    private readonly TextBlock bgmFileNameLabel = new()
    {
        Text = L.T("AudioBgmNone"),
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Button importBgmButton = new()
    {
        Content = L.T("AudioImportBgm"),
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly Button removeBgmButton = new()
    {
        Content = L.T("AudioRemoveBgm"),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Visibility = Visibility.Collapsed
    };
    private readonly EditorBarSlider bgmGainSlider = new()
    {
        Minimum = 0,
        Maximum = 200,
        StepFrequency = 5,
        Value = 35,
        DefaultValue = 35,
        Suffix = "%"
    };

    private void InitializeAudioInspector()
    {
        micNoiseReductionCheck.Click += AudioToggle_Click;
        audioNormalizeCheck.Click += AudioToggle_Click;
        audioDuckingCheck.Click += AudioToggle_Click;
        bgmGainSlider.Committed += AudioBar_Committed;
        importBgmButton.Click += async (_, _) => await SafeAsync(ImportBgmAsync);
        removeBgmButton.Click += async (_, _) => await SafeAsync(RemoveBgmAsync);

        AutomationProperties.SetName(micNoiseReductionCheck, L.T("AudioNoiseReductionLabel"));
        AutomationProperties.SetAutomationId(micNoiseReductionCheck, "editor.audio.micNoiseReduction");
        AutomationProperties.SetName(audioNormalizeCheck, L.T("AudioNormalizeLabel"));
        AutomationProperties.SetAutomationId(audioNormalizeCheck, "editor.audio.normalize");
        AutomationProperties.SetName(audioDuckingCheck, L.T("AudioDuckingLabel"));
        AutomationProperties.SetAutomationId(audioDuckingCheck, "editor.audio.ducking");
        AutomationProperties.SetName(bgmGainSlider, L.T("AudioBgmGainLabel"));
        AutomationProperties.SetAutomationId(bgmGainSlider, "editor.audio.bgmGain");
        AutomationProperties.SetName(importBgmButton, L.T("AudioImportBgm"));
        AutomationProperties.SetAutomationId(importBgmButton, "editor.audio.importBgm");
        AutomationProperties.SetName(removeBgmButton, L.T("AudioRemoveBgm"));
        AutomationProperties.SetAutomationId(removeBgmButton, "editor.audio.removeBgm");

        var sectionStyle = (Style)Root.Resources["SectionLabel"];
        var tileStyle = (Style)Application.Current.Resources["EditorTile"];

        // 1. Audio Enhancement Card
        var enhancementSection = new StackPanel();
        enhancementSection.Children.Add(new TextBlock
        {
            Text = L.T("AudioEnhancementLabel"),
            Style = sectionStyle
        });
        var enhancementBody = new StackPanel { Spacing = 8 };
        enhancementBody.Children.Add(micNoiseReductionCheck);
        enhancementBody.Children.Add(audioNormalizeCheck);
        enhancementBody.Children.Add(audioDuckingCheck);
        enhancementSection.Children.Add(new Border
        {
            Style = tileStyle,
            Child = enhancementBody
        });
        AudioSettings.Children.Add(enhancementSection);

        // 2. Background Music (BGM) Card
        var bgmSection = new StackPanel();
        bgmSection.Children.Add(new TextBlock
        {
            Text = L.T("AudioBgmSectionLabel"),
            Style = sectionStyle
        });
        var bgmBody = new StackPanel { Spacing = 8 };
        bgmFileNameLabel.Foreground = RecorderTheme.Brush(Root, "EditorSecondary");
        bgmBody.Children.Add(bgmFileNameLabel);
        importBgmButton.Style = (Style)Application.Current.Resources["EditorSecondaryButton"];
        bgmBody.Children.Add(importBgmButton);
        removeBgmButton.Style = (Style)Application.Current.Resources["EditorDangerGhostButton"];
        bgmBody.Children.Add(removeBgmButton);
        var bgmGainHeader = new TextBlock
        {
            Text = L.T("AudioBgmGainLabel"),
            Style = (Style)Root.Resources["SmallLabel"]
        };
        bgmBody.Children.Add(bgmGainHeader);
        bgmBody.Children.Add(bgmGainSlider);
        bgmSection.Children.Add(new Border
        {
            Style = tileStyle,
            Child = bgmBody
        });
        AudioSettings.Children.Add(bgmSection);

        Root.ActualThemeChanged += (_, _) =>
        {
            var secondary = RecorderTheme.Brush(Root, "EditorSecondary");
            bgmFileNameLabel.Foreground = secondary;
            bgmGainHeader.Foreground = secondary;
        };
    }

    private void LoadAudioInspector(EditorSettings settings)
    {
        micNoiseReductionCheck.IsChecked = settings.MicNoiseReduction;
        audioNormalizeCheck.IsChecked = settings.AudioNormalize;
        audioDuckingCheck.IsChecked = settings.AudioDucking;
        bgmGainSlider.Value = Math.Clamp(settings.BgmGain * 100.0, 0.0, 200.0);

        bool hasBgm = !string.IsNullOrEmpty(settings.BgmPath);
        bgmFileNameLabel.Text = hasBgm ? Path.GetFileName(settings.BgmPath) : L.T("AudioBgmNone");
        removeBgmButton.Visibility = hasBgm ? Visibility.Visible : Visibility.Collapsed;
    }

    private EditorSettings ApplyAudioInspectorSettings(EditorSettings settings) =>
        settings with
        {
            MicNoiseReduction = micNoiseReductionCheck.IsChecked == true,
            AudioNormalize = audioNormalizeCheck.IsChecked == true,
            AudioDucking = audioDuckingCheck.IsChecked == true,
            BgmGain = Math.Clamp(bgmGainSlider.Value / 100.0, 0.0, 2.0)
        };

    private async Task ImportBgmAsync()
    {
        if (working || editor.IsExporting) return;
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".mp3");
        picker.FileTypeFilter.Add(".wav");
        picker.FileTypeFilter.Add(".m4a");
        picker.FileTypeFilter.Add(".aac");
        picker.FileTypeFilter.Add(".flac");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        await PerformAsync(async () =>
        {
            string relative = await editor.ImportBgmAsync(file.Path);
            var current = editor.Snapshot.Editor ?? new();
            await editor.ExecuteAsync(new { op = "settings", editor = current with { BgmPath = relative } });
        });
    }

    private async Task RemoveBgmAsync()
    {
        var current = editor.Snapshot.Editor ?? new();
        if (string.IsNullOrEmpty(current.BgmPath)) return;
        await EditAsync(new { op = "settings", editor = current with { BgmPath = (string?)null } });
    }
}
