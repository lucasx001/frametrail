using System.Text.Json;
using System.Text.Json.Nodes;
using FrameTrail.Domain;
using Xunit;

namespace FrameTrail.Domain.Tests;

public sealed class SubtitleDomainTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Theory]
    [InlineData("classicOutline")]
    [InlineData("darkPill")]
    [InlineData("karaokePop")]
    [InlineData("bilingualClean")]
    [InlineData("custom")]
    public void SubtitleStylePresetsValidateAndRoundtrip(string preset)
    {
        var style = SubtitleStyleSettings.ForPreset(preset);
        Assert.Equal(preset, style.Preset);
        ProjectJson.ValidateSubtitleStyle(style);

        var project = ProjectJson.Deserialize(Fixture("project-v1.json")) with
        {
            Editor = new EditorSettings { SubtitleStyle = style }
        };

        string json = ProjectJson.Serialize(project);
        var restored = ProjectJson.Deserialize(json);
        Assert.Equal(style, restored.Editor!.SubtitleStyle);
        Assert.Contains($"\"preset\": \"{preset}\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportBurnSubtitlesDefaultsTrueWhenMissingAndRoundtripsFalse()
    {
        var legacy = ProjectJson.Deserialize(Fixture("project-v1.json"));
        Assert.True(legacy.Export.BurnSubtitles);

        var cleanExport = legacy with { Export = legacy.Export with { BurnSubtitles = false } };
        string json = ProjectJson.Serialize(cleanExport);
        Assert.Contains("\"burnSubtitles\": false", json, StringComparison.Ordinal);

        var restored = ProjectJson.Deserialize(json);
        Assert.False(restored.Export.BurnSubtitles);
    }

    [Fact]
    public void SubtitleEffectWithWordsAndStyleOverrideRoundtripsCleanly()
    {
        var parameters = new SubtitleCueParameters
        {
            Text = "欢迎使用 FrameTrail",
            SecondaryText = "Welcome to FrameTrail",
            Words =
            [
                new SubtitleWordTiming { Text = "欢迎", StartOffset = 0.0, EndOffset = 0.4 },
                new SubtitleWordTiming { Text = "使用", StartOffset = 0.4, EndOffset = 0.8 },
                new SubtitleWordTiming { Text = "FrameTrail", StartOffset = 0.8, EndOffset = 1.6 }
            ],
            StyleOverride = new SubtitleCueStyleOverride
            {
                PositionX = 0.5,
                PositionY = 0.82,
                FontSize = 48.0,
                TextColor = "#FFFFFF",
                OutlineColor = "#09090B",
                HighlightColor = "#FACC15",
                Alignment = "center"
            }
        };

        var subtitleEffect = new SourceEffect
        {
            Id = "sub-001",
            Kind = "subtitle",
            SourceStart = MediaTime.FromTicks(10_000_000),
            SourceDuration = MediaTime.FromTicks(20_000_000),
            Parameters = parameters.ToJsonElement()
        };

        var baseProject = ProjectJson.Deserialize(Fixture("project-v1.json"));
        var withSub = baseProject with { Effects = [.. baseProject.Effects, subtitleEffect] };

        var restored = ProjectJson.Deserialize(ProjectJson.Serialize(withSub));
        var restoredSub = restored.Effects.Single(e => e.Id == "sub-001");
        Assert.Equal("subtitle", restoredSub.Kind);

        var parsed = SubtitleCueParameters.FromEffect(restoredSub);
        Assert.Equal("欢迎使用 FrameTrail", parsed.Text);
        Assert.Equal("Welcome to FrameTrail", parsed.SecondaryText);
        Assert.Equal(3, parsed.Words!.Count);
        Assert.Equal(0.8, parsed.Words[2].StartOffset, 4);
        Assert.Equal(48.0, parsed.StyleOverride!.FontSize);
        Assert.Equal("#FACC15", parsed.StyleOverride.HighlightColor);
    }

    [Fact]
    public void InvalidSubtitleStyleAndParametersAreRejected()
    {
        Assert.Throws<ProjectFormatException>(() => ProjectJson.ValidateSubtitleStyle(new SubtitleStyleSettings { FontSize = 4.0 }));
        Assert.Throws<ProjectFormatException>(() => ProjectJson.ValidateSubtitleStyle(new SubtitleStyleSettings { TextColor = "white" }));
        Assert.Throws<ProjectFormatException>(() => ProjectJson.ValidateSubtitleStyle(new SubtitleStyleSettings { HighlightMode = "glow" }));
        Assert.Throws<ProjectFormatException>(() => ProjectJson.ValidateSubtitleStyle(new SubtitleStyleSettings { PositionY = 0.99 }));
        Assert.Throws<ProjectFormatException>(() => ProjectJson.ValidateSubtitleStyle(new SubtitleStyleSettings { Preset = "unknown" }));

        var baseProject = ProjectJson.Deserialize(Fixture("project-v1.json"));

        // Missing text
        var missingText = new SourceEffect
        {
            Id = "sub-bad-1",
            Kind = "subtitle",
            SourceStart = MediaTime.FromTicks(0),
            SourceDuration = MediaTime.FromTicks(10_000_000),
            Parameters = JsonSerializer.SerializeToElement(new { secondaryText = "only secondary" })
        };
        Assert.Throws<ProjectFormatException>(() => ProjectJson.Serialize(baseProject with { Effects = [missingText] }));

        // Inverted word timing
        var badWord = new SourceEffect
        {
            Id = "sub-bad-2",
            Kind = "subtitle",
            SourceStart = MediaTime.FromTicks(0),
            SourceDuration = MediaTime.FromTicks(10_000_000),
            Parameters = JsonSerializer.SerializeToElement(new
            {
                text = "hello",
                words = new[] { new { text = "hello", startOffset = 0.8, endOffset = 0.2 } }
            })
        };
        Assert.Throws<ProjectFormatException>(() => ProjectJson.Serialize(baseProject with { Effects = [badWord] }));
    }

    [Fact]
    public void LocalizationResourcesHaveMatchingKeysIncludingSubtitles()
    {
        var domainAsm = typeof(ProjectSnapshot).Assembly;
        static Dictionary<string, string> LoadEmbedded(System.Reflection.Assembly asm, string suffix)
        {
            var name = asm.GetManifestResourceNames().Single(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            using var stream = asm.GetManifestResourceStream(name)!;
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        }

        var en = LoadEmbedded(domainAsm, "en.json");
        var zh = LoadEmbedded(domainAsm, "zh-Hans.json");

        var missingInZh = en.Keys.Except(zh.Keys, StringComparer.Ordinal).ToArray();
        var missingInEn = zh.Keys.Except(en.Keys, StringComparer.Ordinal).ToArray();
        Assert.Empty(missingInZh);
        Assert.Empty(missingInEn);

        var requiredSubtitleKeys = new[]
        {
            "SubTabTitle", "SubAutoGenerate", "SubAddCue", "SubImport", "SubExport",
            "SubEmptyTitle", "SubEmptyDesc", "SubSectionTranscript", "SubSectionStyle",
            "SubEngineLabel", "SubEngineLocalTurbo", "SubEngineLocalSmall", "SubEngineCloudOpenAi", "SubEngineCloudGroq",
            "SubTrackLabel", "SubTrackAll", "SubTrackMic", "SubTrackSystem",
            "SubScopeLabel", "SubScopeAll", "SubScopeSelected",
            "SubLanguageLabel", "SubLanguageAuto", "SubLanguageZh", "SubLanguageEn", "SubLanguageJa",
            "SubVocabPromptLabel", "SubVocabPromptPlaceholder",
            "SubModelNotInstalled", "SubModelDownloading", "SubModelInstalled", "SubModelDelete",
            "SubGenerating", "SubFindPlaceholder", "SubReplacePlaceholder", "SubReplaceAll",
            "SubBilingualToggle", "SubAiTranslate", "SubTranslating",
            "SubSplitCue", "SubMergePrev", "SubMergeNext", "SubDeleteCue",
            "SubResetStyle", "SubApplyStyleAll",
            "SubPresetLabel", "SubPresetClassic", "SubPresetPill", "SubPresetKaraoke", "SubPresetBilingual",
            "SubFontLabel", "SubFontSizeLabel", "SubTextColorLabel", "SubOutlineColorLabel", "SubOutlineWidthLabel",
            "SubBgColorLabel", "SubBgOpacityLabel", "SubBgRadiusLabel",
            "SubHighlightColorLabel", "SubHighlightModeLabel", "SubHighlightNone", "SubHighlightWordColor", "SubHighlightWordPill",
            "SubPositionYLabel", "SubAlignmentLabel", "SubMaxCharsLabel", "SubFadeLabel",
            "SubExportBurnIn", "SubExportSidecar", "SubExportOnlyTab", "SubExportFormatLabel", "SubCanvasEditHint"
        };

        foreach (var key in requiredSubtitleKeys)
        {
            Assert.True(en.ContainsKey(key) && !string.IsNullOrWhiteSpace(en[key]), $"Missing/empty en.json key: {key}");
            Assert.True(zh.ContainsKey(key) && !string.IsNullOrWhiteSpace(zh[key]), $"Missing/empty zh-Hans.json key: {key}");
        }
    }
}
