using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;
using FrameTrail.Workspace;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace FrameTrail.App;

public sealed class HorizontalResizeGrip : Grid
{
    public HorizontalResizeGrip()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}

public sealed partial class EditorWindow
{
    private const double AiPanelMinWidth = 300;
    private const double AiPanelMaxWidth = 520;
    private const double AiPanelDefaultWidth = 360;

    private static readonly string[] KnownApiKeyProviders = ["openai", "anthropic", "deepseek"];

    private readonly AiAgentBridge aiBridge = new();
    private readonly AiCredentialsStore aiCredentials = new();
    private readonly AiSubscriptionManager aiSubscriptionManager = new();
    private readonly AiPreviewTransactionCoordinator aiPreviewCoordinator = new();
    private readonly AiVoiceRecognizer aiVoiceRecognizer = new();
    private readonly List<AiChatMessage> aiChatHistory = new();
    private readonly Dictionary<string, IReadOnlyList<AiProposedChangeDto>> aiTurnChangeItems = new(StringComparer.Ordinal);

    private AiCopilotWebHost? aiWebHost;
    private AiLocalCommandCallbackServer? aiCallbackServer;
    private LocalAiEnvironmentStatus? aiEnvStatus;
    private CancellationTokenSource? aiTurnCts;
    private AiProviderKind aiProvider = AiProviderKind.OpenAiApi;
    private string aiModelName = "gpt-4o";
    private string aiBaseUrl = "";
    private string? activePendingTurnId;
    private string? aiSidecarError;
    private bool aiCopilotOpen;
    private bool aiSending;
    private bool aiAutoApply;
    private bool aiSidecarOnline;
    private bool aiSidecarStarting;
    private double aiPanelWidth = AiPanelDefaultWidth;
    private bool aiDraggingSplitter;
    private double aiSplitterStartX;
    private double aiSplitterStartWidth;

    private void InitializeAiCopilot()
    {
        var settings = aiCredentials.Load();
        aiProvider = ParseProviderKey(settings.ActiveProvider);
        aiAutoApply = settings.AutoApplyEdits;
        ApplyProviderSelection(aiProvider, settings);

        aiWebHost = new AiCopilotWebHost(AiCopilotWebView);
        aiWebHost.LoadingStarted += OnAiWebViewLoadingStarted;
        aiWebHost.RuntimeMissing += OnAiWebViewRuntimeMissing;
        aiWebHost.LoadFailed += OnAiWebViewLoadFailed;
        aiWebHost.MessageReceived += OnAiWebMessageReceived;
        aiWebHost.SetTheme(Root.ActualTheme);

        aiVoiceRecognizer.StateChanged += OnAiVoiceStateChanged;
        aiVoiceRecognizer.TranscriptReceived += OnAiVoiceTranscriptReceived;

        App.AiSidecarStatusChanged += OnAppSidecarStatusChanged;
        _ = RefreshAiEnvironmentAsync();
    }

    private void OnAiVoiceStateChanged(string state, string? error, bool fallbackToWebSpeech)
    {
        if (closing) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closing) return;
            aiWebHost?.Post("voice.state", new AiVoiceStatePayload(
                State: state,
                Error: error,
                FallbackToWebSpeech: fallbackToWebSpeech ? true : null));
        });
    }

    private void OnAiVoiceTranscriptReceived(string text, bool isFinal)
    {
        if (closing) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closing) return;
            aiWebHost?.Post("voice.transcript", new AiVoiceTranscriptPayload(
                Text: text,
                IsFinal: isFinal));
        });
    }

    private void OnAppSidecarStatusChanged(object? sender, EventArgs e)
    {
        if (closing) return;
        DispatcherQueue.TryEnqueue(() => _ = RefreshAiEnvironmentAsync());
    }

    private void DisposeAiCopilot()
    {
        App.AiSidecarStatusChanged -= OnAppSidecarStatusChanged;

        aiVoiceRecognizer.StateChanged -= OnAiVoiceStateChanged;
        aiVoiceRecognizer.TranscriptReceived -= OnAiVoiceTranscriptReceived;
        try { aiVoiceRecognizer.Dispose(); } catch { }

        try { aiTurnCts?.Cancel(); } catch { }
        try { aiTurnCts?.Dispose(); } catch { }
        aiTurnCts = null;

        if (aiPreviewCoordinator.HasStagedPreview)
        {
            try { aiPreviewCoordinator.RejectPreview(editor); } catch { }
        }
        activePendingTurnId = null;

        try { aiCallbackServer?.Dispose(); } catch { }
        aiCallbackServer = null;

        if (aiWebHost is not null)
        {
            aiWebHost.LoadingStarted -= OnAiWebViewLoadingStarted;
            aiWebHost.RuntimeMissing -= OnAiWebViewRuntimeMissing;
            aiWebHost.LoadFailed -= OnAiWebViewLoadFailed;
            aiWebHost.MessageReceived -= OnAiWebMessageReceived;
            aiWebHost.Dispose();
            aiWebHost = null;
        }
    }

    private void EnsureAiCallbackServer()
    {
        if (aiCallbackServer is not null) return;
        try
        {
            aiCallbackServer = new AiLocalCommandCallbackServer(HandleExternalMcpCommandAsync);
        }
        catch
        {
            // Loopback callback is optional if restricted by OS policy.
        }
    }

    private Task<(bool Ok, long? Revision, string? Error)> HandleExternalMcpCommandAsync(
        JsonElement command,
        CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<(bool Ok, long? Revision, string? Error)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        bool enqueued = DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (closing || editor.IsExporting)
                {
                    tcs.TrySetResult((false, null, "Editor is currently busy or closing."));
                    return;
                }

                var candidate = EngineSession.Edit(editor.Snapshot, command);
                EngineSession.ValidateProject(candidate);

                var before = editor.Snapshot;
                await PerformAsync(() => editor.ExecuteAsync(command));
                if (editor.Snapshot.Revision != before.Revision)
                {
                    tcs.TrySetResult((true, editor.Snapshot.Revision, null));
                }
                else
                {
                    tcs.TrySetResult((false, null, "Command did not produce a new revision."));
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetResult((false, null, ex.Message));
            }
        });

        if (!enqueued)
            tcs.TrySetResult((false, null, "Editor dispatcher unavailable."));

        return tcs.Task;
    }

    internal void UpdateAiContextPills()
    {
        if (!aiCopilotOpen || aiWebHost is null) return;
        aiWebHost.PublishContext(BuildAiContextPayload(), force: true);
    }

    internal void NotifyAiContextThrottled()
    {
        if (!aiCopilotOpen || aiWebHost is null || !aiWebHost.IsReady) return;
        aiWebHost.PublishContext(BuildAiContextPayload(), force: false);
    }

    private void AiCopilotToggle_Click(object sender, RoutedEventArgs e)
    {
        SetAiCopilotOpen(!aiCopilotOpen);
    }

    private void SetAiCopilotOpen(bool open)
    {
        if (aiCopilotOpen == open) return;
        aiCopilotOpen = open;
        AiCopilotColumn.Width = new GridLength(aiCopilotOpen ? aiPanelWidth : 0);
        AiCopilotDividerColumn.Width = new GridLength(aiCopilotOpen ? 5 : 0);
        var vis = aiCopilotOpen ? Visibility.Visible : Visibility.Collapsed;
        AiCopilotPanel.Visibility = vis;
        AiCopilotDivider.Visibility = vis;

        UpdateAiCopilotTheme();
        SizePreview();

        if (aiCopilotOpen)
        {
            _ = EnsureAiWebViewReadyAsync();
        }
    }

    private async Task EnsureAiWebViewReadyAsync()
    {
        if (aiWebHost is null || closing) return;
        await aiWebHost.EnsureInitializedAsync(Root.ActualTheme, AppText.Language);
        if (aiWebHost.IsReady)
        {
            UpdateAiContextPills();
            aiWebHost.Post("composer.focus", new AiEmptyPayload());
        }
    }

    private void UpdateAiCopilotTheme()
    {
        AiCopilotButton.Background = aiCopilotOpen
            ? RecorderTheme.Brush(Root, "EditorAccent")
            : new SolidColorBrush(Colors.Transparent);
        if (AiCopilotButton.Content is StackPanel panel)
        {
            var ink = aiCopilotOpen
                ? RecorderTheme.Brush(Root, "EditorAccentInk")
                : RecorderTheme.Brush(Root, "EditorSecondary");
            foreach (var child in panel.Children)
            {
                if (child is TextBlock text) text.Foreground = ink;
                else if (child is EditorGlyph glyph) glyph.Foreground = ink;
            }
        }

        aiWebHost?.SetTheme(Root.ActualTheme);
    }

    // --- WebView2 Lifecycle & Fallback Overlay ---

    private void OnAiWebViewLoadingStarted()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closing) return;
            AiWebViewLoadingRing.IsActive = true;
            AiWebViewFallbackTitle.Text = L.T("AiWebViewLoading");
            AiWebViewFallbackBody.Text = "";
            AiWebViewDownloadButton.Visibility = Visibility.Collapsed;
            AiWebViewFallbackOverlay.Visibility = Visibility.Visible;
        });
    }

    private void OnAiWebViewRuntimeMissing(string downloadUrl)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closing) return;
            AiWebViewLoadingRing.IsActive = false;
            AiWebViewFallbackTitle.Text = L.T("AiWebViewMissingTitle");
            AiWebViewFallbackBody.Text = L.T("AiWebViewMissingBody");
            AiWebViewDownloadButton.Content = L.T("AiWebViewDownloadRuntime");
            AiWebViewDownloadButton.Tag = downloadUrl;
            AiWebViewDownloadButton.Visibility = Visibility.Visible;
            AiWebViewFallbackOverlay.Visibility = Visibility.Visible;
        });
    }

    private void OnAiWebViewLoadFailed(string reason)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closing) return;
            AiWebViewLoadingRing.IsActive = false;
            AiWebViewFallbackTitle.Text = L.T("AiEditFailed");
            AiWebViewFallbackBody.Text = reason;
            AiWebViewDownloadButton.Visibility = Visibility.Collapsed;
            AiWebViewFallbackOverlay.Visibility = Visibility.Visible;
        });
    }

    private void AiWebViewDownloadRuntime_Click(object sender, RoutedEventArgs e)
    {
        string url = (sender as Button)?.Tag as string ?? AiCopilotWebHost.WebView2DownloadUrl;
        AiCopilotWebHost.TryOpenExternalUri(url);
    }

    // --- Web -> Host Message Dispatcher ---

    private void OnAiWebMessageReceived(string type, JsonElement? payload)
    {
        if (closing) return;
        _ = HandleWebMessageAsync(type, payload);
    }

    private async Task HandleWebMessageAsync(string type, JsonElement? payload)
    {
        try
        {
            switch (type)
            {
                case "ui.ready":
                    AiWebViewLoadingRing.IsActive = false;
                    AiWebViewFallbackOverlay.Visibility = Visibility.Collapsed;
                    aiWebHost?.CompleteInit(BuildInitPayload());
                    aiWebHost?.Post("composer.focus", new AiEmptyPayload());
                    break;

                case "chat.send":
                    if (AiCopilotProtocol.DeserializePayload<AiChatSendPayload>(payload) is { } sendMsg)
                        await SendAiPromptAsync(sendMsg.Prompt);
                    break;

                case "chat.stop":
                    aiTurnCts?.Cancel();
                    break;

                case "chat.newSession":
                    await ResetAiSessionAsync();
                    break;

                case "panel.close":
                    SetAiCopilotOpen(false);
                    break;

                case "timeline.seek":
                    if (AiCopilotProtocol.DeserializePayload<AiTimelineSeekPayload>(payload) is { } seekMsg)
                        await SeekToSecondsAsync(seekMsg.Seconds);
                    break;

                case "changes.accept":
                    if (AiCopilotProtocol.DeserializePayload<AiTurnIdPayload>(payload) is { } acceptMsg)
                        await ApproveStagedPreviewAsync(acceptMsg.TurnId);
                    break;

                case "changes.reject":
                    if (AiCopilotProtocol.DeserializePayload<AiTurnIdPayload>(payload) is { } rejectMsg)
                        await RejectStagedPreviewAsync(rejectMsg.TurnId);
                    break;

                case "changes.undo":
                    if (AiCopilotProtocol.DeserializePayload<AiTurnIdPayload>(payload) is { } undoMsg)
                        await UndoAppliedTurnAsync(undoMsg.TurnId);
                    break;

                case "settings.selectProvider":
                    if (AiCopilotProtocol.DeserializePayload<AiSelectProviderPayload>(payload) is { } selMsg)
                    {
                        aiProvider = ParseProviderKey(selMsg.Provider);
                        ApplyProviderSelection(aiProvider, aiCredentials.Load(), selMsg.ModelOverride);
                        PersistCurrentAiSettings();
                        PublishSettingsUpdated();
                    }
                    break;

                case "settings.updateConfig":
                    if (AiCopilotProtocol.DeserializePayload<AiUpdateConfigPayload>(payload) is { } cfgMsg)
                        ApplyConfigUpdate(cfgMsg);
                    break;

                case "settings.toggleAutoApply":
                    if (AiCopilotProtocol.DeserializePayload<AiToggleAutoApplyPayload>(payload) is { } autoMsg)
                    {
                        aiAutoApply = autoMsg.AutoApply;
                        PersistCurrentAiSettings();
                        PublishSettingsUpdated();
                    }
                    break;

                case "sidecar.start":
                    await StartSidecarFromWebAsync();
                    break;

                case "env.refresh":
                    await RefreshAiEnvironmentAsync();
                    break;

                case "voice.start":
                    var voiceMsg = AiCopilotProtocol.DeserializePayload<AiVoiceStartPayload>(payload);
                    await aiVoiceRecognizer.StartAsync(voiceMsg?.Lang);
                    break;

                case "voice.stop":
                    await aiVoiceRecognizer.StopAsync();
                    break;

                case "editor.shortcut":
                    if (AiCopilotProtocol.DeserializePayload<AiEditorShortcutPayload>(payload) is { } keyMsg &&
                        !keyMsg.Alt &&
                        MapWebKeyToVirtualKey(keyMsg.Key) is VirtualKey vk)
                    {
                        await HandleEditorShortcutAsync(vk, keyMsg.Ctrl, keyMsg.Shift);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            SafeDiagnostics.Record(ex);
        }
    }

    private async Task ResetAiSessionAsync()
    {
        if (aiSending)
        {
            aiTurnCts?.Cancel();
            return;
        }

        if (aiPreviewCoordinator.HasStagedPreview)
        {
            await RejectStagedPreviewAsync(activePendingTurnId);
        }

        aiChatHistory.Clear();
        aiTurnChangeItems.Clear();
        aiWebHost?.Post("session.cleared", new AiEmptyPayload());
        UpdateAiContextPills();
        aiWebHost?.Post("composer.focus", new AiEmptyPayload());
    }

    private async Task StartSidecarFromWebAsync()
    {
        if (aiSidecarStarting) return;
        aiSidecarStarting = true;
        aiSidecarError = null;
        PublishEnvUpdated();

        try
        {
            bool started = await App.EnsureAiSidecarStartedAsync();
            if (!started && !string.IsNullOrWhiteSpace(App.AiSidecar.LastError))
            {
                aiSidecarError = App.AiSidecar.LastError;
            }
            await RefreshAiEnvironmentAsync();
        }
        finally
        {
            aiSidecarStarting = false;
            PublishEnvUpdated();
        }
    }

    private void ApplyConfigUpdate(AiUpdateConfigPayload update)
    {
        if (update.ModelName is not null)
            aiModelName = update.ModelName.Trim();

        if (update.BaseUrl is not null)
            aiBaseUrl = update.BaseUrl.Trim();

        string key = ProviderKey(aiProvider);
        try
        {
            if (update.ClearApiKey == true)
            {
                aiCredentials.RemoveApiKey(key);
            }
            else if (!string.IsNullOrWhiteSpace(update.NewApiKey))
            {
                aiCredentials.SetApiKey(key, update.NewApiKey.Trim());
            }
        }
        catch
        {
            // Keep UI responsive even if local appdata is read-only.
        }

        PersistCurrentAiSettings();
        PublishSettingsUpdated();
    }

    // --- Payload Builders (Redacted Settings, Context, Env, Localization) ---

    private AiInitPayload BuildInitPayload()
    {
        var context = BuildAiContextPayload();
        aiWebHost?.PublishContext(context, force: true);

        return new AiInitPayload(
            Theme: AiCopilotWebHost.ToThemeString(Root.ActualTheme),
            Locale: AppText.Language,
            Strings: BuildAiLocalizedStrings(),
            Context: context,
            Settings: BuildSettingsSummary(),
            Env: BuildEnvPayload());
    }

    private static IReadOnlyDictionary<string, string> BuildAiLocalizedStrings()
    {
        var all = AppText.Resources(AppText.Language);
        var filtered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in all)
        {
            if (k.StartsWith("Ai", StringComparison.Ordinal))
                filtered[k] = v;
        }
        return filtered;
    }

    private AiEditorContextPayload BuildAiContextPayload()
    {
        double posSec = Math.Max(0, position / 10_000_000.0);
        double durSec = Math.Max(0, editor.Timeline.Duration / 10_000_000.0);

        var clips = editor.Timeline.Clips;
        int clipIdx = -1;
        for (int i = 0; i < clips.Count; i++)
        {
            if (clips[i].Id == selected)
            {
                clipIdx = i;
                break;
            }
        }

        bool hasPointer = editor.Snapshot.Media.Any(m => m.Kind == MediaKind.PointerEvents);

        return new AiEditorContextPayload(
            PlayheadSec: Math.Round(posSec, 3),
            PlayheadLabel: AiAgentCommandBuilder.FormatTimestamp(posSec),
            DurationSec: Math.Round(durSec, 3),
            DurationLabel: AiAgentCommandBuilder.FormatTimestamp(durSec),
            SelectedClipIndex: clipIdx,
            ClipCount: clips.Count,
            CanvasWidth: editor.Snapshot.Canvas.Width,
            CanvasHeight: editor.Snapshot.Canvas.Height,
            HasPointer: hasPointer);
    }

    private AiEnvStatusPayload BuildEnvPayload() => new(
        SidecarOnline: aiSidecarOnline,
        SidecarStarting: aiSidecarStarting,
        SidecarError: aiSidecarError,
        HasAntigravityCli: aiEnvStatus?.HasAntigravityCli == true,
        HasClaudeCodeCli: aiEnvStatus?.HasClaudeCodeCli == true,
        HasCodexCli: aiEnvStatus?.HasCodexCli == true,
        HasOllamaServer: aiEnvStatus?.HasOllamaServer == true,
        OllamaModels: aiEnvStatus?.OllamaModels ?? Array.Empty<string>());

    private AiSettingsSummaryDto BuildSettingsSummary()
    {
        var settings = aiCredentials.Load();
        string activeKey = ProviderKey(aiProvider);

        var providerHasKey = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in KnownApiKeyProviders)
        {
            bool exists = settings.EncryptedApiKeys.TryGetValue(p, out var enc) && !string.IsNullOrWhiteSpace(enc);
            providerHasKey[p] = exists;
        }

        bool activeHasKey = providerHasKey.TryGetValue(activeKey, out var has) && has;

        return new AiSettingsSummaryDto(
            Provider: activeKey,
            ModelName: aiModelName,
            BaseUrl: aiBaseUrl,
            BaseUrlPlaceholder: GetBaseUrlPlaceholder(aiProvider),
            AutoApply: aiAutoApply,
            HasApiKey: activeHasKey,
            ProviderHasApiKey: providerHasKey);
    }

    private void PublishSettingsUpdated()
    {
        aiWebHost?.Post("settings.updated", BuildSettingsSummary());
    }

    private void PublishEnvUpdated()
    {
        aiWebHost?.Post("env.updated", BuildEnvPayload());
    }

    // --- Settings Persistence & Environment Probing ---

    private void ApplyProviderSelection(AiProviderKind provider, AiUserSettings settings, string? modelOverride = null)
    {
        string key = ProviderKey(provider);
        string defaultModel = provider switch
        {
            AiProviderKind.OpenAiApi => "gpt-4o",
            AiProviderKind.AnthropicApi => "claude-sonnet-4-20250514",
            AiProviderKind.DeepSeekApi => "deepseek-chat",
            AiProviderKind.OllamaLocal => "qwen2.5-coder:14b",
            AiProviderKind.ClaudeCodeCli => "claude-code-cli",
            AiProviderKind.CodexCli => "codex-cli",
            AiProviderKind.AntigravityCli => "agy-cli",
            _ => "gpt-4o"
        };
        string defaultBase = provider switch
        {
            AiProviderKind.OpenAiApi => "",
            AiProviderKind.AnthropicApi => "",
            AiProviderKind.DeepSeekApi => "https://api.deepseek.com/v1",
            AiProviderKind.OllamaLocal => "http://localhost:11434/v1",
            _ => ""
        };

        aiModelName = !string.IsNullOrWhiteSpace(modelOverride)
            ? modelOverride.Trim()
            : (settings.ProviderModels.TryGetValue(key, out var savedModel) && !string.IsNullOrWhiteSpace(savedModel) ? savedModel : defaultModel);
        aiBaseUrl = settings.ProviderBaseUrls.TryGetValue(key, out var savedUrl) ? savedUrl : defaultBase;
    }

    private static string GetBaseUrlPlaceholder(AiProviderKind provider) => provider switch
    {
        AiProviderKind.OpenAiApi => "https://api.openai.com/v1",
        AiProviderKind.AnthropicApi => "https://api.anthropic.com",
        AiProviderKind.DeepSeekApi => "https://api.deepseek.com/v1",
        AiProviderKind.OllamaLocal => "http://localhost:11434/v1",
        AiProviderKind.ClaudeCodeCli => "Local Claude Code CLI",
        AiProviderKind.CodexCli => "Local OpenAI Codex CLI",
        AiProviderKind.AntigravityCli => "Local Google Antigravity CLI (agy)",
        _ => ""
    };

    private void PersistCurrentAiSettings()
    {
        try
        {
            string key = ProviderKey(aiProvider);
            var current = aiCredentials.Load();
            var models = new Dictionary<string, string>(current.ProviderModels, StringComparer.OrdinalIgnoreCase)
            {
                [key] = aiModelName
            };
            var baseUrls = new Dictionary<string, string>(current.ProviderBaseUrls, StringComparer.OrdinalIgnoreCase)
            {
                [key] = aiBaseUrl
            };

            aiCredentials.Save(current with
            {
                ActiveProvider = key,
                ActiveModel = aiModelName,
                CustomBaseUrl = aiBaseUrl,
                AutoApplyEdits = aiAutoApply,
                ProviderModels = models,
                ProviderBaseUrls = baseUrls
            });
        }
        catch
        {
            // Keep UI responsive even if local appdata is read-only.
        }
    }

    private async Task RefreshAiEnvironmentAsync()
    {
        try
        {
            var healthTask = aiBridge.IsSidecarHealthyAsync("http://127.0.0.1:18765");
            var envTask = aiSubscriptionManager.ProbeEnvironmentAsync();
            await Task.WhenAll(healthTask, envTask);
            if (closing) return;

            aiSidecarOnline = healthTask.Result;
            aiEnvStatus = envTask.Result;
            if (aiSidecarOnline) aiSidecarError = null;

            PublishEnvUpdated();
        }
        catch
        {
            // Ignore probe errors.
        }
    }

    private AiAgentConfig BuildCurrentAiConfig()
    {
        PersistCurrentAiSettings();
        string key = ProviderKey(aiProvider);
        string apiKey = "";
        try
        {
            apiKey = aiCredentials.GetApiKey(key) ?? "";
        }
        catch
        {
            apiKey = "";
        }

        return new AiAgentConfig(
            Provider: aiProvider,
            ModelName: string.IsNullOrWhiteSpace(aiModelName) ? "gpt-4o" : aiModelName,
            ApiKey: apiKey,
            BaseUrl: aiBaseUrl,
            SidecarUrl: "http://127.0.0.1:18765");
    }

    private static string ProviderKey(AiProviderKind provider) => provider switch
    {
        AiProviderKind.OpenAiApi => "openai",
        AiProviderKind.AnthropicApi => "anthropic",
        AiProviderKind.DeepSeekApi => "deepseek",
        AiProviderKind.OllamaLocal => "ollama",
        AiProviderKind.ClaudeCodeCli => "claude_code_cli",
        AiProviderKind.CodexCli => "codex_cli",
        AiProviderKind.AntigravityCli => "antigravity_cli",
        _ => "openai"
    };

    private static AiProviderKind ParseProviderKey(string? key) => (key ?? "").Trim().ToLowerInvariant() switch
    {
        "anthropic" => AiProviderKind.AnthropicApi,
        "deepseek" => AiProviderKind.DeepSeekApi,
        "ollama" => AiProviderKind.OllamaLocal,
        "claude_code_cli" or "claude" => AiProviderKind.ClaudeCodeCli,
        "codex_cli" or "codex" => AiProviderKind.CodexCli,
        "antigravity_cli" or "agy" => AiProviderKind.AntigravityCli,
        _ => AiProviderKind.OpenAiApi
    };

    // --- Core Non-Blocking Streaming Agent Turn ---

    private async Task SendAiPromptAsync(string rawPrompt)
    {
        if (aiSending || closing || editor.IsExporting) return;
        string prompt = rawPrompt?.Trim() ?? "";
        if (string.IsNullOrEmpty(prompt)) return;

        // Auto-reject any previous unreviewed preview transaction before starting a new turn.
        if (aiPreviewCoordinator.HasStagedPreview)
            await RejectStagedPreviewAsync(activePendingTurnId);

        string turnId = Guid.NewGuid().ToString("N");
        aiWebHost?.Post("turn.started", new AiTurnStartedPayload(turnId, prompt));
        aiChatHistory.Add(new AiChatMessage("user", prompt));

        aiSending = true;
        aiTurnCts?.Dispose();
        aiTurnCts = new CancellationTokenSource();
        var ct = aiTurnCts.Token;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            EnsureAiCallbackServer();
            var config = BuildCurrentAiConfig();
            double playheadSec = position / 10_000_000.0;

            // IMPORTANT: Do NOT wrap LLM/sidecar inference in PerformAsync so the editor remains
            // interactive (user can scrub, play/pause, or hit Stop) while the agent is thinking!
            var result = await aiBridge.RunTurnStreamingAsync(
                editor,
                config,
                prompt,
                playheadSec,
                aiChatHistory,
                dryRunOnly: true,
                callbackUrl: aiCallbackServer?.CallbackUrl,
                onEvent: evt => DispatcherQueue.TryEnqueue(() =>
                    aiWebHost?.Post("turn.stream", new AiTurnStreamPayload(
                        TurnId: turnId,
                        Kind: MapStreamKind(evt.Kind),
                        Text: evt.Text,
                        ToolName: evt.ToolName,
                        Succeeded: evt.Kind is AiStreamEventKind.ToolEnd or AiStreamEventKind.ToolStart ? evt.Succeeded : null))),
                ct: ct);

            sw.Stop();
            var toolDtos = (result.ToolCalls ?? Array.Empty<AiToolCallRecord>())
                .Select(tc => new AiToolCallDto(tc.ToolName, tc.Summary, tc.Succeeded))
                .ToArray();

            aiWebHost?.Post("turn.completed", new AiTurnCompletedPayload(
                TurnId: turnId,
                ReplyText: result.ReplyText,
                ElapsedSeconds: Math.Round(sw.Elapsed.TotalSeconds, 2),
                Status: "done",
                ToolCalls: toolDtos));

            if (!string.IsNullOrWhiteSpace(result.ReplyText))
                aiChatHistory.Add(new AiChatMessage("assistant", result.ReplyText));

            if (result.ProposedCommand is not null)
            {
                var changes = AiAgentCommandBuilder.DescribeProposedChanges(
                    result.ProposedCommand, editor.Snapshot, editor.Timeline);
                var itemDtos = changes
                    .Select(c => new AiProposedChangeDto(c.Badge, c.Label, c.Detail, c.SeekSeconds, c.TimeLabel))
                    .ToArray();
                aiTurnChangeItems[turnId] = itemDtos;

                if (aiAutoApply)
                {
                    await PerformAsync(() => editor.ExecuteAsync(result.ProposedCommand));
                    aiWebHost?.Post("turn.changes", new AiTurnChangesPayload(
                        TurnId: turnId,
                        State: "applied",
                        Revision: editor.Snapshot.Revision,
                        Items: itemDtos));
                    UpdateAiContextPills();
                }
                else
                {
                    await StagePreviewAndPublishCardAsync(turnId, result.ProposedCommand, itemDtos);
                }
            }
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            aiWebHost?.Post("turn.completed", new AiTurnCompletedPayload(
                TurnId: turnId,
                ReplyText: L.T("AiCancelled"),
                ElapsedSeconds: Math.Round(sw.Elapsed.TotalSeconds, 2),
                Status: "cancelled",
                ToolCalls: Array.Empty<AiToolCallDto>()));
        }
        catch (Exception ex)
        {
            sw.Stop();
            aiWebHost?.Post("turn.completed", new AiTurnCompletedPayload(
                TurnId: turnId,
                ReplyText: $"**{L.T("AiEditFailed")}**: {ex.Message}",
                ElapsedSeconds: Math.Round(sw.Elapsed.TotalSeconds, 2),
                Status: "error",
                ToolCalls: Array.Empty<AiToolCallDto>()));
        }
        finally
        {
            aiSending = false;
        }
    }

    private static string MapStreamKind(AiStreamEventKind kind) => kind switch
    {
        AiStreamEventKind.Thinking => "thinking",
        AiStreamEventKind.ToolStart => "tool_start",
        AiStreamEventKind.ToolEnd => "tool_end",
        AiStreamEventKind.Token => "token",
        AiStreamEventKind.Done => "done",
        AiStreamEventKind.Error => "error",
        _ => "token"
    };

    private async Task StagePreviewAndPublishCardAsync(
        string turnId,
        object proposedCommand,
        IReadOnlyList<AiProposedChangeDto> itemDtos)
    {
        try
        {
            Pause();
            await transport.StopWork;
            var staged = await aiPreviewCoordinator.StagePreviewBatchAsync(editor, proposedCommand, "AI Preview");
            var resolved = EngineSession.ResolveTimeline(staged);
            long nextPos = Math.Clamp(position, 0, Math.Max(0, resolved.Duration - 1));
            await preview.UpdateProjectAsync(CanvasDisplay(staged), nextPos);
            timeline.Set(resolved, selected, nextPos);
            PreviewProgressScrubber.SetProgress(nextPos, resolved.Duration);

            activePendingTurnId = turnId;
            aiWebHost?.Post("turn.changes", new AiTurnChangesPayload(
                TurnId: turnId,
                State: "previewing",
                Revision: null,
                Items: itemDtos));
        }
        catch (Exception ex)
        {
            ShowNotice(L.T("AiEditFailed"), ex.Message, InfoBarSeverity.Warning);
        }
    }

    private async Task ApproveStagedPreviewAsync(string? turnId)
    {
        if (aiSending || working || closing || !aiPreviewCoordinator.HasStagedPreview) return;

        await PerformAsync(async () =>
        {
            await aiPreviewCoordinator.ApprovePreviewAsync(editor);
        });

        string resolvedTurnId = string.IsNullOrWhiteSpace(turnId) ? (activePendingTurnId ?? "") : turnId;
        activePendingTurnId = null;

        if (!string.IsNullOrEmpty(resolvedTurnId))
        {
            aiTurnChangeItems.TryGetValue(resolvedTurnId, out var items);
            aiWebHost?.Post("turn.changes", new AiTurnChangesPayload(
                TurnId: resolvedTurnId,
                State: "applied",
                Revision: editor.Snapshot.Revision,
                Items: items ?? Array.Empty<AiProposedChangeDto>()));
        }

        UpdateAiContextPills();
    }

    private async Task RejectStagedPreviewAsync(string? turnId)
    {
        if (aiPreviewCoordinator.HasStagedPreview)
        {
            aiPreviewCoordinator.RejectPreview(editor);
            await preview.UpdateProjectAsync(CanvasDisplay(editor.Snapshot), position);
            RefreshTimeline();
            UpdateClock();
        }

        string resolvedTurnId = string.IsNullOrWhiteSpace(turnId) ? (activePendingTurnId ?? "") : turnId;
        activePendingTurnId = null;

        if (!string.IsNullOrEmpty(resolvedTurnId))
        {
            aiTurnChangeItems.TryGetValue(resolvedTurnId, out var items);
            aiWebHost?.Post("turn.changes", new AiTurnChangesPayload(
                TurnId: resolvedTurnId,
                State: "reverted",
                Revision: null,
                Items: items ?? Array.Empty<AiProposedChangeDto>()));
        }
    }

    private async Task UndoAppliedTurnAsync(string? turnId)
    {
        if (aiSending || working || closing || !editor.CanUndo) return;

        await PerformAsync(editor.UndoAsync);

        if (!string.IsNullOrWhiteSpace(turnId))
        {
            aiTurnChangeItems.TryGetValue(turnId, out var items);
            aiWebHost?.Post("turn.changes", new AiTurnChangesPayload(
                TurnId: turnId,
                State: "reverted",
                Revision: editor.Snapshot.Revision,
                Items: items ?? Array.Empty<AiProposedChangeDto>()));
        }

        UpdateAiContextPills();
    }

    private async Task SeekToSecondsAsync(double seconds)
    {
        if (working || closing || editor.IsExporting || !double.IsFinite(seconds)) return;
        long ticks = Math.Clamp((long)Math.Round(seconds * 10_000_000.0), 0, Math.Max(0, editor.Timeline.Duration - 1));
        await SafeAsync(() => SeekAsync(ticks));
    }

    private static VirtualKey? MapWebKeyToVirtualKey(string? key) => (key ?? "").Trim() switch
    {
        " " or "Space" or "Spacebar" => VirtualKey.Space,
        "ArrowLeft" or "Left" => VirtualKey.Left,
        "ArrowRight" or "Right" => VirtualKey.Right,
        "Escape" or "Esc" => VirtualKey.Escape,
        "F11" => VirtualKey.F11,
        "Home" => VirtualKey.Home,
        "End" => VirtualKey.End,
        "Delete" or "Del" => VirtualKey.Delete,
        "z" or "Z" => VirtualKey.Z,
        "y" or "Y" => VirtualKey.Y,
        "c" or "C" => VirtualKey.C,
        "v" or "V" => VirtualKey.V,
        "s" or "S" => VirtualKey.S,
        _ => null
    };

    // --- Draggable Panel Splitter (300px – 520px) with WebView2 Drag Shield ---

    private void AiSplitter_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        AiCopilotDividerLine.Width = 2;
        AiCopilotDividerLine.Background = RecorderTheme.Brush(Root, "EditorAccent");
    }

    private void AiSplitter_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (aiDraggingSplitter) return;
        AiCopilotDividerLine.Width = 1;
        AiCopilotDividerLine.Background = RecorderTheme.Brush(Root, "EditorLine");
    }

    private void AiSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        aiDraggingSplitter = true;
        AiWebViewDragShield.Visibility = Visibility.Visible;
        aiSplitterStartX = e.GetCurrentPoint(Root).Position.X;
        aiSplitterStartWidth = AiCopilotColumn.ActualWidth > 0 ? AiCopilotColumn.ActualWidth : aiPanelWidth;
        AiCopilotDivider.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void AiSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!aiDraggingSplitter) return;
        double delta = e.GetCurrentPoint(Root).Position.X - aiSplitterStartX;
        aiPanelWidth = Math.Clamp(aiSplitterStartWidth + delta, AiPanelMinWidth, AiPanelMaxWidth);
        AiCopilotColumn.Width = new GridLength(aiPanelWidth);
        SizePreview();
    }

    private void AiSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!aiDraggingSplitter) return;
        aiDraggingSplitter = false;
        AiWebViewDragShield.Visibility = Visibility.Collapsed;
        AiCopilotDivider.ReleasePointerCapture(e.Pointer);
        AiCopilotDividerLine.Width = 1;
        AiCopilotDividerLine.Background = RecorderTheme.Brush(Root, "EditorLine");
    }
}
