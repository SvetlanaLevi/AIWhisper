using AIWhisper.Worker.Conversation;

namespace AIWhisper.Worker.Memory;

public sealed record MemoryApplyResult(bool Changed, IReadOnlyList<Guid> UnknownTargets);

public static class MemoryOperationApplier
{
    public static MemoryApplyResult Apply(
        CampaignMemory memory,
        IEnumerable<MemoryOperation>? operations,
        int maxItems,
        Func<Guid>? createId = null)
    {
        createId ??= Guid.NewGuid;
        var changed = false;
        var created = false;
        var unknownTargets = new List<Guid>();

        foreach (var operation in operations ?? [])
        {
            switch (operation.Kind)
            {
                case MemoryOperationKind.Create:
                    var summary = Normalize(operation.Summary);
                    if (string.IsNullOrEmpty(summary) || operation.Category is null) break;
                    memory.LongTermMemory.Add(new ParasiteMemoryItem
                    {
                        Id = createId(),
                        Summary = summary,
                        Category = operation.Category.Value,
                        CharacterName = NormalizeNullable(operation.CharacterName),
                        Tags = NormalizeTags(operation.Tags),
                    });
                    changed = true;
                    created = true;
                    break;

                case MemoryOperationKind.Update:
                    if (!TryFind(memory, operation.TargetId, unknownTargets, out var item)) break;
                    changed |= ApplyUpdate(item!, operation);
                    break;

                case MemoryOperationKind.Remove:
                    if (!TryFind(memory, operation.TargetId, unknownTargets, out item)) break;
                    changed |= memory.LongTermMemory.Remove(item!);
                    break;
            }
        }

        var overflow = created
            ? Math.Max(0, memory.LongTermMemory.Count - Math.Max(0, maxItems))
            : 0;
        if (overflow > 0)
        {
            memory.LongTermMemory.RemoveRange(0, overflow);
            changed = true;
        }

        return new MemoryApplyResult(changed, unknownTargets);
    }

    private static bool TryFind(
        CampaignMemory memory,
        Guid? targetId,
        List<Guid> unknownTargets,
        out ParasiteMemoryItem? item)
    {
        item = targetId is Guid id
            ? memory.LongTermMemory.FirstOrDefault(candidate => candidate.Id == id)
            : null;
        if (item is not null) return true;
        if (targetId is Guid unknown) unknownTargets.Add(unknown);
        return false;
    }

    private static bool ApplyUpdate(ParasiteMemoryItem item, MemoryOperation operation)
    {
        var changed = false;
        var summary = Normalize(operation.Summary);
        if (!string.IsNullOrEmpty(summary) && !string.Equals(item.Summary, summary, StringComparison.Ordinal))
        {
            item.Summary = summary;
            changed = true;
        }
        if (operation.Category is MemoryCategory category && item.Category != category)
        {
            item.Category = category;
            changed = true;
        }
        if (operation.CharacterName is not null)
        {
            var character = NormalizeNullable(operation.CharacterName);
            if (!string.Equals(item.CharacterName, character, StringComparison.Ordinal))
            {
                item.CharacterName = character;
                changed = true;
            }
        }
        if (operation.Tags is not null)
        {
            var tags = NormalizeTags(operation.Tags);
            if (!item.Tags.SequenceEqual(tags, StringComparer.OrdinalIgnoreCase))
            {
                item.Tags = tags;
                changed = true;
            }
        }
        return changed;
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;
    private static string? NormalizeNullable(string? value)
    {
        var normalized = Normalize(value);
        return normalized.Length == 0 ? null : normalized;
    }

    private static IReadOnlyCollection<string> NormalizeTags(IEnumerable<string>? tags) =>
        (tags ?? [])
            .Select(Normalize)
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
