using AIWhisper.Worker.Conversation;

namespace AIWhisper.Worker.AI;

public sealed record AIRequestContext(
    string SystemPrompt,
    string UserPrompt,
    string? DevelopmentPhase = null,
    string? DevelopmentPrompt = null,
    string BaseSystemPromptId = "base:unspecified",
    Action<IReadOnlyList<string>>? SystemInstructionsApplied = null);

/// <summary>
/// Isolates the pipeline from the concrete AI provider. The pipeline only
/// ever sees a "speak" or "silent" decision - "silent" is a normal, expected
/// outcome, never an error.
/// </summary>
public interface IAIDecisionService
{
    Task<AIDecision> DecideAsync(AIRequestContext context, CancellationToken cancellationToken);
    Task<CampaignMemoryUpdate> UpdateCampaignMemoryAsync(
        CampaignMemory currentMemory,
        string transcript,
        string dialogueId,
        CancellationToken cancellationToken);
}
