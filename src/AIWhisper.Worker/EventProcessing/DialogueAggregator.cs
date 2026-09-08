using System.Text.Json;
using System.Threading.Channels;
using AIWhisper.Worker.Logging;

namespace AIWhisper.Worker.EventProcessing;

/// <summary>
/// Correlates a timestamp-ordered event stream (for one campaign) into
/// per-dialogue state, keyed by (campaignId, dialogueId). Multiple dialogues
/// can be active at once. After an end, emits a batch once no new dialogue.start
/// has arrived for the end delay. Other events do not extend the window.
/// </summary>
public sealed class DialogueAggregator : IDisposable
{
    private readonly TimeSpan _endDelay;
    private readonly IWorkerLog _log;
    private readonly Dictionary<DialogueKey, DialogueState> _active = new();
    private readonly Dictionary<DialogueKey, DateTime> _recentlyCompleted = new();
    private readonly TimeSpan _recentlyCompletedRetention;
    private readonly object _gate = new();
    private long _generation;
    private readonly HashSet<DialogueKey> _pendingBatch = new();
    private readonly TimeProvider _timeProvider;
    private readonly ITimer _flushTimer;
    private long _windowStarted;
    private bool _closed;
    private readonly Channel<DialogueState> _completed = Channel.CreateUnbounded<DialogueState>();

    /// <summary>Raised for a session.start event, with (player, region) as reported.</summary>
    public event Action<string, string>? SessionStartReceived;

    public DialogueAggregator(TimeSpan endDelay, IWorkerLog log, TimeSpan? recentlyCompletedRetention = null,
        TimeProvider? timeProvider = null)
    {
        _endDelay = endDelay;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _flushTimer = _timeProvider.CreateTimer(_ => FlushBatch(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _log = log;
        _recentlyCompletedRetention = recentlyCompletedRetention ?? TimeSpan.FromMinutes(30);
    }

    public ChannelReader<DialogueState> Completed => _completed.Reader;

    public void Reset(long generation)
    {
        lock (_gate)
        {
            _generation = generation;
            _pendingBatch.Clear();
            if (!_closed) _flushTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _active.Clear();
            _recentlyCompleted.Clear();
            while (_completed.Reader.TryRead(out _)) { }
        }
    }

    public void Handle(WorkerEvent evt)
    {
        switch (evt.Type)
        {
            case "session.start":
                HandleSessionStart(evt);
                return;
            case "level.started":
                HandleLevelStarted(evt);
                return;
            case "dialogue.start":
                HandleDialogueStart(evt);
                return;
            case "dialogue.speakers":
                HandleSpeakers(evt);
                return;
            case "dialogue.line":
            case "dialogue.choice":
                HandleDialogueContent(evt);
                return;
            case "dialogue.end":
                HandleDialogueEnd(evt);
                return;
            default:
                _log.Info($"unknown event type '{evt.Type}' - ignored by the aggregator (already recorded in worker.log upstream)");
                return;
        }
    }

    private void HandleSessionStart(WorkerEvent evt)
    {
        string? player = null;
        string? region = null;
        if (evt.Data.ValueKind == JsonValueKind.Object)
        {
            if (evt.Data.TryGetProperty("player", out var p) && p.ValueKind == JsonValueKind.String) player = p.GetString();
            if (evt.Data.TryGetProperty("region", out var r) && r.ValueKind == JsonValueKind.String) region = r.GetString();
        }

        if (player is not null || region is not null)
        {
            SessionStartReceived?.Invoke(player ?? string.Empty, region ?? string.Empty);
        }
    }

    private void HandleDialogueStart(WorkerEvent evt)
    {
        if (evt.DialogueId is null)
        {
            _log.Warn("dialogue.start without a dialogueId - ignored");
            return;
        }

        var key = new DialogueKey(evt.CampaignId, evt.DialogueId);
        lock (_gate)
        {
            if (WasRecentlyCompletedLocked(key))
            {
                _log.Warn($"late 'dialogue.start' for already-completed dialogue {evt.DialogueId} - ignored");
                return;
            }

            var state = GetOrCreateLocked(key);
            state.StartTime = evt.Timestamp;
            state.Status = DialogueStatus.Active;
            if (evt.Data.ValueKind == JsonValueKind.Object &&
                evt.Data.TryGetProperty("dialogueResource", out var resource) &&
                resource.ValueKind == JsonValueKind.String)
            {
                state.DialogueResource = resource.GetString();
            }
            state.Events.Add(evt);
            if (_pendingBatch.Count > 0 && !_closed)
            {
                _pendingBatch.Add(key);
                RestartWindowLocked();
            }
            _log.Info($"dialogue {evt.DialogueId} started");
        }
    }

    private void HandleSpeakers(WorkerEvent evt)
    {
        if (evt.DialogueId is null)
        {
            _log.Warn("dialogue.speakers without a dialogueId - ignored");
            return;
        }

        var key = new DialogueKey(evt.CampaignId, evt.DialogueId);
        lock (_gate)
        {
            if (WasRecentlyCompletedLocked(key))
            {
                _log.Warn($"late 'dialogue.speakers' for already-completed dialogue {evt.DialogueId} - ignored");
                return;
            }

            var state = GetOrCreateLocked(key);
            var speakers = new List<SpeakerInfo>();
            if (evt.Data.ValueKind == JsonValueKind.Object &&
                evt.Data.TryGetProperty("speakers", out var speakersProp) &&
                speakersProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in speakersProp.EnumerateArray())
                {
                    var name = s.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                    string? entityUuid = s.TryGetProperty("entityUuid", out var u) ? u.GetString() : null;
                    speakers.Add(new SpeakerInfo(name, entityUuid));
                }
            }
            state.Speakers = speakers;
            state.Events.Add(evt);
            if (_pendingBatch.Count > 0) _pendingBatch.Add(key);
        }
    }

    private void HandleDialogueContent(WorkerEvent evt)
    {
        if (evt.DialogueId is null)
        {
            _log.Warn($"'{evt.Type}' without a dialogueId - ignored");
            return;
        }

        var key = new DialogueKey(evt.CampaignId, evt.DialogueId);
        lock (_gate)
        {
            if (WasRecentlyCompletedLocked(key))
            {
                _log.Warn($"late '{evt.Type}' for already-completed dialogue {evt.DialogueId} - ignored");
                return;
            }

            // GetOrCreate covers the "missing dialogue.start" case: content is
            // still captured under a freshly-created active state.
            var state = GetOrCreateLocked(key);
            state.Events.Add(evt);
            if (_pendingBatch.Count > 0) _pendingBatch.Add(key);
        }
    }

    private void HandleDialogueEnd(WorkerEvent evt)
    {
        if (evt.DialogueId is null)
        {
            _log.Warn("dialogue.end without a dialogueId - ignored");
            return;
        }

        var key = new DialogueKey(evt.CampaignId, evt.DialogueId);
        lock (_gate)
        {
            if (WasRecentlyCompletedLocked(key))
            {
                _log.Warn($"late 'dialogue.end' for already-completed dialogue {evt.DialogueId} - ignored");
                return;
            }

            var state = GetOrCreateLocked(key);
            state.EndTime = evt.Timestamp;
            state.Status = DialogueStatus.EndPending;
            state.Events.Add(evt);
            if (!_closed)
            {
                var startWindow = _pendingBatch.Count == 0;
                _pendingBatch.Add(key);
                if (startWindow) RestartWindowLocked();
            }
            _log.Info($"dialogue {evt.DialogueId} ended; included in the pending AI batch");
        }
    }

    private void HandleLevelStarted(WorkerEvent evt)
    {
        if (evt.Data.ValueKind != JsonValueKind.Object ||
            !evt.Data.TryGetProperty("region", out var region) ||
            region.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var regionName = region.GetString();
        if (!string.IsNullOrWhiteSpace(regionName))
        {
            SessionStartReceived?.Invoke(string.Empty, regionName);
        }
    }

    private void RestartWindowLocked()
    {
        _windowStarted = _timeProvider.GetTimestamp();
        _flushTimer.Change(_endDelay, Timeout.InfiniteTimeSpan);
    }

    private void FlushBatch()
    {
        lock (_gate)
        {
            if (_closed || _pendingBatch.Count == 0) return;
            // A callback queued before a start/reset must respect the new deadline.
            var remaining = _endDelay - _timeProvider.GetElapsedTime(_windowStarted);
            if (remaining > TimeSpan.Zero)
            {
                _flushTimer.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }

            var states = _pendingBatch.Select(key => _active[key]).ToList();
            var batch = new DialogueState
            {
                CampaignId = states[0].CampaignId,
                // Keep a real ID for existing history, memory and audio file naming.
                DialogueId = states[0].DialogueId,
                Generation = _generation,
                DialogueResource = states.Count == 1 ? states[0].DialogueResource : null,
                StartTime = states.Min(state => state.StartTime),
                EndTime = states.All(state => state.Status == DialogueStatus.EndPending)
                    ? states.Max(state => state.EndTime) : null,
                Speakers = states.SelectMany(state => state.Speakers).Distinct().ToArray(),
                Status = DialogueStatus.Completed,
            };
            batch.Events.AddRange(states.SelectMany(state => state.Events).OrderBy(evt => evt.Timestamp));
            PruneRecentlyCompletedLocked();
            foreach (var state in states)
            {
                if (state.Status == DialogueStatus.EndPending)
                {
                    _active.Remove(state.Key);
                    _recentlyCompleted[state.Key] = DateTime.UtcNow;
                }
                else
                {
                    // Snapshot an ongoing dialogue without closing it or sending its content twice.
                    state.Events.Clear();
                }
            }
            _pendingBatch.Clear();
            // Publish under the Reset lock so old batches cannot reappear after a load.
            _completed.Writer.TryWrite(batch);
        }
    }

    private DialogueState GetOrCreateLocked(DialogueKey key)
    {
        if (!_active.TryGetValue(key, out var state))
        {
            state = new DialogueState { CampaignId = key.CampaignId, DialogueId = key.DialogueId, Generation = _generation };
            _active[key] = state;
        }
        return state;
    }

    private bool WasRecentlyCompletedLocked(DialogueKey key) => _recentlyCompleted.ContainsKey(key);

    private void PruneRecentlyCompletedLocked()
    {
        if (_recentlyCompleted.Count == 0) return;
        var cutoff = DateTime.UtcNow - _recentlyCompletedRetention;
        foreach (var k in _recentlyCompleted.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
        {
            _recentlyCompleted.Remove(k);
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _flushTimer.Dispose();
            _pendingBatch.Clear();
            _completed.Writer.TryComplete();
        }
    }

    public void Dispose() => Complete();
}
