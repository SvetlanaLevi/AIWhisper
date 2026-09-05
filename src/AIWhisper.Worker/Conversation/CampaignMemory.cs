namespace AIWhisper.Worker.Conversation;

/// <summary>
/// Compact, persistent facts an observing companion can use beyond the
/// in-process recent-dialogue history.
/// </summary>
public sealed class CampaignMemory
{
    public string Summary { get; set; } = string.Empty;
    public List<string> ImportantEvents { get; set; } = [];
    public Dictionary<string, string> Relationships { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> PlayerTraits { get; set; } = [];
    public List<string> RunningJokes { get; set; } = [];
}
