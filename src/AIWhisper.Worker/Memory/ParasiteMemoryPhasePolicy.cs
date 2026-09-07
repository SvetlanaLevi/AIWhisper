namespace AIWhisper.Worker.Memory;

public sealed class ParasiteMemoryPhasePolicy
{
    private static readonly HashSet<MemoryCategory> AwakeningCategories =
    [
        MemoryCategory.Survival,
        MemoryCategory.RemovalThreat,
        MemoryCategory.IllithidPower,
        MemoryCategory.Ceremorphosis,
        MemoryCategory.ParasiteNature,
        MemoryCategory.HostAttitude,
    ];

    private static readonly string[] AwakeningTags =
    [
        "survival", "removal", "treatment", "illithid", "authority",
        "ceremorphosis", "tadpole", "parasite",
    ];

    public IReadOnlyList<ParasiteMemoryItem> Select(
        IEnumerable<ParasiteMemoryItem> longTermMemory,
        string? developmentPhase)
    {
        var items = longTermMemory.ToList();
        if (!string.Equals(developmentPhase, "Awakening", StringComparison.OrdinalIgnoreCase))
            return items;

        return items.Where(item =>
                AwakeningCategories.Contains(item.Category) ||
                item.Tags.Any(tag => AwakeningTags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
            .ToList();
    }
}
