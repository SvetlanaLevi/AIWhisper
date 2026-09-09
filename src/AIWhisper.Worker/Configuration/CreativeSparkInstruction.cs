namespace AIWhisper.Worker.Configuration;

public static class CreativeSparkInstruction
{
    public static bool ShouldApply(double chance, string? developmentPhase, double randomValue)
    {
        if (string.Equals(developmentPhase, "Instinctive", StringComparison.OrdinalIgnoreCase)) return false;
        if (randomValue is < 0d or >= 1d)
            throw new ArgumentOutOfRangeException(nameof(randomValue), randomValue, "Random value must be in [0, 1).");

        return randomValue < Math.Clamp(chance, 0d, 1d);
    }
}
