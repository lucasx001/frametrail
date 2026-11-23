using System.Collections.Concurrent;
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

    private AiAgentBridge aiBridge = new();
    private readonly AiCredentialsStore aiCredentials = new();
    private readonly AiSubscriptionManager aiSubscriptionManager = new();
    private readonly AiPreviewTransactionCoordinator aiPreviewCoordinator = new();
    private readonly AiVoiceRecognizer aiVoiceRecognizer = new();
    private AiSpeechPolishService? speechPolishService;
    private readonly List<AiChatMessage> aiChatHistory = new();
    private readonly Dictionary<string, IReadOnlyList<AiProposedChangeDto>> aiTurnChangeItems = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> pendingAiConfirmations = new(StringComparer.Ordinal);

    private AiCopilotWebHost? aiWebHost;
    private AiLocalCommandCallbackServer? aiCallbackServer;
    private LocalAiEnvironmentStatus? aiEnvStatus;
    private CancellationTokenSource? aiTurnCts;
    private AiProviderKind aiProvider = AiProviderKind.OpenAiApi;
    private string aiModelName = "gpt-4o";
    private string aiBaseUrl = "";
    private string? activePendingTurnId;
    private string? activeAiTurnId;
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
        speechPolishService = new AiSpeechPolishService(subtitleService);
        aiBridge = new AiAgentBridge(speechPolishRunner: RunHostLocalSpeechPolishAsync);
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
        CancelAllPendingAiConfirmations();

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
            aiCallbackServer = new AiLocalCommandCallbackServer(
                HandleExternalMcpCommandAsync,
                HandleExternalMcpActionAsync);
        }
        catch
        {
            // Loopback callback is optional if restricted by OS policy.
        }
    }

    private void CancelAllPendingAiConfirmations()
    {
        foreach (var kv in pendingAiConfirmations.ToArray())
        {
            if (pendingAiConfirmations.TryRemove(kv.Key, out var tcs))
                tcs.TrySetCanceled();
        }
    }

    private Task<AiLocalCallbackActionResult> HandleExternalMcpActionAsync(
        string action,
        JsonElement parameters,
        CancellationToken ct)
    {
        if (string.Equals(action, "list_whisper_models", StringComparison.OrdinalIgnoreCase))
            return HandleExternalMcpListWhisperModelsAsync();

        if (string.Equals(action, "download_whisper_model", StringComparison.OrdinalIgnoreCase))
            return HandleExternalMcpDownloadWhisperModelAsync(parameters, ct);

        if (string.Equals(action, "generate_subtitles", StringComparison.OrdinalIgnoreCase))
            return HandleExternalMcpGenerateSubtitlesAsync(parameters, ct);

        if (string.Equals(action, "optimize_speech_flow", StringComparison.OrdinalIgnoreCase))
            return HandleExternalMcpOptimizeSpeechFlowAsync(parameters, ct);

        return Task.FromResult(new AiLocalCallbackActionResult(
            Ok: false,
            Error: $"Unsupported host callback action: '{action}'."));
    }

    private Task<AiLocalCallbackActionResult> HandleExternalMcpListWhisperModelsAsync()
    {
        var tcs = new TaskCompletionSource<AiLocalCallbackActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool enqueued = DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var (selectedEngine, selectedModelId) = SelectedAsrEngine();
                var catalog = subtitleService.ModelManager.GetCatalog();
                var models = catalog.Select(desc => new
                {
                    id = desc.Id,
                    displayName = desc.DisplayName,
                    fileName = desc.FileName,
                    expectedBytes = desc.ExpectedBytes,
                    sizeMb = Math.Round(desc.ExpectedBytes / (1024.0 * 1024.0), 1),
                    installed = subtitleService.ModelManager.IsModelInstalled(desc.Id),
                    isSelected = string.Equals(selectedEngine, "whisper-local", StringComparison.OrdinalIgnoreCase) &&
                                 string.Equals(selectedModelId, desc.Id, StringComparison.OrdinalIgnoreCase)
                }).ToArray();

                bool anyInstalled = models.Any(m => m.installed);
                tcs.TrySetResult(new AiLocalCallbackActionResult(
                    Ok: true,
                    Message: anyInstalled
                        ? "Local Whisper model(s) installed."
                        : "No local Whisper model is installed yet.",
                    Data: new
                    {
                        selectedEngine,
                        selectedModelId,
                        anyLocalModelInstalled = anyInstalled,
                        autoApply = aiAutoApply,
                        requiresApproval = !aiAutoApply,
                        modelsDirectory = subtitleService.ModelManager.ModelsDirectory,
                        models
                    }));
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(new AiLocalCallbackActionResult(false, Error: ex.Message));
            }
        });

        if (!enqueued)
            tcs.TrySetResult(new AiLocalCallbackActionResult(false, Error: "Editor dispatcher unavailable."));

        return tcs.Task;
    }

    private async Task<AiLocalCallbackActionResult> HandleExternalMcpDownloadWhisperModelAsync(
        JsonElement parameters,
        CancellationToken ct)
    {
        var prepTcs = new TaskCompletionSource<(
            bool Ok,
            string? Error,
            string ModelId,
            bool AutoApply,
            string TurnId,
            CancellationToken LinkedCt)>(TaskCreationOptions.RunContinuationsAsynchronously);

        bool enqueuedPrep = DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (closing)
                {
                    prepTcs.TrySetResult((false, "Editor is closing.", "", false, "", CancellationToken.None));
                    return;
                }
                if (subtitleBusy)
                {
                    prepTcs.TrySetResult((false, "Another subtitle or Whisper model task is already running.", "", false, "", CancellationToken.None));
                    return;
                }

                var (selectedEngine, selectedModelId) = SelectedAsrEngine();
                string requestedModelId = parameters.TryGetProperty("model_id", out var mEl) &&
                                          mEl.ValueKind == JsonValueKind.String &&
                                          !string.IsNullOrWhiteSpace(mEl.GetString())
                    ? mEl.GetString()!.Trim()
                    : (string.Equals(selectedEngine, "whisper-local", StringComparison.OrdinalIgnoreCase)
                        ? selectedModelId
                        : WhisperModelManager.LargeTurboModelId);

                subtitleAsrCts?.Dispose();
                subtitleAsrCts = CancellationTokenSource.CreateLinkedTokenSource(
                    ct,
                    aiTurnCts?.Token ?? CancellationToken.None);

                prepTcs.TrySetResult((
                    true,
                    null,
                    requestedModelId,
                    aiAutoApply,
                    activeAiTurnId ?? "",
                    subtitleAsrCts.Token));
            }
            catch (Exception ex)
            {
                prepTcs.TrySetResult((false, ex.Message, "", false, "", CancellationToken.None));
            }
        });

        if (!enqueuedPrep)
            return new AiLocalCallbackActionResult(false, Error: "Editor dispatcher unavailable.");

        var prep = await prepTcs.Task.ConfigureAwait(false);
        if (!prep.Ok)
            return new AiLocalCallbackActionResult(false, Error: prep.Error ?? "Unable to prepare model download.");

        WhisperModelDescriptor desc;
        try
        {
            desc = subtitleService.ModelManager.GetDescriptor(prep.ModelId);
        }
        catch (Exception ex)
        {
            return new AiLocalCallbackActionResult(false, Error: ex.Message);
        }

        double sizeMb = Math.Round(desc.ExpectedBytes / (1024.0 * 1024.0), 1);
        if (subtitleService.ModelManager.IsModelInstalled(desc.Id))
        {
            return new AiLocalCallbackActionResult(
                Ok: true,
                EngineUsed: $"whisper-local:{desc.Id}",
                Message: $"Whisper model '{desc.Id}' ({desc.DisplayName}) is already installed.",
                Data: new
                {
                    modelId = desc.Id,
                    displayName = desc.DisplayName,
                    sizeMb,
                    alreadyInstalled = true,
                    approved = true,
                    autoApproved = prep.AutoApply
                });
        }

        string requestId = "confirm-" + Guid.NewGuid().ToString("N")[..12];
        string title = L.T("AiConfirmModelDownloadTitle");
        string description = L.T("AiConfirmModelDownloadDesc", desc.DisplayName);
        string detail = $"{desc.FileName} · {sizeMb:0} MB → {subtitleService.ModelManager.ModelsDirectory}";
        bool cardPosted = false;

        // Review mode (!autoApply): require explicit user approval in the AI Copilot chat panel first.
        if (!prep.AutoApply)
        {
            var approvalTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingAiConfirmations[requestId] = approvalTcs;
            try
            {
                if (!string.IsNullOrEmpty(prep.TurnId))
                {
                    cardPosted = true;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (closing) return;
                        aiWebHost?.Post("confirmation.request", new AiActionConfirmationPayload(
                            RequestId: requestId,
                            TurnId: prep.TurnId,
                            Kind: "whisper_model_download",
                            Title: title,
                            Description: description,
                            Detail: detail,
                            ConfirmLabel: L.T("AiConfirmApproveDownload"),
                            RejectLabel: L.T("AiConfirmReject"),
                            Status: "pending"));
                    });
                }

                bool approved;
                try
                {
                    approved = await approvalTcs.Task.WaitAsync(prep.LinkedCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (cardPosted)
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (closing) return;
                            aiWebHost?.Post("confirmation.updated", new AiActionConfirmationPayload(
                                RequestId: requestId,
                                TurnId: prep.TurnId,
                                Kind: "whisper_model_download",
                                Title: title,
                                Description: description,
                                Detail: detail,
                                Status: "rejected",
                                StatusMessage: L.T("AiConfirmModelRejectedStatus")));
                        });
                    }
                    return new AiLocalCallbackActionResult(
                        Ok: false,
                        Error: "Model download confirmation was cancelled.",
                        Data: new { rejected = true, cancelled = true, modelId = desc.Id });
                }

                if (!approved)
                {
                    if (cardPosted)
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (closing) return;
                            aiWebHost?.Post("confirmation.updated", new AiActionConfirmationPayload(
                                RequestId: requestId,
                                TurnId: prep.TurnId,
                                Kind: "whisper_model_download",
                                Title: title,
                                Description: description,
                                Detail: detail,
                                Status: "rejected",
                                StatusMessage: L.T("AiConfirmModelRejectedStatus")));
                        });
                    }
                    return new AiLocalCallbackActionResult(
                        Ok: false,
                        Error: $"User declined downloading local Whisper model '{desc.Id}' ({desc.DisplayName}). Do not retry downloading unless the user explicitly asks.",
                        Data: new { rejected = true, modelId = desc.Id });
                }
            }
            finally
            {
                pendingAiConfirmations.TryRemove(requestId, out _);
            }
        }

        // Approved (or Auto mode): download with live progress in both AI Copilot chat and Subtitles panel.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closing) return;
            SetSubtitleBusy(true, L.T("SubModelDownloading", 0), 0);
            if (!string.IsNullOrEmpty(prep.TurnId))
            {
                string msgType = cardPosted ? "confirmation.updated" : "confirmation.request";
                aiWebHost?.Post(msgType, new AiActionConfirmationPayload(
                    RequestId: requestId,
                    TurnId: prep.TurnId,
                    Kind: "whisper_model_download",
                    Title: title,
                    Description: description,
                    Detail: detail,
                    Status: "downloading",
                    ProgressPercent: 0,
                    StatusMessage: L.T("AiConfirmModelDownloadingStatus", 0)));
                cardPosted = true;
            }
        });

        double lastReportedPct = -10.0;
        string lastReportedStage = "";
        try
        {
            var downloadProgress = new Progress<WhisperDownloadProgress>(p =>
            {
                double pct = Math.Clamp(p.Percent, 0, 100);
                bool stageChanged = !string.Equals(p.Status, lastReportedStage, StringComparison.Ordinal);
                if (!stageChanged && pct - lastReportedPct < 2.0 && pct < 99.5)
                    return;
                lastReportedPct = pct;
                lastReportedStage = p.Status;

                string statusText = p.Status == "verifying"
                    ? L.T("AiConfirmModelVerifyingStatus")
                    : L.T("AiConfirmModelDownloadingStatus", pct);

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (closing) return;
                    subProgressBar.IsIndeterminate = false;
                    subProgressBar.Value = pct;
                    subProgressLabel.Text = L.T("SubModelDownloading", pct);
                    if (!string.IsNullOrEmpty(prep.TurnId))
                    {
                        aiWebHost?.Post("confirmation.updated", new AiActionConfirmationPayload(
                            RequestId: requestId,
                            TurnId: prep.TurnId,
                            Kind: "whisper_model_download",
                            Title: title,
                            Description: description,
                            Detail: detail,
                            Status: "downloading",
                            ProgressPercent: Math.Round(pct, 1),
                            StatusMessage: statusText));
                    }
                });
            });

            await Task.Run(
                () => subtitleService.ModelManager.EnsureModelAsync(desc.Id, downloadProgress, prep.LinkedCt),
                prep.LinkedCt).ConfigureAwait(false);

            DispatcherQueue.TryEnqueue(() =>
            {
                if (closing) return;
                RefreshLocalModelStatus();
                if (!string.IsNullOrEmpty(prep.TurnId))
                {
                    aiWebHost?.Post("confirmation.updated", new AiActionConfirmationPayload(
                        RequestId: requestId,
                        TurnId: prep.TurnId,
                        Kind: "whisper_model_download",
                        Title: title,
                        Description: description,
                        Detail: detail,
                        Status: "completed",
                        ProgressPercent: 100,
                        StatusMessage: L.T("AiConfirmModelInstalledStatus")));
                }
            });

            return new AiLocalCallbackActionResult(
                Ok: true,
                EngineUsed: $"whisper-local:{desc.Id}",
                Message: $"Downloaded and verified Whisper model '{desc.Id}' ({desc.DisplayName}).",
                Data: new
                {
                    modelId = desc.Id,
                    displayName = desc.DisplayName,
                    sizeMb,
                    alreadyInstalled = false,
                    approved = true,
                    autoApproved = prep.AutoApply
                });
        }
        catch (OperationCanceledException)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (closing) return;
                if (!string.IsNullOrEmpty(prep.TurnId))
                {
                    aiWebHost?.Post("confirmation.updated", new AiActionConfirmationPayload(
                        RequestId: requestId,
                        TurnId: prep.TurnId,
                        Kind: "whisper_model_download",
                        Title: title,
                        Description: description,
                        Detail: detail,
                        Status: "rejected",
                        StatusMessage: L.T("AiConfirmModelRejectedStatus")));
                }
            });
            return new AiLocalCallbackActionResult(false, Error: "Whisper model download was cancelled.");
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (closing) return;
                if (!string.IsNullOrEmpty(prep.TurnId))
                {
                    aiWebHost?.Post("confirmation.updated", new AiActionConfirmationPayload(
                        RequestId: requestId,
                        TurnId: prep.TurnId,
                        Kind: "whisper_model_download",
                        Title: title,
                        Description: description,
                        Detail: detail,
                        Status: "failed",
                        StatusMessage: ex.Message));
                }
            });
            return new AiLocalCallbackActionResult(false, Error: ex.Message);
        }
        finally
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!closing)
                    SetSubtitleBusy(false);
            });
        }
    }

    private async Task<AiLocalCallbackActionResult> HandleExternalMcpGenerateSubtitlesAsync(
        JsonElement parameters,
        CancellationToken ct)
    {
        var prepareTcs = new TaskCompletionSource<(
            bool Ok,
            string? Error,
            object? ErrorData,
            ProjectSnapshot? Snapshot,
            string? ProjectDir,
            string Engine,
            string ModelId,
            bool AutoApply,
            string TurnId,
            bool NeedsModelDownload,
            SubtitleStyleSettings BaseStyle,
            CancellationToken LinkedCt)>(TaskCreationOptions.RunContinuationsAsynchronously);

        bool enqueuedPrepare = DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (closing || editor.IsExporting)
                {
                    prepareTcs.TrySetResult((false, "Editor is currently busy or closing.", null, null, null, "", "", false, "", false, SubtitleStyleSettings.ClassicOutline(), CancellationToken.None));
                    return;
                }
                if (subtitleBusy)
                {
                    prepareTcs.TrySetResult((false, "Subtitle transcription is already running.", null, null, null, "", "", false, "", false, SubtitleStyleSettings.ClassicOutline(), CancellationToken.None));
                    return;
                }

                var (engine, modelId) = SelectedAsrEngine();
                bool needsDownload = false;
                if (string.Equals(engine, "whisper-local", StringComparison.OrdinalIgnoreCase) &&
                    !subtitleService.ModelManager.IsModelInstalled(modelId))
                {
                    // Prefer any already-downloaded local model so AI Copilot runs without unnecessary waiting.
                    var installedAlt = subtitleService.ModelManager
                        .GetCatalog()
                        .FirstOrDefault(m => subtitleService.ModelManager.IsModelInstalled(m.Id));
                    if (installedAlt is not null)
                    {
                        modelId = installedAlt.Id;
                    }
                    else if (!aiAutoApply)
                    {
                        // Review mode: require the Agent to call frametrail_download_whisper_model so the user confirms first.
                        var desc = subtitleService.ModelManager.GetDescriptor(modelId);
                        double sizeMb = Math.Round(desc.ExpectedBytes / (1024.0 * 1024.0), 1);
                        prepareTcs.TrySetResult((
                            false,
                            $"Local Whisper speech-to-text model '{desc.Id}' ({desc.DisplayName}, {sizeMb:0} MB) is not installed yet. Call frametrail_download_whisper_model(model_id='{desc.Id}') (or '{WhisperModelManager.SmallModelId}' ~181 MB) to request user confirmation and download the model, then call frametrail_generate_subtitles again.",
                            new
                            {
                                missingLocalModel = true,
                                recommendedModelId = desc.Id,
                                fastModelId = WhisperModelManager.SmallModelId,
                                requiresApproval = true
                            },
                            null,
                            null,
                            engine,
                            modelId,
                            aiAutoApply,
                            activeAiTurnId ?? "",
                            true,
                            SubtitleStyleSettings.ClassicOutline(),
                            CancellationToken.None));
                        return;
                    }
                    else
                    {
                        needsDownload = true;
                    }
                }

                subtitleAsrCts?.Dispose();
                subtitleAsrCts = CancellationTokenSource.CreateLinkedTokenSource(
                    ct,
                    aiTurnCts?.Token ?? CancellationToken.None);
                SetSubtitleBusy(true, L.T("SubGenerating", 0), 0);

                prepareTcs.TrySetResult((
                    true,
                    null,
                    null,
                    editor.Snapshot,
                    editor.DirectoryPath,
                    engine,
                    modelId,
                    aiAutoApply,
                    activeAiTurnId ?? "",
                    needsDownload,
                    CurrentSubtitleStyle,
                    subtitleAsrCts.Token));
            }
            catch (Exception ex)
            {
                prepareTcs.TrySetResult((false, ex.Message, null, null, null, "", "", false, "", false, SubtitleStyleSettings.ClassicOutline(), CancellationToken.None));
            }
        });

        if (!enqueuedPrepare)
            return new AiLocalCallbackActionResult(false, Error: "Editor dispatcher unavailable.");

        var prep = await prepareTcs.Task.ConfigureAwait(false);
        if (!prep.Ok || prep.Snapshot is null || string.IsNullOrWhiteSpace(prep.ProjectDir))
            return new AiLocalCallbackActionResult(false, Error: prep.Error ?? "Unable to start subtitle generation.", Data: prep.ErrorData);

        try
        {
            var snapshot = prep.Snapshot;

            // Parse optional 1-based clip_indices and validate against current timeline segments.
            List<string>? clipIds = null;
            if (parameters.TryGetProperty("clip_indices", out var clipsEl) && clipsEl.ValueKind == JsonValueKind.Array)
            {
                var rawIndices = new List<int>();
                foreach (var item in clipsEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int idx))
                        rawIndices.Add(idx);
                }

                if (rawIndices.Count > 0)
                {
                    if (snapshot.Segments.Count == 0)
                        return new AiLocalCallbackActionResult(false, Error: "Project has no timeline clips to select by clip_indices.");

                    clipIds = new List<string>();
                    var seen = new HashSet<int>();
                    foreach (int idx in rawIndices)
                    {
                        if (idx < 1 || idx > snapshot.Segments.Count)
                        {
                            return new AiLocalCallbackActionResult(
                                false,
                                Error: $"clip_indices contains {idx}, but the timeline only has {snapshot.Segments.Count} clip(s) (1-based: 1..{snapshot.Segments.Count}).");
                        }
                        if (seen.Add(idx))
                            clipIds.Add(snapshot.Segments[idx - 1].Id);
                    }
                }
            }

            double? startSec = parameters.TryGetProperty("start_sec", out var sEl) && sEl.ValueKind == JsonValueKind.Number
                ? sEl.GetDouble()
                : null;
            double? endSec = parameters.TryGetProperty("end_sec", out var eEl) && eEl.ValueKind == JsonValueKind.Number
                ? eEl.GetDouble()
                : null;

            string language = parameters.TryGetProperty("language", out var langEl) &&
                              langEl.ValueKind == JsonValueKind.String &&
                              !string.IsNullOrWhiteSpace(langEl.GetString())
                ? langEl.GetString()!.Trim()
                : "auto";

            int maxChars = parameters.TryGetProperty("max_chars_per_line", out var mcEl) &&
                           mcEl.ValueKind == JsonValueKind.Number &&
                           mcEl.TryGetInt32(out int parsedMc)
                ? Math.Clamp(parsedMc, 6, 80)
                : prep.BaseStyle.MaxCharsPerLine;

            string? vocabularyPrompt = parameters.TryGetProperty("vocabulary_prompt", out var vpEl) &&
                                       vpEl.ValueKind == JsonValueKind.String &&
                                       !string.IsNullOrWhiteSpace(vpEl.GetString())
                ? vpEl.GetString()!.Trim()
                : null;

            bool forceRefresh = parameters.TryGetProperty("force_refresh", out var frEl) &&
                                frEl.ValueKind == JsonValueKind.True;

            string? stylePreset = parameters.TryGetProperty("style_preset", out var spEl) &&
                                  spEl.ValueKind == JsonValueKind.String &&
                                  !string.IsNullOrWhiteSpace(spEl.GetString())
                ? spEl.GetString()!.Trim()
                : null;

            double? positionY = parameters.TryGetProperty("position_y", out var pyEl) &&
                                pyEl.ValueKind == JsonValueKind.Number
                ? pyEl.GetDouble()
                : null;

            SubtitleStyleSettings? styleOverride = null;
            if (stylePreset is not null || positionY.HasValue)
            {
                var baseStyle = stylePreset is not null
                    ? SubtitleStyleSettings.ForPreset(stylePreset)
                    : prep.BaseStyle;
                styleOverride = baseStyle with
                {
                    PositionY = positionY.HasValue
                        ? Math.Clamp(positionY.Value, 0.05, 0.95)
                        : baseStyle.PositionY,
                    MaxCharsPerLine = maxChars
                };
            }

            var request = new SubtitleTranscriptionRequest(
                Engine: prep.Engine,
                ModelId: prep.ModelId,
                Language: language,
                TrackFilter: "all",
                ClipIds: clipIds,
                VocabularyPrompt: vocabularyPrompt,
                MaxCharsPerLine: maxChars,
                ForceRefresh: forceRefresh,
                StartSeconds: startSec,
                EndSeconds: endSec,
                SubtitleStyle: styleOverride);

            string? autoDownloadCardId = null;
            string autoDownloadTitle = "";
            string autoDownloadDesc = "";
            string autoDownloadDetail = "";
            bool autoDownloadCompleted = false;
            double lastAutoDlPct = -10.0;
            if (prep.NeedsModelDownload && !string.IsNullOrEmpty(prep.TurnId))
            {
                var dlDesc = subtitleService.ModelManager.GetDescriptor(prep.ModelId);
                double dlSizeMb = Math.Round(dlDesc.ExpectedBytes / (1024.0 * 1024.0), 1);
                autoDownloadCardId = "autodl-" + Guid.NewGuid().ToString("N")[..12];
                autoDownloadTitle = L.T("AiConfirmModelDownloadTitle");
                autoDownloadDesc = L.T("AiConfirmModelDownloadDesc", dlDesc.DisplayName);
                autoDownloadDetail = $"{dlDesc.FileName} · {dlSizeMb:0} MB → {subtitleService.ModelManager.ModelsDirectory}";
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (closing) return;
                    aiWebHost?.Post("confirmation.request", new AiActionConfirmationPayload(
                        RequestId: autoDownloadCardId,
                        TurnId: prep.TurnId,
                        Kind: "whisper_model_download",
                        Title: autoDownloadTitle,
                        Description: autoDownloadDesc,
                        Detail: autoDownloadDetail,
                        Status: "downloading",
                        ProgressPercent: 0,
                        StatusMessage: L.T("AiConfirmModelDownloadingStatus", 0)));
                });
            }

            var progress = new Progress<SubtitleTranscriptionProgress>(p =>
            {
                if (autoDownloadCardId is not null)
                {
                    if (p.Stage == "downloading")
                    {
                        // TranscribeProjectAsync scales download percent into [10..35]
                        double dlPct = Math.Clamp((p.Percent - 10.0) / 0.25, 0, 100);
                        if (dlPct - lastAutoDlPct >= 2.0 || dlPct >= 99.5)
                        {
                            lastAutoDlPct = dlPct;
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (closing) return;
                                aiWebHost?.Post("confirmation.updated", new AiActionConfirmationPayload(
                                    RequestId: autoDownloadCardId,
                                    TurnId: prep.TurnId,
                                    Kind: "whisper_model_download",
                                    Title: autoDownloadTitle,
                                    Description: autoDownloadDesc,
                                    Detail: autoDownloadDetail,
                                    Status: "downloading",
                                    ProgressPercent: Math.Round(dlPct, 1),
                                    StatusMessage: L.T("AiConfirmModelDownloadingStatus", dlPct)));
                            });
                        }
                    }
                    else if (!autoDownloadCompleted && p.Stage is "transcribing" or "segmenting" or "done")
                    {
                        autoDownloadCompleted = true;
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (closing) return;
                            RefreshLocalModelStatus();
                            aiWebHost?.Post("confirmation.updated", new AiActionConfirmationPayload(
                                RequestId: autoDownloadCardId,
                                TurnId: prep.TurnId,
                                Kind: "whisper_model_download",
                                Title: autoDownloadTitle,
                                Description: autoDownloadDesc,
                                Detail: autoDownloadDetail,
                                Status: "completed",
                                ProgressPercent: 100,
                                StatusMessage: L.T("AiConfirmModelInstalledStatus")));
                        });
                    }
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (closing) return;
                    subProgressBar.IsIndeterminate = false;
                    subProgressBar.Value = Math.Clamp(p.Percent, 0, 100);
                    subProgressLabel.Text = p.Stage == "downloading"
                        ? L.T("SubModelDownloading", p.Percent)
                        : L.T("SubGenerating", p.Percent);
                });
            });

            var result = await Task.Run(
                () => subtitleService.TranscribeProjectAsync(prep.ProjectDir, snapshot, request, progress, prep.LinkedCt),
                prep.LinkedCt).ConfigureAwait(false);

            if (result.Cues.Count == 0)
                return new AiLocalCallbackActionResult(false, Error: "No spoken speech was detected in the requested range.");

            var applyTcs = new TaskCompletionSource<AiLocalCallbackActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool enqueuedApply = DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    if (closing || editor.IsExporting)
                    {
                        applyTcs.TrySetResult(new AiLocalCallbackActionResult(false, Error: "Editor is currently busy or closing."));
                        return;
                    }

                    await PerformAsync(() => editor.ExecuteAsync(result.ReplaceCommand));
                    if (result.Cues.Count > 0)
                    {
                        selectedSubtitleId = result.Cues[0].Id;
                        selectedSubtitleIds.Clear();
                        selectedSubtitleIds.Add(selectedSubtitleId);
                        timeline.SelectVisual(selectedSubtitleId);
                    }
                    RefreshSubtitleInspector();
                    UpdateAiContextPills();

                    applyTcs.TrySetResult(new AiLocalCallbackActionResult(
                        Ok: true,
                        Revision: editor.Snapshot.Revision,
                        CueCount: result.Cues.Count,
                        EngineUsed: result.EngineUsed,
                        Message: $"Generated {result.Cues.Count} subtitle cue(s) via {result.EngineUsed}."));
                }
                catch (Exception ex)
                {
                    applyTcs.TrySetResult(new AiLocalCallbackActionResult(false, Error: ex.Message));
                }
            });

            if (!enqueuedApply)
                return new AiLocalCallbackActionResult(false, Error: "Editor dispatcher unavailable.");

            return await applyTcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new AiLocalCallbackActionResult(false, Error: "Subtitle transcription was cancelled.");
        }
        catch (Exception ex)
        {
            return new AiLocalCallbackActionResult(false, Error: ex.Message);
        }
        finally
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!closing)
                    SetSubtitleBusy(false);
            });
        }
    }

    private async Task<SpeechPolishResult> RunHostLocalSpeechPolishAsync(
        EditorSession session,
        SpeechPolishOptions baseOptions,
        Action<AiStreamEvent>? onEvent,
        CancellationToken ct)
    {
        var service = speechPolishService ?? new AiSpeechPolishService(subtitleService);
        var configTcs = new TaskCompletionSource<(string Engine, string ModelId, bool HasLocalModel)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (DispatcherQueue.TryEnqueue(() =>
        {
            var (engine, modelId) = SelectedAsrEngine();
            bool hasLocal = false;
            if (string.Equals(engine, "whisper-local", StringComparison.OrdinalIgnoreCase))
            {
                if (subtitleService.ModelManager.IsModelInstalled(modelId))
                {
                    hasLocal = true;
                }
                else
                {
                    var installedAlt = subtitleService.ModelManager
                        .GetCatalog()
                        .FirstOrDefault(m => subtitleService.ModelManager.IsModelInstalled(m.Id));
                    if (installedAlt is not null)
                    {
                        modelId = installedAlt.Id;
                        hasLocal = true;
                    }
                }
            }
            configTcs.TrySetResult((engine, modelId, hasLocal));
        }))
        {
            var (engine, modelId, hasLocal) = await configTcs.Task.ConfigureAwait(false);
            bool skipUninstalledLocal = string.Equals(engine, "whisper-local", StringComparison.OrdinalIgnoreCase) && !hasLocal;
            var resolved = baseOptions with
            {
                Engine = engine,
                ModelId = modelId,
                RemoveFillers = baseOptions.RemoveFillers && !skipUninstalledLocal,
                RemoveRepeats = baseOptions.RemoveRepeats && !skipUninstalledLocal
            };
            var progress = new Progress<SubtitleTranscriptionProgress>(p =>
                onEvent?.Invoke(new AiStreamEvent(AiStreamEventKind.Thinking, $"{p.Message} ({p.Percent:0}%)")));

            var result = await service.AnalyzeAndBuildCommandAsync(
                session.DirectoryPath, session.Snapshot, resolved, progress, ct).ConfigureAwait(false);

            if (skipUninstalledLocal && baseOptions.RemoveFillers)
            {
                var desc = subtitleService.ModelManager.GetDescriptor(modelId);
                return result with
                {
                    Warning = $"本地 Whisper 模型 '{desc.DisplayName}' 尚未下载，本轮仅执行波形静音停顿清理。"
                };
            }
            return result;
        }

        return await service.AnalyzeAndBuildCommandAsync(
            session.DirectoryPath, session.Snapshot, baseOptions, null, ct).ConfigureAwait(false);
    }

    private async Task<AiLocalCallbackActionResult> HandleExternalMcpOptimizeSpeechFlowAsync(
        JsonElement parameters,
        CancellationToken ct)
    {
        var prepareTcs = new TaskCompletionSource<(
            bool Ok,
            string? Error,
            object? ErrorData,
            ProjectSnapshot? Snapshot,
            ResolvedTimeline? Timeline,
            string? ProjectDir,
            string Engine,
            string ModelId,
            bool AutoApply,
            string TurnId,
            bool NeedsModelDownload,
            CancellationToken LinkedCt)>(TaskCreationOptions.RunContinuationsAsynchronously);

        bool removePauses = !parameters.TryGetProperty("remove_pauses", out var rpEl) || rpEl.ValueKind != JsonValueKind.False;
        bool removeFillers = !parameters.TryGetProperty("remove_fillers", out var rfEl) || rfEl.ValueKind != JsonValueKind.False;
        bool removeRepeats = !parameters.TryGetProperty("remove_repeats", out var rrEl) || rrEl.ValueKind != JsonValueKind.False;
        bool applyToHost = !parameters.TryGetProperty("apply", out var apEl) || apEl.ValueKind != JsonValueKind.False;
        string intensityRaw = parameters.TryGetProperty("intensity", out var intEl) && intEl.ValueKind == JsonValueKind.String
            ? intEl.GetString() ?? "standard"
            : "standard";
        var intensity = SpeechPolishOptions.ParseIntensity(intensityRaw);
        bool needsAsr = removeFillers || (removeRepeats && intensity != SpeechPolishIntensity.Gentle);

        bool enqueuedPrepare = DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (closing || editor.IsExporting)
                {
                    prepareTcs.TrySetResult((false, "Editor is currently busy or closing.", null, null, null, null, "", "", false, "", false, CancellationToken.None));
                    return;
                }
                if (subtitleBusy)
                {
                    prepareTcs.TrySetResult((false, "Another speech or subtitle task is already running.", null, null, null, null, "", "", false, "", false, CancellationToken.None));
                    return;
                }

                var (engine, modelId) = SelectedAsrEngine();
                bool needsDownload = false;
                if (needsAsr &&
                    string.Equals(engine, "whisper-local", StringComparison.OrdinalIgnoreCase) &&
                    !subtitleService.ModelManager.IsModelInstalled(modelId))
                {
                    var installedAlt = subtitleService.ModelManager
                        .GetCatalog()
                        .FirstOrDefault(m => subtitleService.ModelManager.IsModelInstalled(m.Id));
                    if (installedAlt is not null)
                    {
                        modelId = installedAlt.Id;
                    }
                    else if (!aiAutoApply)
                    {
                        var desc = subtitleService.ModelManager.GetDescriptor(modelId);
                        double sizeMb = Math.Round(desc.ExpectedBytes / (1024.0 * 1024.0), 1);
                        prepareTcs.TrySetResult((
                            false,
                            $"Local Whisper speech-to-text model '{desc.Id}' ({desc.DisplayName}, {sizeMb:0} MB) is not installed yet. Call frametrail_download_whisper_model(model_id='{desc.Id}') (or '{WhisperModelManager.SmallModelId}' ~181 MB) to request user confirmation and download the model, then call frametrail_optimize_speech_flow again.",
                            new
                            {
                                missingLocalModel = true,
                                recommendedModelId = desc.Id,
                                fastModelId = WhisperModelManager.SmallModelId,
                                requiresApproval = true
                            },
                            null,
                            null,
                            null,
                            engine,
                            modelId,
                            aiAutoApply,
                            activeAiTurnId ?? "",
                            true,
                            CancellationToken.None));
                        return;
                    }
                    else
                    {
                        needsDownload = true;
                    }
                }

                subtitleAsrCts?.Dispose();
                subtitleAsrCts = CancellationTokenSource.CreateLinkedTokenSource(
                    ct,
                    aiTurnCts?.Token ?? CancellationToken.None);
                SetSubtitleBusy(true, L.T("SubGenerating", 0), 0);

                prepareTcs.TrySetResult((
                    true,
                    null,
                    null,
                    editor.Snapshot,
                    editor.Timeline,
                    editor.DirectoryPath,
                    engine,
                    modelId,
                    aiAutoApply,
                    activeAiTurnId ?? "",
                    needsDownload,
                    subtitleAsrCts.Token));
            }
            catch (Exception ex)
            {
                prepareTcs.TrySetResult((false, ex.Message, null, null, null, null, "", "", false, "", false, CancellationToken.None));
            }
        });

        if (!enqueuedPrepare)
            return new AiLocalCallbackActionResult(false, Error: "Editor dispatcher unavailable.");

        var prep = await prepareTcs.Task.ConfigureAwait(false);
        if (!prep.Ok || prep.Snapshot is null || prep.Timeline is null || string.IsNullOrWhiteSpace(prep.ProjectDir))
            return new AiLocalCallbackActionResult(false, Error: prep.Error ?? "Unable to prepare speech polish.", Data: prep.ErrorData);

        try
        {
            var snapshot = prep.Snapshot;
            IReadOnlyList<string>? clipIds = null;
            if (parameters.TryGetProperty("clip_indices", out var ciEl) && ciEl.ValueKind == JsonValueKind.Array)
            {
                var resolvedIds = new List<string>();
                foreach (var item in ciEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out int idx1))
                        continue;
                    if (idx1 >= 1 && idx1 <= snapshot.Segments.Count)
                        resolvedIds.Add(snapshot.Segments[idx1 - 1].Id);
                }
                if (resolvedIds.Count > 0)
                    clipIds = resolvedIds.Distinct(StringComparer.Ordinal).ToArray();
            }

            double? startSec = parameters.TryGetProperty("start_sec", out var sEl) && sEl.ValueKind == JsonValueKind.Number
                ? sEl.GetDouble()
                : null;
            double? endSec = parameters.TryGetProperty("end_sec", out var eEl) && eEl.ValueKind == JsonValueKind.Number
                ? eEl.GetDouble()
                : null;
            double? minPauseSec = parameters.TryGetProperty("min_pause_sec", out var mpEl) && mpEl.ValueKind == JsonValueKind.Number
                ? mpEl.GetDouble()
                : null;
            double? pausePaddingSec = parameters.TryGetProperty("pause_padding_sec", out var ppEl) && ppEl.ValueKind == JsonValueKind.Number
                ? ppEl.GetDouble()
                : null;
            double thresholdDb = parameters.TryGetProperty("threshold_db", out var thEl) && thEl.ValueKind == JsonValueKind.Number
                ? thEl.GetDouble()
                : -38.0;
            string language = parameters.TryGetProperty("language", out var langEl) && langEl.ValueKind == JsonValueKind.String
                ? (langEl.GetString() ?? "auto")
                : "auto";
            bool forceRefresh = parameters.TryGetProperty("force_refresh", out var frEl) && frEl.ValueKind == JsonValueKind.True;

            var options = new SpeechPolishOptions(
                Intensity: intensity,
                RemovePauses: removePauses,
                RemoveFillers: removeFillers,
                RemoveRepeats: removeRepeats,
                MinPauseSeconds: minPauseSec,
                PausePaddingSeconds: pausePaddingSec,
                SilenceThresholdDb: thresholdDb,
                Language: language,
                Engine: prep.Engine,
                ModelId: prep.ModelId,
                ClipIds: clipIds,
                StartSeconds: startSec,
                EndSeconds: endSec,
                ForceRefresh: forceRefresh);

            var progress = new Progress<SubtitleTranscriptionProgress>(p =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (closing) return;
                    subProgressBar.IsIndeterminate = false;
                    subProgressBar.Value = Math.Clamp(p.Percent, 0, 100);
                    subProgressLabel.Text = p.Stage == "downloading"
                        ? L.T("SubModelDownloading", p.Percent)
                        : L.T("SubGenerating", p.Percent);
                });
            });

            var service = speechPolishService ?? new AiSpeechPolishService(subtitleService);
            var result = await Task.Run(
                () => service.AnalyzeAndBuildCommandAsync(prep.ProjectDir, snapshot, options, progress, prep.LinkedCt),
                prep.LinkedCt).ConfigureAwait(false);

            var cutSummaries = result.Cuts.Select(c => new
            {
                startSec = c.StartSeconds,
                endSec = c.EndSeconds,
                durationSec = c.DurationSeconds,
                reason = c.Reason,
                label = c.Label
            }).ToArray();

            if (result.Command is null || result.Cuts.Count == 0)
            {
                return new AiLocalCallbackActionResult(
                    Ok: true,
                    CueCount: 0,
                    EngineUsed: result.EngineUsed,
                    Message: "No removable pauses, filler words, or stutters were detected.",
                    Data: new
                    {
                        cutCount = 0,
                        pauseCount = 0,
                        fillerCount = 0,
                        repeatCount = 0,
                        totalRemovedSeconds = 0.0,
                        usedAsr = result.UsedAsr,
                        warning = result.Warning,
                        cuts = cutSummaries
                    });
            }

            if (!applyToHost)
            {
                return new AiLocalCallbackActionResult(
                    Ok: true,
                    CueCount: result.Cuts.Count,
                    EngineUsed: result.EngineUsed,
                    Message: $"Planned {result.Cuts.Count} speech cut(s) removing {result.TotalRemovedSeconds:0.1}s.",
                    Data: new
                    {
                        command = result.Command,
                        cutCount = result.Cuts.Count,
                        pauseCount = result.PauseCount,
                        fillerCount = result.FillerCount,
                        repeatCount = result.RepeatCount,
                        totalRemovedSeconds = result.TotalRemovedSeconds,
                        usedAsr = result.UsedAsr,
                        warning = result.Warning,
                        cuts = cutSummaries
                    });
            }

            var applyTcs = new TaskCompletionSource<AiLocalCallbackActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool enqueuedApply = DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    if (closing || editor.IsExporting)
                    {
                        applyTcs.TrySetResult(new AiLocalCallbackActionResult(false, Error: "Editor is currently busy or closing."));
                        return;
                    }

                    var changes = AiAgentCommandBuilder.DescribeProposedChanges(result.Command, editor.Snapshot, editor.Timeline);
                    var itemDtos = changes
                        .Select(c => new AiProposedChangeDto(c.Badge, c.Label, c.Detail, c.SeekSeconds, c.TimeLabel))
                        .ToArray();
                    string turnId = !string.IsNullOrEmpty(prep.TurnId) ? prep.TurnId : ("mcp-" + Guid.NewGuid().ToString("N")[..8]);
                    aiTurnChangeItems[turnId] = itemDtos;

                    if (aiAutoApply)
                    {
                        await PerformAsync(() => editor.ExecuteAsync(result.Command));
                        aiWebHost?.Post("turn.changes", new AiTurnChangesPayload(
                            TurnId: turnId,
                            State: "applied",
                            Revision: editor.Snapshot.Revision,
                            Items: itemDtos));
                        UpdateAiContextPills();

                        applyTcs.TrySetResult(new AiLocalCallbackActionResult(
                            Ok: true,
                            Revision: editor.Snapshot.Revision,
                            CueCount: result.Cuts.Count,
                            EngineUsed: result.EngineUsed,
                            Message: $"Applied {result.Cuts.Count} speech polish cut(s) removing {result.TotalRemovedSeconds:0.1}s.",
                            Data: new
                            {
                                stagedPreview = false,
                                cutCount = result.Cuts.Count,
                                pauseCount = result.PauseCount,
                                fillerCount = result.FillerCount,
                                repeatCount = result.RepeatCount,
                                totalRemovedSeconds = result.TotalRemovedSeconds,
                                usedAsr = result.UsedAsr,
                                warning = result.Warning,
                                cuts = cutSummaries
                            }));
                    }
                    else
                    {
                        await StagePreviewAndPublishCardAsync(turnId, result.Command, itemDtos);
                        applyTcs.TrySetResult(new AiLocalCallbackActionResult(
                            Ok: true,
                            CueCount: result.Cuts.Count,
                            EngineUsed: result.EngineUsed,
                            Message: $"Staged preview of {result.Cuts.Count} speech polish cut(s) removing {result.TotalRemovedSeconds:0.1}s for user review.",
                            Data: new
                            {
                                stagedPreview = true,
                                cutCount = result.Cuts.Count,
                                pauseCount = result.PauseCount,
                                fillerCount = result.FillerCount,
                                repeatCount = result.RepeatCount,
                                totalRemovedSeconds = result.TotalRemovedSeconds,
                                usedAsr = result.UsedAsr,
                                warning = result.Warning,
                                cuts = cutSummaries
                            }));
                    }
                }
                catch (Exception ex)
                {
                    applyTcs.TrySetResult(new AiLocalCallbackActionResult(false, Error: ex.Message));
                }
            });

            if (!enqueuedApply)
                return new AiLocalCallbackActionResult(false, Error: "Editor dispatcher unavailable.");

            return await applyTcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new AiLocalCallbackActionResult(false, Error: "Speech polish optimization was cancelled.");
        }
        catch (Exception ex)
        {
            return new AiLocalCallbackActionResult(false, Error: ex.Message);
        }
        finally
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!closing)
                    SetSubtitleBusy(false);
            });
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

                case "confirmation.respond":
                    if (AiCopilotProtocol.DeserializePayload<AiConfirmationRespondPayload>(payload) is { } confirmMsg &&
                        !string.IsNullOrWhiteSpace(confirmMsg.RequestId) &&
                        pendingAiConfirmations.TryRemove(confirmMsg.RequestId, out var pendingTcs))
                    {
                        pendingTcs.TrySetResult(confirmMsg.Approved);
                    }
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
            AiProviderKind.DeepSeekApi => "deepseek-flash",
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

        string candidateModel = !string.IsNullOrWhiteSpace(modelOverride)
            ? modelOverride.Trim()
            : (settings.ProviderModels.TryGetValue(key, out var savedModel) && !string.IsNullOrWhiteSpace(savedModel) ? savedModel : defaultModel);
        if (provider == AiProviderKind.DeepSeekApi &&
            (candidateModel.Equals("deepseek-chat", StringComparison.OrdinalIgnoreCase) ||
             candidateModel.Equals("deepseek-reasoner", StringComparison.OrdinalIgnoreCase)))
        {
            candidateModel = "deepseek-flash";
        }
        aiModelName = candidateModel;
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
        activeAiTurnId = turnId;
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
            activeAiTurnId = null;
            CancelAllPendingAiConfirmations();
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
