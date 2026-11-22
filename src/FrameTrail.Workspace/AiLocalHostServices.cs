using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrameTrail.Workspace;

/// <summary>Result returned by host-level MCP callback actions (such as Whisper model management or subtitle transcription).</summary>
public sealed record AiLocalCallbackActionResult(
    bool Ok,
    long? Revision = null,
    string? Error = null,
    int? CueCount = null,
    string? EngineUsed = null,
    string? Message = null,
    object? Data = null);

/// <summary>
/// Lightweight localhost HTTP callback server so external MCP servers (spawned by
/// Antigravity CLI, Claude Code CLI, or Codex CLI) dispatch editing commands directly
/// into the live <see cref="EditorSession"/> instead of overwriting project.json on disk.
/// </summary>
public sealed class AiLocalCommandCallbackServer : IAsyncDisposable, IDisposable
{
    private readonly HttpListener listener = new();
    private readonly Func<JsonElement, CancellationToken, Task<(bool Ok, long? Revision, string? Error)>> commandHandler;
    private readonly Func<string, JsonElement, CancellationToken, Task<AiLocalCallbackActionResult>>? actionHandler;
    private readonly string sessionToken;
    private readonly CancellationTokenSource cts = new();
    private Task? loopTask;
    private bool disposed;

    public string CallbackUrl { get; }

    public AiLocalCommandCallbackServer(
        Func<JsonElement, CancellationToken, Task<(bool Ok, long? Revision, string? Error)>> commandHandler,
        Func<string, JsonElement, CancellationToken, Task<AiLocalCallbackActionResult>>? actionHandler = null)
    {
        this.commandHandler = commandHandler ?? throw new ArgumentNullException(nameof(commandHandler));
        this.actionHandler = actionHandler;
        sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        int port = FindFreeLoopbackPort();
        string prefix = $"http://127.0.0.1:{port}/api/commands/";
        listener.Prefixes.Add(prefix);
        listener.Start();
        CallbackUrl = $"{prefix}?token={sessionToken}";
        loopTask = Task.Run(() => ListenLoopAsync(cts.Token));
    }

    private static int FindFreeLoopbackPort()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        return ((IPEndPoint)tcp.LocalEndpoint).Port;
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (context is not null)
                _ = HandleRequestAsync(context, token);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken token)
    {
        try
        {
            var req = context.Request;
            var res = context.Response;

            if (!string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(res, 405, new { ok = false, error = "Method not allowed." }).ConfigureAwait(false);
                return;
            }

            string? providedToken = req.QueryString["token"] ?? req.Headers["X-FrameTrail-Token"];
            if (string.IsNullOrEmpty(providedToken) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(providedToken),
                    Encoding.UTF8.GetBytes(sessionToken)))
            {
                await WriteJsonAsync(res, 401, new { ok = false, error = "Unauthorized callback token." }).ConfigureAwait(false);
                return;
            }

            using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
            string body = await reader.ReadToEndAsync(token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("action", out var actionEl) &&
                actionEl.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(actionEl.GetString()))
            {
                if (actionHandler is null)
                {
                    await WriteJsonAsync(res, 501, new { ok = false, error = "Host callback actions are not supported." }).ConfigureAwait(false);
                    return;
                }

                string actionName = actionEl.GetString()!.Trim();
                JsonElement paramsEl = doc.RootElement.TryGetProperty("params", out var pEl) && pEl.ValueKind == JsonValueKind.Object
                    ? pEl.Clone()
                    : doc.RootElement.Clone();

                var actionResult = await actionHandler(actionName, paramsEl, token).ConfigureAwait(false);
                if (actionResult.Ok)
                {
                    await WriteJsonAsync(res, 200, new
                    {
                        ok = true,
                        revision = actionResult.Revision,
                        cueCount = actionResult.CueCount,
                        engineUsed = actionResult.EngineUsed,
                        message = actionResult.Message,
                        data = actionResult.Data
                    }).ConfigureAwait(false);
                }
                else
                {
                    await WriteJsonAsync(res, 422, new
                    {
                        ok = false,
                        error = actionResult.Error ?? "Action rejected.",
                        data = actionResult.Data
                    }).ConfigureAwait(false);
                }
                return;
            }

            if (!doc.RootElement.TryGetProperty("command", out var cmdEl) || cmdEl.ValueKind != JsonValueKind.Object)
            {
                await WriteJsonAsync(res, 400, new { ok = false, error = "Missing 'command' JSON object." }).ConfigureAwait(false);
                return;
            }

            var (ok, revision, error) = await commandHandler(cmdEl.Clone(), token).ConfigureAwait(false);
            if (ok)
                await WriteJsonAsync(res, 200, new { ok = true, revision }).ConfigureAwait(false);
            else
                await WriteJsonAsync(res, 422, new { ok = false, error = error ?? "Command rejected." }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                await WriteJsonAsync(context.Response, 500, new { ok = false, error = ex.Message }).ConfigureAwait(false);
            }
            catch
            {
                // Ignore response write errors on disconnected clients.
            }
        }
    }

    private static readonly JsonSerializerOptions CallbackJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, object payload)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, CallbackJsonOptions));
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { cts.Cancel(); } catch { }
        try { if (listener.IsListening) listener.Stop(); } catch { }
        try { listener.Close(); } catch { }
        cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (loopTask is not null)
        {
            try { await loopTask.ConfigureAwait(false); } catch { }
        }
    }
}

/// <summary>
/// Manages the app-wide lifecycle of the local Python LangGraph FastAPI sidecar process
/// (<c>python -m uvicorn agent.server:app</c>). Auto-starts with the application, coalesces
/// concurrent startup requests, binds child lifetime to the parent PID, and cleans up on exit.
/// </summary>
public sealed class AiSidecarProcessManager : IDisposable
{
    private readonly object gate = new();
    private readonly StringBuilder stderrTail = new();
    private Process? process;
    private Task<bool>? inflightStart;
    private bool disposed;

    public string SidecarUrl { get; }
    public string? LastError { get; private set; }
    public string? ResolvedPythonPath { get; private set; }
    public string? ResolvedProjectRoot { get; private set; }

    public bool IsRunning
    {
        get
        {
            lock (gate)
            {
                return process is not null && !process.HasExited;
            }
        }
    }

    public AiSidecarProcessManager(string sidecarUrl = "http://127.0.0.1:18765")
    {
        SidecarUrl = sidecarUrl.TrimEnd('/');
    }

    public Task<bool> EnsureStartedAsync(
        AiAgentBridge bridge,
        string? workspaceRoot = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        lock (gate)
        {
            if (disposed) return Task.FromResult(false);
            if (inflightStart is not null && !inflightStart.IsCompleted)
                return inflightStart;
            inflightStart = EnsureStartedCoreAsync(bridge, workspaceRoot, cancellationToken);
            return inflightStart;
        }
    }

    private async Task<bool> EnsureStartedCoreAsync(
        AiAgentBridge bridge,
        string? workspaceRoot,
        CancellationToken cancellationToken)
    {
        if (await bridge.IsSidecarHealthyAsync(SidecarUrl, cancellationToken).ConfigureAwait(false))
        {
            LastError = null;
            return true;
        }

        string? root = ResolveProjectRoot(workspaceRoot);
        ResolvedProjectRoot = root;

        // Priority 1: Bundled self-contained PyInstaller executable (zero Python required on end-user PCs)
        string? bundledExe = ResolveBundledSidecarExecutable(root);
        string? python = bundledExe is null ? ResolvePythonExecutable(root) : null;
        ResolvedPythonPath = bundledExe ?? python;

        if (bundledExe is null && (root is null || python is null))
        {
            LastError = root is null
                ? "Bundled ai-agent-runtime\\frametrail-agent.exe not found and agent/server.py directory missing."
                : "Bundled ai-agent-runtime\\frametrail-agent.exe not found and Python executable not found.";
            return false;
        }

        var uri = new Uri(SidecarUrl);
        lock (gate)
        {
            if (disposed) return false;
            if (process is null || process.HasExited)
            {
                stderrTail.Clear();
                var startInfo = new ProcessStartInfo
                {
                    FileName = bundledExe ?? python!,
                    WorkingDirectory = bundledExe is not null ? Path.GetDirectoryName(bundledExe)! : root!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };
                startInfo.Environment["FRAMETRAIL_PARENT_PID"] = Environment.ProcessId.ToString();
                startInfo.Environment["PYTHONUTF8"] = "1";
                if (bundledExe is not null)
                {
                    startInfo.ArgumentList.Add("--host");
                    startInfo.ArgumentList.Add(uri.Host);
                    startInfo.ArgumentList.Add("--port");
                    startInfo.ArgumentList.Add(uri.Port.ToString());
                    startInfo.ArgumentList.Add("--parent-pid");
                    startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
                }
                else
                {
                    startInfo.ArgumentList.Add("-m");
                    startInfo.ArgumentList.Add("uvicorn");
                    startInfo.ArgumentList.Add("agent.server:app");
                    startInfo.ArgumentList.Add("--host");
                    startInfo.ArgumentList.Add(uri.Host);
                    startInfo.ArgumentList.Add("--port");
                    startInfo.ArgumentList.Add(uri.Port.ToString());
                }

                try
                {
                    var proc = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                    proc.ErrorDataReceived += (_, args) =>
                    {
                        if (string.IsNullOrWhiteSpace(args.Data)) return;
                        lock (gate)
                        {
                            if (stderrTail.Length > 2048) stderrTail.Remove(0, stderrTail.Length - 1024);
                            stderrTail.AppendLine(args.Data);
                        }
                    };
                    proc.OutputDataReceived += (_, _) => { };
                    if (!proc.Start())
                    {
                        LastError = "Failed to spawn Python sidecar process.";
                        return false;
                    }
                    proc.BeginErrorReadLine();
                    proc.BeginOutputReadLine();
                    process = proc;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    return false;
                }
            }
        }

        // Poll /api/health up to ~10 seconds for LangGraph/FastAPI cold import
        for (int attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await bridge.IsSidecarHealthyAsync(SidecarUrl, cancellationToken).ConfigureAwait(false))
            {
                LastError = null;
                return true;
            }

            lock (gate)
            {
                if (disposed) return false;
                if (process is not null && process.HasExited)
                {
                    string err = stderrTail.ToString().Trim();
                    string lastLine = err.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
                    LastError = string.IsNullOrEmpty(lastLine)
                        ? $"Sidecar exited with code {process.ExitCode}."
                        : lastLine;
                    return false;
                }
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        LastError = "Sidecar startup timed out waiting for /api/health.";
        return false;
    }

    public void Stop()
    {
        lock (gate)
        {
            if (process is null) return;
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore termination race conditions.
            }
            finally
            {
                try { process.Dispose(); } catch { }
                process = null;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
        }
        Stop();
    }

    public static string? ResolveBundledSidecarExecutable(string? projectRoot = null)
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "ai-agent-runtime", "frametrail-agent.exe"),
        };

        if (!string.IsNullOrWhiteSpace(projectRoot))
            candidates.Add(Path.Combine(projectRoot, "artifacts", "ai-agent-runtime", "frametrail-agent.exe"));

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            candidates.Add(Path.Combine(localAppData, "FrameTrail", "ai-agent-runtime", "frametrail-agent.exe"));

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return Path.GetFullPath(path);
        }
        return null;
    }

    public static string? ResolveProjectRoot(string? hint = null)
    {
        if (!string.IsNullOrWhiteSpace(hint) && File.Exists(Path.Combine(hint, "agent", "server.py")))
            return Path.GetFullPath(hint);

        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "agent", "server.py")))
                    return dir.FullName;
            }
        }
        return null;
    }

    public static string? ResolvePythonExecutable(string? projectRoot = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            candidates.Add(Path.Combine(projectRoot, ".tools", "agent-venv", "Scripts", "python.exe"));
            candidates.Add(Path.Combine(projectRoot, "agent", ".venv", "Scripts", "python.exe"));
            candidates.Add(Path.Combine(projectRoot, ".venv", "Scripts", "python.exe"));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            candidates.Add(Path.Combine(localAppData, "FrameTrail", "agent-venv", "Scripts", "python.exe"));

        foreach (var venvPython in candidates)
        {
            if (File.Exists(venvPython))
                return Path.GetFullPath(venvPython);
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Directory.Exists(dir)) continue;
            // Skip Microsoft Store execution alias stubs (they exit 9009 when spawned)
            if (dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var name in new[] { "python.exe", "python3.exe", "python" })
            {
                var full = Path.Combine(dir, name);
                if (File.Exists(full)) return Path.GetFullPath(full);
            }
        }
        return null;
    }
}
