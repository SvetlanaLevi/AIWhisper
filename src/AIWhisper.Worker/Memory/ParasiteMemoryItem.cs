using System.Text.Json.Serialization;

namespace AIWhisper.Worker.Memory;

public sealed class ParasiteMemoryItem
{
    public Guid Id { get; init; }
    public string Summary { get; set; } = string.Empty;
    public MemoryCategory Category { get; set; }
    public string? CharacterName { get; set; }
    public IReadOnlyCollection<string> Tags { get; set; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MemoryCategory
{
    Survival,
    RemovalThreat,
    IllithidPower,
    Ceremorphosis,
    ParasiteNature,
    HostAttitude,
    HostBehavior,
    Relationship,
    Trust,
    Protection,
    Threat,
    Betrayal,
    Conflict,
    CharacterOpinion,
    CharacterRelationship,
    GeneralObservation,
    ParasiteIntent,
}
