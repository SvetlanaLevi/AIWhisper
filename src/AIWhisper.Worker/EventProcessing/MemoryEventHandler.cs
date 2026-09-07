using System.Text.Json;
using System.Text.Json.Serialization;
using AIWhisper.Worker.Conversation;
using AIWhisper.Worker.Development;
using AIWhisper.Worker.Logging;
using AIWhisper.Worker.Persistence;

namespace AIWhisper.Worker.EventProcessing;

public sealed class MemoryEventHandler(
    CampaignContext campaign,
    CampaignMemoryStore workingStore,
    IWorkerLog log,
    ParasiteDevelopmentPolicy? developmentPolicy = null,
    Action? resetDialogues = null,
    Func<CancellationToken, Task>? saveCheckpoint = null)
{
    public async Task<bool> HandleAsync(WorkerEvent evt, CancellationToken cancellationToken)
    {
        if (evt.Type is not ("save.start" or "memory.load" or "save.end")) return false;
        if (evt.Source != "server" || evt.CampaignId != campaign.CampaignId) return true;
        if (evt.Type == "save.end") return true;
        SnapshotEventData? data;
        try
        {
            data = evt.Data.ValueKind == JsonValueKind.Object ? evt.Data.Deserialize<SnapshotEventData>() : null;
        }
        catch (JsonException ex)
        {
            log.Warn($"campaign {campaign.CampaignId}: invalid {evt.Type} data: {ex.Message}");
            return true;
        }
        if (data is null || (evt.Type == "save.start" && data.SnapshotId is null))
        {
            log.Warn($"campaign {campaign.CampaignId}: invalid {evt.Type} data");
            return true;
        }

        // Cancel before waiting: an old AI/TTS request must not delay a load indefinitely.
        if (evt.Type == "memory.load") campaign.DialogueCancellation.Cancel();
        await campaign.ProcessingGate.WaitAsync(cancellationToken);
        try
        {
            var store = new CampaignSnapshotStore(campaign.Directory);
            if (evt.Type == "save.start")
            {
                await store.SaveAsync(data.SnapshotId!.Value, new CampaignStateSnapshot
                {
                    Memory = campaign.Memory,
                    Development = campaign.Development,
                    Session = campaign.Session,
                }, cancellationToken);
            }
            else
            {
                campaign.Generation++;
                resetDialogues?.Invoke();
                campaign.DialogueCancellation.Dispose();
                campaign.DialogueCancellation = new CancellationTokenSource();
                var snapshot = data.SnapshotId is Guid id ? await store.LoadAsync(id, cancellationToken) : null;
                if (data.SnapshotId is not null && snapshot is null)
                    log.Warn($"campaign {campaign.CampaignId}: snapshot {data.SnapshotId} is missing or invalid; using empty state");
                snapshot ??= new CampaignStateSnapshot();
                campaign.Memory = snapshot.Memory;
                campaign.Development = snapshot.Development;
                campaign.Session.Player = snapshot.Session.Player;
                campaign.Session.Region = snapshot.Session.Region;
                campaign.History.Clear();
                campaign.LastAppliedSystemInstructions = [];
                developmentPolicy?.EnsureInitialized(campaign.Development, out _);
                await workingStore.SaveAsync(campaign.Memory, cancellationToken);
                if (saveCheckpoint is not null) await saveCheckpoint(cancellationToken);
            }
            return true;
        }
        finally { campaign.ProcessingGate.Release(); }
    }

    private sealed class SnapshotEventData
    {
        [JsonPropertyName("snapshotId")]
        public Guid? SnapshotId { get; init; }
    }
}
