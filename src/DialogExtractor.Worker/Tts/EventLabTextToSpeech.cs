using System.Net.Http.Headers;
using System.Net.Http.Json;
using DialogExtractor.Worker.Configuration;
using DialogExtractor.Worker.Logging;

namespace DialogExtractor.Worker.Tts;

/// <summary>
/// Event Lab is the required TTS provider for the first version. The exact
/// integration surface Event Lab exposes was not specified in the brief, so
/// this implementation assumes a local HTTP endpoint that accepts JSON
/// { text, voice, format, speaker } and returns raw audio bytes. This
/// assumption is isolated entirely behind <see cref="ITextToSpeech"/> -
/// confirm the real contract with Event Lab and adjust only this file if it
/// differs (CLI invocation, named pipe, etc. would all replace only this
/// class).
/// </summary>
public sealed class EventLabTextToSpeech : ITextToSpeech, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly TtsOptions _options;
    private readonly string _audioDirectory;
    private readonly IWorkerLog _log;

    public EventLabTextToSpeech(TtsOptions options, string audioDirectory, IWorkerLog log, HttpClient? httpClient = null)
    {
        _options = options;
        _audioDirectory = audioDirectory;
        _log = log;
        Directory.CreateDirectory(_audioDirectory);

        _httpClient = httpClient ?? new HttpClient();
        if (!string.IsNullOrEmpty(_options.BaseUrl))
        {
            _httpClient.BaseAddress = new Uri(_options.BaseUrl, UriKind.Absolute);
        }
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds));

        if (!string.IsNullOrEmpty(_options.ApiKeyEnvironmentVariable))
        {
            var apiKey = Environment.GetEnvironmentVariable(_options.ApiKeyEnvironmentVariable);
            if (!string.IsNullOrEmpty(apiKey))
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
        }
    }

    public async Task<AudioResult> SynthesizeAsync(string text, VoiceContext context, CancellationToken cancellationToken)
    {
        var endpoint = string.IsNullOrEmpty(_options.Endpoint) ? "/v1/tts" : _options.Endpoint;
        var voice = context.Voice ?? _options.Voice;

        var requestBody = new
        {
            text,
            voice,
            format = _options.Format,
            speaker = context.Speaker,
        };

        using var response = await _httpClient.PostAsJsonAsync(endpoint, requestBody, cancellationToken);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        var fileName = BuildFileName(context);
        var filePath = Path.Combine(_audioDirectory, fileName);
        await File.WriteAllBytesAsync(filePath, bytes, cancellationToken);

        _log.Info($"synthesized audio for dialogue {context.DialogueId} -> {fileName}");

        return new AudioResult(filePath, _options.Format);
    }

    private string BuildFileName(VoiceContext context)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
        var safeDialogueId = string.Join("_", context.DialogueId.Split(Path.GetInvalidFileNameChars()));
        return $"{context.CampaignId}_{safeDialogueId}_{timestamp}.{_options.Format}";
    }

    public void Dispose() => _httpClient.Dispose();
}
