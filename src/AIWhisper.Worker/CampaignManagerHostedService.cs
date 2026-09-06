using AIWhisper.Worker.AI;
using AIWhisper.Worker.Configuration;
using AIWhisper.Worker.Logging;
using AIWhisper.Worker.Knowledge;
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
    private readonly AiRequestLoggingOptions _aiRequestLoggingOptions;
    private readonly TtsOptions _ttsOptions;
    private readonly VoiceEffectsOptions _voiceEffectsOptions;
    private readonly PreSpeechCueOptions _preSpeechCueOptions;
    private readonly MemoryOptions _memoryOptions;
    private readonly ParasiteDevelopmentOptions _parasiteDevelopmentOptions;
    private CampaignManager? _campaignManager;

    private const string DefaultSystemPrompt =
        "You are an unseen companion observing a Baldur's Gate 3 conversation. " +
        "Decide whether to speak up with a short, in-character remark, or to stay " +
        "silent - silence is a normal and often better choice. Respond only with " +
        "the requested structured action.";

    public CampaignManagerHostedService(
        IOptions<WorkerOptions> workerOptions,
        IOptions<OpenAIOptions> openAiOptions,
        IOptions<AiRequestLoggingOptions> aiRequestLoggingOptions,
        IOptions<TtsOptions> ttsOptions,
        IOptions<VoiceEffectsOptions> voiceEffectsOptions,
        IOptions<PreSpeechCueOptions> preSpeechCueOptions,
        IOptions<MemoryOptions> memoryOptions,
        IOptions<ParasiteDevelopmentOptions> parasiteDevelopmentOptions)
    {
        _workerOptions = workerOptions.Value;
        _openAiOptions = openAiOptions.Value;
        _aiRequestLoggingOptions = aiRequestLoggingOptions.Value;
        _ttsOptions = ttsOptions.Value;
        _voiceEffectsOptions = voiceEffectsOptions.Value;
        _preSpeechCueOptions = preSpeechCueOptions.Value;
        _memoryOptions = memoryOptions.Value;
        _parasiteDevelopmentOptions = parasiteDevelopmentOptions.Value;
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
        var characterKnowledge = new CharacterKnowledgeProvider(
            Path.Combine(AppContext.BaseDirectory, "Data", "Knowledge", "Characters"),
            rootLog);

        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            rootLog.Error("OPENAI_API_KEY is not set - the worker cannot call the OpenAI Responses API and will exit.");
            throw new InvalidOperationException("OPENAI_API_KEY environment variable must be set.");
        }

        var systemPrompt = await LoadPromptAsync(
            _workerOptions.SystemPromptPath,
            "system",
            DefaultSystemPrompt,
            rootLog,
            stoppingToken);
        var memoryPrompt = await LoadPromptAsync(
            _workerOptions.MemoryPromptPath,
            "memory",
            CampaignMemoryPrompt.DefaultTemplate,
            rootLog,
            stoppingToken);
        await LoadDevelopmentPromptsAsync(_parasiteDevelopmentOptions, rootLog, stoppingToken);

        using var aiRequestLog = CreateAiRequestLog(rootLog);
        IAIDecisionService aiDecisionService = new OpenAIDecisionService(
            apiKey,
            _openAiOptions,
            rootLog,
            aiRequestLog,
            memoryPrompt.Content,
            memoryPrompt.Id);

        _campaignManager = new CampaignManager(
            _workerOptions,
            _memoryOptions,
            _parasiteDevelopmentOptions,
            characterKnowledge,
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
            systemPrompt.Content,
            systemPrompt.Id);

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

    private IAiRequestLog CreateAiRequestLog(IWorkerLog rootLog)
    {
        if (!_aiRequestLoggingOptions.Enabled)
        {
            return NullAiRequestLog.Instance;
        }

        if (string.IsNullOrWhiteSpace(_aiRequestLoggingOptions.FileName) || Path.IsPathRooted(_aiRequestLoggingOptions.FileName))
        {
            rootLog.Warn("AiRequestLogging:FileName must be a relative file name; AI request logging is disabled");
            return NullAiRequestLog.Instance;
        }

        var filePath = Path.Combine(_workerOptions.RootDirectory, _aiRequestLoggingOptions.FileName);
        rootLog.Info($"AI request diagnostic logging is enabled: '{filePath}' (system prompts are excluded)");
        return new AiRequestFileLog(filePath);
    }

    private static async Task<LoadedPrompt> LoadPromptAsync(
        string configuredPath,
        string promptName,
        string fallback,
        IWorkerLog log,
        CancellationToken cancellationToken)
    {
        var path = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(AppContext.BaseDirectory, configuredPath);
        if (!File.Exists(path))
        {
            log.Warn($"{promptName} prompt file was not found at '{path}' - using a built-in default prompt");
            return new LoadedPrompt(fallback, $"built-in:{promptName}");
        }

        return new LoadedPrompt(
            await File.ReadAllTextAsync(path, cancellationToken),
            $"file:{Path.GetFileName(path)}");
    }

    private static async Task LoadDevelopmentPromptsAsync(
        ParasiteDevelopmentOptions options,
        IWorkerLog log,
        CancellationToken cancellationToken)
    {
        options.PhasePrompts.Clear();
        if (!options.Enabled) return;

        foreach (var phase in options.PhaseSequence.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!TryGetValue(options.PromptPaths, phase, out var promptPath))
            {
                log.Warn($"parasite development phase '{phase}' has no configured prompt file; it will be skipped");
                continue;
            }

            var path = Path.IsPathRooted(promptPath)
                ? promptPath
                : Path.Combine(AppContext.BaseDirectory, promptPath);
            if (!File.Exists(path))
            {
                log.Warn($"parasite development prompt for phase '{phase}' was not found at '{path}'; it will be skipped");
                continue;
            }

            var prompt = await File.ReadAllTextAsync(path, cancellationToken);
            if (string.IsNullOrWhiteSpace(prompt))
            {
                log.Warn($"parasite development prompt for phase '{phase}' is empty at '{path}'; it will be skipped");
                continue;
            }

            options.PhasePrompts[phase] = prompt;
        }
    }

    private static bool TryGetValue(IReadOnlyDictionary<string, string> values, string key, out string value)
    {
        foreach (var pair in values)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private sealed record LoadedPrompt(string Content, string Id);
}
