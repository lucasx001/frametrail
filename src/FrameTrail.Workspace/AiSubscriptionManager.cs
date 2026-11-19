using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FrameTrail.Domain;
using FrameTrail.Interop;

namespace FrameTrail.Workspace;

/// <summary>Snapshot of local AI subscription CLIs, Ollama models, and Python sidecar availability.</summary>
public sealed record LocalAiEnvironmentStatus(
    bool HasClaudeCodeCli,
    string? ClaudeCodePath,
    bool HasCodexCli,
    string? CodexPath,
    bool HasOllamaServer,
    IReadOnlyList<string> OllamaModels,
    bool HasPythonSidecar,
    bool HasAntigravityCli = false,
    string? AntigravityPath = null);

/// <summary>Persisted AI provider preferences and encrypted API key map.</summary>
public sealed record AiUserSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string ActiveProvider { get; init; } = "openai";
    public string? ActiveModel { get; init; }
    public string? CustomBaseUrl { get; init; }
    public string? PreferredSubscriptionCli { get; init; }
    public bool AutoApplyEdits { get; init; }
    public IReadOnlyDictionary<string, string> ProviderModels { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> ProviderBaseUrls { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> EncryptedApiKeys { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Persists user AI settings and AES-GCM encrypted API keys locally under
/// <c>%LOCALAPPDATA%\FrameTrail\ai-settings.json</c> using cross-platform .NET 8 cryptography.
/// </summary>
public sealed class AiCredentialsStore
{
    private const string CipherPrefix = "v1:";
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;
    private const int Pbkdf2Iterations = 100_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object gate = new();

    public string SettingsPath { get; }

    public AiCredentialsStore(string? settingsPath = null)
    {
        SettingsPath = Path.GetFullPath(settingsPath ?? GetDefaultSettingsPath());
    }

    public static string GetDefaultSettingsPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".frametrail");
        }
        return Path.Combine(localAppData, "FrameTrail", "ai-settings.json");
    }

    public AiUserSettings Load()
    {
        lock (gate)
        {
            if (!File.Exists(SettingsPath))
            {
                return new AiUserSettings();
            }

            var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AiUserSettings();
            }

            var loaded = JsonSerializer.Deserialize<AiUserSettings>(json, JsonOptions);
            if (loaded is null)
            {
                return new AiUserSettings();
            }

            return loaded with
            {
                EncryptedApiKeys = new Dictionary<string, string>(
                    loaded.EncryptedApiKeys ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase)
            };
        }
    }

    public Task<AiUserSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Load());
    }

    public void Save(AiUserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (gate)
        {
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var tempPath = Path.Combine(directory, $".ai-settings.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, SettingsPath, overwrite: true);
        }
    }

    public Task SaveAsync(AiUserSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Save(settings);
        return Task.CompletedTask;
    }

    public void SetApiKey(string provider, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new ArgumentException("Provider name is required.", nameof(provider));
        }
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("API key cannot be empty.", nameof(apiKey));
        }

        var normalized = provider.Trim().ToLowerInvariant();
        var encrypted = EncryptSecret(apiKey);
        lock (gate)
        {
            var current = Load();
            var keys = new Dictionary<string, string>(current.EncryptedApiKeys, StringComparer.OrdinalIgnoreCase)
            {
                [normalized] = encrypted
            };
            Save(current with { EncryptedApiKeys = keys });
        }
    }

    public string? GetApiKey(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return null;
        }

        var normalized = provider.Trim().ToLowerInvariant();
        lock (gate)
        {
            var current = Load();
            if (!current.EncryptedApiKeys.TryGetValue(normalized, out var payload) || string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }
            return DecryptSecret(payload);
        }
    }

    public bool RemoveApiKey(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return false;
        }

        var normalized = provider.Trim().ToLowerInvariant();
        lock (gate)
        {
            var current = Load();
            var keys = new Dictionary<string, string>(current.EncryptedApiKeys, StringComparer.OrdinalIgnoreCase);
            if (!keys.Remove(normalized))
            {
                return false;
            }
            Save(current with { EncryptedApiKeys = keys });
            return true;
        }
    }

    public IReadOnlyList<string> ListConfiguredProviders()
    {
        lock (gate)
        {
            return Load().EncryptedApiKeys.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public string EncryptSecret(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        Span<byte> salt = stackalloc byte[SaltBytes];
        Span<byte> nonce = stackalloc byte[NonceBytes];
        RandomNumberGenerator.Fill(salt);
        RandomNumberGenerator.Fill(nonce);

        var key = DeriveMachineKey(salt);
        try
        {
            var plainBytes = Encoding.UTF8.GetBytes(plaintext);
            var cipherBytes = new byte[plainBytes.Length];
            Span<byte> tag = stackalloc byte[TagBytes];

            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

            return string.Concat(
                CipherPrefix,
                Convert.ToBase64String(salt),
                ":",
                Convert.ToBase64String(nonce),
                ":",
                Convert.ToBase64String(tag),
                ":",
                Convert.ToBase64String(cipherBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public string DecryptSecret(string encryptedPayload)
    {
        if (string.IsNullOrWhiteSpace(encryptedPayload) || !encryptedPayload.StartsWith(CipherPrefix, StringComparison.Ordinal))
        {
            throw new CryptographicException("Unsupported encrypted credential format.");
        }

        var parts = encryptedPayload[CipherPrefix.Length..].Split(':');
        if (parts.Length != 4)
        {
            throw new CryptographicException("Malformed encrypted credential payload.");
        }

        var salt = Convert.FromBase64String(parts[0]);
        var nonce = Convert.FromBase64String(parts[1]);
        var tag = Convert.FromBase64String(parts[2]);
        var cipherBytes = Convert.FromBase64String(parts[3]);

        if (salt.Length != SaltBytes || nonce.Length != NonceBytes || tag.Length != TagBytes)
        {
            throw new CryptographicException("Invalid encrypted credential envelope dimensions.");
        }

        var key = DeriveMachineKey(salt);
        try
        {
            var plainBytes = new byte[cipherBytes.Length];
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
            return Encoding.UTF8.GetString(plainBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] DeriveMachineKey(ReadOnlySpan<byte> salt)
    {
        var entropy = string.Join(
            '|',
            "FrameTrail.AI.Credentials.v1",
            Environment.MachineName,
            Environment.UserName,
            Environment.UserDomainName,
            Environment.OSVersion.Platform);

        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(entropy),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            KeyBytes);
    }
}

/// <summary>
/// Coordinates Human-in-the-loop (HITL) live preview transactions on <see cref="EditorSession"/>.
/// Staged edits render in <see cref="EditorSession.Preview"/> while keeping <see cref="EditorSession.Snapshot"/>
/// unchanged until explicitly approved or rejected.
/// </summary>
public sealed class AiPreviewTransactionCoordinator
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public bool HasStagedPreview { get; private set; }
    public string? StagedDescription { get; private set; }
    public ProjectSnapshot? StagedPreviewSnapshot { get; private set; }

    public async Task<ProjectSnapshot> StagePreviewBatchAsync(
        EditorSession session,
        object command,
        string description,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var startedTransactionHere = false;
            if (!HasStagedPreview)
            {
                session.BeginTransaction();
                startedTransactionHere = true;
            }

            try
            {
                var preview = session.PreviewCommand(command);
                EngineSession.ValidateProject(preview);

                HasStagedPreview = true;
                StagedDescription = description;
                StagedPreviewSnapshot = preview;
                return preview;
            }
            catch
            {
                if (startedTransactionHere)
                {
                    session.CancelTransaction();
                    ClearStagedState();
                }
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> ApprovePreviewAsync(
        EditorSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!HasStagedPreview)
            {
                return false;
            }

            await session.CommitTransactionAsync().ConfigureAwait(false);
            ClearStagedState();
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public bool RejectPreview(EditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        gate.Wait();
        try
        {
            if (!HasStagedPreview)
            {
                return false;
            }

            session.CancelTransaction();
            ClearStagedState();
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private void ClearStagedState()
    {
        HasStagedPreview = false;
        StagedDescription = null;
        StagedPreviewSnapshot = null;
    }
}

/// <summary>
/// Discovers local subscription CLIs (Claude Code, OpenAI Codex) and local Ollama servers,
/// and builds MCP configuration files for connecting external CLIs to a <see cref="ProjectWorkspace"/>.
/// </summary>
public sealed class AiSubscriptionManager
{
    public const string DefaultOllamaTagsUrl = "http://127.0.0.1:11434/api/tags";

    private static readonly JsonSerializerOptions McpJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly IReadOnlyList<string>? overrideSearchDirectories;
    private readonly string ollamaTagsUrl;
    private readonly string? agentRootOverride;

    public AiSubscriptionManager(
        HttpMessageHandler? httpMessageHandler = null,
        IEnumerable<string>? searchDirectories = null,
        string? ollamaTagsUrl = null,
        string? agentRootDirectory = null)
    {
        if (httpMessageHandler is null)
        {
            httpClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(900) };
            ownsHttpClient = true;
        }
        else
        {
            httpClient = new HttpClient(httpMessageHandler, disposeHandler: false)
            {
                Timeout = TimeSpan.FromMilliseconds(900)
            };
            ownsHttpClient = true;
        }

        overrideSearchDirectories = searchDirectories?.Where(d => !string.IsNullOrWhiteSpace(d)).ToArray();
        this.ollamaTagsUrl = string.IsNullOrWhiteSpace(ollamaTagsUrl) ? DefaultOllamaTagsUrl : ollamaTagsUrl;
        agentRootOverride = agentRootDirectory;
    }

    public async Task<LocalAiEnvironmentStatus> ProbeEnvironmentAsync(CancellationToken cancellationToken = default)
    {
        var searchDirs = GetSearchDirectories();
        var claudePath = FindExecutable(searchDirs, "claude.exe", "claude.cmd", "claude");
        var codexPath = FindExecutable(searchDirs, "codex.exe", "codex.cmd", "codex");
        var agyPath = FindExecutable(searchDirs, "agy.exe", "agy.cmd", "agy.ps1", "agy");
        var pythonPath = FindExecutable(searchDirs, "python.exe", "python3.exe", "python");

        var (hasOllama, ollamaModels) = await ProbeOllamaAsync(cancellationToken).ConfigureAwait(false);
        var hasBundled = overrideSearchDirectories is null && agentRootOverride is null &&
                         AiSidecarProcessManager.ResolveBundledSidecarExecutable() is not null;
        var hasSidecar = hasBundled || (pythonPath is not null && FindAgentSidecarScript() is not null);

        return new LocalAiEnvironmentStatus(
            HasClaudeCodeCli: claudePath is not null,
            ClaudeCodePath: claudePath,
            HasCodexCli: codexPath is not null,
            CodexPath: codexPath,
            HasOllamaServer: hasOllama,
            OllamaModels: ollamaModels,
            HasPythonSidecar: hasSidecar,
            HasAntigravityCli: agyPath is not null,
            AntigravityPath: agyPath);
    }

    public string BuildMcpConfigJson(
        ProjectWorkspace workspace,
        string? callbackUrl = null,
        string? pythonExecutable = null,
        string? mcpServerScriptPath = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return BuildMcpConfigJson(workspace.DirectoryPath, callbackUrl, pythonExecutable, mcpServerScriptPath);
    }

    public string BuildMcpConfigJson(
        string projectDirectory,
        string? callbackUrl = null,
        string? pythonExecutable = null,
        string? mcpServerScriptPath = null)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            throw new ArgumentException("Project directory is required.", nameof(projectDirectory));
        }

        var fullProjectDir = Path.GetFullPath(projectDirectory);
        string? bundledExe = (string.IsNullOrWhiteSpace(pythonExecutable) &&
                              string.IsNullOrWhiteSpace(mcpServerScriptPath) &&
                              overrideSearchDirectories is null &&
                              agentRootOverride is null)
            ? AiSidecarProcessManager.ResolveBundledSidecarExecutable()
            : null;

        string command;
        List<string> args;
        if (bundledExe is not null)
        {
            command = bundledExe;
            args = new List<string> { "--mcp", "--project-dir", fullProjectDir };
        }
        else
        {
            command = string.IsNullOrWhiteSpace(pythonExecutable)
                ? (FindExecutable(GetSearchDirectories(), "python.exe", "python3.exe", "python") ?? "python")
                : pythonExecutable;
            var scriptPath = string.IsNullOrWhiteSpace(mcpServerScriptPath)
                ? (FindMcpServerScript() ?? Path.Combine(AppContext.BaseDirectory, "agent", "mcp_server.py"))
                : Path.GetFullPath(mcpServerScriptPath);
            args = new List<string> { scriptPath, "--project-dir", fullProjectDir };
        }

        var env = new Dictionary<string, string>
        {
            ["FRAMETRAIL_PROJECT_DIR"] = fullProjectDir
        };

        if (!string.IsNullOrWhiteSpace(callbackUrl))
        {
            var trimmed = callbackUrl.Trim();
            args.Add("--callback-url");
            args.Add(trimmed);
            env["FRAMETRAIL_CALLBACK_URL"] = trimmed;
        }

        var payload = new
        {
            mcpServers = new
            {
                frametrail = new
                {
                    command,
                    args,
                    env
                }
            }
        };

        return JsonSerializer.Serialize(payload, McpJsonOptions);
    }

    public async Task<string> WriteMcpConfigFileAsync(
        ProjectWorkspace workspace,
        string? outputPath = null,
        string? callbackUrl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        cancellationToken.ThrowIfCancellationRequested();

        var json = BuildMcpConfigJson(workspace, callbackUrl);
        var target = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(Path.GetTempPath(), "FrameTrail", $"frametrail-mcp-{workspace.SessionEpoch:N}.json")
            : Path.GetFullPath(outputPath);

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);
        return target;
    }

    private async Task<(bool Available, IReadOnlyList<string> Models)> ProbeOllamaAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(900));
            using var response = await httpClient.GetAsync(ollamaTagsUrl, timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, Array.Empty<string>());
            }

            var content = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
            {
                return (true, Array.Empty<string>());
            }

            using var doc = JsonDocument.Parse(content);
            var models = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var modelsEl) && modelsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in modelsEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty("name", out var nameEl) &&
                        nameEl.ValueKind == JsonValueKind.String)
                    {
                        var name = nameEl.GetString();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            models.Add(name);
                        }
                    }
                }
            }
            return (true, models);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, Array.Empty<string>());
        }
        catch (HttpRequestException)
        {
            return (false, Array.Empty<string>());
        }
        catch (JsonException)
        {
            return (true, Array.Empty<string>());
        }
        finally
        {
            _ = ownsHttpClient;
        }
    }

    private IReadOnlyList<string> GetSearchDirectories()
    {
        if (overrideSearchDirectories is not null)
        {
            return overrideSearchDirectories;
        }

        var dirs = new List<string>();
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            dirs.AddRange(pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        AddIfSet(dirs, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        AddIfSet(dirs, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy\\bin", "Programs", "Programs\\Antigravity", "Programs\\Antigravity\\bin", "Microsoft\\WinGet\\Links", "npm");
        AddIfSet(dirs, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local\\bin", ".claude\\local", ".codex\\bin", ".gemini\\antigravity-cli", ".gemini\\antigravity\\bin");
        AddIfSet(dirs, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "GitHub CLI", "Google\\Antigravity");

        return dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddIfSet(List<string> target, string? root, params string[] subFolders)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }
        foreach (var sub in subFolders)
        {
            target.Add(Path.Combine(root, sub));
        }
    }

    private static string? FindExecutable(IEnumerable<string> directories, params string[] candidateFileNames)
    {
        foreach (var dir in directories)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                continue;
            }
            foreach (var name in candidateFileNames)
            {
                var full = Path.Combine(dir, name);
                if (File.Exists(full))
                {
                    return Path.GetFullPath(full);
                }
            }
        }
        return null;
    }

    private string? FindAgentSidecarScript()
    {
        foreach (var root in GetAgentDirectoryCandidates())
        {
            var serverPy = Path.Combine(root, "server.py");
            if (File.Exists(serverPy))
            {
                return Path.GetFullPath(serverPy);
            }
            var mcpPy = Path.Combine(root, "mcp_server.py");
            if (File.Exists(mcpPy))
            {
                return Path.GetFullPath(mcpPy);
            }
        }
        return null;
    }

    private string? FindMcpServerScript()
    {
        foreach (var root in GetAgentDirectoryCandidates())
        {
            var mcpPy = Path.Combine(root, "mcp_server.py");
            if (File.Exists(mcpPy))
            {
                return Path.GetFullPath(mcpPy);
            }
        }
        return null;
    }

    private IEnumerable<string> GetAgentDirectoryCandidates()
    {
        if (!string.IsNullOrWhiteSpace(agentRootOverride))
        {
            yield return agentRootOverride;
        }

        yield return Path.Combine(AppContext.BaseDirectory, "agent");

        var current = Directory.GetCurrentDirectory();
        for (var dir = new DirectoryInfo(current); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "agent");
            if (Directory.Exists(candidate))
            {
                yield return candidate;
                break;
            }
        }
    }
}
