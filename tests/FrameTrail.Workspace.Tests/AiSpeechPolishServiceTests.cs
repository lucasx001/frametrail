using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;
using Xunit;

namespace FrameTrail.Workspace.Tests;

public sealed class AiSpeechPolishServiceTests
{
    private static ProjectSnapshot CreateProject(double durationSec = 30.0)
    {
        long durTicks = (long)Math.Round(durationSec * AiSpeechPolishService.TicksPerSecond);
        return new ProjectSnapshot
        {
            SchemaVersion = 1,
            ProjectId = "speech-polish-test",
            Revision = 1,
            SourceDuration = MediaTime.FromTicks(durTicks),
            Media =
            [
                new MediaAsset
                {
                    Id = "screen-1",
                    Kind = MediaKind.Screen,
                    Path = "media/screen-001.mp4",
                    Duration = MediaTime.FromTicks(durTicks),
                    StartOffset = MediaTime.FromTicks(0),
                    Width = 1920,
                    Height = 1080
                },
                new MediaAsset
                {
                    Id = "mic-1",
                    Kind = MediaKind.Microphone,
                    Path = "media/mic-001.wav",
                    Duration = MediaTime.FromTicks(durTicks),
                    StartOffset = MediaTime.FromTicks(0),
                    SampleRate = 16000,
                    Channels = 1
                }
            ],
            Segments =
            [
                new RecordingSegment
                {
                    Id = "clip-1",
                    SourceStart = MediaTime.FromTicks(0),
                    SourceDuration = MediaTime.FromTicks(durTicks),
                    Rate = new Rational { Numerator = 1, Denominator = 1 }
                }
            ],
            Canvas = new CanvasSettings
            {
                Width = 1920,
                Height = 1080,
                Padding = 0.05,
                Background = new SdrColor { R = 0.08, G = 0.08, B = 0.1, A = 1.0 },
                Screen = new NormalizedRect { X = 0.05, Y = 0.05, Width = 0.9, Height = 0.9 },
                Camera = new CameraLayout { Enabled = false, X = 0.7, Y = 0.7, Width = 0.25, Height = 0.25 }
            },
            Export = new ExportSettings
            {
                Width = 1920,
                Height = 1080,
                Fps = new Rational { Numerator = 30, Denominator = 1 },
                VideoCodec = "h264",
                AudioCodec = "aac",
                Container = "mp4"
            },
            Editor = new EditorSettings()
        };
    }

    [Fact]
    public void DetectFillerAndRepeatCuts_CatchesChineseFillersAndProtectsNaturalContext()
    {
        var segments = new[]
        {
            new TranscribedSegment(
                0.0,
                12.0,
                "嗯，大家好，那个，点击那个按钮太好了啊。",
                "microphone",
                new[]
                {
                    new TranscribedWord("嗯，", 0.20, 0.55),       // strong filler -> CUT
                    new TranscribedWord("大家", 0.65, 1.00),
                    new TranscribedWord("好，", 1.00, 1.30),
                    new TranscribedWord("那", 1.50, 1.65),        // pause-bounded "那个，" -> CUT
                    new TranscribedWord("个，", 1.65, 1.85),
                    new TranscribedWord("点击", 2.10, 2.40),
                    new TranscribedWord("那个", 2.42, 2.65),      // continuous "点击那个按钮" -> KEEP
                    new TranscribedWord("按钮", 2.66, 3.00),
                    new TranscribedWord("太好了", 3.10, 3.55),
                    new TranscribedWord("啊。", 3.55, 3.70),       // glued sentence-final particle -> KEEP in Standard
                    new TranscribedWord("哦，", 4.10, 4.45),      // isolated "哦，" -> CUT
                    new TranscribedWord("谢谢", 4.60, 5.00)
                })
        };

        var cuts = SpeechPolishAnalyzer.DetectFillerAndRepeatCuts(
            segments,
            new SpeechPolishOptions(Intensity: SpeechPolishIntensity.Standard));

        Assert.Equal(3, cuts.Count);
        Assert.Contains(cuts, c => c.Reason == "filler" && c.MatchedText == "嗯");
        Assert.Contains(cuts, c => c.Reason == "filler" && c.MatchedText == "那个" && c.StartSeconds < 2.0);
        Assert.Contains(cuts, c => c.Reason == "filler" && c.MatchedText == "哦");
        Assert.DoesNotContain(cuts, c => c.StartSeconds >= 2.35 && c.EndSeconds <= 2.75);
        Assert.DoesNotContain(cuts, c => c.MatchedText == "啊");
    }

    [Fact]
    public void DetectFillerAndRepeatCuts_CatchesStuttersAndPhraseRestartsWhileProtectingReduplications()
    {
        var segments = new[]
        {
            new TranscribedSegment(
                0.0,
                15.0,
                "我 我 我们慢慢看，我们打开设置 我们打开设置面板，um ok",
                "microphone",
                new[]
                {
                    new TranscribedWord("我", 0.50, 0.68),         // stutter -> CUT
                    new TranscribedWord("我", 0.72, 0.90),
                    new TranscribedWord("我们", 0.95, 1.25),
                    new TranscribedWord("慢", 1.30, 1.45),         // legitimate reduplication "慢慢" (gap 0.01s) -> KEEP
                    new TranscribedWord("慢", 1.46, 1.62),
                    new TranscribedWord("看，", 1.65, 1.90),
                    new TranscribedWord("我们", 2.50, 2.75),       // phrase restart "我们 打开" -> CUT [2.50, 3.50)
                    new TranscribedWord("打开", 2.76, 3.05),
                    new TranscribedWord("设", 3.06, 3.20),
                    new TranscribedWord("我们", 3.50, 3.75),
                    new TranscribedWord("打开", 3.76, 4.05),
                    new TranscribedWord("设置", 4.06, 4.45),
                    new TranscribedWord("um", 5.00, 5.30),         // English strong filler -> CUT
                    new TranscribedWord("ok", 5.40, 5.70)
                })
        };

        var standardCuts = SpeechPolishAnalyzer.DetectFillerAndRepeatCuts(
            segments,
            new SpeechPolishOptions(Intensity: SpeechPolishIntensity.Standard));

        Assert.Contains(standardCuts, c => c.Reason == "repeat" && c.MatchedText == "我");
        Assert.Contains(standardCuts, c => c.Reason == "repeat" && c.MatchedText == "我们打开");
        Assert.Contains(standardCuts, c => c.Reason == "filler" && string.Equals(c.MatchedText, "um", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(standardCuts, c => c.MatchedText == "慢");

        // Gentle mode disables repeats
        var gentleCuts = SpeechPolishAnalyzer.DetectFillerAndRepeatCuts(
            segments,
            new SpeechPolishOptions(Intensity: SpeechPolishIntensity.Gentle));
        Assert.DoesNotContain(gentleCuts, c => c.Reason == "repeat");
    }

    [Fact]
    public void SnapToEnergyValley_MovesCutEdgesToLowestEnergyFrames()
    {
        // 100 frames @ 10ms = 1.0s; valley at frame 18 (185ms) and frame 52 (525ms)
        var db = Enumerable.Repeat(-15.0, 100).ToArray();
        db[18] = -58.0;
        db[52] = -60.0;
        var env = new AudioTrackEnergyEnvelope("microphone", 0, 0.01, db);

        var (s, e) = SpeechPolishAnalyzer.SnapToEnergyValley(
            rawStartTicks: 2_000_000L, // 200ms
            rawEndTicks: 5_000_000L,   // 500ms
            minAllowedStartTicks: 1_000_000L,
            maxAllowedEndTicks: 6_000_000L,
            envelopes: [env]);

        Assert.InRange(s, 1_800_000L, 1_900_000L);
        Assert.InRange(e, 5_200_000L, 5_300_000L);
    }

    [Fact]
    public void BuildCutRangesCommand_ExecutesAtomicallyInNativeEngineAndDescribesChanges()
    {
        var snapshot = CreateProject(300.0);
        var timeline = EngineSession.ResolveTimeline(snapshot);

        // 120 cuts (>100 batch cap) mixing pauses, fillers, and repeats
        var rawCuts = Enumerable.Range(0, 120)
            .Select(i => new SpeechCutItem(
                StartTicks: (long)((i * 2.2 + 0.8) * AiSpeechPolishService.TicksPerSecond),
                EndTicks: (long)((i * 2.2 + 1.2) * AiSpeechPolishService.TicksPerSecond),
                Reason: i % 3 == 0 ? "pause" : (i % 3 == 1 ? "filler" : "repeat"),
                Label: i % 3 == 0 ? "0.4s" : (i % 3 == 1 ? "「嗯」" : "「我们」")))
            .ToArray();

        var merged = SpeechPolishAnalyzer.MergeAndClampCuts(snapshot, rawCuts);
        object cmd = AiAgentCommandBuilder.BuildCutRangesCommand(merged);

        var edited = EngineSession.Edit(snapshot, cmd);
        EngineSession.ValidateProject(edited);
        Assert.Equal(121, edited.Segments.Count);

        var changes = AiAgentCommandBuilder.DescribeProposedChanges(cmd, snapshot, timeline);
        Assert.Equal(120, changes.Count);
        Assert.All(changes, item =>
        {
            Assert.Equal("−", item.Badge);
            Assert.NotNull(item.SeekSeconds);
            Assert.False(string.IsNullOrWhiteSpace(item.TimeLabel));
        });
    }

    [Fact]
    public void BridgePromptParser_RecognizesSpeechPolishIntentAndIntensityFlags()
    {
        Assert.True(AiAgentBridge.IsSpeechPolishPrompt("一键优化配音表达，去掉嗯啊哦语气词和停顿"));
        Assert.True(AiAgentBridge.IsSpeechPolishPrompt("/polish"));
        Assert.True(AiAgentBridge.IsSpeechPolishPrompt("Please polish voiceover and remove filler words"));
        Assert.False(AiAgentBridge.IsSpeechPolishPrompt("在 1s 到 3s 添加 2x 放大"));

        var tight = AiAgentBridge.ParseSpeechPolishOptionsFromPrompt("紧凑模式优化配音，只去语气词不要删停顿");
        Assert.Equal(SpeechPolishIntensity.Tight, tight.Intensity);
        Assert.False(tight.RemovePauses);
        Assert.True(tight.RemoveFillers);
        Assert.False(tight.RemoveRepeats);

        var gentle = AiAgentBridge.ParseSpeechPolishOptionsFromPrompt("轻度口播精剪");
        Assert.Equal(SpeechPolishIntensity.Gentle, gentle.Intensity);
        Assert.True(gentle.RemovePauses);
        Assert.True(gentle.RemoveFillers);
        Assert.False(gentle.EffectiveRemoveRepeats);
    }
}
