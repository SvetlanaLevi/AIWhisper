using AIWhisper.Worker.AI;
using AIWhisper.Worker.Configuration;
using AIWhisper.Worker.Conversation;
using AIWhisper.Worker.Development;
using AIWhisper.Worker.EventProcessing;
using AIWhisper.Worker.FileMonitoring;
using AIWhisper.Worker.Logging;
using AIWhisper.Worker.Memory;
using AIWhisper.Worker.Knowledge;
using AIWhisper.Worker.Persistence;
using AIWhisper.Worker.Tts;

namespace AIWhisper.Worker.Pipeline;

/// <summary>
/// Owns everything scoped to a single campaign: its two log readers, its
/// merger/aggregator, its conversation state, and its checkpoint. Nothing
/// here is ever shared with another campaign's runtime.
/// </summary>
public sealed class CampaignRuntime : IAsyncDisposable
{
    private readonly string _campaignId;
    private readonly string _campaignDirectory;
    private readonly WorkerOptions _options;
    private readonly MemoryOptions _memoryOptions;
    private readonly ParasiteDevelopmentPolicy _developmentPolicy;
    private readonly IWorkerLog _log;
    private readonly CheckpointStore _checkpointStore;
    private readonly CampaignMemoryStore _memoryStore;
    private readonly MemoryEventHandler _memoryEventHandler;
    private readonly LogFileWatcher _serverWatcher;
    private readonly LogFileWatcher _clientWatcher;
    private readonly EventMerger _merger;
    private readonly DialogueAggregator _aggregator;
    private readonly CampaignContext _campaignContext;
    private readonly ConversationManager _conversationManager;
    private readonly bool _startAtEnd;

    private CancellationTokenSource? _cts;
    private readonly List<Task> _loopTasks = new();
    private Timer? _checkpointTimer;
    private readonly SemaphoreSlim _checkpointSaveGate = new(1, 1);
    private bool _sessionActive;

    public CampaignRuntime(
        string campaignId,
        string campaignDirectory,
        WorkerOptions options,
        MemoryOptions memoryOptions,
        ParasiteDevelopmentOptions parasiteDevelopmentOptions,
        ICharacterKnowledgeProvider characterKnowledge,
        IWorkerLog log,
        IAIDecisionService aiDecisionService,
        IMemoryEvaluator memoryEvaluator,
        ITextToSpeech textToSpeech,
        string systemPrompt,
        string systemPromptId = "base:unspecified",
        bool startAtEnd = false)
    {
        _campaignId = campaignId;
        _campaignDirectory = campaignDirectory;
        _options = options;
        _memoryOptions = memoryOptions;
        _developmentPolicy = new ParasiteDevelopmentPolicy(parasiteDevelopmentOptions);
        _log = log;
        _startAtEnd = startAtEnd;

        _checkpointStore = new CheckpointStore(Path.Combine(campaignDirectory, options.CheckpointFileName));
        _memoryStore = new CampaignMemoryStore(Path.Combine(campaignDirectory, options.MemoryFileName));

        _serverWatcher = new LogFileWatcher(Path.Combine(campaignDirectory, options.ServerLogFileName), TimeSpan.FromMilliseconds(options.FilePollIntervalMs));
        _clientWatcher = new LogFileWatcher(Path.Combine(campaignDirectory, options.ClientLogFileName), TimeSpan.FromMilliseconds(options.FilePollIntervalMs));
        _serverWatcher.PollFailed += ex => _log.Error("server.log poll failed", ex);
        _clientWatcher.PollFailed += ex => _log.Error("client.log poll failed", ex);

        _merger = new EventMerger(TimeSpan.FromMilliseconds(options.EventOrderingDelayMs));
        _aggregator = new DialogueAggregator(
            TimeSpan.FromMilliseconds(options.DialogueEndDelayMs),
            log,
            TimeSpan.FromMinutes(options.LateEventCompletedRetentionMinutes));

        _campaignContext = new CampaignContext { CampaignId = campaignId, Directory = campaignDirectory };
        _memoryEventHandler = new MemoryEventHandler(_campaignContext, _memoryStore, log,
            _developmentPolicy, () => _aggregator.Reset(_campaignContext.Generation), SaveCheckpointAsync);
        _aggregator.SessionStartReceived += (player, region) =>
        {
            if (!string.IsNullOrEmpty(player)) _campaignContext.Session.Player = player;
            if (!string.IsNullOrEmpty(region))
            {
                _campaignContext.Session.Region = region;
                _log.Info($"campaign {_campaignId}: current region is '{region}'");
                if (_developmentPolicy.TryAdvance(_campaignContext.Development, region, out var previousPhase, out var warning))
                {
                    _log.Info($"campaign {_campaignId}: parasite development advanced {previousPhase} -> {_campaignContext.Development.CurrentPhase} in region {region}");
                }
                else if (warning is not null)
                {
                    _log.Warn($"campaign {_campaignId}: {warning}");
                }
            }
            _ = SaveCheckpointAsync(CancellationToken.None);
        };

        _conversationManager = new ConversationManager(
            _campaignContext,
            new AIContextBuilder(characterKnowledge, memoryOptions),
            aiDecisionService,
            textToSpeech,
            log,
            systemPrompt,
            options.MaxConversationHistoryEntries,
            _memoryStore,
            _memoryOptions,
            _developmentPolicy,
            systemPromptId,
            SaveCheckpointAsync,
            memoryEvaluator,
            options.IgnoredDialogueResources);
    }

    public async Task StartAsync(CancellationToken outerToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        var token = _cts.Token;

        var checkpoint = await _checkpointStore.LoadAsync(token);
        var restoredSession = checkpoint.Session ?? new SessionContext();
        _campaignContext.Session.Player = restoredSession.Player;
        _campaignContext.Session.Region = restoredSession.Region;
        _campaignContext.Development = checkpoint.Development ?? new ParasiteDevelopmentState();
        _campaignContext.LastAppliedSystemInstructions = checkpoint.LastAppliedSystemInstructions?.ToArray() ?? [];
        foreach (var fingerprint in checkpoint.ProcessedDialogueFingerprints ?? [])
        {
            if (!string.IsNullOrWhiteSpace(fingerprint))
                _campaignContext.ProcessedDialogueFingerprints.TryAdd(fingerprint, 0);
        }
        if (_developmentPolicy.EnsureInitialized(_campaignContext.Development, out var developmentWarning))
        {
            _log.Info($"campaign {_campaignId}: initialized parasite development phase '{_campaignContext.Development.CurrentPhase}'");
            await SaveCheckpointAsync(token);
        }
        else if (string.IsNullOrWhiteSpace(_campaignContext.Development.CurrentPhase))
        {
            throw new InvalidOperationException(
                developmentWarning ?? "ParasiteDevelopment:PhaseSequence must contain at least one phase.");
        }
        else if (developmentWarning is not null)
        {
            _log.Warn($"campaign {_campaignId}: {developmentWarning}");
        }
        var memoryAlreadyExists = _memoryStore.Exists;
        try
        {
            _campaignContext.Memory = await _memoryStore.LoadAsync(token);
            var duplicateMemoryCount = MemoryOperationApplier.DeduplicateExisting(_campaignContext.Memory);
            if (duplicateMemoryCount > 0)
            {
                await _memoryStore.SaveAsync(_campaignContext.Memory, token);
                _log.Info($"campaign {_campaignId}: removed {duplicateMemoryCount} duplicate long-term memory item(s)");
            }
            _log.Info(memoryAlreadyExists
                ? $"loaded campaign memory for {_campaignId}"
                : $"started with new empty campaign memory for {_campaignId}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _campaignContext.Memory = new CampaignMemory();
            _log.Error($"failed to load campaign memory for {_campaignId}; using empty memory", ex);
        }
        if (_startAtEnd)
        {
            _serverWatcher.StartAtEnd();
            _clientWatcher.StartAtEnd();
            _log.Info($"campaign {_campaignId}: existing logs skipped; waiting for a new session.start event");
        }
        else
        {
            if (checkpoint.Files.TryGetValue(_options.ServerLogFileName, out var serverCheckpoint))
                _serverWatcher.RestoreCheckpoint(serverCheckpoint);
            if (checkpoint.Files.TryGetValue(_options.ClientLogFileName, out var clientCheckpoint))
                _clientWatcher.RestoreCheckpoint(clientCheckpoint);
        }

        _serverWatcher.Start(token);
        _clientWatcher.Start(token);

        _loopTasks.Add(PumpLinesAsync(_serverWatcher, "server", token));
        _loopTasks.Add(PumpLinesAsync(_clientWatcher, "client", token));
        _loopTasks.Add(PumpMergedEventsAsync(token));
        _loopTasks.Add(PumpCompletedDialoguesAsync(token));

        _checkpointTimer = new Timer(_ => _ = SaveCheckpointAsync(CancellationToken.None), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        _log.Info($"campaign {_campaignId} runtime started; watching '{_options.ServerLogFileName}' and '{_options.ClientLogFileName}' in '{_campaignDirectory}'");
    }

    private async Task PumpLinesAsync(LogFileWatcher watcher, string sourceLabel, CancellationToken token)
    {
        await foreach (var line in watcher.Lines.ReadAllAsync(token))
        {
            if (EventParser.TryParse(line, _campaignId, out var evt, out var error))
            {
                _log.Debug($"[{sourceLabel}] received event '{evt!.Type}'{(evt.DialogueId is null ? string.Empty : $" for dialogue {evt.DialogueId}")}");
                _merger.Publish(evt!);
            }
            else if (error is not null)
            {
                _log.Warn($"[{sourceLabel}] {DescribeParseError(error)}");
            }
        }
    }

    private static string DescribeParseError(EventParseError error) => error.Kind switch
    {
        EventParseErrorKind.CampaignIdMismatch => error.Message,
        _ => $"malformed event ({error.Kind}): {error.Message}",
    };

    private async Task PumpMergedEventsAsync(CancellationToken token)
    {
        await foreach (var evt in _merger.Output.ReadAllAsync(token))
        {
            try
            {
                if (evt.Type == "session.start")
                {
                    if (!_sessionActive)
                        _log.Info($"campaign {_campaignId}: live session started; dialogue processing activated");
                    _sessionActive = true;
                }
                if (!_sessionActive)
                {
                    _log.Debug($"campaign {_campaignId}: ignored '{evt.Type}' while waiting for session.start");
                    continue;
                }
                if (!await _memoryEventHandler.HandleAsync(evt, token))
                    _aggregator.Handle(evt);
            }
            catch (Exception ex)
            {
                _log.Error($"failed to aggregate event of type '{evt.Type}' - continuing", ex);
            }
        }
    }

    private async Task PumpCompletedDialoguesAsync(CancellationToken token)
    {
        await foreach (var dialogue in _aggregator.Completed.ReadAllAsync(token))
        {
            _log.Info($"dialogue {dialogue.DialogueId} completed with {dialogue.Events.Count} event(s) - handing off to conversation manager");
            try
            {
                await _conversationManager.ProcessAsync(dialogue, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Error($"unexpected error processing dialogue {dialogue.DialogueId}", ex);
            }
        }
    }

    private async Task SaveCheckpointAsync(CancellationToken token)
    {
        await _checkpointSaveGate.WaitAsync(token);
        try
        {
            var checkpoint = new WorkerCheckpoint
            {
                CampaignId = _campaignId,
                Files = new Dictionary<string, FileCheckpoint>
                {
                    [_options.ServerLogFileName] = _serverWatcher.CurrentCheckpoint(),
                    [_options.ClientLogFileName] = _clientWatcher.CurrentCheckpoint(),
                },
                Session = new SessionContext
                {
                    Player = _campaignContext.Session.Player,
                    Region = _campaignContext.Session.Region,
                },
                Development = _campaignContext.Development,
                LastAppliedSystemInstructions = _campaignContext.LastAppliedSystemInstructions.ToList(),
                ProcessedDialogueFingerprints = _campaignContext.ProcessedDialogueFingerprints.Keys.ToList(),
            };
            await _checkpointStore.SaveAsync(checkpoint, token);
        }
        catch (Exception ex)
        {
            _log.Error("failed to save checkpoint", ex);
        }
        finally
        {
            _checkpointSaveGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _checkpointTimer?.Dispose();
        _cts?.Cancel();

        await _serverWatcher.DisposeAsync();
        await _clientWatcher.DisposeAsync();
        _merger.Complete();
        _aggregator.Complete();

        foreach (var task in _loopTasks)
        {
            try { await task; }
            catch (OperationCanceledException) { }
        }

        await SaveCheckpointAsync(CancellationToken.None);
        _merger.Dispose();
        _aggregator.Dispose();
        _cts?.Dispose();
        _checkpointSaveGate.Dispose();

        _log.Info($"campaign {_campaignId} runtime stopped");
    }
}
