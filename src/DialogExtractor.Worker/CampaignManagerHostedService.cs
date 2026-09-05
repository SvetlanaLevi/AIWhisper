using DialogExtractor.Worker.AI;
using DialogExtractor.Worker.Configuration;
using DialogExtractor.Worker.Logging;
using DialogExtractor.Worker.Pipeline;
using DialogExtractor.Worker.Tts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DialogExtractor.Worker;

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
    private CampaignManager? _campaignManager;

    private const string DefaultSystemPrompt =
        "You are an unseen companion observing a Baldur's Gate 3 conversation. " +
        "Decide whether to speak up with a short, in-character remark, or to stay " +
        "silent - silence is a normal and often better choice. Respond only with " +
        "the requested structured action.";

    public CampaignManagerHostedService(
        IOptions<WorkerOptions> workerOptions,
        IOptions<OpenAIOptions> openAiOptions,
        IOptions<TtsOptions> ttsOptions)
    {
        _workerOptions = workerOptions.Value;
        _openAiOptions = openAiOptions.Value;
        _ttsOptions = ttsOptions.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_workerOptions.RootDirectory);
        var rootLog = new CampaignFileLog(
            Path.Combine(_workerOptions.RootDirectory, "_root-worker.log"),
            alsoWriteToConsole: true);

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
            audioDirectory => new EventLabTextToSpeech(_ttsOptions, audioDirectory, rootLog),
            campaignDirectory => new CampaignFileLog(Path.Combine(campaignDirectory, _workerOptions.WorkerLogFileName)),
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
