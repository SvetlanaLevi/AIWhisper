namespace AIWhisper.Worker.Configuration;

public enum MinimumReactionLevel
{
    None,
    Normal,
    Critical,
}

public static class MinimumReactionLevelSelector
{
    public const double LowNormalProbability = 0.25d;
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
        => $"MINIMUM REACTION LEVEL: {level.ToString().ToUpperInvariant()}";
}
