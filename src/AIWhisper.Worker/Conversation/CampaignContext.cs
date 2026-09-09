namespace AIWhisper.Worker.Conversation;

using AIWhisper.Worker.Development;
using System.Collections.Concurrent;

/// <summary>
/// Everything scoped to a single campaign: its session info and its
/// conversation history. Never shared across campaigns.
/// </summary>
public sealed class CampaignContext
{
    public required string CampaignId { get; init; }
    public required string Directory { get; init; }
    public SessionContext Session { get; } = new();
    public List<ConversationHistoryEntry> History { get; } = new();
    public ConcurrentDictionary<string, byte> ProcessedDialogueFingerprints { get; } = new(StringComparer.Ordinal);
    public CampaignMemory Memory { get; set; } = new();
    public SemaphoreSlim MemoryGate { get; } = new(1, 1);
    public SemaphoreSlim ProcessingGate { get; } = new(1, 1);
    public CancellationTokenSource DialogueCancellation { get; set; } = new();
    public long Generation { get; set; }
    public ParasiteDevelopmentState Development { get; set; } = new();
    public IReadOnlyList<string> LastAppliedSystemInstructions { get; set; } = [];
}
