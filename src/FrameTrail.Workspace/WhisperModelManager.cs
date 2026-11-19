using System.Security.Cryptography;

namespace FrameTrail.Workspace;

public sealed record WhisperModelDescriptor(
    string Id,
    string DisplayName,
    string FileName,
    long ExpectedBytes,
    string? Sha256,
    string PrimaryUrl,
    string MirrorUrl);

public sealed record WhisperDownloadProgress(
    string ModelId,
    long BytesDownloaded,
    long TotalBytes,
    double Percent,
    string Status);

/// <summary>
/// Manages local quantized Whisper GGML models under <c>%LOCALAPPDATA%\FrameTrail\models\whisper\</c>
/// with HuggingFace + mirror fallback, streaming progress, SHA-256 verification, and atomic staging.
/// </summary>
public sealed class WhisperModelManager
{
    public const string SmallModelId = "small-q5";
    public const string LargeTurboModelId = "large-v3-turbo-q5_0";

    private static readonly IReadOnlyList<WhisperModelDescriptor> DefaultCatalog =
    [
        new WhisperModelDescriptor(
            Id: SmallModelId,
            DisplayName: "Whisper Small (Q5_1, ~190 MB)",
            FileName: "ggml-small-q5_1.bin",
            ExpectedBytes: 190_085_487L,
            Sha256: "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb",
            PrimaryUrl: "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin",
            MirrorUrl: "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin"),
        new WhisperModelDescriptor(
            Id: LargeTurboModelId,
            DisplayName: "Whisper Large v3 Turbo (Q5_0, ~574 MB)",
            FileName: "ggml-large-v3-turbo-q5_0.bin",
            ExpectedBytes: 574_041_195L,
            Sha256: "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2",
            PrimaryUrl: "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin",
            MirrorUrl: "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin")
    ];

    private readonly IReadOnlyList<WhisperModelDescriptor> catalog;
    private readonly HttpClient httpClient;

    public string ModelsDirectory { get; }

    public WhisperModelManager(
        string? modelsDirectory = null,
        HttpClient? httpClient = null,
        IReadOnlyList<WhisperModelDescriptor>? customCatalog = null)
    {
        ModelsDirectory = Path.GetFullPath(modelsDirectory ?? GetDefaultModelsDirectory());
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        catalog = customCatalog is { Count: > 0 } ? customCatalog : DefaultCatalog;
    }

    public static string GetDefaultModelsDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".frametrail");
        }
        return Path.Combine(localAppData, "FrameTrail", "models", "whisper");
    }

    public IReadOnlyList<WhisperModelDescriptor> GetCatalog() => catalog;

    public WhisperModelDescriptor GetDescriptor(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            throw new ArgumentException("Whisper model ID is required.", nameof(modelId));
        var normalized = modelId.Trim();
        foreach (var item in catalog)
        {
            if (string.Equals(item.Id, normalized, StringComparison.OrdinalIgnoreCase))
                return item;
        }
        throw new ArgumentException($"Unknown Whisper model ID: '{modelId}'.", nameof(modelId));
    }

    public string GetModelPath(string modelId)
    {
        var desc = GetDescriptor(modelId);
        return Path.Combine(ModelsDirectory, desc.FileName);
    }

    public bool IsModelInstalled(string modelId)
    {
        var desc = GetDescriptor(modelId);
        var path = Path.Combine(ModelsDirectory, desc.FileName);
        if (!File.Exists(path)) return false;
        var info = new FileInfo(path);
        if (info.Length <= 0) return false;
        if (desc.ExpectedBytes > 0 && info.Length != desc.ExpectedBytes) return false;
        return true;
    }

    public bool DeleteModel(string modelId)
    {
        var path = GetModelPath(modelId);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    public async Task<string> EnsureModelAsync(
        string modelId,
        IProgress<WhisperDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var desc = GetDescriptor(modelId);
        var targetPath = Path.Combine(ModelsDirectory, desc.FileName);
        if (IsModelInstalled(desc.Id))
        {
            long existingSize = new FileInfo(targetPath).Length;
            progress?.Report(new WhisperDownloadProgress(desc.Id, existingSize, existingSize, 100.0, "installed"));
            return targetPath;
        }

        Directory.CreateDirectory(ModelsDirectory);

        var urls = new List<string>();
        if (!string.IsNullOrWhiteSpace(desc.PrimaryUrl)) urls.Add(desc.PrimaryUrl);
        if (!string.IsNullOrWhiteSpace(desc.MirrorUrl) && !string.Equals(desc.MirrorUrl, desc.PrimaryUrl, StringComparison.OrdinalIgnoreCase))
            urls.Add(desc.MirrorUrl);

        if (urls.Count == 0)
            throw new InvalidOperationException($"No download URLs configured for Whisper model '{desc.Id}'.");

        Exception? lastError = null;
        for (int attempt = 0; attempt < urls.Count; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string url = urls[attempt];
            string tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await DownloadAndVerifyAsync(desc, url, tempPath, progress, cancellationToken).ConfigureAwait(false);
                File.Move(tempPath, targetPath, overwrite: true);
                long finalSize = new FileInfo(targetPath).Length;
                progress?.Report(new WhisperDownloadProgress(desc.Id, finalSize, finalSize, 100.0, "installed"));
                return targetPath;
            }
            catch (OperationCanceledException)
            {
                TryDeleteFile(tempPath);
                throw;
            }
            catch (Exception ex)
            {
                TryDeleteFile(tempPath);
                lastError = ex;
            }
        }

        throw new IOException(
            $"Failed to download Whisper model '{desc.Id}' from all configured endpoints: {lastError?.Message}",
            lastError);
    }

    private async Task DownloadAndVerifyAsync(
        WhisperModelDescriptor desc,
        string url,
        string tempPath,
        IProgress<WhisperDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new WhisperDownloadProgress(desc.Id, 0, desc.ExpectedBytes, 0.0, "downloading"));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long totalBytes = response.Content.Headers.ContentLength ?? desc.ExpectedBytes;
        await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        byte[] buffer = new byte[81920];
        long downloaded = 0;
        long lastReportedBytes = -1;
        int read;
        while ((read = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            hasher.AppendData(buffer, 0, read);
            downloaded += read;

            if (downloaded - lastReportedBytes >= 262_144 || (totalBytes > 0 && downloaded >= totalBytes))
            {
                lastReportedBytes = downloaded;
                double pct = totalBytes > 0 ? Math.Clamp(downloaded * 100.0 / totalBytes, 0.0, 99.5) : 0.0;
                progress?.Report(new WhisperDownloadProgress(desc.Id, downloaded, totalBytes, pct, "downloading"));
            }
        }

        await destStream.FlushAsync(cancellationToken).ConfigureAwait(false);

        if (downloaded <= 0)
            throw new InvalidDataException($"Downloaded Whisper model '{desc.Id}' is empty.");
        if (desc.ExpectedBytes > 0 && downloaded != desc.ExpectedBytes)
            throw new InvalidDataException($"Whisper model '{desc.Id}' size mismatch: expected {desc.ExpectedBytes} bytes, got {downloaded} bytes.");

        progress?.Report(new WhisperDownloadProgress(desc.Id, downloaded, totalBytes > 0 ? totalBytes : downloaded, 99.8, "verifying"));
        string actualHash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(desc.Sha256) &&
            !string.Equals(actualHash, desc.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Whisper model '{desc.Id}' SHA-256 verification failed. Expected {desc.Sha256}, actual {actualHash}.");
        }
    }

    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Ignore cleanup errors.
        }
    }
}
