namespace AIWhisper.Worker.Conversation;

using AIWhisper.Worker.Memory;

/// <summary>
/// Compact, persistent facts an observing companion can use beyond the
/// in-process recent-dialogue history.
/// </summary>
public sealed class CampaignMemory
{
    public List<ParasiteMemoryItem> LongTermMemory { get; set; } = [];
}
