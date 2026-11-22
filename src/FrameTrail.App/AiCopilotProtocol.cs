using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrameTrail.App;

/// <summary>
/// Shared camelCase JSON protocol contracts and context throttling for the WinUI 3 AI Copilot WebView2 host.
/// Security invariant: no DTO in this file ever carries plaintext API keys.
/// </summary>
public static class AiCopilotProtocol
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string SerializeEnvelope(string type, object? payload = null)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("Message type is required.", nameof(type));

        var envelope = new AiCopilotEnvelope(type, payload);
        return JsonSerializer.Serialize(envelope, JsonOptions);
    }

    public static bool TryParseIncoming(string? rawJson, out AiCopilotIncomingMessage? message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(rawJson))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            // WebView2 postMessage may deliver either a JSON object or a JSON-encoded string.
            if (root.ValueKind == JsonValueKind.String)
            {
                string? nested = root.GetString();
                if (string.IsNullOrWhiteSpace(nested))
                    return false;

                using var nestedDoc = JsonDocument.Parse(nested);
                return TryExtractIncoming(nestedDoc.RootElement, out message);
            }

            return TryExtractIncoming(root, out message);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static T? DeserializePayload<T>(JsonElement? payload)
    {
        if (payload is null || payload.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return default;

        return payload.Value.Deserialize<T>(JsonOptions);
    }

    private static bool TryExtractIncoming(JsonElement root, out AiCopilotIncomingMessage? message)
    {
        message = null;
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            return false;

        string? type = typeEl.GetString();
        if (string.IsNullOrWhiteSpace(type))
            return false;

        JsonElement? payload = root.TryGetProperty("payload", out var payloadEl) &&
                               payloadEl.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
            ? payloadEl.Clone()
            : null;

        message = new AiCopilotIncomingMessage(type, payload);
        return true;
    }
}

public sealed record AiCopilotEnvelope(string Type, object? Payload = null);

public sealed record AiCopilotIncomingMessage(string Type, JsonElement? Payload = null);

public sealed record AiEmptyPayload;

// --- Host -> Web Payloads ---

public sealed record AiInitPayload(
    string Theme,
    string Locale,
    IReadOnlyDictionary<string, string> Strings,
    AiEditorContextPayload Context,
    AiSettingsSummaryDto Settings,
    AiEnvStatusPayload Env);

public sealed record AiThemeChangedPayload(string Theme);

public sealed record AiEditorContextPayload(
    double PlayheadSec,
    string PlayheadLabel,
    double DurationSec,
    string DurationLabel,
    int SelectedClipIndex,
    int ClipCount,
    uint CanvasWidth,
    uint CanvasHeight,
    bool HasPointer);

public sealed record AiEnvStatusPayload(
    bool SidecarOnline,
    bool SidecarStarting,
    string? SidecarError,
    bool HasAntigravityCli,
    bool HasClaudeCodeCli,
    bool HasCodexCli,
    bool HasOllamaServer,
    IReadOnlyList<string> OllamaModels);

/// <summary>
/// Redacted AI settings snapshot sent to the WebView. Never includes plaintext API keys.
/// </summary>
public sealed record AiSettingsSummaryDto(
    string Provider,
    string ModelName,
    string BaseUrl,
    string BaseUrlPlaceholder,
    bool AutoApply,
    bool HasApiKey,
    IReadOnlyDictionary<string, bool> ProviderHasApiKey);

public sealed record AiTurnStartedPayload(
    string TurnId,
    string UserPrompt);

public sealed record AiTurnStreamPayload(
    string TurnId,
    string Kind,
    string? Text = null,
    string? ToolName = null,
    bool? Succeeded = null);

public sealed record AiToolCallDto(
    string ToolName,
    string Summary,
    bool Succeeded);

public sealed record AiTurnCompletedPayload(
    string TurnId,
    string ReplyText,
    double ElapsedSeconds,
    string Status,
    IReadOnlyList<AiToolCallDto> ToolCalls);

public sealed record AiProposedChangeDto(
    string Badge,
    string Label,
    string Detail,
    double? SeekSeconds,
    string TimeLabel);

public sealed record AiTurnChangesPayload(
    string TurnId,
    string State,
    long? Revision,
    IReadOnlyList<AiProposedChangeDto> Items);

public sealed record AiVoiceStatePayload(
    string State,
    string? Error = null,
    bool? FallbackToWebSpeech = null);

public sealed record AiVoiceTranscriptPayload(
    string Text,
    bool IsFinal);

public sealed record AiActionConfirmationPayload(
    string RequestId,
    string TurnId,
    string Kind,
    string Title,
    string Description,
    string? Detail = null,
    string? ConfirmLabel = null,
    string? RejectLabel = null,
    string Status = "pending",
    double? ProgressPercent = null,
    string? StatusMessage = null);

// --- Web -> Host Payloads ---

public sealed record AiVoiceStartPayload(string? Lang = null);

public sealed record AiConfirmationRespondPayload(
    string RequestId,
    bool Approved);

public sealed record AiChatSendPayload(string Prompt);

public sealed record AiTimelineSeekPayload(double Seconds);

public sealed record AiTurnIdPayload(string TurnId);

public sealed record AiSelectProviderPayload(
    string Provider,
    string? ModelOverride = null);

public sealed record AiUpdateConfigPayload(
    string? ModelName = null,
    string? BaseUrl = null,
    string? NewApiKey = null,
    bool? ClearApiKey = null);

public sealed record AiToggleAutoApplyPayload(bool AutoApply);

public sealed record AiEditorShortcutPayload(
    string Key,
    bool Ctrl,
    bool Shift,
    bool Alt);

/// <summary>
/// Rate-limits high-frequency playhead updates during video playback while forwarding
/// structural timeline/canvas changes immediately.
/// </summary>
public sealed class AiContextThrottle
{
    private readonly TimeSpan minInterval;
    private readonly double minPlayheadDeltaSec;
    private readonly Func<DateTimeOffset> utcNow;

    private AiEditorContextPayload? lastPublished;
    private DateTimeOffset lastPublishedAt = DateTimeOffset.MinValue;

    public AiContextThrottle(
        TimeSpan? minInterval = null,
        double minPlayheadDeltaSec = 0.1,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.minInterval = minInterval ?? TimeSpan.FromMilliseconds(150);
        this.minPlayheadDeltaSec = minPlayheadDeltaSec;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public bool ShouldPublish(AiEditorContextPayload next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var now = utcNow();

        if (lastPublished is null)
        {
            lastPublished = next;
            lastPublishedAt = now;
            return true;
        }

        bool structuralChanged =
            lastPublished.SelectedClipIndex != next.SelectedClipIndex ||
            lastPublished.ClipCount != next.ClipCount ||
            lastPublished.CanvasWidth != next.CanvasWidth ||
            lastPublished.CanvasHeight != next.CanvasHeight ||
            lastPublished.HasPointer != next.HasPointer ||
            Math.Abs(lastPublished.DurationSec - next.DurationSec) >= 0.01;

        if (structuralChanged)
        {
            lastPublished = next;
            lastPublishedAt = now;
            return true;
        }

        if (now - lastPublishedAt < minInterval)
            return false;

        if (Math.Abs(lastPublished.PlayheadSec - next.PlayheadSec) < minPlayheadDeltaSec)
            return false;

        lastPublished = next;
        lastPublishedAt = now;
        return true;
    }

    public void MarkPublished(AiEditorContextPayload published)
    {
        ArgumentNullException.ThrowIfNull(published);
        lastPublished = published;
        lastPublishedAt = utcNow();
    }

    public void Reset()
    {
        lastPublished = null;
        lastPublishedAt = DateTimeOffset.MinValue;
    }
}
