namespace AIWhisper.Worker.Conversation;

/// <summary>
/// Compact, persistent facts an observing companion can use beyond the
/// in-process recent-dialogue history.
/// </summary>
public sealed class CampaignMemory
{
    public string Summary { get; set; } = string.Empty;
    public string CurrentSituation { get; set; } = string.Empty;
    public List<string> ImportantEvents { get; set; } = [];
    public Dictionary<string, string> Relationships { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> PlayerTraits { get; set; } = [];
    public List<string> RunningJokes { get; set; } = [];

    // Provenance is maintenance metadata. AIContextBuilder deliberately does
    // not expose dialogue IDs to the speaking model.
    public Dictionary<string, List<string>> ImportantEventSources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> RelationshipSources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> PlayerTraitSources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> RunningJokeSources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PlayerTraitCandidate> PlayerTraitCandidates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PlayerTraitCandidate
{
    public string Description { get; set; } = string.Empty;
    public List<string> DialogueIds { get; set; } = [];
}
