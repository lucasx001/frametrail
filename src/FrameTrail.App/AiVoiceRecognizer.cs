using Windows.Globalization;
using Windows.Media.SpeechRecognition;

namespace FrameTrail.App;

/// <summary>
/// Thread-safe wrapper around <see cref="SpeechRecognizer"/> for continuous voice dictation
/// in the AI Copilot panel, with automatic fallback notification when native speech recognition
/// is unavailable or disabled by OS privacy settings.
/// </summary>
internal sealed class AiVoiceRecognizer : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private SpeechRecognizer? recognizer;
    private bool isListening;
    private bool disposed;

    internal event Action<string, bool>? TranscriptReceived;
    internal event Action<string, string?, bool>? StateChanged;

    internal async Task StartAsync(string? languageTag)
    {
        if (disposed) return;

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;

            await StopInternalAsync().ConfigureAwait(false);

            SpeechRecognizer? created = null;
            string? tag = NormalizeLanguageTag(languageTag);
            if (!string.IsNullOrWhiteSpace(tag))
            {
                try
                {
                    if (Language.IsWellFormed(tag))
                    {
                        created = new SpeechRecognizer(new Language(tag));
                    }
                }
                catch
                {
                    created = null;
                }
            }

            created ??= new SpeechRecognizer();
            recognizer = created;

            recognizer.Constraints.Add(
                new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));

            var compilation = await recognizer.CompileConstraintsAsync();
            if (compilation.Status != SpeechRecognitionResultStatus.Success)
            {
                string statusText = $"Speech constraint compilation failed: {compilation.Status}";
                await StopInternalAsync().ConfigureAwait(false);
                StateChanged?.Invoke("error", statusText, true);
                return;
            }

            recognizer.HypothesisGenerated += OnHypothesisGenerated;
            recognizer.ContinuousRecognitionSession.ResultGenerated += OnResultGenerated;
            recognizer.ContinuousRecognitionSession.Completed += OnSessionCompleted;

            await recognizer.ContinuousRecognitionSession.StartAsync();
            isListening = true;
            StateChanged?.Invoke("listening", null, false);
        }
        catch (Exception ex)
        {
            await StopInternalAsync().ConfigureAwait(false);
            StateChanged?.Invoke("error", ex.Message, true);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task StopAsync()
    {
        if (disposed) return;

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool wasListening = isListening;
            await StopInternalAsync().ConfigureAwait(false);
            if (wasListening)
            {
                StateChanged?.Invoke("stopped", null, false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task StopInternalAsync()
    {
        isListening = false;
        var current = recognizer;
        recognizer = null;
        if (current is null)
            return;

        try
        {
            current.HypothesisGenerated -= OnHypothesisGenerated;
            current.ContinuousRecognitionSession.ResultGenerated -= OnResultGenerated;
            current.ContinuousRecognitionSession.Completed -= OnSessionCompleted;
        }
        catch
        {
        }

        try
        {
            await current.ContinuousRecognitionSession.StopAsync();
        }
        catch
        {
        }

        try
        {
            current.Dispose();
        }
        catch
        {
        }
    }

    private void OnHypothesisGenerated(
        SpeechRecognizer sender,
        SpeechRecognitionHypothesisGeneratedEventArgs args)
    {
        if (disposed) return;
        string? text = args.Hypothesis?.Text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            TranscriptReceived?.Invoke(text, false);
        }
    }

    private void OnResultGenerated(
        SpeechContinuousRecognitionSession sender,
        SpeechContinuousRecognitionResultGeneratedEventArgs args)
    {
        if (disposed) return;
        string? text = args.Result?.Text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            TranscriptReceived?.Invoke(text, true);
        }
    }

    private void OnSessionCompleted(
        SpeechContinuousRecognitionSession sender,
        SpeechContinuousRecognitionCompletedEventArgs args)
    {
        if (disposed) return;
        isListening = false;
        StateChanged?.Invoke("stopped", null, false);
    }

    private static string? NormalizeLanguageTag(string? rawTag)
    {
        if (string.IsNullOrWhiteSpace(rawTag))
            return null;

        string trimmed = rawTag.Trim();
        if (string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.Equals(trimmed, "zh-Hans", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "zh", StringComparison.OrdinalIgnoreCase))
            return "zh-CN";
        if (string.Equals(trimmed, "en", StringComparison.OrdinalIgnoreCase))
            return "en-US";
        return trimmed;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        isListening = false;

        var current = Interlocked.Exchange(ref recognizer, null);
        if (current is not null)
        {
            try
            {
                current.HypothesisGenerated -= OnHypothesisGenerated;
                current.ContinuousRecognitionSession.ResultGenerated -= OnResultGenerated;
                current.ContinuousRecognitionSession.Completed -= OnSessionCompleted;
            }
            catch
            {
            }

            try
            {
                current.Dispose();
            }
            catch
            {
            }
        }
    }
}
