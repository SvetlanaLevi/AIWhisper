using System.Text.Json;
using System.Threading.Channels;
using AIWhisper.Worker.Logging;

namespace AIWhisper.Worker.EventProcessing;

/// <summary>
/// Correlates a timestamp-ordered event stream (for one campaign) into
/// per-dialogue state, keyed by (campaignId, dialogueId). Multiple dialogues
/// can be active at once; there is no notion of "the current dialogue".
/// </summary>
public sealed class DialogueAggregator : IDisposable
{
    private readonly TimeSpan _endDelay;
    private readonly IWorkerLog _log;
    private readonly Dictionary<DialogueKey, DialogueState> _active = new();
    private readonly Dictionary<DialogueKey, DateTime> _recentlyCompleted = new();
    private readonly TimeSpan _recentlyCompletedRetention;
    private readonly object _gate = new();
    private readonly Channel<DialogueState> _completed = Channel.CreateUnbounded<DialogueState>();

    /// <summary>Raised for a session.start event, with (player, region) as reported.</summary>
    public event Action<string, string>? SessionStartReceived;

    public DialogueAggregator(TimeSpan endDelay, IWorkerLog log, TimeSpan? recentlyCompletedRetention = null)
    {
        _endDelay = endDelay;
        _log = log;
        _recentlyCompletedRetention = recentlyCompletedRetention ?? TimeSpan.FromMinutes(30);
    }

    public ChannelReader<DialogueState> Completed => _completed.Reader;

    public void Handle(WorkerEvent evt)
    {
        switch (evt.Type)
        {
            case "session.start":
                HandleSessionStart(evt);
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
        }

        _ = FinalizeAfterDelayAsync(key);
    }

    private async Task FinalizeAfterDelayAsync(DialogueKey key)
    {
        try
        {
            await Task.Delay(_endDelay);
        }
        catch (Exception ex)
        {
            _log.Error("unexpected error while waiting for the dialogue completion delay", ex);
        }
        FinalizeDialogue(key);
    }

    private void FinalizeDialogue(DialogueKey key)
    {
        DialogueState? state;
        lock (_gate)
        {
            if (!_active.TryGetValue(key, out state)) return;
            if (state.Status != DialogueStatus.EndPending)
            {
                // A later dialogue.start reactivated this key before we got here; leave it alone.
                return;
            }

            state.Events.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            state.Status = DialogueStatus.Completed;
            _active.Remove(key);
            PruneRecentlyCompletedLocked();
            _recentlyCompleted[key] = DateTime.UtcNow;
        }

        _completed.Writer.TryWrite(state);
    }

    private DialogueState GetOrCreateLocked(DialogueKey key)
    {
        if (!_active.TryGetValue(key, out var state))
        {
            state = new DialogueState { CampaignId = key.CampaignId, DialogueId = key.DialogueId };
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

    public void Complete() => _completed.Writer.TryComplete();

    public void Dispose()
    {
        // No unmanaged resources; Complete() is the explicit shutdown signal.
    }
}
