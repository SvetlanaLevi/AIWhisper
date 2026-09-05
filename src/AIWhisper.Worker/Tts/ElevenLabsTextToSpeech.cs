using System.Net.Http.Json;
using System.Text.Json;
using AIWhisper.Worker.Configuration;
using AIWhisper.Worker.Logging;

namespace AIWhisper.Worker.Tts;

/// <summary>
/// Direct adapter for the ElevenLabs text-to-speech API. It sends each
/// dialogue to the public HTTPS API and saves the returned audio locally.
/// </summary>
public sealed class ElevenLabsTextToSpeech : ITextToSpeech, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly TtsOptions _options;
    private readonly string _audioDirectory;
    private readonly IWorkerLog _log;
    private readonly IAudioPlayback? _audioPlayback;
    private readonly PreSpeechCueOptions _preSpeechCueOptions;

    public ElevenLabsTextToSpeech(
        TtsOptions options,
        string audioDirectory,
        IWorkerLog log,
        HttpClient? httpClient = null,
        IAudioPlayback? audioPlayback = null,
        VoiceEffectsOptions? voiceEffectsOptions = null,
        PreSpeechCueOptions? preSpeechCueOptions = null)
    {
        _options = options;
        _audioDirectory = audioDirectory;
        _log = log;
        _audioPlayback = options.PlayOnWindows
            ? audioPlayback ?? new WindowsAudioPlayback(new PsychicDoubleVoiceEffectProcessor(voiceEffectsOptions ?? new VoiceEffectsOptions()))
            : null;
        _preSpeechCueOptions = preSpeechCueOptions ?? new PreSpeechCueOptions();
        Directory.CreateDirectory(_audioDirectory);

        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress = new Uri(_options.BaseUrl, UriKind.Absolute);
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds));

        var apiKey = Environment.GetEnvironmentVariable(_options.ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"{_options.ApiKeyEnvironmentVariable} is not set.");
        }

        _httpClient.DefaultRequestHeaders.Add("xi-api-key", apiKey);
    }

    public async Task<AudioResult> SynthesizeAsync(string text, VoiceContext context, CancellationToken cancellationToken)
    {
        var voice = context.Voice ?? _options.Voice;
        if (string.IsNullOrWhiteSpace(voice))
        {
            throw new InvalidOperationException("No ElevenLabs voice ID is configured.");
        }

        var requestBody = new
        {
            text,
            model_id = _options.Model,
        };

        var endpoint = $"/v1/text-to-speech/{Uri.EscapeDataString(voice)}?output_format={Uri.EscapeDataString(_options.OutputFormat)}";
        _log.Info($"requesting ElevenLabs speech for dialogue {context.DialogueId} ({text.Length} character(s), model '{_options.Model}', format '{_options.OutputFormat}')");
        using var response = await _httpClient.PostAsJsonAsync(endpoint, requestBody, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"ElevenLabs TTS request failed with {(int)response.StatusCode}: {GetErrorMessage(error)}", null, response.StatusCode);
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        var fileName = BuildFileName(context);
        var filePath = Path.Combine(_audioDirectory, fileName);
        await File.WriteAllBytesAsync(filePath, bytes, cancellationToken);

        _log.Info($"synthesized audio for dialogue {context.DialogueId} -> {fileName}");

        if (_audioPlayback is not null)
        {
            await PlayPreSpeechCueAsync(cancellationToken);
            _log.Info($"playing dialogue {context.DialogueId} through the Windows default audio device");
            await _audioPlayback.PlayAsync(filePath, cancellationToken);
            _log.Info($"finished playing dialogue {context.DialogueId}");
        }

        return new AudioResult(filePath, GetFileExtension(_options.OutputFormat));
    }

    private async Task PlayPreSpeechCueAsync(CancellationToken cancellationToken)
    {
        if (!_preSpeechCueOptions.Enabled || _audioPlayback is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_preSpeechCueOptions.FilePath))
        {
            _log.Warn("pre-speech cue is enabled but PreSpeechCue:FilePath is empty; skipping cue");
            return;
        }

        var cuePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(_preSpeechCueOptions.FilePath));
        if (!File.Exists(cuePath))
        {
            _log.Warn($"pre-speech cue file was not found at '{cuePath}'; skipping cue");
            return;
        }

        _log.Info($"playing pre-speech cue '{Path.GetFileName(cuePath)}'");
        await _audioPlayback.PlayUnprocessedAsync(
            cuePath,
            _preSpeechCueOptions.Volume,
            _preSpeechCueOptions.DurationMs,
            cancellationToken);
    }

    private string BuildFileName(VoiceContext context)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
        var safeDialogueId = string.Join("_", context.DialogueId.Split(Path.GetInvalidFileNameChars()));
        return $"{context.CampaignId}_{safeDialogueId}_{timestamp}.{GetFileExtension(_options.OutputFormat)}";
    }

    private static string GetFileExtension(string outputFormat)
        => outputFormat.Split('_', 2)[0].ToLowerInvariant();

    private static string GetErrorMessage(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("detail", out var detail))
            {
                return detail.ToString();
            }
        }
        catch (JsonException)
        {
            // Fall back to the raw response below.
        }

        return responseBody.Length <= 512 ? responseBody : responseBody[..512];
    }

    public void Dispose() => _httpClient.Dispose();
}
