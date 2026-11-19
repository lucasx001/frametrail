using System.Text.Json;
using Xunit;

namespace FrameTrail.App.Tests;

public sealed class AiCopilotProtocolTests
{
    [Fact]
    public void InitEnvelopeSerializesInCamelCaseAndPreservesAllFields()
    {
        var init = new AiInitPayload(
            Theme: "dark",
            Locale: "zh-Hans",
            Strings: new Dictionary<string, string>
            {
                ["AiCopilotTitle"] = "AI 剪辑助手",
                ["AiSend"] = "发送"
            },
            Context: new AiEditorContextPayload(
                PlayheadSec: 3.5,
                PlayheadLabel: "00:03.5",
                DurationSec: 12.0,
                DurationLabel: "00:12.0",
                SelectedClipIndex: 0,
                ClipCount: 2,
                CanvasWidth: 1920,
                CanvasHeight: 1080,
                HasPointer: true),
            Settings: new AiSettingsSummaryDto(
                Provider: "openai",
                ModelName: "gpt-4o",
                BaseUrl: "",
                BaseUrlPlaceholder: "https://api.openai.com/v1",
                AutoApply: true,
                HasApiKey: true,
                ProviderHasApiKey: new Dictionary<string, bool>
                {
                    ["openai"] = true,
                    ["anthropic"] = false,
                    ["deepseek"] = false
                }),
            Env: new AiEnvStatusPayload(
                SidecarOnline: true,
                SidecarStarting: false,
                SidecarError: null,
                HasAntigravityCli: true,
                HasClaudeCodeCli: false,
                HasCodexCli: false,
                HasOllamaServer: true,
                OllamaModels: ["qwen2.5-coder:14b"]));

        string json = AiCopilotProtocol.SerializeEnvelope("init", init);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("init", root.GetProperty("type").GetString());
        var payload = root.GetProperty("payload");
        Assert.Equal("dark", payload.GetProperty("theme").GetString());
        Assert.Equal("zh-Hans", payload.GetProperty("locale").GetString());
        Assert.Equal("AI 剪辑助手", payload.GetProperty("strings").GetProperty("AiCopilotTitle").GetString());
        Assert.Equal(3.5, payload.GetProperty("context").GetProperty("playheadSec").GetDouble());
        Assert.True(payload.GetProperty("context").GetProperty("hasPointer").GetBoolean());
        Assert.True(payload.GetProperty("settings").GetProperty("hasApiKey").GetBoolean());
        Assert.True(payload.GetProperty("env").GetProperty("sidecarOnline").GetBoolean());
        Assert.Equal("qwen2.5-coder:14b", payload.GetProperty("env").GetProperty("ollamaModels")[0].GetString());
        Assert.False(payload.GetProperty("env").TryGetProperty("sidecarError", out _));
    }

    [Fact]
    public void SettingsSummaryDtoNeverExposesPlaintextApiKeyPropertyOrField()
    {
        var propertyNames = typeof(AiSettingsSummaryDto)
            .GetProperties()
            .Select(p => p.Name)
            .ToArray();

        Assert.DoesNotContain(propertyNames, name =>
            name.Equals("ApiKey", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NewApiKey", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Secret", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("EncryptedApiKeys", StringComparison.OrdinalIgnoreCase));

        var dto = new AiSettingsSummaryDto(
            Provider: "openai",
            ModelName: "gpt-4o",
            BaseUrl: "https://api.openai.com/v1",
            BaseUrlPlaceholder: "https://api.openai.com/v1",
            AutoApply: false,
            HasApiKey: true,
            ProviderHasApiKey: new Dictionary<string, bool> { ["openai"] = true });

        string json = AiCopilotProtocol.SerializeEnvelope("settings.updated", dto);
        using var doc = JsonDocument.Parse(json);
        var payload = doc.RootElement.GetProperty("payload");
        Assert.True(payload.GetProperty("hasApiKey").GetBoolean());
        Assert.False(payload.TryGetProperty("apiKey", out _));
        Assert.False(payload.TryGetProperty("newApiKey", out _));
    }

    [Fact]
    public void IncomingMessagesParseFromObjectAndStringifiedJson()
    {
        const string objectMsg = """{"type":"chat.send","payload":{"prompt":"Add 2x zoom at 2s"}}""";
        Assert.True(AiCopilotProtocol.TryParseIncoming(objectMsg, out var parsedObj));
        Assert.NotNull(parsedObj);
        Assert.Equal("chat.send", parsedObj.Type);
        var sendPayload = AiCopilotProtocol.DeserializePayload<AiChatSendPayload>(parsedObj.Payload);
        Assert.Equal("Add 2x zoom at 2s", sendPayload?.Prompt);

        const string stringifiedMsg = "\"{\\\"type\\\":\\\"settings.updateConfig\\\",\\\"payload\\\":{\\\"modelName\\\":\\\"claude-sonnet-4\\\",\\\"clearApiKey\\\":true}}\"";
        Assert.True(AiCopilotProtocol.TryParseIncoming(stringifiedMsg, out var parsedStr));
        Assert.NotNull(parsedStr);
        Assert.Equal("settings.updateConfig", parsedStr.Type);
        var cfg = AiCopilotProtocol.DeserializePayload<AiUpdateConfigPayload>(parsedStr.Payload);
        Assert.Equal("claude-sonnet-4", cfg?.ModelName);
        Assert.True(cfg?.ClearApiKey);

        const string readyMsg = """{"type":"ui.ready"}""";
        Assert.True(AiCopilotProtocol.TryParseIncoming(readyMsg, out var parsedReady));
        Assert.Equal("ui.ready", parsedReady?.Type);
        Assert.Null(parsedReady?.Payload);

        Assert.False(AiCopilotProtocol.TryParseIncoming("not json", out _));
        Assert.False(AiCopilotProtocol.TryParseIncoming("""{"payload":{}}""", out _));
    }

    [Fact]
    public void TurnStreamAndChangesRoundTripCorrectly()
    {
        var streamJson = AiCopilotProtocol.SerializeEnvelope("turn.stream", new AiTurnStreamPayload(
            TurnId: "t1",
            Kind: "tool_end",
            Text: "Added 1.8x zoom",
            ToolName: "add_zoom_effect",
            Succeeded: true));
        using (var doc = JsonDocument.Parse(streamJson))
        {
            var payload = doc.RootElement.GetProperty("payload");
            Assert.Equal("t1", payload.GetProperty("turnId").GetString());
            Assert.Equal("tool_end", payload.GetProperty("kind").GetString());
            Assert.Equal("add_zoom_effect", payload.GetProperty("toolName").GetString());
            Assert.True(payload.GetProperty("succeeded").GetBoolean());
        }

        var changesJson = AiCopilotProtocol.SerializeEnvelope("turn.changes", new AiTurnChangesPayload(
            TurnId: "t1",
            State: "previewing",
            Revision: null,
            Items: [new AiProposedChangeDto("+", "Zoom", "1.8x", 2.5, "00:02.5")]));
        using (var doc = JsonDocument.Parse(changesJson))
        {
            var payload = doc.RootElement.GetProperty("payload");
            Assert.Equal("previewing", payload.GetProperty("state").GetString());
            Assert.False(payload.TryGetProperty("revision", out _));
            Assert.Equal(2.5, payload.GetProperty("items")[0].GetProperty("seekSeconds").GetDouble());
        }
    }

    [Fact]
    public void ContextThrottleRespectsIntervalAndPlayheadThreshold()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var throttle = new AiContextThrottle(TimeSpan.FromMilliseconds(150), 0.1, () => now);

        AiEditorContextPayload MakeCtx(double playhead, int clipIdx = 0, int clips = 1, double dur = 10.0, uint w = 1920) =>
            new(playhead, "00:00.0", dur, "00:10.0", clipIdx, clips, w, 1080, false);

        // First call always publishes.
        Assert.True(throttle.ShouldPublish(MakeCtx(0.0)));

        // Within 150ms, even large playhead jumps are throttled.
        now = now.AddMilliseconds(100);
        Assert.False(throttle.ShouldPublish(MakeCtx(1.5)));

        // After 150ms, tiny playhead jitter (< 0.1s) is still throttled.
        now = now.AddMilliseconds(60);
        Assert.False(throttle.ShouldPublish(MakeCtx(0.05)));

        // After 150ms with >= 0.1s playhead change, publishes.
        Assert.True(throttle.ShouldPublish(MakeCtx(0.25)));

        // Structural changes (clip index, clip count, duration, canvas) publish immediately even within 150ms.
        now = now.AddMilliseconds(20);
        Assert.True(throttle.ShouldPublish(MakeCtx(0.25, clipIdx: 1)));

        now = now.AddMilliseconds(20);
        Assert.True(throttle.ShouldPublish(MakeCtx(0.25, clipIdx: 1, clips: 2)));

        now = now.AddMilliseconds(20);
        Assert.True(throttle.ShouldPublish(MakeCtx(0.25, clipIdx: 1, clips: 2, dur: 12.0)));

        now = now.AddMilliseconds(20);
        Assert.True(throttle.ShouldPublish(MakeCtx(0.25, clipIdx: 1, clips: 2, dur: 12.0, w: 1080)));
    }

    [Fact]
    public void VoicePayloadsRoundTripCorrectly()
    {
        const string startMsg = """{"type":"voice.start","payload":{"lang":"zh-CN"}}""";
        Assert.True(AiCopilotProtocol.TryParseIncoming(startMsg, out var parsedStart));
        Assert.NotNull(parsedStart);
        Assert.Equal("voice.start", parsedStart.Type);
        var startPayload = AiCopilotProtocol.DeserializePayload<AiVoiceStartPayload>(parsedStart.Payload);
        Assert.Equal("zh-CN", startPayload?.Lang);

        var stateJson = AiCopilotProtocol.SerializeEnvelope(
            "voice.state",
            new AiVoiceStatePayload("error", "Microphone unavailable", true));
        using (var doc = JsonDocument.Parse(stateJson))
        {
            var root = doc.RootElement;
            Assert.Equal("voice.state", root.GetProperty("type").GetString());
            var payload = root.GetProperty("payload");
            Assert.Equal("error", payload.GetProperty("state").GetString());
            Assert.Equal("Microphone unavailable", payload.GetProperty("error").GetString());
            Assert.True(payload.GetProperty("fallbackToWebSpeech").GetBoolean());
        }

        var transcriptJson = AiCopilotProtocol.SerializeEnvelope(
            "voice.transcript",
            new AiVoiceTranscriptPayload("在 2 秒处添加放大特效", true));
        using (var doc = JsonDocument.Parse(transcriptJson))
        {
            var root = doc.RootElement;
            Assert.Equal("voice.transcript", root.GetProperty("type").GetString());
            var payload = root.GetProperty("payload");
            Assert.Equal("在 2 秒处添加放大特效", payload.GetProperty("text").GetString());
            Assert.True(payload.GetProperty("isFinal").GetBoolean());
        }
    }
}
