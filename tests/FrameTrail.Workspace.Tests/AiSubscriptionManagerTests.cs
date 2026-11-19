using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Interop;
using Xunit;

namespace FrameTrail.Workspace.Tests;

public sealed class AiSubscriptionManagerTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "FrameTrail-AiSub-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
        return Task.CompletedTask;
    }

    private async Task<ProjectWorkspace> CreateWorkspaceAsync(string folderName = "ai-edit.frametrailproject")
    {
        var projectDir = Path.Combine(root, folderName);
        var workspace = ProjectWorkspace.Create(projectDir, "ai-editor-project", EngineSession.ValidateProject, (_, _) => { });
        await File.WriteAllBytesAsync(workspace.Resolve("media/screen.mp4"), PersistenceTests.FinalizedMp4Header);
        var snapshot = new ProjectSnapshot
        {
            ProjectId = "ai-editor-project",
            Revision = 1,
            SourceDuration = MediaTime.FromTicks(30_000_000),
            Media = new[]
            {
                new MediaAsset
                {
                    Id = "screen",
                    Kind = MediaKind.Screen,
                    Path = "media/screen.mp4",
                    StartOffset = MediaTime.FromTicks(0),
                    Duration = MediaTime.FromTicks(30_000_000),
                    Width = 640,
                    Height = 360
                }
            },
            Segments = new[]
            {
                new RecordingSegment
                {
                    Id = "clip",
                    SourceStart = MediaTime.FromTicks(0),
                    SourceDuration = MediaTime.FromTicks(30_000_000)
                }
            }
        };
        await workspace.SaveAsync(snapshot, workspace.SessionEpoch);
        return workspace;
    }

    private static object BuildZoomCommand(string effectId, double scale) => new
    {
        op = "effectUpsert",
        effectId,
        effect = new
        {
            id = effectId,
            kind = "zoom",
            sourceStart = new { ticks = 2_000_000L, timescale = 10_000_000 },
            sourceDuration = new { ticks = 5_000_000L, timescale = 10_000_000 },
            parameters = new
            {
                scale,
                focusX = 0.5,
                focusY = 0.5,
                enter = 0.7,
                exit = 0.7,
                easing = "cubic",
                origin = "manual"
            }
        }
    };

    [Fact]
    public void CredentialsStoreEncryptsApiKeysOnDiskAndRoundTripsAcrossInstances()
    {
        var settingsPath = Path.Combine(root, "config", "ai-settings.json");
        var store = new AiCredentialsStore(settingsPath);

        const string openAiKey = "sk-test-openai-secret-987654321";
        const string anthropicKey = "sk-ant-api03-secret-123456789";

        store.SetApiKey("OpenAI", openAiKey);
        store.SetApiKey("anthropic", anthropicKey);

        Assert.True(File.Exists(settingsPath));
        var rawDiskJson = File.ReadAllText(settingsPath, Encoding.UTF8);
        Assert.DoesNotContain(openAiKey, rawDiskJson, StringComparison.Ordinal);
        Assert.DoesNotContain(anthropicKey, rawDiskJson, StringComparison.Ordinal);
        Assert.Contains("v1:", rawDiskJson, StringComparison.Ordinal);

        var reloadedStore = new AiCredentialsStore(settingsPath);
        Assert.Equal(openAiKey, reloadedStore.GetApiKey("openai"));
        Assert.Equal(anthropicKey, reloadedStore.GetApiKey("ANTHROPIC"));
        Assert.Equal(new[] { "anthropic", "openai" }, reloadedStore.ListConfiguredProviders());

        Assert.True(reloadedStore.RemoveApiKey("openai"));
        Assert.Null(reloadedStore.GetApiKey("openai"));
        Assert.Equal(anthropicKey, reloadedStore.GetApiKey("anthropic"));

        // Tampered ciphertext must fail authentication tag verification
        Assert.ThrowsAny<CryptographicException>(() => reloadedStore.DecryptSecret("v1:AAAA:BBBB:CCCC:DDDD"));
    }

    [Fact]
    public async Task PreviewTransactionCoordinatorStagesRefinesRejectsAndApprovesOnRealEditorSession()
    {
        await using var workspace = await CreateWorkspaceAsync();
        var editor = new EditorSession(workspace);
        var coordinator = new AiPreviewTransactionCoordinator();

        var initialJson = ProjectJson.Serialize(editor.Snapshot);
        Assert.False(coordinator.HasStagedPreview);

        // Stage initial 1.6x zoom preview
        var staged1 = await coordinator.StagePreviewBatchAsync(
            editor,
            BuildZoomCommand("ai-zoom-1", 1.6),
            "Add 1.6x zoom at 0.2s");

        Assert.True(coordinator.HasStagedPreview);
        Assert.Equal("Add 1.6x zoom at 0.2s", coordinator.StagedDescription);
        Assert.NotNull(coordinator.StagedPreviewSnapshot);
        Assert.NotNull(editor.Preview);
        Assert.Single(editor.Preview!.Effects);
        Assert.Equal(1.6, editor.Preview.Effects[0].Parameters.GetProperty("scale").GetDouble(), 4);
        Assert.Equal(staged1.Revision, editor.Preview.Revision);
        // Authoritative snapshot remains untouched while staged
        Assert.Equal(initialJson, ProjectJson.Serialize(editor.Snapshot));
        Assert.False(editor.CanUndo);

        // Multi-turn refinement on the staged preview changes scale to 2.2 without altering Snapshot
        await coordinator.StagePreviewBatchAsync(
            editor,
            BuildZoomCommand("ai-zoom-1", 2.2),
            "Adjust zoom scale to 2.2x");

        Assert.True(coordinator.HasStagedPreview);
        Assert.NotNull(editor.Preview);
        Assert.Single(editor.Preview!.Effects);
        Assert.Equal(2.2, editor.Preview.Effects[0].Parameters.GetProperty("scale").GetDouble(), 4);
        Assert.Equal(initialJson, ProjectJson.Serialize(editor.Snapshot));

        // Rejecting discards preview and restores clean state
        Assert.True(coordinator.RejectPreview(editor));
        Assert.False(coordinator.HasStagedPreview);
        Assert.Null(coordinator.StagedDescription);
        Assert.Null(coordinator.StagedPreviewSnapshot);
        Assert.Null(editor.Preview);
        Assert.Equal(initialJson, ProjectJson.Serialize(editor.Snapshot));
        Assert.False(editor.CanUndo);

        // Re-stage and approve commits to Snapshot and pushes a single undo step
        await coordinator.StagePreviewBatchAsync(
            editor,
            BuildZoomCommand("ai-zoom-1", 2.2),
            "Commit 2.2x zoom");
        Assert.True(await coordinator.ApprovePreviewAsync(editor));

        Assert.False(coordinator.HasStagedPreview);
        Assert.Null(editor.Preview);
        Assert.Single(editor.Snapshot.Effects);
        Assert.Equal(2.2, editor.Snapshot.Effects[0].Parameters.GetProperty("scale").GetDouble(), 4);
        Assert.Equal(2, editor.Snapshot.Revision);
        Assert.True(editor.CanUndo);

        await editor.UndoAsync();
        Assert.Empty(editor.Snapshot.Effects);
    }

    [Fact]
    public async Task PreviewTransactionCoordinatorRollsBackOnInvalidCommand()
    {
        await using var workspace = await CreateWorkspaceAsync("invalid-stage.frametrailproject");
        var editor = new EditorSession(workspace);
        var coordinator = new AiPreviewTransactionCoordinator();

        var invalidZoom = BuildZoomCommand("bad-zoom", 9.5); // scale max is 4.0
        await Assert.ThrowsAnyAsync<Exception>(() =>
            coordinator.StagePreviewBatchAsync(editor, invalidZoom, "Invalid zoom"));

        Assert.False(coordinator.HasStagedPreview);
        Assert.Null(editor.Preview);
        Assert.Empty(editor.Snapshot.Effects);
    }

    [Fact]
    public async Task SubscriptionManagerDiscoversToolsProbesOllamaAndBuildsMcpConfig()
    {
        var fakeBin = Path.Combine(root, "fake-bin");
        var fakeAgent = Path.Combine(root, "fake-agent");
        Directory.CreateDirectory(fakeBin);
        Directory.CreateDirectory(fakeAgent);

        var claudePath = Path.Combine(fakeBin, "claude.cmd");
        var codexPath = Path.Combine(fakeBin, "codex.exe");
        var agyPath = Path.Combine(fakeBin, "agy.cmd");
        var pythonPath = Path.Combine(fakeBin, "python.exe");
        var mcpScript = Path.Combine(fakeAgent, "mcp_server.py");
        var serverScript = Path.Combine(fakeAgent, "server.py");

        await File.WriteAllTextAsync(claudePath, "@echo off\r\n");
        await File.WriteAllBytesAsync(codexPath, Array.Empty<byte>());
        await File.WriteAllTextAsync(agyPath, "@echo off\r\n");
        await File.WriteAllBytesAsync(pythonPath, Array.Empty<byte>());
        await File.WriteAllTextAsync(mcpScript, "# mcp placeholder\n");
        await File.WriteAllTextAsync(serverScript, "# server placeholder\n");

        using var ollamaHandler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"models":[{"name":"qwen2.5-coder:14b"},{"name":"llama3.2:latest"}]}""",
                Encoding.UTF8,
                "application/json")
        });

        var manager = new AiSubscriptionManager(
            httpMessageHandler: ollamaHandler,
            searchDirectories: new[] { fakeBin },
            agentRootDirectory: fakeAgent);

        var status = await manager.ProbeEnvironmentAsync();
        Assert.True(status.HasClaudeCodeCli);
        Assert.Equal(Path.GetFullPath(claudePath), status.ClaudeCodePath);
        Assert.True(status.HasCodexCli);
        Assert.Equal(Path.GetFullPath(codexPath), status.CodexPath);
        Assert.True(status.HasAntigravityCli);
        Assert.Equal(Path.GetFullPath(agyPath), status.AntigravityPath);
        Assert.True(status.HasOllamaServer);
        Assert.Equal(new[] { "qwen2.5-coder:14b", "llama3.2:latest" }, status.OllamaModels);
        Assert.True(status.HasPythonSidecar);

        await using var workspace = await CreateWorkspaceAsync("mcp-cfg.frametrailproject");
        var configJson = manager.BuildMcpConfigJson(
            workspace,
            callbackUrl: "http://127.0.0.1:18765/api/commands");

        using var doc = JsonDocument.Parse(configJson);
        var server = doc.RootElement.GetProperty("mcpServers").GetProperty("frametrail");
        Assert.Equal(Path.GetFullPath(pythonPath), server.GetProperty("command").GetString());

        var args = server.GetProperty("args").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(Path.GetFullPath(mcpScript), args[0]);
        Assert.Contains("--project-dir", args);
        Assert.Contains(workspace.DirectoryPath, args);
        Assert.Contains("--callback-url", args);
        Assert.Contains("http://127.0.0.1:18765/api/commands", args);

        var env = server.GetProperty("env");
        Assert.Equal(workspace.DirectoryPath, env.GetProperty("FRAMETRAIL_PROJECT_DIR").GetString());
        Assert.Equal("http://127.0.0.1:18765/api/commands", env.GetProperty("FRAMETRAIL_CALLBACK_URL").GetString());

        // Offline Ollama probe should gracefully report unavailable without throwing
        using var offlineHandler = new StubHttpMessageHandler(_ => throw new HttpRequestException("Connection refused"));
        var offlineManager = new AiSubscriptionManager(
            httpMessageHandler: offlineHandler,
            searchDirectories: Array.Empty<string>(),
            agentRootDirectory: Path.Combine(root, "missing-agent"));

        var offlineStatus = await offlineManager.ProbeEnvironmentAsync();
        Assert.False(offlineStatus.HasClaudeCodeCli);
        Assert.False(offlineStatus.HasCodexCli);
        Assert.False(offlineStatus.HasAntigravityCli);
        Assert.False(offlineStatus.HasOllamaServer);
        Assert.Empty(offlineStatus.OllamaModels);
        Assert.False(offlineStatus.HasPythonSidecar);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
