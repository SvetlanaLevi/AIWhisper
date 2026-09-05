namespace DialogExtractor.Worker.AI;

public sealed record AIRequestContext(string SystemPrompt, string UserPrompt);

/// <summary>
/// Isolates the pipeline from the concrete AI provider. The pipeline only
/// ever sees a "speak" or "silent" decision - "silent" is a normal, expected
/// outcome, never an error.
/// </summary>
public interface IAIDecisionService
{
    Task<AIDecision> DecideAsync(AIRequestContext context, CancellationToken cancellationToken);
}
