namespace DialogExtractor.Worker.Conversation;

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
}
