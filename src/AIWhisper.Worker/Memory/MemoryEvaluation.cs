using AIWhisper.Worker.Conversation;
using System.Text.Json.Serialization;

namespace AIWhisper.Worker.Memory;

public sealed record MemoryEvaluationRequest(
    string CampaignId,
    IReadOnlyList<ParasiteMemoryItem> ExistingMemory,
    IReadOnlyList<DiscoveredCharacterKnowledge> ExistingCharacterKnowledge,
    IReadOnlyList<string> TrackedCharacterNames,
    string Transcript,
    string DialogueId,
    string? DevelopmentPhase,
    string? Region,
    IReadOnlyList<string> CurrentCharacters,
    string? ParasiteRemark);

public sealed class MemoryEvaluationResult
{
    public List<MemoryOperation> Operations { get; set; } = [];
    public List<CharacterKnowledgeUpdate> CharacterKnowledgeUpdates { get; set; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MemoryOperationKind
{
    Create,
    Update,
    Remove,
}

public sealed class MemoryOperation
{
    public MemoryOperationKind Kind { get; set; }
    public Guid? TargetId { get; set; }
    public string? Summary { get; set; }
    public MemoryCategory? Category { get; set; }
    public string? CharacterName { get; set; }
    public IReadOnlyCollection<string>? Tags { get; set; }
}

public interface IMemoryEvaluator
{
    Task<MemoryEvaluationResult> EvaluateAsync(
        MemoryEvaluationRequest request,
        CancellationToken cancellationToken);
}
