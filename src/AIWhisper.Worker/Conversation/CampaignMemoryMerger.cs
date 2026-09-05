using AIWhisper.Worker.Configuration;

namespace AIWhisper.Worker.Conversation;

public static class CampaignMemoryMerger
{
    public static bool Apply(CampaignMemory memory, CampaignMemoryUpdate update, MemoryOptions options)
    {
        var changed = false;
        var summary = Normalize(update.UpdatedSummary);
        if (!string.IsNullOrEmpty(summary))
        {
            summary = summary.Length > options.MaxSummaryCharacters
                ? summary[..options.MaxSummaryCharacters].TrimEnd()
                : summary;
            if (!string.Equals(memory.Summary, summary, StringComparison.Ordinal))
            {
                memory.Summary = summary;
                changed = true;
            }
        }

        changed |= AddDistinct(memory.ImportantEvents, update.ImportantEventsToAdd, options.MaxImportantEvents);
        changed |= AddDistinct(memory.PlayerTraits, update.PlayerTraitsToAdd, options.MaxPlayerTraits);
        changed |= AddDistinct(memory.RunningJokes, update.RunningJokesToAdd, options.MaxRunningJokes);

        foreach (var relationshipUpdate in update.RelationshipUpdates)
        {
            var name = Normalize(relationshipUpdate.Name);
            var description = Normalize(relationshipUpdate.Description);
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(description)) continue;

            var existingName = memory.Relationships.Keys.FirstOrDefault(key =>
                string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
            if (existingName is not null && !string.Equals(existingName, name, StringComparison.Ordinal))
            {
                memory.Relationships.Remove(existingName);
            }

            if (!memory.Relationships.TryGetValue(name, out var current) ||
                !string.Equals(current, description, StringComparison.Ordinal))
            {
                memory.Relationships[name] = description;
                changed = true;
            }
        }

        return changed;
    }

    private static bool AddDistinct(List<string> destination, IEnumerable<string>? additions, int maxCount)
    {
        var changed = false;
        foreach (var raw in additions ?? [])
        {
            var value = Normalize(raw);
            if (string.IsNullOrEmpty(value) || destination.Any(existing =>
                    string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))) continue;

            destination.Add(value);
            changed = true;
        }

        var removeCount = Math.Max(0, destination.Count - Math.Max(0, maxCount));
        if (removeCount > 0)
        {
            destination.RemoveRange(0, removeCount);
            changed = true;
        }

        return changed;
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;
}
