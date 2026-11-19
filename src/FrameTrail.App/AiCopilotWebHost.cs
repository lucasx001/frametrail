using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.UI;

namespace FrameTrail.App;

/// <summary>
/// Manages the WinUI 3 WebView2 host for the AI Copilot panel: isolated user data folder,
/// virtual host mappings, anti-flash theme background, browser hardening, external link
/// delegation, ui.ready message queuing, and runtime-missing fallback handling.
/// </summary>
internal sealed class AiCopilotWebHost : IDisposable
{
    internal const string UiHostName = "ai-ui.frametrail.local";
    internal const string AssetsHostName = "ai-assets.frametrail.local";
    internal const string WebView2DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    private readonly WebView2 webView;
    private readonly AiContextThrottle contextThrottle = new();
    private readonly Queue<string> pendingMessages = new();

    private CoreWebView2? core;
    private Task? initTask;
    private ElementTheme currentTheme = ElementTheme.Light;
    private string currentLocale = "en";
    private string? devOrigin;
    private bool isUiReady;
    private bool disposed;

    internal bool IsReady => isUiReady;
    internal bool IsInitialized => core is not null;

    internal event Action<string, JsonElement?>? MessageReceived;
    internal event Action<string>? RuntimeMissing;
    internal event Action<string>? LoadFailed;
    internal event Action? LoadingStarted;

    internal AiCopilotWebHost(WebView2 webView)
    {
        this.webView = webView ?? throw new ArgumentNullException(nameof(webView));
        ApplyBackgroundColor(currentTheme);
    }

    internal Task EnsureInitializedAsync(ElementTheme theme, string locale)
    {
        if (disposed) return Task.CompletedTask;
        currentTheme = theme;
        currentLocale = string.IsNullOrWhiteSpace(locale) ? "en" : locale;
        ApplyBackgroundColor(theme);

        if (initTask is not null && !initTask.IsFaulted && !initTask.IsCanceled)
            return initTask;

        initTask = InitializeCoreAsync();
        return initTask;
    }

    internal void SetTheme(ElementTheme theme)
    {
        currentTheme = theme;
        ApplyBackgroundColor(theme);
        if (isUiReady)
        {
            Post("theme.changed", new AiThemeChangedPayload(ToThemeString(theme)));
        }
    }

    internal void PublishContext(AiEditorContextPayload context, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (disposed) return;

        if (force)
        {
            contextThrottle.MarkPublished(context);
            Post("context.updated", context);
            return;
        }

        if (contextThrottle.ShouldPublish(context))
        {
            Post("context.updated", context);
        }
    }

    internal void CompleteInit(AiInitPayload initPayload)
    {
        ArgumentNullException.ThrowIfNull(initPayload);
        if (disposed || core is null) return;

        string initJson = AiCopilotProtocol.SerializeEnvelope("init", initPayload);
        core.PostWebMessageAsJson(initJson);
        isUiReady = true;

        while (pendingMessages.Count > 0)
        {
            string queued = pendingMessages.Dequeue();
            core.PostWebMessageAsJson(queued);
        }
    }

    internal void Post(string type, object? payload = null)
    {
        if (disposed) return;

        string json = AiCopilotProtocol.SerializeEnvelope(type, payload);
        if (!isUiReady || core is null)
        {
            pendingMessages.Enqueue(json);
            return;
        }

        core.PostWebMessageAsJson(json);
    }

    private async Task InitializeCoreAsync()
    {
        try
        {
            LoadingStarted?.Invoke();

            if (!IsWebView2RuntimeAvailable())
            {
                RuntimeMissing?.Invoke(WebView2DownloadUrl);
                return;
            }

            string userDataFolder = ResolveUserDataFolder();
            Directory.CreateDirectory(userDataFolder);

            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: null);

            if (disposed) return;

            await webView.EnsureCoreWebView2Async(environment);
            if (disposed) return;

            core = webView.CoreWebView2;
            ApplyBackgroundColor(currentTheme);
            ConfigureSettings(core.Settings);
            ConfigureVirtualHosts(core);

            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.NavigationCompleted += OnNavigationCompleted;
            core.PermissionRequested += OnPermissionRequested;

            string? targetUrl = ResolveTargetUrl();
            if (targetUrl is null)
            {
                LoadFailed?.Invoke("AI Copilot UI assets (ai-ui/index.html) were not found in the application directory.");
                return;
            }

            core.Navigate(targetUrl);
        }
        catch (Exception ex) when (IsRuntimeMissingException(ex) || !IsWebView2RuntimeAvailable())
        {
            RuntimeMissing?.Invoke(WebView2DownloadUrl);
        }
        catch (Exception ex)
        {
            LoadFailed?.Invoke(ex.Message);
        }
    }

    private static bool IsWebView2RuntimeAvailable()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRuntimeMissingException(Exception ex)
    {
        const int ErrorFileNotFound = unchecked((int)0x80070002);
        const int ClassNotRegistered = unchecked((int)0x80040154);

        return ex is FileNotFoundException ||
               (ex is COMException com && (com.HResult == ErrorFileNotFound || com.HResult == ClassNotRegistered));
    }

    private static string ResolveUserDataFolder()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA")
                ?? Path.GetTempPath();
        }
        return Path.Combine(localAppData, "FrameTrail", "WebView2");
    }

    private void ApplyBackgroundColor(ElementTheme theme)
    {
        webView.DefaultBackgroundColor = theme == ElementTheme.Dark
            ? Color.FromArgb(255, 17, 17, 19)    // #111113 (EditorPanel Dark)
            : Color.FromArgb(255, 255, 255, 255); // #FFFFFF (EditorPanel Light)
    }

    private static void ConfigureSettings(CoreWebView2Settings settings)
    {
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsStatusBarEnabled = false;
#if DEBUG
        settings.AreDevToolsEnabled = true;
#else
        settings.AreDevToolsEnabled = false;
#endif
    }

    private static void ConfigureVirtualHosts(CoreWebView2 coreWebView)
    {
        string uiDir = Path.Combine(AppContext.BaseDirectory, "ai-ui");
        if (Directory.Exists(uiDir))
        {
            coreWebView.SetVirtualHostNameToFolderMapping(
                UiHostName,
                uiDir,
                CoreWebView2HostResourceAccessKind.Allow);
        }

        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        if (Directory.Exists(assetsDir))
        {
            coreWebView.SetVirtualHostNameToFolderMapping(
                AssetsHostName,
                assetsDir,
                CoreWebView2HostResourceAccessKind.Allow);
        }
    }

    private string? ResolveTargetUrl()
    {
        string themeParam = ToThemeString(currentTheme);
        string langParam = Uri.EscapeDataString(currentLocale);

#if DEBUG
        string? devUrl = Environment.GetEnvironmentVariable("FRAMETRAIL_AI_UI_DEV_URL");
        if (!string.IsNullOrWhiteSpace(devUrl) && Uri.TryCreate(devUrl.Trim(), UriKind.Absolute, out var parsedDev))
        {
            devOrigin = $"{parsedDev.Scheme}://{parsedDev.Authority}";
            string trimmed = devUrl.Trim().TrimEnd('/');
            string sep = trimmed.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            return $"{trimmed}{sep}theme={themeParam}&lang={langParam}";
        }
#endif

        string indexPath = Path.Combine(AppContext.BaseDirectory, "ai-ui", "index.html");
        if (!File.Exists(indexPath))
            return null;

        return $"https://{UiHostName}/index.html?theme={themeParam}&lang={langParam}";
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (disposed) return;

        if (AiCopilotProtocol.TryParseIncoming(args.WebMessageAsJson, out var message) && message is not null)
        {
            MessageReceived?.Invoke(message.Type, message.Payload);
        }
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (disposed) return;

        if (IsAllowedNavigationUri(args.Uri))
        {
            if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) &&
                (string.Equals(uri.Host, UiHostName, StringComparison.OrdinalIgnoreCase) ||
                 (devOrigin is not null && args.Uri.StartsWith(devOrigin, StringComparison.OrdinalIgnoreCase))))
            {
                isUiReady = false;
            }
            return;
        }

        args.Cancel = true;
        TryOpenExternalUri(args.Uri);
    }

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        TryOpenExternalUri(args.Uri);
    }

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (disposed) return;

        if (!args.IsSuccess &&
            args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled &&
            !isUiReady)
        {
            LoadFailed?.Invoke($"WebView2 navigation failed ({args.WebErrorStatus}).");
        }
    }

    private void OnPermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        if (disposed) return;

        if (args.PermissionKind == CoreWebView2PermissionKind.Microphone &&
            IsAllowedNavigationUri(args.Uri))
        {
            args.State = CoreWebView2PermissionState.Allow;
            args.Handled = true;
        }
    }

    private bool IsAllowedNavigationUri(string? rawUri)
    {
        if (string.IsNullOrWhiteSpace(rawUri))
            return false;

        if (rawUri.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase) ||
            rawUri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            rawUri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var uri))
            return false;

        if (string.Equals(uri.Host, UiHostName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, AssetsHostName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (devOrigin is not null &&
            string.Equals($"{uri.Scheme}://{uri.Authority}", devOrigin, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    internal static void TryOpenExternalUri(string? rawUri)
    {
        if (string.IsNullOrWhiteSpace(rawUri)) return;
        if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is not ("http" or "https")) return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Ignore OS launcher failures for external links.
        }
    }

    internal static string ToThemeString(ElementTheme theme) =>
        theme == ElementTheme.Dark ? "dark" : "light";

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        pendingMessages.Clear();

        if (core is not null)
        {
            core.WebMessageReceived -= OnWebMessageReceived;
            core.NavigationStarting -= OnNavigationStarting;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.NavigationCompleted -= OnNavigationCompleted;
            core.PermissionRequested -= OnPermissionRequested;
            core = null;
        }

        try
        {
            webView.Close();
        }
        catch
        {
            // Ignore WebView2 shutdown exceptions if the window HWND is already torn down.
        }
    }
}
