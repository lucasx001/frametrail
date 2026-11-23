using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;
using Whisper.net;

namespace FrameTrail.Workspace;

public sealed record SubtitleTranscriptionRequest(
    string Engine = "whisper-local",
    string ModelId = WhisperModelManager.LargeTurboModelId,
    string Language = "auto",
    string TrackFilter = "all",
    IReadOnlyList<string>? ClipIds = null,
    string? VocabularyPrompt = null,
    int MaxCharsPerLine = 18,
    bool ForceRefresh = false,
    string? ApiKeyOverride = null,
    string? BaseUrlOverride = null,
    double? StartSeconds = null,
    double? EndSeconds = null,
    SubtitleStyleSettings? SubtitleStyle = null);

public sealed record SubtitleTranscriptionProgress(
    string Stage,
    double Percent,
    string Message);

public sealed record SubtitleTranscriptionResult(
    IReadOnlyList<SourceEffect> Cues,
    object ReplaceCommand,
    string EngineUsed,
    double DurationSeconds);

public sealed record TranscribedWord(
    string Text,
    double StartSeconds,
    double EndSeconds);

public sealed record TranscribedSegment(
    double StartSeconds,
    double EndSeconds,
    string Text,
    string TrackKind = "microphone",
    IReadOnlyList<TranscribedWord>? Words = null);

public sealed record AudioTrackEnergyEnvelope(
    string TrackKind,
    long SourceStartTicks,
    double FrameSeconds,
    double[] FrameDb);

public sealed record RawSpeechTranscriptionResult(
    IReadOnlyList<TranscribedSegment> Segments,
    IReadOnlyList<AudioTrackEnergyEnvelope> Envelopes,
    string EngineUsed,
    IReadOnlyList<(long Start, long End)>? TargetRanges);

/// <summary>
/// Dual-engine ASR subtitle transcription service supporting offline Whisper.net (Vulkan GPU / CPU)
/// and cloud BYOK OpenAI / Groq Whisper APIs with smart punctuation segmentation and word-level timing.
/// </summary>
public sealed class AiSubtitleTranscriptionService
{
    public const long TicksPerSecond = 10_000_000L;
    public const int TargetSampleRate = 16_000;
    private const long MaxCloudWavBytes = 23L * 1024 * 1024; // stay safely under 25 MB API limit
    private const double PauseSplitThresholdSeconds = 0.45;
    private const double MinCueDurationSeconds = 0.25;
    private const double MaxCueDurationSeconds = 7.0;

    private static readonly HashSet<char> StrongBreakPunctuation =
    [
        '。', '！', '？', '!', '?', '.', '；', ';'
    ];

    private static readonly HashSet<char> SoftBreakPunctuation =
    [
        '，', '、', ',', '：', ':', '—', '…'
    ];

    private readonly WhisperModelManager modelManager;
    private readonly AiCredentialsStore credentialsStore;
    private readonly AiMultimodalService multimodalService;
    private readonly HttpClient httpClient;
    private readonly Action<string, string, long, long> audioExtractor;

    public WhisperModelManager ModelManager => modelManager;

    public AiSubtitleTranscriptionService(
        WhisperModelManager? modelManager = null,
        AiCredentialsStore? credentialsStore = null,
        AiMultimodalService? multimodalService = null,
        HttpClient? httpClient = null,
        Action<string, string, long, long>? audioExtractor = null)
    {
        this.modelManager = modelManager ?? new WhisperModelManager();
        this.credentialsStore = credentialsStore ?? new AiCredentialsStore();
        this.multimodalService = multimodalService ?? new AiMultimodalService();
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        this.audioExtractor = audioExtractor ?? DefaultExtractAudioPcm16k;
    }

    public async Task<SubtitleTranscriptionResult> TranscribeProjectAsync(
        string projectDirectory,
        ProjectSnapshot snapshot,
        SubtitleTranscriptionRequest request,
        IProgress<SubtitleTranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
            throw new ArgumentException("Project directory is required.", nameof(projectDirectory));
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);

        long totalSourceTicks = VisualResources.Ticks(snapshot.SourceDuration);
        int maxChars = Math.Clamp(request.MaxCharsPerLine, 6, 80);
        string normalizedEngine = NormalizeEngine(request.Engine);

        progress?.Report(new SubtitleTranscriptionProgress("extracting", 5, "Preparing audio tracks..."));

        var targetRanges = ResolveTargetSourceRanges(snapshot, request.ClipIds, request.StartSeconds, request.EndSeconds);
        var audioSlices = ResolveAudioSlices(projectDirectory, snapshot, request.TrackFilter, targetRanges);
        if (audioSlices.Count == 0)
            throw new InvalidOperationException("No audio track found in this project for subtitle transcription.");

        string tempRoot = Path.Combine(Path.GetTempPath(), "FrameTrail-ASR-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var extractedSlices = new List<(AudioSlice Slice, string WavPath)>();
            for (int i = 0; i < audioSlices.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slice = audioSlices[i];
                string wavPath = Path.Combine(tempRoot, $"slice-{i:000}.wav");
                await Task.Run(() => audioExtractor(slice.FullPath, wavPath, slice.LocalStartTicks, slice.DurationTicks), cancellationToken)
                    .ConfigureAwait(false);
                if (File.Exists(wavPath) && new FileInfo(wavPath).Length > 44)
                    extractedSlices.Add((slice, wavPath));
            }

            if (extractedSlices.Count == 0)
                throw new InvalidOperationException("Audio extraction produced no readable audio samples.");

            var rawSegments = new List<TranscribedSegment>();
            string engineUsed = normalizedEngine;

            if (normalizedEngine == "whisper-local")
            {
                string modelId = string.IsNullOrWhiteSpace(request.ModelId)
                    ? WhisperModelManager.LargeTurboModelId
                    : request.ModelId.Trim();

                var downloadProgress = new Progress<WhisperDownloadProgress>(p =>
                {
                    double scaled = 10.0 + p.Percent * 0.25;
                    progress?.Report(new SubtitleTranscriptionProgress("downloading", scaled, $"Downloading {modelId} ({p.Percent:0}%)..."));
                });

                string modelPath = await modelManager.EnsureModelAsync(modelId, downloadProgress, cancellationToken)
                    .ConfigureAwait(false);
                engineUsed = $"whisper-local:{modelId}";

                for (int i = 0; i < extractedSlices.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (slice, wavPath) = extractedSlices[i];
                    if (IsEntirelySilent(wavPath))
                        continue;

                    double sliceBasePct = 35.0 + (55.0 * i / extractedSlices.Count);
                    double sliceSpanPct = 55.0 / extractedSlices.Count;
                    var sliceSegments = await TranscribeLocalWavAsync(
                        wavPath,
                        modelPath,
                        request.Language,
                        request.VocabularyPrompt,
                        slice.TrackKind,
                        slice.SourceStartTicks / (double)TicksPerSecond,
                        pct => progress?.Report(new SubtitleTranscriptionProgress(
                            "transcribing",
                            sliceBasePct + sliceSpanPct * (pct / 100.0),
                            $"Transcribing locally ({i + 1}/{extractedSlices.Count})...")),
                        cancellationToken).ConfigureAwait(false);
                    rawSegments.AddRange(sliceSegments);
                }
            }
            else
            {
                var (apiKey, endpoint, cloudModel) = ResolveCloudConfig(normalizedEngine, request);
                engineUsed = $"{normalizedEngine}:{cloudModel}";

                for (int i = 0; i < extractedSlices.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (slice, wavPath) = extractedSlices[i];
                    if (IsEntirelySilent(wavPath))
                        continue;

                    double pct = 35.0 + (55.0 * i / extractedSlices.Count);
                    progress?.Report(new SubtitleTranscriptionProgress(
                        "transcribing",
                        pct,
                        $"Transcribing via {normalizedEngine} ({i + 1}/{extractedSlices.Count})..."));

                    var sliceSegments = await TranscribeCloudWavAsync(
                        wavPath,
                        apiKey,
                        endpoint,
                        cloudModel,
                        request.Language,
                        request.VocabularyPrompt,
                        slice.TrackKind,
                        slice.SourceStartTicks / (double)TicksPerSecond,
                        tempRoot,
                        cancellationToken).ConfigureAwait(false);
                    rawSegments.AddRange(sliceSegments);
                }
            }

            rawSegments.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));

            progress?.Report(new SubtitleTranscriptionProgress("segmenting", 92, "Formatting subtitle cues..."));
            var cues = SegmentCues(rawSegments, maxChars, totalSourceTicks);

            // Persist transcript cache for AI Copilot cross-modal search.
            var transcriptSegments = cues
                .Select(c =>
                {
                    long st = VisualResources.Ticks(c.SourceStart);
                    long dur = VisualResources.Ticks(c.SourceDuration);
                    var p = SubtitleCueParameters.FromEffect(c);
                    return new TranscriptSegment(st, st + dur, p.Text, ResolvePrimaryTrackLabel(request.TrackFilter));
                })
                .ToArray();
            if (transcriptSegments.Length > 0)
            {
                await multimodalService.SaveTranscriptCacheAsync(projectDirectory, transcriptSegments, cancellationToken)
                    .ConfigureAwait(false);
            }

            object replaceCommand = BuildScopedReplaceCommand(snapshot, targetRanges, cues, request.SubtitleStyle);
            double durationSec = totalSourceTicks / (double)TicksPerSecond;

            progress?.Report(new SubtitleTranscriptionProgress("done", 100, $"Generated {cues.Count} subtitle cue(s)."));
            return new SubtitleTranscriptionResult(cues, replaceCommand, engineUsed, durationSec);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    /// <summary>
    /// Extracts 10ms RMS dBFS energy envelopes from project audio/video tracks (supporting both recorded WAVs and imported MP4/MOV clips).
    /// </summary>
    public async Task<(IReadOnlyList<AudioTrackEnergyEnvelope> Envelopes, IReadOnlyList<(long Start, long End)>? TargetRanges)> ExtractAudioEnvelopesAsync(
        string projectDirectory,
        ProjectSnapshot snapshot,
        string trackFilter = "all",
        IReadOnlyList<string>? clipIds = null,
        double? startSeconds = null,
        double? endSeconds = null,
        double frameSeconds = 0.01,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
            throw new ArgumentException("Project directory is required.", nameof(projectDirectory));
        ArgumentNullException.ThrowIfNull(snapshot);

        var targetRanges = ResolveTargetSourceRanges(snapshot, clipIds, startSeconds, endSeconds);
        var audioSlices = ResolveAudioSlices(projectDirectory, snapshot, trackFilter, targetRanges);
        if (audioSlices.Count == 0)
            return (Array.Empty<AudioTrackEnergyEnvelope>(), targetRanges);

        string tempRoot = Path.Combine(Path.GetTempPath(), "FrameTrail-Env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var envelopes = new List<AudioTrackEnergyEnvelope>();
            for (int i = 0; i < audioSlices.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slice = audioSlices[i];
                string wavPath = Path.Combine(tempRoot, $"env-{i:000}.wav");
                try
                {
                    await Task.Run(() => audioExtractor(slice.FullPath, wavPath, slice.LocalStartTicks, slice.DurationTicks), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch when (!cancellationToken.IsCancellationRequested)
                {
                    // Skip video-only or unreadable slices so voiced clips in the same project still analyze cleanly.
                    continue;
                }
                if (File.Exists(wavPath) && new FileInfo(wavPath).Length > 44)
                {
                    double[] frameDb = ComputePcm16kEnergyEnvelopeDb(wavPath, frameSeconds);
                    if (frameDb.Length > 0)
                        envelopes.Add(new AudioTrackEnergyEnvelope(slice.TrackKind, slice.SourceStartTicks, frameSeconds, frameDb));
                }
            }
            return (envelopes, targetRanges);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    /// <summary>
    /// Runs word-level ASR transcription and computes 10ms PCM energy envelopes in a single extraction pass
    /// for speech expression optimization (pause, filler word, and stutter removal).
    /// </summary>
    public async Task<RawSpeechTranscriptionResult> TranscribeRawWordsAsync(
        string projectDirectory,
        ProjectSnapshot snapshot,
        SubtitleTranscriptionRequest request,
        double envelopeFrameSeconds = 0.01,
        IProgress<SubtitleTranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
            throw new ArgumentException("Project directory is required.", nameof(projectDirectory));
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);

        string normalizedEngine = NormalizeEngine(request.Engine);
        progress?.Report(new SubtitleTranscriptionProgress("extracting", 5, "Preparing audio tracks..."));

        var targetRanges = ResolveTargetSourceRanges(snapshot, request.ClipIds, request.StartSeconds, request.EndSeconds);
        var audioSlices = ResolveAudioSlices(projectDirectory, snapshot, request.TrackFilter, targetRanges);
        if (audioSlices.Count == 0)
            return new RawSpeechTranscriptionResult(Array.Empty<TranscribedSegment>(), Array.Empty<AudioTrackEnergyEnvelope>(), "none", targetRanges);

        string tempRoot = Path.Combine(Path.GetTempPath(), "FrameTrail-Speech-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var extractedSlices = new List<(AudioSlice Slice, string WavPath)>();
            var envelopes = new List<AudioTrackEnergyEnvelope>();
            for (int i = 0; i < audioSlices.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slice = audioSlices[i];
                string wavPath = Path.Combine(tempRoot, $"slice-{i:000}.wav");
                try
                {
                    await Task.Run(() => audioExtractor(slice.FullPath, wavPath, slice.LocalStartTicks, slice.DurationTicks), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch when (!cancellationToken.IsCancellationRequested)
                {
                    // Skip video-only or unreadable slices so voiced clips in the same project still transcribe cleanly.
                    continue;
                }
                if (File.Exists(wavPath) && new FileInfo(wavPath).Length > 44)
                {
                    extractedSlices.Add((slice, wavPath));
                    double[] frameDb = ComputePcm16kEnergyEnvelopeDb(wavPath, envelopeFrameSeconds);
                    if (frameDb.Length > 0)
                        envelopes.Add(new AudioTrackEnergyEnvelope(slice.TrackKind, slice.SourceStartTicks, envelopeFrameSeconds, frameDb));
                }
            }

            if (extractedSlices.Count == 0)
                return new RawSpeechTranscriptionResult(Array.Empty<TranscribedSegment>(), envelopes, "none", targetRanges);

            var rawSegments = new List<TranscribedSegment>();
            string engineUsed = normalizedEngine;

            if (normalizedEngine == "whisper-local")
            {
                string modelId = string.IsNullOrWhiteSpace(request.ModelId)
                    ? WhisperModelManager.LargeTurboModelId
                    : request.ModelId.Trim();

                var downloadProgress = new Progress<WhisperDownloadProgress>(p =>
                {
                    double scaled = 10.0 + p.Percent * 0.25;
                    progress?.Report(new SubtitleTranscriptionProgress("downloading", scaled, $"Downloading {modelId} ({p.Percent:0}%)..."));
                });

                string modelPath = await modelManager.EnsureModelAsync(modelId, downloadProgress, cancellationToken)
                    .ConfigureAwait(false);
                engineUsed = $"whisper-local:{modelId}";

                for (int i = 0; i < extractedSlices.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (slice, wavPath) = extractedSlices[i];
                    if (IsEntirelySilent(wavPath))
                        continue;

                    double sliceBasePct = 35.0 + (55.0 * i / extractedSlices.Count);
                    double sliceSpanPct = 55.0 / extractedSlices.Count;
                    var sliceSegments = await TranscribeLocalWavAsync(
                        wavPath,
                        modelPath,
                        request.Language,
                        request.VocabularyPrompt,
                        slice.TrackKind,
                        slice.SourceStartTicks / (double)TicksPerSecond,
                        pct => progress?.Report(new SubtitleTranscriptionProgress(
                            "transcribing",
                            sliceBasePct + sliceSpanPct * (pct / 100.0),
                            $"Analyzing speech ({i + 1}/{extractedSlices.Count})...")),
                        cancellationToken).ConfigureAwait(false);
                    rawSegments.AddRange(sliceSegments);
                }
            }
            else
            {
                var (apiKey, endpoint, cloudModel) = ResolveCloudConfig(normalizedEngine, request);
                engineUsed = $"{normalizedEngine}:{cloudModel}";

                for (int i = 0; i < extractedSlices.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (slice, wavPath) = extractedSlices[i];
                    if (IsEntirelySilent(wavPath))
                        continue;

                    double pct = 35.0 + (55.0 * i / extractedSlices.Count);
                    progress?.Report(new SubtitleTranscriptionProgress(
                        "transcribing",
                        pct,
                        $"Analyzing speech via {normalizedEngine} ({i + 1}/{extractedSlices.Count})..."));

                    var sliceSegments = await TranscribeCloudWavAsync(
                        wavPath,
                        apiKey,
                        endpoint,
                        cloudModel,
                        request.Language,
                        request.VocabularyPrompt,
                        slice.TrackKind,
                        slice.SourceStartTicks / (double)TicksPerSecond,
                        tempRoot,
                        cancellationToken).ConfigureAwait(false);
                    rawSegments.AddRange(sliceSegments);
                }
            }

            rawSegments.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
            return new RawSpeechTranscriptionResult(rawSegments, envelopes, engineUsed, targetRanges);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    /// <summary>Computes per-frame RMS dBFS values from a 16kHz 16-bit mono PCM WAV file.</summary>
    public static double[] ComputePcm16kEnergyEnvelopeDb(string wavPath, double frameSeconds = 0.01)
    {
        if (!File.Exists(wavPath))
            return Array.Empty<double>();
        byte[] bytes = File.ReadAllBytes(wavPath);
        const int headerSize = 44;
        if (bytes.Length <= headerSize)
            return Array.Empty<double>();

        int totalSamples = (bytes.Length - headerSize) / 2;
        int frameSamples = Math.Max(1, (int)Math.Round(TargetSampleRate * Math.Clamp(frameSeconds, 0.005, 0.1)));
        int frameCount = (totalSamples + frameSamples - 1) / frameSamples;
        var result = new double[frameCount];
        const double inv = 1.0 / 32768.0;

        for (int f = 0; f < frameCount; f++)
        {
            int startSample = f * frameSamples;
            int endSample = Math.Min(totalSamples, startSample + frameSamples);
            double sumSq = 0.0;
            for (int s = startSample; s < endSample; s++)
            {
                short sample = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(headerSize + s * 2, 2));
                double norm = sample * inv;
                sumSq += norm * norm;
            }
            int count = Math.Max(1, endSample - startSample);
            double rms = Math.Sqrt(sumSq / count);
            result[f] = rms <= 1e-6 ? -120.0 : Math.Max(-120.0, 20.0 * Math.Log10(rms));
        }

        return result;
    }

    /// <summary>
    /// Segments raw ASR segments (with optional word-level timestamps) into clean, readable subtitle
    /// <see cref="SourceEffect"/> cues respecting CJK/Latin punctuation, pause gaps, and <paramref name="maxCharsPerLine"/>.
    /// </summary>
    public static IReadOnlyList<SourceEffect> SegmentCues(
        IReadOnlyList<TranscribedSegment> segments,
        int maxCharsPerLine = 18,
        long? maxSourceDurationTicks = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
            return Array.Empty<SourceEffect>();

        int lineLimit = Math.Clamp(maxCharsPerLine, 6, 80);
        var result = new List<SourceEffect>();

        foreach (var segment in segments.OrderBy(s => s.StartSeconds))
        {
            if (string.IsNullOrWhiteSpace(segment.Text))
                continue;

            double segStart = Math.Max(0.0, double.IsFinite(segment.StartSeconds) ? segment.StartSeconds : 0.0);
            double segEnd = double.IsFinite(segment.EndSeconds) ? segment.EndSeconds : segStart + MinCueDurationSeconds;
            if (segEnd <= segStart)
                segEnd = segStart + MinCueDurationSeconds;

            if (segment.Words is { Count: > 0 })
            {
                EmitWordSegmentedCues(segment.Words, segStart, segEnd, lineLimit, maxSourceDurationTicks, result);
            }
            else
            {
                EmitTextSegmentedCues(segment.Text.Trim(), segStart, segEnd, lineLimit, maxSourceDurationTicks, result);
            }
        }

        result.Sort((a, b) => a.SourceStart.Ticks.CompareTo(b.SourceStart.Ticks));
        return result;
    }

    /// <summary>Parses OpenAI / Groq Whisper <c>response_format=verbose_json</c> payload into segments and words.</summary>
    public static IReadOnlyList<TranscribedSegment> ParseCloudWhisperVerboseJson(
        string json,
        double offsetSeconds = 0.0,
        string trackKind = "microphone")
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<TranscribedSegment>();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return Array.Empty<TranscribedSegment>();

        var topLevelWords = new List<TranscribedWord>();
        if (root.TryGetProperty("words", out var wordsEl) && wordsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in wordsEl.EnumerateArray())
            {
                if (TryParseCloudWord(item, offsetSeconds, out var word))
                    topLevelWords.Add(word);
            }
        }

        var segments = new List<TranscribedSegment>();
        if (root.TryGetProperty("segments", out var segsEl) && segsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var seg in segsEl.EnumerateArray())
            {
                if (seg.ValueKind != JsonValueKind.Object) continue;
                string text = seg.TryGetProperty("text", out var tEl) && tEl.ValueKind == JsonValueKind.String
                    ? (tEl.GetString() ?? "").Trim()
                    : "";
                if (string.IsNullOrEmpty(text)) continue;

                double start = ReadJsonDouble(seg, "start", 0.0) + offsetSeconds;
                double end = ReadJsonDouble(seg, "end", start + MinCueDurationSeconds) + offsetSeconds;
                if (end <= start) end = start + MinCueDurationSeconds;

                List<TranscribedWord>? segWords = null;
                if (seg.TryGetProperty("words", out var swEl) && swEl.ValueKind == JsonValueKind.Array)
                {
                    segWords = new List<TranscribedWord>();
                    foreach (var wItem in swEl.EnumerateArray())
                    {
                        if (TryParseCloudWord(wItem, offsetSeconds, out var w))
                            segWords.Add(w);
                    }
                }
                else if (topLevelWords.Count > 0)
                {
                    segWords = topLevelWords
                        .Where(w => w.StartSeconds >= start - 0.08 && w.EndSeconds <= end + 0.08)
                        .ToList();
                }

                segments.Add(new TranscribedSegment(
                    start,
                    end,
                    text,
                    trackKind,
                    segWords is { Count: > 0 } ? segWords : null));
            }
        }
        else if (root.TryGetProperty("text", out var rootText) && rootText.ValueKind == JsonValueKind.String)
        {
            string text = (rootText.GetString() ?? "").Trim();
            if (!string.IsNullOrEmpty(text))
            {
                double duration = ReadJsonDouble(root, "duration", 0.0);
                double start = topLevelWords.Count > 0 ? topLevelWords[0].StartSeconds : offsetSeconds;
                double end = topLevelWords.Count > 0
                    ? topLevelWords[^1].EndSeconds
                    : offsetSeconds + Math.Max(MinCueDurationSeconds, duration);
                segments.Add(new TranscribedSegment(
                    start,
                    Math.Max(end, start + MinCueDurationSeconds),
                    text,
                    trackKind,
                    topLevelWords.Count > 0 ? topLevelWords : null));
            }
        }

        return segments;
    }

    private async Task<IReadOnlyList<TranscribedSegment>> TranscribeLocalWavAsync(
        string wavPath,
        string modelPath,
        string language,
        string? vocabularyPrompt,
        string trackKind,
        double offsetSeconds,
        Action<int>? onProgressPercent,
        CancellationToken cancellationToken)
    {
        using var factory = WhisperFactory.FromPath(modelPath);
        var builder = factory.CreateBuilder()
            .WithTokenTimestamps()
            .WithThreads(Math.Clamp(Environment.ProcessorCount - 1, 1, 8));

        string normLang = NormalizeLanguageCode(language);
        if (string.IsNullOrEmpty(normLang) || normLang == "auto")
            builder.WithLanguageDetection();
        else
            builder.WithLanguage(normLang);

        if (!string.IsNullOrWhiteSpace(vocabularyPrompt))
            builder.WithPrompt(vocabularyPrompt.Trim());

        if (onProgressPercent is not null)
            builder.WithProgressHandler(percent => onProgressPercent(Math.Clamp(percent, 0, 100)));

        await using var processor = builder.Build();
        await using var wavStream = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);

        var segments = new List<TranscribedSegment>();
        await foreach (var seg in processor.ProcessAsync(wavStream, cancellationToken).ConfigureAwait(false))
        {
            string text = (seg.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            double start = offsetSeconds + Math.Max(0.0, seg.Start.TotalSeconds);
            double end = offsetSeconds + Math.Max(seg.Start.TotalSeconds + 0.05, seg.End.TotalSeconds);
            var words = ExtractWordsFromWhisperTokens(seg.Tokens, offsetSeconds, start, end);

            segments.Add(new TranscribedSegment(
                start,
                end,
                text,
                trackKind,
                words.Count > 0 ? words : null));
        }

        return segments;
    }

    private static IReadOnlyList<TranscribedWord> ExtractWordsFromWhisperTokens(
        WhisperToken[]? tokens,
        double offsetSeconds,
        double segmentStart,
        double segmentEnd)
    {
        if (tokens is null || tokens.Length == 0)
            return Array.Empty<TranscribedWord>();

        var words = new List<TranscribedWord>();
        var currentText = new StringBuilder();
        double currentStart = segmentStart;
        double currentEnd = segmentStart;

        void Flush()
        {
            string value = currentText.ToString().Trim();
            currentText.Clear();
            if (string.IsNullOrEmpty(value)) return;
            double s = Math.Clamp(currentStart, segmentStart, segmentEnd);
            double e = Math.Clamp(Math.Max(currentEnd, s + 0.02), s, segmentEnd);
            words.Add(new TranscribedWord(value, s, e));
        }

        foreach (var token in tokens)
        {
            string raw = token.Text ?? "";
            if (string.IsNullOrEmpty(raw))
                continue;
            // Skip Whisper special control tokens like [_BEG_], [_TT_100], <|endoftext|>.
            if ((raw.StartsWith("[_", StringComparison.Ordinal) && raw.EndsWith(']')) ||
                (raw.StartsWith("<|", StringComparison.Ordinal) && raw.EndsWith("|>", StringComparison.Ordinal)))
                continue;

            // WhisperToken Start/End are in 10ms centiseconds (-1 when absent).
            bool hasTs = token.Start >= 0 && token.End >= token.Start;
            double tokStart = hasTs ? offsetSeconds + token.Start * 0.01 : currentEnd;
            double tokEnd = hasTs ? offsetSeconds + token.End * 0.01 : tokStart;

            bool startsNewLatinWord = raw.Length > 0 && char.IsWhiteSpace(raw[0]);
            bool isCjkToken = ContainsCjk(raw);

            if (isCjkToken)
            {
                Flush();
                string trimmed = raw.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    words.Add(new TranscribedWord(
                        trimmed,
                        Math.Clamp(tokStart, segmentStart, segmentEnd),
                        Math.Clamp(Math.Max(tokEnd, tokStart + 0.02), segmentStart, segmentEnd)));
                }
                continue;
            }

            if (startsNewLatinWord && currentText.Length > 0)
                Flush();

            if (currentText.Length == 0)
                currentStart = tokStart;
            currentText.Append(raw);
            currentEnd = Math.Max(currentEnd, tokEnd);
        }

        Flush();
        // If all words collapsed to segmentStart (e.g., model did not emit valid token timestamps),
        // distribute timings proportionally by visual character length across [segmentStart, segmentEnd].
        if (words.Count > 1 && words.All(w => Math.Abs(w.StartSeconds - segmentStart) < 0.001))
        {
            double totalChars = words.Sum(w => Math.Max(1.0, MeasureVisualChars(w.Text)));
            double span = Math.Max(0.05, segmentEnd - segmentStart);
            double cursor = segmentStart;
            for (int i = 0; i < words.Count; i++)
            {
                double portion = span * (Math.Max(1.0, MeasureVisualChars(words[i].Text)) / totalChars);
                double next = i == words.Count - 1 ? segmentEnd : Math.Min(segmentEnd, cursor + portion);
                words[i] = words[i] with { StartSeconds = cursor, EndSeconds = Math.Max(cursor + 0.01, next) };
                cursor = next;
            }
        }
        return words;
    }

    private async Task<IReadOnlyList<TranscribedSegment>> TranscribeCloudWavAsync(
        string wavPath,
        string apiKey,
        string endpoint,
        string model,
        string language,
        string? vocabularyPrompt,
        string trackKind,
        double baseOffsetSeconds,
        string tempRoot,
        CancellationToken cancellationToken)
    {
        var chunkFiles = SplitPcm16kWavIfTooLarge(wavPath, tempRoot);
        var results = new List<TranscribedSegment>();

        foreach (var (chunkPath, chunkOffsetSec) in chunkFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var form = new MultipartFormDataContent();
            await using var fileStream = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            using var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(fileContent, "file", Path.GetFileName(chunkPath));
            form.Add(new StringContent(model), "model");
            form.Add(new StringContent("verbose_json"), "response_format");
            form.Add(new StringContent("word"), "timestamp_granularities[]");
            form.Add(new StringContent("segment"), "timestamp_granularities[]");

            string normLang = NormalizeLanguageCode(language);
            if (!string.IsNullOrEmpty(normLang) && normLang != "auto")
                form.Add(new StringContent(normLang), "language");

            if (!string.IsNullOrWhiteSpace(vocabularyPrompt))
                form.Add(new StringContent(vocabularyPrompt.Trim()), "prompt");

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Cloud Whisper API request failed ({(int)response.StatusCode}): {TruncateText(body, 400)}");
            }

            results.AddRange(ParseCloudWhisperVerboseJson(body, baseOffsetSeconds + chunkOffsetSec, trackKind));
        }

        return results;
    }

    private (string ApiKey, string Endpoint, string Model) ResolveCloudConfig(
        string normalizedEngine,
        SubtitleTranscriptionRequest request)
    {
        bool isGroq = normalizedEngine == "groq-whisper";
        var settings = credentialsStore.Load();

        string? apiKey = !string.IsNullOrWhiteSpace(request.ApiKeyOverride)
            ? request.ApiKeyOverride.Trim()
            : isGroq
                ? credentialsStore.GetApiKey("groq") ?? credentialsStore.GetApiKey("openai") ?? Environment.GetEnvironmentVariable("GROQ_API_KEY")
                : credentialsStore.GetApiKey("openai") ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(isGroq
                ? "Groq API key is required for cloud Whisper transcription. Configure it in AI Settings."
                : "OpenAI API key is required for cloud Whisper transcription. Configure it in AI Settings.");
        }

        string? rawBaseUrl = request.BaseUrlOverride;
        if (string.IsNullOrWhiteSpace(rawBaseUrl))
        {
            string providerKey = isGroq ? "groq" : "openai";
            if (settings.ProviderBaseUrls.TryGetValue(providerKey, out var configuredBase) && !string.IsNullOrWhiteSpace(configuredBase))
                rawBaseUrl = configuredBase;
            else if (!isGroq && !string.IsNullOrWhiteSpace(settings.CustomBaseUrl))
                rawBaseUrl = settings.CustomBaseUrl;
        }

        string defaultEndpoint = isGroq
            ? "https://api.groq.com/openai/v1/audio/transcriptions"
            : "https://api.openai.com/v1/audio/transcriptions";
        string endpoint = NormalizeTranscriptionsEndpoint(rawBaseUrl, defaultEndpoint);

        string defaultModel = isGroq ? "whisper-large-v3-turbo" : "whisper-1";
        string model = !string.IsNullOrWhiteSpace(request.ModelId) &&
                       !request.ModelId.StartsWith("small-", StringComparison.OrdinalIgnoreCase) &&
                       !request.ModelId.StartsWith("large-v3-turbo-q", StringComparison.OrdinalIgnoreCase)
            ? request.ModelId.Trim()
            : defaultModel;

        return (apiKey, endpoint, model);
    }

    private static string NormalizeTranscriptionsEndpoint(string? baseUrl, string defaultEndpoint)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return defaultEndpoint;
        string trimmed = baseUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/audio/transcriptions", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return trimmed + "/audio/transcriptions";
        return trimmed + "/v1/audio/transcriptions";
    }

    private static void EmitWordSegmentedCues(
        IReadOnlyList<TranscribedWord> words,
        double segStart,
        double segEnd,
        int maxCharsPerLine,
        long? maxSourceDurationTicks,
        List<SourceEffect> output)
    {
        var buffer = new List<TranscribedWord>();
        double visualWidth = 0.0;

        void FlushBuffer()
        {
            if (buffer.Count == 0) return;

            double start = Math.Max(0.0, buffer[0].StartSeconds);
            double end = Math.Max(start + 0.05, buffer[^1].EndSeconds);
            string text = JoinWordsCleanly(buffer);
            if (string.IsNullOrWhiteSpace(text))
            {
                buffer.Clear();
                visualWidth = 0.0;
                return;
            }

            var timings = new List<SubtitleWordTiming>(Math.Min(buffer.Count, 256));
            double cueDuration = Math.Max(0.05, end - start);
            foreach (var w in buffer.Take(256))
            {
                string wText = w.Text.Trim();
                if (string.IsNullOrEmpty(wText)) continue;
                double ws = Math.Clamp(w.StartSeconds - start, 0.0, cueDuration);
                double we = Math.Clamp(Math.Max(w.EndSeconds - start, ws), ws, cueDuration);
                timings.Add(new SubtitleWordTiming
                {
                    Text = wText,
                    StartOffset = Math.Round(ws, 4),
                    EndOffset = Math.Round(we, 4)
                });
            }

            AddSubtitleEffect(output, start, end, text, timings.Count > 0 ? timings : null, maxSourceDurationTicks);
            buffer.Clear();
            visualWidth = 0.0;
        }

        for (int i = 0; i < words.Count; i++)
        {
            var word = words[i];
            string cleaned = word.Text.Trim();
            if (string.IsNullOrEmpty(cleaned))
                continue;

            double wStart = Math.Clamp(double.IsFinite(word.StartSeconds) ? word.StartSeconds : segStart, segStart, segEnd);
            double wEnd = Math.Clamp(double.IsFinite(word.EndSeconds) ? word.EndSeconds : wStart + 0.05, wStart, segEnd);
            var normalized = new TranscribedWord(cleaned, wStart, wEnd);

            if (buffer.Count > 0)
            {
                double gap = normalized.StartSeconds - buffer[^1].EndSeconds;
                double span = normalized.EndSeconds - buffer[0].StartSeconds;
                double nextWidth = visualWidth + MeasureVisualChars(cleaned) + (NeedsSpaceBetween(buffer[^1].Text, cleaned) ? 0.5 : 0.0);

                if (gap >= PauseSplitThresholdSeconds ||
                    span > MaxCueDurationSeconds ||
                    nextWidth > maxCharsPerLine ||
                    buffer.Count >= 256)
                {
                    FlushBuffer();
                }
            }

            if (buffer.Count > 0 && NeedsSpaceBetween(buffer[^1].Text, cleaned))
                visualWidth += 0.5;
            buffer.Add(normalized);
            visualWidth += MeasureVisualChars(cleaned);

            char lastChar = cleaned[^1];
            if (StrongBreakPunctuation.Contains(lastChar) ||
                (SoftBreakPunctuation.Contains(lastChar) && visualWidth >= maxCharsPerLine * 0.6))
            {
                FlushBuffer();
            }
        }

        FlushBuffer();
    }

    private static void EmitTextSegmentedCues(
        string text,
        double segStart,
        double segEnd,
        int maxCharsPerLine,
        long? maxSourceDurationTicks,
        List<SourceEffect> output)
    {
        var chunks = SplitTextByPunctuationAndWidth(text, maxCharsPerLine);
        if (chunks.Count == 0) return;

        double totalWeight = chunks.Sum(c => Math.Max(1.0, MeasureVisualChars(c)));
        double totalDuration = Math.Max(MinCueDurationSeconds, segEnd - segStart);
        double cursor = segStart;

        for (int i = 0; i < chunks.Count; i++)
        {
            double weight = Math.Max(1.0, MeasureVisualChars(chunks[i]));
            double chunkDuration = i == chunks.Count - 1
                ? Math.Max(0.05, segEnd - cursor)
                : Math.Max(0.05, totalDuration * (weight / totalWeight));
            double chunkEnd = i == chunks.Count - 1 ? Math.Max(cursor + 0.05, segEnd) : Math.Min(segEnd, cursor + chunkDuration);
            AddSubtitleEffect(output, cursor, chunkEnd, chunks[i], null, maxSourceDurationTicks);
            cursor = chunkEnd;
        }
    }

    private static List<string> SplitTextByPunctuationAndWidth(string text, int maxCharsPerLine)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        double width = 0.0;

        void Flush()
        {
            string value = current.ToString().Trim();
            current.Clear();
            width = 0.0;
            if (!string.IsNullOrEmpty(value))
                chunks.Add(value);
        }

        int i = 0;
        while (i < text.Length)
        {
            // Keep contiguous ASCII alphanumeric words together so English words aren't broken mid-word.
            if (IsAsciiWordChar(text[i]))
            {
                int start = i;
                while (i < text.Length && IsAsciiWordChar(text[i]))
                    i++;
                string token = text[start..i];
                double tokenWidth = MeasureVisualChars(token);
                if (current.Length > 0 && width + tokenWidth > maxCharsPerLine)
                    Flush();
                current.Append(token);
                width += tokenWidth;
                continue;
            }

            char c = text[i++];
            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0 && current[^1] != ' ')
                {
                    current.Append(' ');
                    width += 0.5;
                }
                continue;
            }

            double cw = IsCjkChar(c) ? 1.0 : 0.5;
            if (current.Length > 0 && width + cw > maxCharsPerLine &&
                !StrongBreakPunctuation.Contains(c) && !SoftBreakPunctuation.Contains(c))
            {
                Flush();
            }

            current.Append(c);
            width += cw;

            if (StrongBreakPunctuation.Contains(c) || (SoftBreakPunctuation.Contains(c) && width >= maxCharsPerLine * 0.6))
                Flush();
        }

        Flush();
        return chunks;
    }

    private static void AddSubtitleEffect(
        List<SourceEffect> output,
        double startSec,
        double endSec,
        string text,
        IReadOnlyList<SubtitleWordTiming>? words,
        long? maxSourceDurationTicks)
    {
        string cleaned = TruncateUtf8(text.Trim(), 1024);
        if (string.IsNullOrEmpty(cleaned)) return;

        long startTicks = Math.Max(0L, (long)Math.Round(startSec * TicksPerSecond, MidpointRounding.AwayFromZero));
        long endTicks = Math.Max(startTicks + 500_000L, (long)Math.Round(endSec * TicksPerSecond, MidpointRounding.AwayFromZero));

        if (maxSourceDurationTicks.HasValue && maxSourceDurationTicks.Value > 0)
        {
            if (startTicks >= maxSourceDurationTicks.Value) return;
            endTicks = Math.Min(endTicks, maxSourceDurationTicks.Value);
            if (endTicks <= startTicks) return;
        }

        var parameters = new SubtitleCueParameters
        {
            Text = cleaned,
            Words = words
        };

        output.Add(new SourceEffect
        {
            Id = "sub-" + Guid.NewGuid().ToString("N")[..12],
            Kind = "subtitle",
            SourceStart = MediaTime.FromTicks(startTicks),
            SourceDuration = MediaTime.FromTicks(endTicks - startTicks),
            Parameters = parameters.ToJsonElement()
        });
    }

    private static object BuildScopedReplaceCommand(
        ProjectSnapshot snapshot,
        IReadOnlyList<(long Start, long End)>? targetRanges,
        IReadOnlyList<SourceEffect> generatedCues,
        SubtitleStyleSettings? subtitleStyle = null)
    {
        if (targetRanges is null || targetRanges.Count == 0)
            return AiAgentCommandBuilder.BuildSubtitlesReplaceCommand(generatedCues, subtitleStyle: subtitleStyle);

        if (targetRanges.Count == 1)
        {
            return AiAgentCommandBuilder.BuildSubtitlesReplaceCommand(
                generatedCues,
                rangeStartTicks: targetRanges[0].Start,
                rangeEndTicks: targetRanges[0].End,
                subtitleStyle: subtitleStyle);
        }

        // Multiple disjoint clip ranges: preserve untouched subtitle effects and atomically replace track.
        var combined = new List<SourceEffect>();
        foreach (var existing in snapshot.Effects)
        {
            if (!string.Equals(existing.Kind, "subtitle", StringComparison.Ordinal))
                continue;
            long s = VisualResources.Ticks(existing.SourceStart);
            long e = s + VisualResources.Ticks(existing.SourceDuration);
            bool overlapsTarget = targetRanges.Any(r => s < r.End && e > r.Start);
            if (!overlapsTarget)
                combined.Add(existing);
        }
        combined.AddRange(generatedCues);
        combined.Sort((a, b) => VisualResources.Ticks(a.SourceStart).CompareTo(VisualResources.Ticks(b.SourceStart)));
        return AiAgentCommandBuilder.BuildSubtitlesReplaceCommand(combined, subtitleStyle: subtitleStyle);
    }

    private static IReadOnlyList<(long Start, long End)>? ResolveTargetSourceRanges(
        ProjectSnapshot snapshot,
        IReadOnlyList<string>? clipIds,
        double? startSeconds = null,
        double? endSeconds = null)
    {
        long totalTicks = VisualResources.Ticks(snapshot.SourceDuration);
        var raw = new List<(long Start, long End)>();

        if (clipIds is not null && clipIds.Count > 0)
        {
            var idSet = new HashSet<string>(clipIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal);
            if (idSet.Count > 0)
            {
                foreach (var seg in snapshot.Segments)
                {
                    if (!idSet.Contains(seg.Id)) continue;
                    long s = VisualResources.Ticks(seg.SourceStart);
                    long e = s + VisualResources.Ticks(seg.SourceDuration);
                    if (e > s) raw.Add((s, e));
                }

                if (raw.Count == 0)
                    throw new ArgumentException("None of the requested clip IDs exist in the project.", nameof(clipIds));
            }
        }

        if (startSeconds.HasValue || endSeconds.HasValue)
        {
            long wStart = startSeconds.HasValue
                ? Math.Max(0L, (long)Math.Round(startSeconds.Value * TicksPerSecond))
                : 0L;
            long wEnd = endSeconds.HasValue
                ? (long)Math.Round(endSeconds.Value * TicksPerSecond)
                : (totalTicks > 0 ? totalTicks : wStart + TicksPerSecond * 3600);
            if (totalTicks > 0)
                wEnd = Math.Min(totalTicks, wEnd);
            if (wEnd <= wStart)
                throw new ArgumentException($"Invalid time range [{startSeconds}, {endSeconds}]: end_sec must exceed start_sec.");

            if (raw.Count > 0)
            {
                var clipped = new List<(long Start, long End)>();
                foreach (var (rStart, rEnd) in raw)
                {
                    long ovStart = Math.Max(rStart, wStart);
                    long ovEnd = Math.Min(rEnd, wEnd);
                    if (ovEnd > ovStart)
                        clipped.Add((ovStart, ovEnd));
                }
                if (clipped.Count == 0)
                    throw new ArgumentException("Requested clips do not overlap the specified [start_sec, end_sec] time range.");
                raw = clipped;
            }
            else
            {
                raw.Add((wStart, wEnd));
            }
        }

        if (raw.Count == 0)
            return null;

        raw.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(long Start, long End)>();
        foreach (var range in raw)
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, range.End));
            else
                merged.Add(range);
        }
        return merged;
    }

    private sealed record AudioSlice(string FullPath, string TrackKind, long SourceStartTicks, long LocalStartTicks, long DurationTicks);

    private static List<AudioSlice> ResolveAudioSlices(
        string projectDirectory,
        ProjectSnapshot snapshot,
        string trackFilter,
        IReadOnlyList<(long Start, long End)>? targetRanges)
    {
        string filter = (trackFilter ?? "all").Trim();
        var audioAssets = snapshot.Media
            .Where(m => MatchesTrackFilter(m.Kind, filter))
            .OrderBy(m => VisualResources.Ticks(m.StartOffset))
            .ToList();

        // Fallback: when filter is "all", also include Screen video assets whose time range is not
        // already covered by a dedicated audio track (e.g. imported MP4/MOV clips without separate audio assets).
        if (string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase))
        {
            var dedicatedRanges = audioAssets
                .Select(a => (Start: VisualResources.Ticks(a.StartOffset), End: VisualResources.Ticks(a.StartOffset) + VisualResources.Ticks(a.Duration)))
                .ToList();
            foreach (var screenAsset in snapshot.Media
                .Where(m => m.Kind == MediaKind.Screen)
                .OrderBy(m => VisualResources.Ticks(m.StartOffset)))
            {
                long sStart = VisualResources.Ticks(screenAsset.StartOffset);
                long sEnd = sStart + VisualResources.Ticks(screenAsset.Duration);
                bool covered = dedicatedRanges.Any(r => sStart < r.End && sEnd > r.Start);
                if (!covered)
                    audioAssets.Add(screenAsset);
            }
        }

        var slices = new List<AudioSlice>();
        foreach (var asset in audioAssets)
        {
            string fullPath = Path.GetFullPath(Path.Combine(projectDirectory, asset.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(fullPath))
                continue;

            long assetStart = VisualResources.Ticks(asset.StartOffset);
            long assetDur = VisualResources.Ticks(asset.Duration);
            long assetEnd = assetStart + assetDur;
            string trackKind = asset.Kind switch
            {
                MediaKind.Microphone => "microphone",
                MediaKind.SystemAudio => "systemAudio",
                MediaKind.AndroidAudio => "androidAudio",
                _ => "systemAudio"
            };

            if (targetRanges is null || targetRanges.Count == 0)
            {
                slices.Add(new AudioSlice(fullPath, trackKind, assetStart, 0, assetDur));
            }
            else
            {
                foreach (var (rStart, rEnd) in targetRanges)
                {
                    long ovStart = Math.Max(assetStart, rStart);
                    long ovEnd = Math.Min(assetEnd, rEnd);
                    if (ovEnd <= ovStart) continue;
                    slices.Add(new AudioSlice(fullPath, trackKind, ovStart, ovStart - assetStart, ovEnd - ovStart));
                }
            }
        }

        return slices;
    }

    private static bool MatchesTrackFilter(MediaKind kind, string filter) => filter.ToLowerInvariant() switch
    {
        "microphone" or "mic" => kind == MediaKind.Microphone,
        "systemaudio" or "system" => kind == MediaKind.SystemAudio,
        "androidaudio" or "android" => kind == MediaKind.AndroidAudio,
        _ => kind is MediaKind.Microphone or MediaKind.SystemAudio or MediaKind.AndroidAudio
    };

    private bool IsEntirelySilent(string wavPath)
    {
        try
        {
            long bytes = new FileInfo(wavPath).Length;
            if (bytes <= 44) return true;
            double totalSec = (bytes - 44) / (double)(TargetSampleRate * 2);
            if (totalSec < 0.3) return false;
            var silences = multimodalService.DetectWavSilence(wavPath, minSilenceSeconds: Math.Max(0.2, totalSec * 0.95), thresholdDb: -50.0);
            return silences.Count == 1 && silences[0].DurationSeconds >= totalSec * 0.95;
        }
        catch
        {
            return false;
        }
    }

    private static List<(string Path, double OffsetSeconds)> SplitPcm16kWavIfTooLarge(string wavPath, string tempRoot)
    {
        long length = new FileInfo(wavPath).Length;
        if (length <= MaxCloudWavBytes)
            return [(wavPath, 0.0)];

        byte[] all = File.ReadAllBytes(wavPath);
        const int headerSize = 44;
        int dataBytes = Math.Max(0, all.Length - headerSize);
        int bytesPerSecond = TargetSampleRate * 2;
        int maxChunkDataBytes = (int)((MaxCloudWavBytes - headerSize) / 2 * 2);

        var chunks = new List<(string, double)>();
        int offset = 0;
        int idx = 0;
        while (offset < dataBytes)
        {
            int count = Math.Min(maxChunkDataBytes, dataBytes - offset);
            string chunkPath = Path.Combine(tempRoot, $"cloud-chunk-{idx++:000}.wav");
            WritePcm16kMonoWavBytes(chunkPath, all.AsSpan(headerSize + offset, count));
            chunks.Add((chunkPath, offset / (double)bytesPerSecond));
            offset += count;
        }
        return chunks;
    }

    /// <summary>
    /// Default audio extractor: delegates to native <see cref="MediaSession.ExtractAudioPcm16k"/> (supporting
    /// WAV, MP4, MOV, M4A via Media Foundation) and falls back to managed WAV resampling when native DLLs are unavailable.
    /// </summary>
    public static void DefaultExtractAudioPcm16k(string sourcePath, string outputWavPath, long sourceStartTicks, long sourceDurationTicks)
    {
        try
        {
            MediaSession.ExtractAudioPcm16k(sourcePath, outputWavPath, sourceStartTicks, sourceDurationTicks);
            return;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            if (string.Equals(Path.GetExtension(sourcePath), ".wav", StringComparison.OrdinalIgnoreCase))
            {
                ExtractWavToPcm16kManaged(sourcePath, outputWavPath, sourceStartTicks, sourceDurationTicks);
                return;
            }
            throw;
        }
    }

    /// <summary>Pure managed RIFF/RF64 WAV -> 16 kHz / 16-bit / mono PCM WAV downsampler for fallback and unit testing.</summary>
    public static void ExtractWavToPcm16kManaged(string sourceWavPath, string outputWavPath, long sourceStartTicks = 0, long sourceDurationTicks = -1)
    {
        using var stream = new FileStream(sourceWavPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        Span<byte> riff = stackalloc byte[12];
        if (stream.Read(riff) < 12 || (!riff[0..4].SequenceEqual("RIFF"u8) && !riff[0..4].SequenceEqual("RF64"u8)) || !riff[8..12].SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Not a valid RIFF/RF64 WAV file.");

        ushort formatTag = 0;
        int channels = 0;
        int sampleRate = 0;
        int bitsPerSample = 0;
        long dataOffset = -1;
        long dataSize = 0;

        Span<byte> hdr = stackalloc byte[8];
        while (stream.Read(hdr) == 8)
        {
            string id = Encoding.ASCII.GetString(hdr[0..4]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(hdr[4..8]);
            long pos = stream.Position;
            if (id == "fmt " && size >= 16)
            {
                byte[] fmt = new byte[Math.Min(size, 64u)];
                stream.ReadExactly(fmt);
                formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(0, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2, 2));
                sampleRate = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4, 4)));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14, 2));
                if (formatTag == 0xFFFE && fmt.Length >= 40)
                    formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(24, 2));
            }
            else if (id == "data")
            {
                dataOffset = pos;
                dataSize = Math.Min(size, stream.Length - pos);
                break;
            }
            stream.Position = Math.Min(stream.Length, pos + size + (size & 1));
        }

        if (dataOffset < 0 || channels <= 0 || sampleRate <= 0 || bitsPerSample is not (16 or 32) || formatTag is not (1 or 3))
            throw new NotSupportedException("Unsupported WAV format for managed fallback.");

        int bytesPerSample = bitsPerSample / 8;
        int frameBytes = bytesPerSample * channels;
        long totalFrames = dataSize / frameBytes;

        long startFrame = Math.Clamp((long)Math.Round(Math.Max(0L, sourceStartTicks) * (double)sampleRate / TicksPerSecond), 0L, totalFrames);
        long endFrame = sourceDurationTicks > 0
            ? Math.Clamp(startFrame + (long)Math.Round(sourceDurationTicks * (double)sampleRate / TicksPerSecond), startFrame, totalFrames)
            : totalFrames;
        int sliceFrames = checked((int)Math.Max(0L, endFrame - startFrame));

        byte[] raw = new byte[sliceFrames * frameBytes];
        stream.Position = dataOffset + startFrame * frameBytes;
        int bytesRead = 0;
        while (bytesRead < raw.Length)
        {
            int n = stream.Read(raw, bytesRead, raw.Length - bytesRead);
            if (n <= 0) break;
            bytesRead += n;
        }
        int actualFrames = bytesRead / frameBytes;

        float[] mono = new float[actualFrames];
        for (int f = 0; f < actualFrames; f++)
        {
            double sum = 0.0;
            for (int c = 0; c < channels; c++)
            {
                int idx = (f * channels + c) * bytesPerSample;
                sum += formatTag == 3 && bitsPerSample == 32
                    ? BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(idx, 4))
                    : bitsPerSample == 16
                        ? BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(idx, 2)) / 32768.0
                        : BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(idx, 4)) / 2147483648.0;
            }
            mono[f] = (float)(sum / channels);
        }

        int outFrames = actualFrames == 0 ? 0 : Math.Max(1, (int)Math.Round(actualFrames * (double)TargetSampleRate / sampleRate));
        byte[] pcmBytes = new byte[outFrames * 2];
        double ratio = (double)sampleRate / TargetSampleRate;
        for (int i = 0; i < outFrames; i++)
        {
            double srcPos = i * ratio;
            int idx0 = Math.Clamp((int)srcPos, 0, Math.Max(0, actualFrames - 1));
            int idx1 = Math.Min(idx0 + 1, Math.Max(0, actualFrames - 1));
            double frac = srcPos - idx0;
            double sample = actualFrames > 0 ? mono[idx0] * (1.0 - frac) + mono[idx1] * frac : 0.0;
            short pcm = (short)Math.Clamp((int)Math.Round(sample * 32767.0), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcmBytes.AsSpan(i * 2, 2), pcm);
        }

        WritePcm16kMonoWavBytes(outputWavPath, pcmBytes);
    }

    private static void WritePcm16kMonoWavBytes(string path, ReadOnlySpan<byte> pcm16Data)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);
        bw.Write("RIFF"u8);
        bw.Write(36 + pcm16Data.Length);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(TargetSampleRate);
        bw.Write(TargetSampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write("data"u8);
        bw.Write(pcm16Data.Length);
        bw.Write(pcm16Data);
    }

    private static bool TryParseCloudWord(JsonElement item, double offsetSeconds, out TranscribedWord word)
    {
        word = default!;
        if (item.ValueKind != JsonValueKind.Object) return false;
        string text = item.TryGetProperty("word", out var wEl) && wEl.ValueKind == JsonValueKind.String
            ? (wEl.GetString() ?? "").Trim()
            : item.TryGetProperty("text", out var tEl) && tEl.ValueKind == JsonValueKind.String
                ? (tEl.GetString() ?? "").Trim()
                : "";
        if (string.IsNullOrEmpty(text)) return false;

        double start = ReadJsonDouble(item, "start", 0.0) + offsetSeconds;
        double end = ReadJsonDouble(item, "end", start + 0.05) + offsetSeconds;
        if (end < start) end = start;
        word = new TranscribedWord(text, start, end);
        return true;
    }

    private static double ReadJsonDouble(JsonElement obj, string name, double fallback)
    {
        if (obj.TryGetProperty(name, out var prop) && prop.TryGetDouble(out var v) && double.IsFinite(v))
            return v;
        return fallback;
    }

    private static string NormalizeEngine(string? engine) => (engine ?? "whisper-local").Trim().ToLowerInvariant() switch
    {
        "openai" or "openai-whisper" => "openai-whisper",
        "groq" or "groq-whisper" => "groq-whisper",
        _ => "whisper-local"
    };

    private static string NormalizeLanguageCode(string? language)
    {
        string trimmed = (language ?? "auto").Trim().ToLowerInvariant();
        if (trimmed is "" or "auto" or "detect") return "auto";
        if (trimmed.StartsWith("zh", StringComparison.Ordinal)) return "zh";
        if (trimmed.StartsWith("en", StringComparison.Ordinal)) return "en";
        if (trimmed.StartsWith("ja", StringComparison.Ordinal)) return "ja";
        return trimmed;
    }

    private static string ResolvePrimaryTrackLabel(string? trackFilter) => (trackFilter ?? "all").Trim().ToLowerInvariant() switch
    {
        "systemaudio" or "system" => "systemAudio",
        "androidaudio" or "android" => "androidAudio",
        _ => "microphone"
    };

    private static string JoinWordsCleanly(List<TranscribedWord> words)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < words.Count; i++)
        {
            string w = words[i].Text.Trim();
            if (string.IsNullOrEmpty(w)) continue;
            if (sb.Length > 0 && NeedsSpaceBetween(sb.ToString(), w))
                sb.Append(' ');
            sb.Append(w);
        }
        return sb.ToString().Trim();
    }

    private static bool NeedsSpaceBetween(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0) return false;
        char lc = left[^1];
        char rc = right[0];
        if (char.IsWhiteSpace(lc) || char.IsWhiteSpace(rc)) return false;
        if (StrongBreakPunctuation.Contains(rc) || SoftBreakPunctuation.Contains(rc)) return false;
        if (IsCjkChar(lc) || IsCjkChar(rc)) return false;
        return true;
    }

    private static double MeasureVisualChars(string value)
    {
        double sum = 0.0;
        foreach (char c in value)
            sum += IsCjkChar(c) ? 1.0 : 0.5;
        return sum;
    }

    private static bool IsAsciiWordChar(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c is '\'' or '-' or '_';

    private static bool ContainsCjk(string text)
    {
        foreach (char c in text)
            if (IsCjkChar(c)) return true;
        return false;
    }

    private static bool IsCjkChar(char c) =>
        (c >= '\u4E00' && c <= '\u9FFF') ||
        (c >= '\u3400' && c <= '\u4DBF') ||
        (c >= '\u3040' && c <= '\u30FF') ||
        (c >= '\uAC00' && c <= '\uD7AF');

    private static string TruncateUtf8(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;
        var sb = new StringBuilder();
        int used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            int len = rune.Utf8SequenceLength;
            if (used + len > maxBytes) break;
            sb.Append(rune.ToString());
            used += len;
        }
        return sb.ToString();
    }

    private static string TruncateText(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars] + "...";

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Ignore temp cleanup errors.
        }
    }
}
