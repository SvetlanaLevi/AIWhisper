using AIWhisper.Worker.Conversation;

namespace AIWhisper.Worker.Memory;

public sealed record MemoryApplyResult(bool Changed, IReadOnlyList<Guid> UnknownTargets);

public static class MemoryOperationApplier
{
    private static readonly HashSet<string> SimilarityStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "as", "at", "be", "but", "by", "for", "from", "has", "have",
        "he", "her", "him", "his", "in", "into", "is", "it", "its", "of", "on", "or",
        "she", "that", "the", "their", "them", "they", "this", "to", "was", "were", "while",
        "with"
    };

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
                    var characterName = NormalizeNullable(operation.CharacterName);
                    var tags = NormalizeTags(operation.Tags);
                    if (memory.LongTermMemory.Any(existing => IsSimilar(
                            existing,
                            summary,
                            operation.Category.Value,
                            characterName,
                            tags)))
                    {
                        break;
                    }
                    memory.LongTermMemory.Add(new ParasiteMemoryItem
                    {
                        Id = createId(),
                        Summary = summary,
                        Category = operation.Category.Value,
                        CharacterName = characterName,
                        Tags = tags,
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

    public static int DeduplicateExisting(CampaignMemory memory)
    {
        var removed = 0;
        var keptNewestFirst = new List<ParasiteMemoryItem>();

        for (var index = memory.LongTermMemory.Count - 1; index >= 0; index--)
        {
            var candidate = memory.LongTermMemory[index];
            if (keptNewestFirst.Any(existing => IsSimilar(
                    existing,
                    Normalize(candidate.Summary),
                    candidate.Category,
                    NormalizeNullable(candidate.CharacterName),
                    NormalizeTags(candidate.Tags))))
            {
                memory.LongTermMemory.RemoveAt(index);
                removed++;
                continue;
            }

            keptNewestFirst.Add(candidate);
        }

        return removed;
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

    private static bool IsSimilar(
        ParasiteMemoryItem existing,
        string summary,
        MemoryCategory category,
        string? characterName,
        IReadOnlyCollection<string> tags)
    {
        if (existing.Category != category ||
            !string.Equals(NormalizeNullable(existing.CharacterName), characterName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(NormalizeForComparison(existing.Summary), NormalizeForComparison(summary), StringComparison.Ordinal))
            return true;

        var summarySimilarity = DiceSimilarity(Tokenize(existing.Summary), Tokenize(summary));
        if (summarySimilarity >= 0.72) return true;

        var tagSimilarity = DiceSimilarity(
            existing.Tags.Select(NormalizeForComparison).Where(tag => tag.Length > 0),
            tags.Select(NormalizeForComparison).Where(tag => tag.Length > 0));
        return tagSimilarity >= 0.75 && summarySimilarity >= 0.50;
    }

    private static IReadOnlyCollection<string> Tokenize(string value) =>
        NormalizeForComparison(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 1 && !SimilarityStopWords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string NormalizeForComparison(string value)
    {
        var characters = value
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
            .ToArray();
        return string.Join(' ', new string(characters)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static double DiceSimilarity(IEnumerable<string> left, IEnumerable<string> right)
    {
        var leftSet = left.ToHashSet(StringComparer.Ordinal);
        var rightSet = right.ToHashSet(StringComparer.Ordinal);
        if (leftSet.Count == 0 || rightSet.Count == 0) return 0;

        var common = leftSet.Count(rightSet.Contains);
        return 2d * common / (leftSet.Count + rightSet.Count);
    }
}
