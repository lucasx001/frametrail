using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Workspace;
using Xunit;

namespace FrameTrail.Workspace.Tests;

public sealed class AiSubtitleTranscriptionServiceTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FrameTrail-SubAsr-" + Guid.NewGuid().ToString("N"));

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

    [Fact]
    public void SegmentCuesSplitsLongCjkAndLatinTextWhilePreservingRelativeWordOffsets()
    {
        var words = new[]
        {
            new TranscribedWord("今天", 1.00, 1.30),
            new TranscribedWord("我们", 1.30, 1.60),
            new TranscribedWord("演示", 1.60, 1.90),
            new TranscribedWord("FrameTrail", 1.90, 2.50),
            new TranscribedWord("字幕功能。", 2.50, 3.10),
            // Pause > 0.45s triggers split even before punctuation
            new TranscribedWord("Second", 3.80, 4.10),
            new TranscribedWord("sentence", 4.10, 4.50),
            new TranscribedWord("starts", 4.50, 4.80),
            new TranscribedWord("here!", 4.80, 5.20)
        };

        var segments = new[]
        {
            new TranscribedSegment(1.0, 5.2, "今天我们演示 FrameTrail 字幕功能。Second sentence starts here!", "microphone", words)
        };

        var cues = AiSubtitleTranscriptionService.SegmentCues(segments, maxCharsPerLine: 18, maxSourceDurationTicks: 100_000_000L);
        Assert.Equal(2, cues.Count);

        var cue1Params = SubtitleCueParameters.FromEffect(cues[0]);
        Assert.Contains("FrameTrail", cue1Params.Text);
        Assert.EndsWith("。", cue1Params.Text);
        Assert.Equal(10_000_000L, cues[0].SourceStart.Ticks);
        Assert.NotNull(cue1Params.Words);
        Assert.Equal(5, cue1Params.Words!.Count);
        Assert.Equal(0.0, cue1Params.Words[0].StartOffset, 3);
        Assert.InRange(cue1Params.Words[3].StartOffset, 0.89, 0.91);

        var cue2Params = SubtitleCueParameters.FromEffect(cues[1]);
        Assert.Equal("Second sentence starts here!", cue2Params.Text);
        Assert.Equal(38_000_000L, cues[1].SourceStart.Ticks);
        Assert.NotNull(cue2Params.Words);
        Assert.Equal(0.0, cue2Params.Words![0].StartOffset, 3);
        Assert.True(cue2Params.Words[^1].EndOffset <= cues[1].SourceDuration.Ticks / 1e7 + 0.001);
    }

    [Fact]
    public void ParseCloudWhisperVerboseJsonExtractsSegmentsWordsAndAppliesOffset()
    {
        const string verboseJson = """
        {
          "task": "transcribe",
          "language": "chinese",
          "duration": 4.2,
          "text": "你好世界。Welcome back.",
          "words": [
            { "word": "你好", "start": 0.2, "end": 0.7 },
            { "word": "世界。", "start": 0.7, "end": 1.3 },
            { "word": "Welcome", "start": 2.0, "end": 2.6 },
            { "word": "back.", "start": 2.6, "end": 3.1 }
          ],
          "segments": [
            { "id": 0, "start": 0.2, "end": 1.3, "text": "你好世界。" },
            { "id": 1, "start": 2.0, "end": 3.1, "text": "Welcome back." }
          ]
        }
        """;

        var segments = AiSubtitleTranscriptionService.ParseCloudWhisperVerboseJson(verboseJson, offsetSeconds: 10.0, trackKind: "microphone");
        Assert.Equal(2, segments.Count);

        Assert.Equal(10.2, segments[0].StartSeconds, 3);
        Assert.Equal(11.3, segments[0].EndSeconds, 3);
        Assert.Equal(2, segments[0].Words!.Count);
        Assert.Equal(10.2, segments[0].Words![0].StartSeconds, 3);

        Assert.Equal(12.0, segments[1].StartSeconds, 3);
        Assert.Equal("Welcome back.", segments[1].Text);
        Assert.Equal(2, segments[1].Words!.Count);
    }

    [Fact]
    public async Task TranscribeProjectAsync_CloudByokModeExtractsWavCallsEndpointCachesTranscriptAndBuildsReplaceCommand()
    {
        string projectDir = Path.Combine(root, "cloud-proj");
        Directory.CreateDirectory(Path.Combine(projectDir, "media"));
        string wavPath = Path.Combine(projectDir, "media", "mic.wav");
        WriteToneWav(wavPath, sampleRate: 48000, channels: 2, durationSec: 4.0);

        var credStore = new AiCredentialsStore(Path.Combine(root, "ai-settings.json"));
        credStore.SetApiKey("groq", "gsk_test_secret_key");

        string? capturedEndpoint = null;
        string? capturedAuth = null;
        string? capturedBody = null;

        var handler = new StubHttpMessageHandler(async req =>
        {
            capturedEndpoint = req.RequestUri?.ToString();
            capturedAuth = req.Headers.Authorization?.ToString();
            capturedBody = await req.Content!.ReadAsStringAsync();

            const string respJson = """
            {
              "text": "测试云端字幕识别。",
              "words": [
                { "word": "测试", "start": 0.1, "end": 0.5 },
                { "word": "云端", "start": 0.5, "end": 1.0 },
                { "word": "字幕识别。", "start": 1.0, "end": 1.8 }
              ],
              "segments": [
                { "id": 0, "start": 0.1, "end": 1.8, "text": "测试云端字幕识别。" }
              ]
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respJson, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var multimodal = new AiMultimodalService();
        var service = new AiSubtitleTranscriptionService(
            modelManager: new WhisperModelManager(Path.Combine(root, "models")),
            credentialsStore: credStore,
            multimodalService: multimodal,
            httpClient: http,
            audioExtractor: AiSubtitleTranscriptionService.ExtractWavToPcm16kManaged);

        var snapshot = new ProjectSnapshot
        {
            ProjectId = "cloud-asr",
            Revision = 1,
            SourceDuration = MediaTime.FromTicks(40_000_000L),
            Media =
            [
                new MediaAsset
                {
                    Id = "mic-1",
                    Kind = MediaKind.Microphone,
                    Path = "media/mic.wav",
                    StartOffset = MediaTime.FromTicks(0),
                    Duration = MediaTime.FromTicks(40_000_000L),
                    SampleRate = 48000,
                    Channels = 2
                }
            ],
            Segments =
            [
                new RecordingSegment { Id = "clip-1", SourceStart = MediaTime.FromTicks(0), SourceDuration = MediaTime.FromTicks(20_000_000L) },
                new RecordingSegment { Id = "clip-2", SourceStart = MediaTime.FromTicks(20_000_000L), SourceDuration = MediaTime.FromTicks(20_000_000L) }
            ]
        };

        var request = new SubtitleTranscriptionRequest(
            Engine: "groq-whisper",
            Language: "zh",
            TrackFilter: "microphone",
            ClipIds: ["clip-2"],
            VocabularyPrompt: "FrameTrail",
            MaxCharsPerLine: 18);

        var result = await service.TranscribeProjectAsync(projectDir, snapshot, request);

        Assert.Equal("https://api.groq.com/openai/v1/audio/transcriptions", capturedEndpoint);
        Assert.Equal("Bearer gsk_test_secret_key", capturedAuth);
        Assert.Contains("whisper-large-v3-turbo", capturedBody);
        Assert.Contains("FrameTrail", capturedBody);
        Assert.Equal("groq-whisper:whisper-large-v3-turbo", result.EngineUsed);

        var cue = Assert.Single(result.Cues);
        // Clip 2 starts at 2.0s; word starts at +0.1s => 2.1s = 21_000_000 ticks!
        Assert.Equal(21_000_000L, cue.SourceStart.Ticks);
        Assert.Equal("测试云端字幕识别。", SubtitleCueParameters.FromEffect(cue).Text);

        // Verify replace command carries scoped rangeStart/rangeEnd
        var cmdJson = JsonSerializer.SerializeToElement(result.ReplaceCommand, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Equal("subtitlesReplace", cmdJson.GetProperty("op").GetString());
        Assert.Equal(20_000_000L, cmdJson.GetProperty("rangeStart").GetInt64());
        Assert.Equal(40_000_000L, cmdJson.GetProperty("rangeEnd").GetInt64());

        // Verify transcript cache was written
        var cached = await multimodal.LoadTranscriptCacheAsync(projectDir);
        Assert.Single(cached);
        Assert.Equal("测试云端字幕识别。", cached[0].Text);
    }

    [Fact]
    public async Task WhisperModelManager_DownloadsWithMirrorFallbackVerifiesSha256AndRejectsCorruptPayload()
    {
        byte[] payload = Encoding.UTF8.GetBytes("mock-ggml-whisper-binary-payload-v1");
        string validSha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        var customCatalog = new[]
        {
            new WhisperModelDescriptor(
                Id: "test-small",
                DisplayName: "Test Small",
                FileName: "ggml-test-small.bin",
                ExpectedBytes: payload.Length,
                Sha256: validSha256,
                PrimaryUrl: "https://primary.invalid/model.bin",
                MirrorUrl: "https://mirror.valid/model.bin")
        };

        var requestedUrls = new List<string>();
        var handler = new StubHttpMessageHandler(req =>
        {
            string url = req.RequestUri!.ToString();
            requestedUrls.Add(url);
            if (url.Contains("primary.invalid"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
        });

        using var http = new HttpClient(handler);
        string modelsDir = Path.Combine(root, "whisper-models");
        var manager = new WhisperModelManager(modelsDir, http, customCatalog);

        Assert.False(manager.IsModelInstalled("test-small"));

        var progressEvents = new List<WhisperDownloadProgress>();
        var syncProgress = new ImmediateProgress<WhisperDownloadProgress>(p => progressEvents.Add(p));
        string installedPath = await manager.EnsureModelAsync("test-small", syncProgress);

        Assert.Equal(2, requestedUrls.Count);
        Assert.True(File.Exists(installedPath));
        Assert.True(manager.IsModelInstalled("test-small"));
        Assert.Equal(validSha256, await WhisperModelManager.ComputeSha256Async(installedPath));
        Assert.Contains(progressEvents, p => p.Status == "installed" && p.Percent == 100.0);

        Assert.True(manager.DeleteModel("test-small"));
        Assert.False(manager.IsModelInstalled("test-small"));

        // Now verify corrupt SHA-256 fails and leaves no installed file.
        var badCatalog = new[]
        {
            customCatalog[0] with { Id = "test-bad", Sha256 = new string('0', 64), PrimaryUrl = "https://mirror.valid/model.bin", MirrorUrl = "" }
        };
        var badManager = new WhisperModelManager(modelsDir, http, badCatalog);
        await Assert.ThrowsAsync<IOException>(() => badManager.EnsureModelAsync("test-bad"));
        Assert.False(badManager.IsModelInstalled("test-bad"));
    }

    [Fact]
    public void WhisperModelManager_DefaultCatalogContainsQuantizedSmallAndLargeTurboPresets()
    {
        var manager = new WhisperModelManager(Path.Combine(root, "default-cat"));
        var catalog = manager.GetCatalog();
        Assert.Contains(catalog, m => m.Id == WhisperModelManager.SmallModelId && m.FileName == "ggml-small-q5_1.bin" && !string.IsNullOrEmpty(m.Sha256));
        Assert.Contains(catalog, m => m.Id == WhisperModelManager.LargeTurboModelId && m.FileName == "ggml-large-v3-turbo-q5_0.bin" && !string.IsNullOrEmpty(m.Sha256));
    }

    [Fact]
    public async Task AiLocalCommandCallbackServer_DispatchesGenerateSubtitlesActionAndReportsStructuredResult()
    {
        string? observedAction = null;
        int[]? observedClips = null;
        await using var server = new AiLocalCommandCallbackServer(
            commandHandler: (_, _) => Task.FromResult<(bool, long?, string?)>((true, 1, null)),
            actionHandler: (action, parameters, _) =>
            {
                observedAction = action;
                if (parameters.TryGetProperty("clip_indices", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    observedClips = arr.EnumerateArray().Select(e => e.GetInt32()).ToArray();

                if (observedClips is not null && observedClips.Contains(3))
                {
                    return Task.FromResult(new AiLocalCallbackActionResult(
                        Ok: false,
                        Error: "clip_indices contains 3, but the timeline only has 2 clip(s) (1-based: 1..2)."));
                }

                return Task.FromResult(new AiLocalCallbackActionResult(
                    Ok: true,
                    Revision: 93,
                    CueCount: 12,
                    EngineUsed: "whisper-local:ggml-large-v3-turbo-q5_0",
                    Message: "Generated 12 subtitle cue(s) via whisper-local:ggml-large-v3-turbo-q5_0."));
            });

        using var client = new HttpClient();

        // 1. Valid action request succeeds with 200 and full metadata
        var okResp = await client.PostAsync(
            server.CallbackUrl,
            new StringContent("""{"action":"generate_subtitles","params":{"clip_indices":[2],"language":"zh"}}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
        using (var okDoc = JsonDocument.Parse(await okResp.Content.ReadAsStringAsync()))
        {
            Assert.Equal("generate_subtitles", observedAction);
            Assert.NotNull(observedClips);
            Assert.Equal([2], observedClips!);
            Assert.True(okDoc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(93, okDoc.RootElement.GetProperty("revision").GetInt64());
            Assert.Equal(12, okDoc.RootElement.GetProperty("cueCount").GetInt32());
            Assert.Equal("whisper-local:ggml-large-v3-turbo-q5_0", okDoc.RootElement.GetProperty("engineUsed").GetString());
        }

        // 2. Out-of-range clip index returns 422 with actionable error message
        var errResp = await client.PostAsync(
            server.CallbackUrl,
            new StringContent("""{"action":"generate_subtitles","params":{"clip_indices":[2,3],"language":"zh"}}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, errResp.StatusCode);
        using (var errDoc = JsonDocument.Parse(await errResp.Content.ReadAsStringAsync()))
        {
            Assert.False(errDoc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains("only has 2 clip(s)", errDoc.RootElement.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task AiLocalCommandCallbackServer_ReturnsNotImplementedWhenNoActionHandlerConfigured()
    {
        await using var server = new AiLocalCommandCallbackServer(
            commandHandler: (_, _) => Task.FromResult<(bool, long?, string?)>((true, 1, null)));

        using var client = new HttpClient();
        var resp = await client.PostAsync(
            server.CallbackUrl,
            new StringContent("""{"action":"generate_subtitles","params":{}}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
    }

    private static void WriteToneWav(string path, int sampleRate, short channels, double durationSec)
    {
        int frames = (int)(sampleRate * durationSec);
        int dataBytes = frames * channels * 2;
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        bw.Write("RIFF"u8);
        bw.Write(36 + dataBytes);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1);
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * 2);
        bw.Write((short)(channels * 2));
        bw.Write((short)16);
        bw.Write("data"u8);
        bw.Write(dataBytes);
        Span<byte> buf = stackalloc byte[2];
        for (int i = 0; i < frames; i++)
        {
            short pcm = (short)(MathF.Sin(2f * MathF.PI * 440f * i / sampleRate) * 16000f);
            BinaryPrimitives.WriteInt16LittleEndian(buf, pcm);
            for (int c = 0; c < channels; c++)
                bw.Write(buf);
        }
    }

    private sealed class ImmediateProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request);
    }
}
