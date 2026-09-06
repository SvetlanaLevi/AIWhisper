using AIWhisper.Worker.Configuration;

namespace AIWhisper.Worker.Conversation;

public static class CampaignMemoryMerger
{
    public static bool Apply(
        CampaignMemory memory,
        CampaignMemoryUpdate update,
        MemoryOptions options,
        string? dialogueId = null)
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

        if (update.UpdatedCurrentSituation is not null)
        {
            var situation = Normalize(update.UpdatedCurrentSituation);
            situation = situation.Length > options.MaxCurrentSituationCharacters
                ? situation[..options.MaxCurrentSituationCharacters].TrimEnd()
                : situation;
            if (!string.Equals(memory.CurrentSituation, situation, StringComparison.Ordinal))
            {
                memory.CurrentSituation = situation;
                changed = true;
            }
        }

        changed |= RemoveMatching(memory.ImportantEvents, update.ImportantEventsToRemove, memory.ImportantEventSources);
        changed |= RemoveMatching(memory.PlayerTraits, update.PlayerTraitsToRemove, memory.PlayerTraitSources);
        foreach (var trait in update.PlayerTraitsToRemove ?? [])
        {
            var candidateKey = memory.PlayerTraitCandidates.Keys.FirstOrDefault(key =>
                string.Equals(key, Normalize(trait), StringComparison.OrdinalIgnoreCase));
            if (candidateKey is not null) changed |= memory.PlayerTraitCandidates.Remove(candidateKey);
        }
        changed |= RemoveMatching(memory.RunningJokes, update.RunningJokesToRemove, memory.RunningJokeSources);
        changed |= RemoveRelationships(memory, update.RelationshipsToRemove);

        changed |= AddDistinct(memory.ImportantEvents, update.ImportantEventsToAdd, options.MaxImportantEvents, memory.ImportantEventSources, dialogueId);
        changed |= MergeTraitEvidence(memory, update.PlayerTraitsToAdd, options, dialogueId);
        changed |= AddDistinct(memory.RunningJokes, update.RunningJokesToAdd, options.MaxRunningJokes, memory.RunningJokeSources, dialogueId);

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
                if (memory.RelationshipSources.Remove(existingName, out var existingSources))
                {
                    memory.RelationshipSources[name] = existingSources;
                }
            }

            if (!memory.Relationships.TryGetValue(name, out var current) ||
                !string.Equals(current, description, StringComparison.Ordinal))
            {
                memory.Relationships[name] = description;
                changed = true;
            }
            changed |= AddSource(memory.RelationshipSources, name, dialogueId);
        }

        return changed;
    }

    private static bool AddDistinct(
        List<string> destination,
        IEnumerable<string>? additions,
        int maxCount,
        Dictionary<string, List<string>> sources,
        string? dialogueId)
    {
        var changed = false;
        foreach (var raw in additions ?? [])
        {
            var value = Normalize(raw);
            if (string.IsNullOrEmpty(value)) continue;

            var existing = destination.FirstOrDefault(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                changed |= AddSource(sources, existing, dialogueId);
                continue;
            }

            destination.Add(value);
            AddSource(sources, value, dialogueId);
            changed = true;
        }

        var removeCount = Math.Max(0, destination.Count - Math.Max(0, maxCount));
        if (removeCount > 0)
        {
            foreach (var removed in destination.Take(removeCount)) sources.Remove(removed);
            destination.RemoveRange(0, removeCount);
            changed = true;
        }

        return changed;
    }

    private static bool MergeTraitEvidence(
        CampaignMemory memory,
        IEnumerable<string>? observations,
        MemoryOptions options,
        string? dialogueId)
    {
        var changed = false;
        foreach (var raw in observations ?? [])
        {
            var trait = Normalize(raw);
            if (string.IsNullOrEmpty(trait)) continue;

            var existing = memory.PlayerTraits.FirstOrDefault(item => string.Equals(item, trait, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                changed |= AddSource(memory.PlayerTraitSources, existing, dialogueId);
                continue;
            }

            if (string.IsNullOrWhiteSpace(dialogueId))
            {
                changed |= AddDistinct(memory.PlayerTraits, [trait], options.MaxPlayerTraits, memory.PlayerTraitSources, null);
                continue;
            }

            if (!memory.PlayerTraitCandidates.TryGetValue(trait, out var candidate))
            {
                candidate = new PlayerTraitCandidate { Description = trait };
                memory.PlayerTraitCandidates[trait] = candidate;
                changed = true;
            }
            if (!candidate.DialogueIds.Contains(dialogueId, StringComparer.OrdinalIgnoreCase))
            {
                candidate.DialogueIds.Add(dialogueId);
                changed = true;
            }

            if (candidate.DialogueIds.Count >= Math.Max(1, options.MinimumTraitEvidenceDialogues))
            {
                changed |= AddDistinct(memory.PlayerTraits, [candidate.Description], options.MaxPlayerTraits, memory.PlayerTraitSources, dialogueId);
                memory.PlayerTraitSources[candidate.Description] = candidate.DialogueIds.ToList();
                memory.PlayerTraitCandidates.Remove(trait);
            }
        }

        while (memory.PlayerTraitCandidates.Count > Math.Max(0, options.MaxTraitCandidates))
        {
            memory.PlayerTraitCandidates.Remove(memory.PlayerTraitCandidates.Keys.First());
            changed = true;
        }
        return changed;
    }

    private static bool RemoveMatching(
        List<string> destination,
        IEnumerable<string>? removals,
        Dictionary<string, List<string>> sources)
    {
        var changed = false;
        foreach (var raw in removals ?? [])
        {
            var requested = Normalize(raw);
            var existing = destination.FirstOrDefault(item => string.Equals(item, requested, StringComparison.OrdinalIgnoreCase));
            if (existing is null) continue;
            destination.Remove(existing);
            sources.Remove(existing);
            changed = true;
        }
        return changed;
    }

    private static bool RemoveRelationships(CampaignMemory memory, IEnumerable<string>? removals)
    {
        var changed = false;
        foreach (var raw in removals ?? [])
        {
            var requested = Normalize(raw);
            var existing = memory.Relationships.Keys.FirstOrDefault(name => string.Equals(name, requested, StringComparison.OrdinalIgnoreCase));
            if (existing is null) continue;
            memory.Relationships.Remove(existing);
            memory.RelationshipSources.Remove(existing);
            changed = true;
        }
        return changed;
    }

    private static bool AddSource(Dictionary<string, List<string>> sources, string key, string? dialogueId)
    {
        if (string.IsNullOrWhiteSpace(dialogueId)) return false;
        if (!sources.TryGetValue(key, out var dialogueIds))
        {
            dialogueIds = [];
            sources[key] = dialogueIds;
        }
        if (dialogueIds.Contains(dialogueId, StringComparer.OrdinalIgnoreCase)) return false;
        dialogueIds.Add(dialogueId);
        return true;
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;
}
