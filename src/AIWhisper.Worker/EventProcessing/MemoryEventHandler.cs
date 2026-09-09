using System.Text.Json;
using System.Text.Json.Serialization;
using AIWhisper.Worker.Conversation;
using AIWhisper.Worker.Development;
using AIWhisper.Worker.Logging;
using AIWhisper.Worker.Memory;
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
        var memoryId = data?.MemoryId ?? data?.SnapshotId;
        if (data is null || (evt.Type == "save.start" && memoryId is null))
        {
            log.Warn($"campaign {campaign.CampaignId}: invalid {evt.Type} data");
            return true;
        }

        // Cancel before waiting: an old AI/TTS request must not delay a load indefinitely.
        if (evt.Type == "memory.load") campaign.DialogueCancellation.Cancel();
        await campaign.ProcessingGate.WaitAsync(cancellationToken);
        try
        {
            await campaign.MemoryGate.WaitAsync(cancellationToken);
            try
            {
                var store = new CampaignSnapshotStore(campaign.Directory);
                if (evt.Type == "save.start")
                {
                    await store.SaveAsync(memoryId!.Value, new CampaignStateSnapshot
                    {
                        Memory = campaign.Memory,
                        Development = campaign.Development,
                        Session = campaign.Session,
                        ProcessedDialogueFingerprints = campaign.ProcessedDialogueFingerprints.Keys.ToList(),
                    }, cancellationToken);
                }
                else
                {
                    campaign.Generation++;
                    resetDialogues?.Invoke();
                    campaign.DialogueCancellation.Dispose();
                    campaign.DialogueCancellation = new CancellationTokenSource();
                    var snapshot = memoryId is Guid id ? await store.LoadAsync(id, cancellationToken) : null;
                    if (memoryId is not null && snapshot is null)
                        log.Warn($"campaign {campaign.CampaignId}: snapshot {memoryId} is missing or invalid; using empty state");
                    snapshot ??= new CampaignStateSnapshot();
                    campaign.Memory = snapshot.Memory;
                    var duplicateMemoryCount = MemoryOperationApplier.DeduplicateExisting(campaign.Memory);
                    if (duplicateMemoryCount > 0)
                        log.Info($"campaign {campaign.CampaignId}: removed {duplicateMemoryCount} duplicate long-term memory item(s) after load");
                    campaign.Development = snapshot.Development;
                    campaign.Session.Player = snapshot.Session.Player;
                    campaign.Session.Region = snapshot.Session.Region;
                    campaign.ProcessedDialogueFingerprints.Clear();
                    foreach (var fingerprint in snapshot.ProcessedDialogueFingerprints ?? [])
                    {
                        if (!string.IsNullOrWhiteSpace(fingerprint))
                            campaign.ProcessedDialogueFingerprints.TryAdd(fingerprint, 0);
                    }
                    campaign.History.Clear();
                    campaign.LastAppliedSystemInstructions = [];
                    developmentPolicy?.EnsureInitialized(campaign.Development, out _);
                    await workingStore.SaveAsync(campaign.Memory, cancellationToken);
                    if (saveCheckpoint is not null) await saveCheckpoint(cancellationToken);
                }
                return true;
            }
            finally
            {
                campaign.MemoryGate.Release();
            }
        }
        finally
        {
            campaign.ProcessingGate.Release();
        }
    }

    private sealed class SnapshotEventData
    {
        [JsonPropertyName("memoryId")]
        public Guid? MemoryId { get; init; }

        [JsonPropertyName("snapshotId")]
        public Guid? SnapshotId { get; init; }
    }
}
