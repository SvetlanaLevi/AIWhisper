using AIWhisper.Worker.AI;
using AIWhisper.Worker.Configuration;
using AIWhisper.Worker.Logging;
using AIWhisper.Worker.Pipeline;
using AIWhisper.Worker.Tts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AIWhisper.Worker;

/// <summary>
/// The single hosted service that drives the whole worker: reads
/// configuration and secrets, wires up the concrete AI/TTS providers, and
/// starts the <see cref="CampaignManager"/>. All the actual dialogue
/// processing logic lives below this composition root and knows nothing
/// about Microsoft.Extensions.Hosting or dependency injection.
/// </summary>
public sealed class CampaignManagerHostedService : BackgroundService
{
    private readonly WorkerOptions _workerOptions;
    private readonly OpenAIOptions _openAiOptions;
    private readonly TtsOptions _ttsOptions;
    private readonly VoiceEffectsOptions _voiceEffectsOptions;
    private readonly PreSpeechCueOptions _preSpeechCueOptions;
    private CampaignManager? _campaignManager;

    private const string DefaultSystemPrompt =
        "You are an unseen companion observing a Baldur's Gate 3 conversation. " +
        "Decide whether to speak up with a short, in-character remark, or to stay " +
        "silent - silence is a normal and often better choice. Respond only with " +
        "the requested structured action.";

    public CampaignManagerHostedService(
        IOptions<WorkerOptions> workerOptions,
        IOptions<OpenAIOptions> openAiOptions,
        IOptions<TtsOptions> ttsOptions,
        IOptions<VoiceEffectsOptions> voiceEffectsOptions,
        IOptions<PreSpeechCueOptions> preSpeechCueOptions)
    {
        _workerOptions = workerOptions.Value;
        _openAiOptions = openAiOptions.Value;
        _ttsOptions = ttsOptions.Value;
        _voiceEffectsOptions = voiceEffectsOptions.Value;
        _preSpeechCueOptions = preSpeechCueOptions.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configuredRootDirectory = Environment.ExpandEnvironmentVariables(_workerOptions.RootDirectory);
        if (configuredRootDirectory.Contains('%') || !Path.IsPathFullyQualified(configuredRootDirectory))
        {
            throw new InvalidOperationException(
                "Worker:RootDirectory must resolve to an absolute path. " +
                "Use a value such as %LOCALAPPDATA%\\Larian Studios\\Baldur's Gate 3\\Script Extender\\DialogExtractor.");
        }

        _workerOptions.RootDirectory = Path.GetFullPath(configuredRootDirectory);
        Directory.CreateDirectory(_workerOptions.RootDirectory);
        var rootLog = new CampaignFileLog(
            Path.Combine(_workerOptions.RootDirectory, "_root-worker.log"),
            alsoWriteToConsole: true);

        rootLog.Info($"AIWhisper is monitoring '{_workerOptions.RootDirectory}' for campaign folders");

        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            rootLog.Error("OPENAI_API_KEY is not set - the worker cannot call the OpenAI Responses API and will exit.");
            throw new InvalidOperationException("OPENAI_API_KEY environment variable must be set.");
        }

        string systemPrompt;
        if (File.Exists(_workerOptions.SystemPromptPath))
        {
            systemPrompt = await File.ReadAllTextAsync(_workerOptions.SystemPromptPath, stoppingToken);
        }
        else
        {
            rootLog.Warn($"system prompt file not found at '{_workerOptions.SystemPromptPath}' - using a built-in default prompt");
            systemPrompt = DefaultSystemPrompt;
        }

        IAIDecisionService aiDecisionService = new OpenAIDecisionService(apiKey, _openAiOptions, rootLog);

        _campaignManager = new CampaignManager(
            _workerOptions,
            aiDecisionService,
            audioDirectory => new ElevenLabsTextToSpeech(
                _ttsOptions,
                audioDirectory,
                rootLog,
                voiceEffectsOptions: _voiceEffectsOptions,
                preSpeechCueOptions: _preSpeechCueOptions),
            campaignDirectory => new CampaignFileLog(
                Path.Combine(campaignDirectory, _workerOptions.WorkerLogFileName),
                alsoWriteToConsole: true),
            rootLog,
            systemPrompt);

        await _campaignManager.StartAsync(stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_campaignManager is not null)
        {
            await _campaignManager.DisposeAsync();
        }
        await base.StopAsync(cancellationToken);
    }
}
