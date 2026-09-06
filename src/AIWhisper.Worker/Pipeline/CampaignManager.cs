using AIWhisper.Worker.AI;
using AIWhisper.Worker.Configuration;
using AIWhisper.Worker.FileMonitoring;
using AIWhisper.Worker.Logging;
using AIWhisper.Worker.Knowledge;
using AIWhisper.Worker.Tts;

namespace AIWhisper.Worker.Pipeline;

/// <summary>
/// Root of the worker: discovers campaign directories under
/// WorkerOptions.RootDirectory and owns one independent CampaignRuntime per
/// campaign. Campaigns never share readers, merger, aggregator, or
/// conversation state.
/// </summary>
public sealed class CampaignManager : IAsyncDisposable
{
    private readonly WorkerOptions _options;
    private readonly MemoryOptions _memoryOptions;
    private readonly ParasiteDevelopmentOptions _parasiteDevelopmentOptions;
    private readonly ICharacterKnowledgeProvider _characterKnowledge;
    private readonly IAIDecisionService _aiDecisionService;
    private readonly Func<string, ITextToSpeech> _ttsFactory;
    private readonly Func<string, IWorkerLog> _logFactory;
    private readonly IWorkerLog _rootLog;
    private readonly string _systemPrompt;
    private readonly string _systemPromptId;

    private readonly Dictionary<string, CampaignRuntime> _runtimes = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private CampaignDirectoryWatcher? _directoryWatcher;
    private Task? _watcherTask;
    private CancellationTokenSource? _cts;

    public CampaignManager(
        WorkerOptions options,
        MemoryOptions memoryOptions,
        ParasiteDevelopmentOptions parasiteDevelopmentOptions,
        ICharacterKnowledgeProvider characterKnowledge,
        IAIDecisionService aiDecisionService,
        Func<string, ITextToSpeech> ttsFactory,
        Func<string, IWorkerLog> logFactory,
        IWorkerLog rootLog,
        string systemPrompt,
        string systemPromptId = "base:unspecified")
    {
        _options = options;
        _memoryOptions = memoryOptions;
        _parasiteDevelopmentOptions = parasiteDevelopmentOptions;
        _characterKnowledge = characterKnowledge;
        _aiDecisionService = aiDecisionService;
        _ttsFactory = ttsFactory;
        _logFactory = logFactory;
        _rootLog = rootLog;
        _systemPrompt = systemPrompt;
        _systemPromptId = systemPromptId;
    }

    public Task StartAsync(CancellationToken outerToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        var token = _cts.Token;

        var existingCampaignCount = Directory.EnumerateDirectories(_options.RootDirectory).Count();
        if (existingCampaignCount == 0)
        {
            _rootLog.Warn("no campaign folders found yet; waiting for DialogExtractor to create one");
        }
        else
        {
            _rootLog.Info($"found {existingCampaignCount} existing campaign folder(s); starting their watchers");
        }

        _directoryWatcher = new CampaignDirectoryWatcher(_options.RootDirectory, TimeSpan.FromMilliseconds(_options.DirectoryPollIntervalMs));
        _directoryWatcher.CampaignDiscovered += campaignId => _ = OnCampaignDiscoveredAsync(campaignId, token);
        _watcherTask = _directoryWatcher.RunAsync(token);

        return Task.CompletedTask;
    }

    private async Task OnCampaignDiscoveredAsync(string campaignId, CancellationToken token)
    {
        CampaignRuntime runtime;
        lock (_gate)
        {
            if (_runtimes.ContainsKey(campaignId)) return;
            var campaignDirectory = Path.Combine(_options.RootDirectory, campaignId);
            var campaignLog = _logFactory(campaignDirectory);
            var audioDirectory = Path.Combine(campaignDirectory, _options.AudioDirectoryName);

            runtime = new CampaignRuntime(
                campaignId,
                campaignDirectory,
                _options,
                _memoryOptions,
                _parasiteDevelopmentOptions,
                _characterKnowledge,
                campaignLog,
                _aiDecisionService,
                _ttsFactory(audioDirectory),
                _systemPrompt,
                _systemPromptId);

            _runtimes[campaignId] = runtime;
        }

        _rootLog.Info($"discovered new campaign '{campaignId}'");
        try
        {
            await runtime.StartAsync(token);
        }
        catch (Exception ex)
        {
            _rootLog.Error($"failed to start runtime for campaign '{campaignId}'", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _directoryWatcher?.Dispose();

        if (_watcherTask is not null)
        {
            try { await _watcherTask; } catch (OperationCanceledException) { }
        }

        List<CampaignRuntime> runtimes;
        lock (_gate)
        {
            runtimes = _runtimes.Values.ToList();
        }

        foreach (var runtime in runtimes)
        {
            await runtime.DisposeAsync();
        }

        _cts?.Dispose();
    }
}
