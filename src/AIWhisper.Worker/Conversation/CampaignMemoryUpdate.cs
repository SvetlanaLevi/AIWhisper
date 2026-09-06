namespace AIWhisper.Worker.Conversation;

/// <summary>
/// A model-proposed delta. It is always merged in C#; the model never writes
/// a whole memory document or touches disk directly.
/// </summary>
public sealed class CampaignMemoryUpdate
{
    public string? UpdatedSummary { get; set; }
    public string? UpdatedCurrentSituation { get; set; }
    public List<string> ImportantEventsToAdd { get; set; } = [];
    public List<string> ImportantEventsToRemove { get; set; } = [];
    public List<RelationshipMemoryUpdate> RelationshipUpdates { get; set; } = [];
    public List<string> RelationshipsToRemove { get; set; } = [];
    public List<string> PlayerTraitsToAdd { get; set; } = [];
    public List<string> PlayerTraitsToRemove { get; set; } = [];
    public List<string> RunningJokesToAdd { get; set; } = [];
    public List<string> RunningJokesToRemove { get; set; } = [];
}

public sealed class RelationshipMemoryUpdate
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
