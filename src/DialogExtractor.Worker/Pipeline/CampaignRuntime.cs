using DialogExtractor.Worker.AI;
using DialogExtractor.Worker.Configuration;
using DialogExtractor.Worker.Conversation;
using DialogExtractor.Worker.EventProcessing;
using DialogExtractor.Worker.FileMonitoring;
using DialogExtractor.Worker.Logging;
using DialogExtractor.Worker.Persistence;
using DialogExtractor.Worker.Tts;

namespace DialogExtractor.Worker.Pipeline;

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
    private readonly IWorkerLog _log;
    private readonly CheckpointStore _checkpointStore;
    private readonly LogFileWatcher _serverWatcher;
    private readonly LogFileWatcher _clientWatcher;
    private readonly EventMerger _merger;
    private readonly DialogueAggregator _aggregator;
    private readonly CampaignContext _campaignContext;
    private readonly ConversationManager _conversationManager;

    private CancellationTokenSource? _cts;
    private readonly List<Task> _loopTasks = new();
    private Timer? _checkpointTimer;

    public CampaignRuntime(
        string campaignId,
        string campaignDirectory,
        WorkerOptions options,
        IWorkerLog log,
        IAIDecisionService aiDecisionService,
        ITextToSpeech textToSpeech,
        string systemPrompt)
    {
        _campaignId = campaignId;
        _campaignDirectory = campaignDirectory;
        _options = options;
        _log = log;

        _checkpointStore = new CheckpointStore(Path.Combine(campaignDirectory, options.CheckpointFileName));

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
        _aggregator.SessionStartReceived += (player, region) =>
        {
            if (!string.IsNullOrEmpty(player)) _campaignContext.Session.Player = player;
            if (!string.IsNullOrEmpty(region)) _campaignContext.Session.Region = region;
        };

        _conversationManager = new ConversationManager(
            _campaignContext,
            new AIContextBuilder(),
            aiDecisionService,
            textToSpeech,
            log,
            systemPrompt,
            options.MaxConversationHistoryEntries);
    }

    public async Task StartAsync(CancellationToken outerToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        var token = _cts.Token;

        var checkpoint = await _checkpointStore.LoadAsync(token);
        if (checkpoint.Files.TryGetValue(_options.ServerLogFileName, out var serverCheckpoint))
        {
            _serverWatcher.RestoreCheckpoint(serverCheckpoint);
        }
        if (checkpoint.Files.TryGetValue(_options.ClientLogFileName, out var clientCheckpoint))
        {
            _clientWatcher.RestoreCheckpoint(clientCheckpoint);
        }

        _serverWatcher.Start(token);
        _clientWatcher.Start(token);

        _loopTasks.Add(PumpLinesAsync(_serverWatcher, "server", token));
        _loopTasks.Add(PumpLinesAsync(_clientWatcher, "client", token));
        _loopTasks.Add(PumpMergedEventsAsync(token));
        _loopTasks.Add(PumpCompletedDialoguesAsync(token));

        _checkpointTimer = new Timer(_ => _ = SaveCheckpointAsync(CancellationToken.None), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        _log.Info($"campaign {_campaignId} runtime started ({_campaignDirectory})");
    }

    private async Task PumpLinesAsync(LogFileWatcher watcher, string sourceLabel, CancellationToken token)
    {
        await foreach (var line in watcher.Lines.ReadAllAsync(token))
        {
            if (EventParser.TryParse(line, _campaignId, out var evt, out var error))
            {
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
            };
            await _checkpointStore.SaveAsync(checkpoint, token);
        }
        catch (Exception ex)
        {
            _log.Error("failed to save checkpoint", ex);
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

        _log.Info($"campaign {_campaignId} runtime stopped");
    }
}
