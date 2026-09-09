using System.Text.RegularExpressions;

namespace AIWhisper.Worker.Memory;

public sealed class ActiveMemorySelector(
    ParasiteMemoryPhasePolicy phasePolicy,
    int maxItems)
{
    private static readonly Regex WordRegex = new("[A-Za-z]+", RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<MemoryCategory, string[]> CategoryTerms =
        new Dictionary<MemoryCategory, string[]>
        {
            [MemoryCategory.Survival] = ["survive", "survival", "danger", "kill", "death"],
            [MemoryCategory.RemovalThreat] = ["remove", "removal", "extract", "cure", "treatment", "healer"],
            [MemoryCategory.IllithidPower] = ["illithid", "authority", "power", "tadpole"],
            [MemoryCategory.Ceremorphosis] = ["ceremorphosis", "transform", "transformation"],
            [MemoryCategory.ParasiteNature] = ["parasite", "tadpole", "nature"],
            [MemoryCategory.HostAttitude] = ["parasite", "tadpole", "illithid", "remove", "protect"],
            [MemoryCategory.Threat] = ["threat", "danger", "kill", "attack"],
            [MemoryCategory.Protection] = ["protect", "defend", "save"],
            [MemoryCategory.Trust] = ["trust", "believe", "promise"],
            [MemoryCategory.Betrayal] = ["betray", "lie", "deceive"],
            [MemoryCategory.Conflict] = ["conflict", "fight", "argue", "attack"],
            [MemoryCategory.ParasiteIntent] = ["want", "demand", "promise", "warn", "kill", "spare", "trust"],
        };

    public IReadOnlyList<ParasiteMemoryItem> Select(
        IEnumerable<ParasiteMemoryItem> longTermMemory,
        string? developmentPhase,
        string currentContext,
        IEnumerable<string> currentCharacters)
    {
        var characters = currentCharacters
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var words = WordRegex.Matches(currentContext)
            .Select(match => match.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return phasePolicy.Select(longTermMemory, developmentPhase)
            .Select((item, index) => new { Item = item, Score = Score(item, characters, words), Index = index })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Index)
            .Take(Math.Max(0, maxItems))
            .Select(candidate => candidate.Item)
            .ToList();
    }

    private static int Score(
        ParasiteMemoryItem item,
        IReadOnlySet<string> currentCharacters,
        IReadOnlySet<string> contextWords)
    {
        var score = 0;
        if (item.CharacterName is not null && currentCharacters.Contains(item.CharacterName)) score += 100;
        score += item.Tags.Count(tag => MatchesTerm(tag, contextWords)) * 20;
        if (CategoryTerms.TryGetValue(item.Category, out var terms))
            score += terms.Count(term => MatchesTerm(term, contextWords)) * 10;
        return score;
    }

    private static bool MatchesTerm(string term, IReadOnlySet<string> contextWords)
    {
        var termWords = WordRegex.Matches(term).Select(match => match.Value);
        return termWords.Any(termWord => contextWords.Any(contextWord =>
            contextWord.StartsWith(termWord, StringComparison.OrdinalIgnoreCase)));
    }
}
