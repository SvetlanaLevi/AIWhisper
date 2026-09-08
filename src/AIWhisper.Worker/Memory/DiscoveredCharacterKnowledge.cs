using AIWhisper.Worker.Conversation;

namespace AIWhisper.Worker.Memory;

public sealed class DiscoveredCharacterKnowledge
{
    public string CharacterName { get; set; } = string.Empty;
    public List<string> KnownFacts { get; set; } = [];
}

public sealed class CharacterKnowledgeUpdate
{
    public string CharacterName { get; set; } = string.Empty;
    public IReadOnlyCollection<string> KnownFacts { get; set; } = [];
}

public sealed record CharacterKnowledgeApplyResult(bool Changed, IReadOnlyList<string> RejectedCharacters);

public static class DiscoveredCharacterKnowledgeApplier
{
    public static CharacterKnowledgeApplyResult Apply(
        CampaignMemory memory,
        IEnumerable<CharacterKnowledgeUpdate>? updates,
        IEnumerable<string> trackedCharacters,
        int maxFactsPerCharacter)
    {
        var tracked = trackedCharacters
            .Select(Normalize)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(name => name, StringComparer.OrdinalIgnoreCase);
        var rejected = new List<string>();
        var changed = false;

        foreach (var update in updates ?? [])
        {
            var requestedName = Normalize(update.CharacterName);
            if (!tracked.TryGetValue(requestedName, out var canonicalName))
            {
                if (requestedName.Length > 0) rejected.Add(requestedName);
                continue;
            }

            var facts = (update.KnownFacts ?? [])
                .Select(Normalize)
                .Where(fact => fact.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(0, maxFactsPerCharacter))
                .ToList();
            var existing = memory.CharacterKnowledge.FirstOrDefault(item =>
                string.Equals(item.CharacterName, canonicalName, StringComparison.OrdinalIgnoreCase));

            if (facts.Count == 0)
            {
                if (existing is not null) changed |= memory.CharacterKnowledge.Remove(existing);
                continue;
            }

            if (existing is null)
            {
                memory.CharacterKnowledge.Add(new DiscoveredCharacterKnowledge
                {
                    CharacterName = canonicalName,
                    KnownFacts = facts,
                });
                changed = true;
                continue;
            }

            if (existing.KnownFacts.SequenceEqual(facts, StringComparer.OrdinalIgnoreCase)) continue;
            existing.CharacterName = canonicalName;
            existing.KnownFacts = facts;
            changed = true;
        }

        return new CharacterKnowledgeApplyResult(changed, rejected);
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;
}
