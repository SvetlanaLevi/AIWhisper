namespace AIWhisper.Worker.Configuration;

public enum MinimumReactionLevel
{
    None,
    Normal,
    Critical,
}

public static class MinimumReactionLevelSelector
{
    public const double LowNormalProbability = 0.10d;
    public const double MediumNormalProbability = 0.50d;

    public static MinimumReactionLevel Select(CommentFrequency frequency) => frequency switch
    {
        CommentFrequency.High => MinimumReactionLevel.Normal,
        CommentFrequency.All => MinimumReactionLevel.None,
        CommentFrequency.Low or CommentFrequency.Medium => Select(frequency, Random.Shared.NextDouble()),
        _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, null),
    };

    public static bool AppliesToPhase(string? developmentPhase)
        => !string.Equals(developmentPhase, "Instinctive", StringComparison.OrdinalIgnoreCase);

    public static MinimumReactionLevel Select(CommentFrequency frequency, double randomValue)
    {
        if (randomValue is < 0d or >= 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(randomValue), randomValue, "Random value must be in [0, 1).");
        }

        return frequency switch
        {
            CommentFrequency.Low => randomValue < LowNormalProbability
                ? MinimumReactionLevel.Normal
                : MinimumReactionLevel.Critical,
            CommentFrequency.Medium => randomValue < MediumNormalProbability
                ? MinimumReactionLevel.Normal
                : MinimumReactionLevel.Critical,
            CommentFrequency.High => MinimumReactionLevel.Normal,
            CommentFrequency.All => MinimumReactionLevel.None,
            _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, null),
        };
    }
}

public static class MinimumReactionLevelInstruction
{
    public static string Create(MinimumReactionLevel level)
        => level switch
        {
            MinimumReactionLevel.None => """
                MINIMUM REACTION LEVEL: NONE
                Apply the current parasite-development rules without an additional reaction threshold.
                """,
            MinimumReactionLevel.Normal => """
                MINIMUM REACTION LEVEL: NORMAL
                Use the ordinary reaction threshold defined by the current parasite-development phase.
                """,
            MinimumReactionLevel.Critical => """
                MINIMUM REACTION LEVEL: CRITICAL
                Default to silent. Speak only when the CURRENT EVENT itself contains an explicit,
                exceptional development that directly affects you or fundamentally changes your
                view of the host. A merely possible use, danger, advantage, future connection, or
                resemblance is below this threshold. Never invent a way for an ordinary creature,
                object, weapon, conversation, or hazard to affect you in order to justify speaking.
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
        };
}
