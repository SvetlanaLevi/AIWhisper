using System.Text.Json;
using AIWhisper.Worker.Logging;

namespace AIWhisper.Worker.Knowledge;

public sealed class CharacterKnowledgeProvider : ICharacterKnowledgeProvider
{
    private readonly Dictionary<string, CharacterKnowledge> _characters = new(StringComparer.OrdinalIgnoreCase);
    private readonly IWorkerLog? _log;

    public CharacterKnowledgeProvider(string knowledgeDirectory, IWorkerLog? log = null)
    {
        _log = log;
        if (!Directory.Exists(knowledgeDirectory))
        {
            Warn($"character knowledge directory was not found at '{knowledgeDirectory}'; continuing without static character knowledge");
            return;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(knowledgeDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                CharacterKnowledge? character;
                try
                {
                    character = JsonSerializer.Deserialize<CharacterKnowledge>(File.ReadAllText(path),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    Warn($"could not read character knowledge file '{Path.GetFileName(path)}'; skipping it: {ex.Message}");
                    continue;
                }

                if (character is null || string.IsNullOrWhiteSpace(character.Name) ||
                    string.IsNullOrWhiteSpace(character.Category) ||
                    string.IsNullOrWhiteSpace(character.Role) || string.IsNullOrWhiteSpace(character.Summary) ||
                    character.Personality is null)
                {
                    Warn($"character knowledge file '{Path.GetFileName(path)}' is missing required fields; skipping it");
                    continue;
                }

                if (!_characters.TryAdd(character.Name.Trim(), character))
                {
                    Warn($"duplicate character knowledge name '{character.Name}' in '{Path.GetFileName(path)}'; skipping it");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"could not enumerate character knowledge directory '{knowledgeDirectory}'; continuing with { _characters.Count } loaded profile(s): {ex.Message}");
        }
    }

    public CharacterKnowledge? Find(string? speakerName)
        => string.IsNullOrWhiteSpace(speakerName) ? null : _characters.GetValueOrDefault(speakerName.Trim());

    private void Warn(string message) => _log?.Warn(message);
}

public static class CharacterKnowledgeFormatter
{
    public static string Format(CharacterKnowledge character)
    {
        var identity = string.Join(" ", new[] { character.Race, character.Class }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var text = string.IsNullOrWhiteSpace(identity)
            ? $"{character.Name} — {character.Role}."
            : $"{character.Name} — {identity}, {character.Role}.";
        if (character.Personality.Count > 0) text += $" Personality: {string.Join(", ", character.Personality)}.";
        return text + $" Background: {character.Summary}";
    }
}
