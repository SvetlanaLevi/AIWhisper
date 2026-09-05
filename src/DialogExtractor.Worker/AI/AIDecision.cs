namespace DialogExtractor.Worker.AI;

public enum AIDecisionAction
{
    Speak,
    Silent,
}

public sealed record AIDecision(AIDecisionAction Action, string? Text);
