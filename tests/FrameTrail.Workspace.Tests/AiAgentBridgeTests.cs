using System.Net;
using System.Text;
using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;
using FrameTrail.Workspace;
using Xunit;

namespace FrameTrail.Workspace.Tests;

public sealed class AiAgentBridgeTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FrameTrail-AiAgent-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private async Task<ProjectWorkspace> CreateProjectAsync(long durationTicks = 50_000_000)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".frametrailproject");
        var workspace = ProjectWorkspace.Create(path, "ai-test-project", EngineSession.ValidateProject, (_, _) => { });
        await File.WriteAllBytesAsync(workspace.Resolve("media/screen.mp4"), PersistenceTests.FinalizedMp4Header);
        var snapshot = new ProjectSnapshot
        {
            ProjectId = "ai-test-project",
            Revision = 1,
            SourceDuration = MediaTime.FromTicks(durationTicks),
            Media = new[]
            {
                new MediaAsset
                {
                    Id = "screen",
                    Kind = MediaKind.Screen,
                    Path = "media/screen.mp4",
                    StartOffset = MediaTime.FromTicks(0),
                    Duration = MediaTime.FromTicks(durationTicks),
                    Width = 1920,
                    Height = 1080
                }
            },
            Segments = new[]
            {
                new RecordingSegment
                {
                    Id = "clip-main",
                    SourceStart = MediaTime.FromTicks(0),
                    SourceDuration = MediaTime.FromTicks(durationTicks)
                }
            }
        };
        await workspace.SaveAsync(snapshot, workspace.SessionEpoch);
        return workspace;
    }

    [Fact]
    public async Task BuildTimelineOverviewReflectsClipsEffectsAndCanvas()
    {
        await using var workspace = await CreateProjectAsync(40_000_000);
        var editor = new EditorSession(workspace);
        var zoomCmd = AiAgentCommandBuilder.BuildZoomEffectCommand(editor.Snapshot, 1.0, 3.0, scale: 2.0, effectId: "zoom-1");
        await editor.ExecuteAsync(zoomCmd);

        var overview = AiAgentCommandBuilder.BuildTimelineOverview(editor.Snapshot, editor.Timeline);
        Assert.Equal(4.0, overview.TotalDurationSec, 3);
        Assert.Single(overview.Clips);
        Assert.Equal("clip-main", overview.Clips[0].Id);
        Assert.Equal(1.0, overview.Clips[0].Rate, 3);
        Assert.Single(overview.Effects);
        Assert.Equal("zoom-1", overview.Effects[0].Id);
        Assert.Equal("zoom", overview.Effects[0].Kind);
        Assert.Equal(1.0, overview.Effects[0].StartSec, 3);
        Assert.Equal(2.0, overview.Effects[0].DurationSec, 3);
        Assert.Equal(1920u, overview.Canvas.Width);
        Assert.Equal(1080u, overview.Canvas.Height);
    }

    [Fact]
    public async Task EffectCommandsExecuteValidateAndUndoCleanly()
    {
        await using var workspace = await CreateProjectAsync(60_000_000);
        var editor = new EditorSession(workspace);

        var zoomCmd = AiAgentCommandBuilder.BuildZoomEffectCommand(editor.Snapshot, 0.5, 2.5, scale: 2.2, focusX: 0.3, focusY: 0.7, effectId: "eff-zoom");
        await editor.ExecuteAsync(zoomCmd);
        Assert.Single(editor.Snapshot.Effects);
        Assert.Equal("zoom", editor.Snapshot.Effects[0].Kind);
        Assert.Equal(2.2, editor.Snapshot.Effects[0].Parameters.GetProperty("scale").GetDouble(), 3);

        var focusCmd = AiAgentCommandBuilder.BuildFocusOrMaskEffectCommand(editor.Snapshot, "focus", 1.0, 3.0, x: 0.1, y: 0.2, width: 0.4, height: 0.4, strength: 24, effectId: "eff-focus");
        await editor.ExecuteAsync(focusCmd);
        Assert.Equal(2, editor.Snapshot.Effects.Count);
        Assert.Equal("focus", editor.Snapshot.Effects[1].Kind);

        var maskCmd = AiAgentCommandBuilder.BuildFocusOrMaskEffectCommand(editor.Snapshot, "mask", 2.0, 4.0, x: 0.8, y: 0.8, width: 0.5, height: 0.5, strength: 32, effectId: "eff-mask");
        await editor.ExecuteAsync(maskCmd);
        var mask = editor.Snapshot.Effects.Single(e => e.Id == "eff-mask");
        Assert.Equal("mask", mask.Kind);
        // Clamped so x + width <= 1 and y + height <= 1
        Assert.True(mask.Parameters.GetProperty("x").GetDouble() + mask.Parameters.GetProperty("width").GetDouble() <= 1.0001);
        Assert.True(mask.Parameters.GetProperty("y").GetDouble() + mask.Parameters.GetProperty("height").GetDouble() <= 1.0001);

        var tiltCmd = AiAgentCommandBuilder.BuildTiltEffectCommand(editor.Snapshot, 3.0, 5.0, angleX: 12, angleY: -15, effectId: "eff-tilt");
        await editor.ExecuteAsync(tiltCmd);
        var tilt = editor.Snapshot.Effects.Single(e => e.Id == "eff-tilt");
        Assert.Equal("tilt", tilt.Kind);
        Assert.Equal(12, tilt.Parameters.GetProperty("angleX").GetDouble(), 3);
        Assert.Equal(-15, tilt.Parameters.GetProperty("angleY").GetDouble(), 3);

        EngineSession.ValidateProject(editor.Snapshot);

        await editor.UndoAsync();
        Assert.DoesNotContain(editor.Snapshot.Effects, e => e.Id == "eff-tilt");
    }

    [Fact]
    public async Task SpeedAndCanvasStyleCommandsExecuteAndUndo()
    {
        await using var workspace = await CreateProjectAsync(40_000_000);
        var editor = new EditorSession(workspace);

        var speedCmd = AiAgentCommandBuilder.BuildSpeedCommand("clip-main", 2.0);
        await editor.ExecuteAsync(speedCmd);
        Assert.Equal(2, editor.Snapshot.Segments[0].Rate.Numerator);
        Assert.Equal(1, editor.Snapshot.Segments[0].Rate.Denominator);
        Assert.Equal(20_000_000L, editor.Timeline.Duration);

        var canvasCmd = AiAgentCommandBuilder.BuildCanvasStyleCommand(editor.Snapshot, width: 1080, height: 1920, padding: 0.08, cornerRadius: 24, frameStyle: "browser");
        await editor.ExecuteAsync(canvasCmd);
        Assert.Equal(1080u, editor.Snapshot.Canvas.Width);
        Assert.Equal(1920u, editor.Snapshot.Canvas.Height);
        Assert.Equal(0.08, editor.Snapshot.Canvas.Padding, 4);
        Assert.Equal(24, editor.Snapshot.Editor!.CornerRadius, 4);
        Assert.Equal("browser", editor.Snapshot.Editor!.FrameStyle);

        await editor.UndoAsync();
        Assert.Equal(1920u, editor.Snapshot.Canvas.Width);
        Assert.Equal(1080u, editor.Snapshot.Canvas.Height);

        await editor.UndoAsync();
        Assert.Equal(40_000_000L, editor.Timeline.Duration);
    }

    [Fact]
    public async Task CutTimeRangeHandlesMiddleStartEndAndRejectsFullCut()
    {
        await using var workspace = await CreateProjectAsync(50_000_000);
        var editor = new EditorSession(workspace);

        // Middle cut [1s, 2s] -> [0..1s] and [2..5s]
        var midCut = AiAgentCommandBuilder.BuildCutTimeRangeCommand(editor.Snapshot, editor.Timeline, 1.0, 2.0);
        await editor.ExecuteAsync(midCut);
        Assert.Equal(2, editor.Snapshot.Segments.Count);
        Assert.Equal(40_000_000L, editor.Timeline.Duration);

        // Cut across boundary [0.5s, 4.0s] -> trims first clip to [0..0.5s] and deletes second clip!
        var spanCut = AiAgentCommandBuilder.BuildCutTimeRangeCommand(editor.Snapshot, editor.Timeline, 0.5, 4.0);
        await editor.ExecuteAsync(spanCut);
        Assert.Single(editor.Snapshot.Segments);
        Assert.Equal(5_000_000L, editor.Timeline.Duration);

        // Full cut should fail cleanly before touching engine
        Assert.Throws<InvalidOperationException>(() =>
            AiAgentCommandBuilder.BuildCutTimeRangeCommand(editor.Snapshot, editor.Timeline, 0.0, 0.5));

        await editor.UndoAsync();
        Assert.Equal(40_000_000L, editor.Timeline.Duration);
    }

    [Theory]
    [InlineData("在 1s 到 3s 添加 2 倍放大特效", "zoom")]
    [InlineData("在 2s 到 4s 添加聚焦虚化", "focus")]
    [InlineData("在 1s 到 3s 添加 3D 倾斜", "tilt")]
    public async Task BridgeOfflineFallbackExecutesEffectPromptsAndCanUndo(string prompt, string expectedKind)
    {
        await using var workspace = await CreateProjectAsync(50_000_000);
        var editor = new EditorSession(workspace);
        var bridge = new AiAgentBridge();
        var config = new AiAgentConfig(SidecarUrl: "");

        var result = await bridge.RunTurnAsync(editor, config, prompt);
        Assert.NotNull(result.AppliedRevision);
        Assert.Single(editor.Snapshot.Effects);
        Assert.Equal(expectedKind, editor.Snapshot.Effects[0].Kind);
        Assert.NotEmpty(result.ToolCalls);
        Assert.True(result.ToolCalls[0].Succeeded);

        await editor.UndoAsync();
        Assert.Empty(editor.Snapshot.Effects);
    }

    [Fact]
    public async Task BridgeOfflineFallbackHandlesCanvasSpeedCutAndOverviewPrompts()
    {
        await using var workspace = await CreateProjectAsync(50_000_000);
        var editor = new EditorSession(workspace);
        var bridge = new AiAgentBridge();
        var config = new AiAgentConfig(SidecarUrl: "");

        var overview = await bridge.RunTurnAsync(editor, config, "查看时间线概览");
        Assert.Null(overview.AppliedRevision);
        Assert.Contains("5s", overview.ReplyText);

        var portrait = await bridge.RunTurnAsync(editor, config, "把画布改为 9:16 竖屏");
        Assert.NotNull(portrait.AppliedRevision);
        Assert.Equal(1080u, editor.Snapshot.Canvas.Width);
        Assert.Equal(1920u, editor.Snapshot.Canvas.Height);

        var speed = await bridge.RunTurnAsync(editor, config, "把片段倍速设为 2x");
        Assert.NotNull(speed.AppliedRevision);
        Assert.Equal(25_000_000L, editor.Timeline.Duration);

        var cut = await bridge.RunTurnAsync(editor, config, "剪掉 1s 到 2s");
        Assert.NotNull(cut.AppliedRevision);
        Assert.Equal(15_000_000L, editor.Timeline.Duration);
    }

    [Fact]
    public async Task BridgeAppliesSidecarProposedCommandWhenSidecarResponds()
    {
        await using var workspace = await CreateProjectAsync(40_000_000);
        var editor = new EditorSession(workspace);

        var handler = new StubHttpMessageHandler(_ =>
        {
            var payload = new
            {
                reply = "Added zoom via sidecar.",
                tool_calls = new[]
                {
                    new { tool_name = "add_zoom_effect", summary = "Zoom 1.8x (1s-3s)", succeeded = true }
                },
                proposed_command = new
                {
                    op = "effectUpsert",
                    effectId = "sidecar-zoom-1",
                    effect = new
                    {
                        id = "sidecar-zoom-1",
                        kind = "zoom",
                        sourceStart = new { ticks = 10_000_000L, timescale = 10_000_000 },
                        sourceDuration = new { ticks = 20_000_000L, timescale = 10_000_000 },
                        parameters = new { origin = "manual", scale = 1.8, focusX = 0.5, focusY = 0.5, easing = "cubic", enter = 0.7, exit = 0.7 }
                    }
                }
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var bridge = new AiAgentBridge(http);
        var config = new AiAgentConfig(SidecarUrl: "http://127.0.0.1:18765");

        var result = await bridge.RunTurnAsync(editor, config, "Add 1.8x zoom from 1s to 3s");
        Assert.Equal("Added zoom via sidecar.", result.ReplyText);
        Assert.Equal(2L, result.AppliedRevision);
        Assert.Single(editor.Snapshot.Effects);
        Assert.Equal("sidecar-zoom-1", editor.Snapshot.Effects[0].Id);
        Assert.Single(result.ToolCalls);
        Assert.Equal("add_zoom_effect", result.ToolCalls[0].ToolName);
    }

    [Fact]
    public async Task StreamingSidecarTurnParsesSseEventsAndProposesValidatedCommand()
    {
        await using var workspace = await CreateProjectAsync(40_000_000);
        var editor = new EditorSession(workspace);

        string sseBody =
            "event: thinking\n" +
            "data: {\"text\":\"Checking timeline...\"}\n\n" +
            "event: tool_start\n" +
            "data: {\"tool_name\":\"add_zoom_effect\",\"summary\":\"1s-3s\"}\n\n" +
            "event: tool_end\n" +
            "data: {\"tool_name\":\"add_zoom_effect\",\"summary\":\"Zoom 2.0x\",\"succeeded\":true}\n\n" +
            "event: token\n" +
            "data: {\"text\":\"Added **2x zoom** at 00:01.0\"}\n\n" +
            "event: done\n" +
            "data: {\"reply\":\"Added **2x zoom** at 00:01.0\",\"tool_calls\":[{\"tool_name\":\"add_zoom_effect\",\"summary\":\"Zoom 2.0x\",\"succeeded\":true}],\"proposed_command\":{\"op\":\"effectUpsert\",\"effectId\":\"sse-zoom-1\",\"effect\":{\"id\":\"sse-zoom-1\",\"kind\":\"zoom\",\"sourceStart\":{\"ticks\":10000000,\"timescale\":10000000},\"sourceDuration\":{\"ticks\":20000000,\"timescale\":10000000},\"parameters\":{\"origin\":\"manual\",\"scale\":2.0,\"focusX\":0.5,\"focusY\":0.5,\"easing\":\"cubic\",\"enter\":0.7,\"exit\":0.7}}}}\n\n";

        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sseBody, Encoding.UTF8, "text/event-stream")
        });

        using var http = new HttpClient(handler);
        var bridge = new AiAgentBridge(http);
        var config = new AiAgentConfig(SidecarUrl: "http://127.0.0.1:18765");
        var seenKinds = new List<AiStreamEventKind>();

        var result = await bridge.RunTurnStreamingAsync(
            editor,
            config,
            "Add 2x zoom from 1s to 3s",
            dryRunOnly: true,
            onEvent: evt => seenKinds.Add(evt.Kind));

        Assert.Contains(AiStreamEventKind.Thinking, seenKinds);
        Assert.Contains(AiStreamEventKind.ToolStart, seenKinds);
        Assert.Contains(AiStreamEventKind.ToolEnd, seenKinds);
        Assert.Contains(AiStreamEventKind.Token, seenKinds);
        Assert.Contains(AiStreamEventKind.Done, seenKinds);
        Assert.NotNull(result.ProposedCommand);
        Assert.Null(result.AppliedRevision); // dryRunOnly = true for Review mode

        var diffItems = AiAgentCommandBuilder.DescribeProposedChanges(result.ProposedCommand, editor.Snapshot, editor.Timeline);
        var zoomRow = Assert.Single(diffItems);
        Assert.Equal("+", zoomRow.Badge);
        Assert.Equal("Zoom", zoomRow.Label);
        Assert.Equal(1.0, zoomRow.SeekSeconds);
    }

    [Fact]
    public async Task LocalCommandCallbackServerEnforcesTokenAndDispatchesCommands()
    {
        string? receivedOp = null;
        await using var server = new AiLocalCommandCallbackServer((cmd, _) =>
        {
            receivedOp = cmd.GetProperty("op").GetString();
            return Task.FromResult<(bool, long?, string?)>((true, 42L, null));
        });

        using var client = new HttpClient();
        string baseUrlWithoutToken = server.CallbackUrl.Split('?')[0];

        // 1. Missing/invalid token must be rejected with 401
        using var unauthResp = await client.PostAsync(
            baseUrlWithoutToken + "?token=wrong",
            new StringContent("""{"command":{"op":"rate"}}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthResp.StatusCode);

        // 2. Valid token must succeed and dispatch command
        using var okResp = await client.PostAsync(
            server.CallbackUrl,
            new StringContent("""{"command":{"op":"effectUpsert"}}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
        Assert.Equal("effectUpsert", receivedOp);
    }

    [Fact]
    public void SubtitleCommandBuildersAndDiffDescriptionsFormatCleanly()
    {
        var snapshot = new ProjectSnapshot
        {
            ProjectId = "sub-bridge-test",
            Revision = 1,
            SourceDuration = MediaTime.FromTicks(60_000_000L),
            Media =
            [
                new MediaAsset
                {
                    Id = "screen",
                    Kind = MediaKind.Screen,
                    Path = "media/screen.mp4",
                    StartOffset = MediaTime.FromTicks(0),
                    Duration = MediaTime.FromTicks(60_000_000L),
                    Width = 1920,
                    Height = 1080
                }
            ],
            Segments =
            [
                new RecordingSegment { Id = "clip-1", SourceStart = MediaTime.FromTicks(0), SourceDuration = MediaTime.FromTicks(60_000_000L) }
            ]
        };
        var timeline = new ResolvedTimeline(
            Duration: 60_000_000L,
            Clips: [new ResolvedClip("clip-1", 0, 60_000_000L, 0, 60_000_000L, 1, 1)],
            Markers: Array.Empty<ResolvedMarker>());

        // 1. BuildSubtitleUpsertCommand + diff row
        var upsertCmd = AiAgentCommandBuilder.BuildSubtitleUpsertCommand(snapshot, 1.5, 3.5, "你好 FrameTrail", "Hello FrameTrail", effectId: "sub-test-1");
        var upsertItems = AiAgentCommandBuilder.DescribeProposedChanges(upsertCmd, snapshot, timeline);
        var upsertRow = Assert.Single(upsertItems);
        Assert.Equal("+", upsertRow.Badge);
        Assert.Equal("Subtitle", upsertRow.Label);
        Assert.Equal("你好 FrameTrail", upsertRow.Detail);
        Assert.Equal(1.5, upsertRow.SeekSeconds);

        // 2. BuildSubtitlesReplaceCommand (populated) + diff row
        var cues = new[]
        {
            new SourceEffect
            {
                Id = "sub-1",
                Kind = "subtitle",
                SourceStart = MediaTime.FromTicks(21_000_000L),
                SourceDuration = MediaTime.FromTicks(15_000_000L),
                Parameters = new SubtitleCueParameters { Text = "第一句" }.ToJsonElement()
            },
            new SourceEffect
            {
                Id = "sub-2",
                Kind = "subtitle",
                SourceStart = MediaTime.FromTicks(40_000_000L),
                SourceDuration = MediaTime.FromTicks(18_000_000L),
                Parameters = new SubtitleCueParameters { Text = "第二句" }.ToJsonElement()
            }
        };
        var replaceCmd = AiAgentCommandBuilder.BuildSubtitlesReplaceCommand(cues, rangeStartTicks: 20_000_000L, rangeEndTicks: 60_000_000L, subtitleStyle: SubtitleStyleSettings.KaraokePop());
        var replaceItems = AiAgentCommandBuilder.DescribeProposedChanges(replaceCmd, snapshot, timeline);
        var replaceRow = Assert.Single(replaceItems);
        Assert.Equal("+", replaceRow.Badge);
        Assert.Equal("Subtitles", replaceRow.Label);
        Assert.Equal("2 cues", replaceRow.Detail);
        Assert.Equal(2.1, replaceRow.SeekSeconds);
        Assert.Equal("00:02.1–00:05.8", replaceRow.TimeLabel);

        // 3. BuildSubtitlesReplaceCommand (empty/clear) + diff row
        var clearCmd = AiAgentCommandBuilder.BuildSubtitlesReplaceCommand(Array.Empty<SourceEffect>(), rangeStartTicks: 10_000_000L, rangeEndTicks: 30_000_000L);
        var clearRow = Assert.Single(AiAgentCommandBuilder.DescribeProposedChanges(clearCmd, snapshot, timeline));
        Assert.Equal("−", clearRow.Badge);
        Assert.Equal("Subtitles", clearRow.Label);
        Assert.Equal("Cleared", clearRow.Detail);

        // 4. BuildSubtitleStyleCommand + diff row
        var styleCmd = AiAgentCommandBuilder.BuildSubtitleStyleCommand(snapshot, SubtitleStyleSettings.DarkPill());
        var styleRow = Assert.Single(AiAgentCommandBuilder.DescribeProposedChanges(styleCmd, snapshot, timeline));
        Assert.Equal("~", styleRow.Badge);
        Assert.Contains("subtitles:darkPill", styleRow.Detail);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
