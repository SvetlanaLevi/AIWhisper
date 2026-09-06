using System.Text.Json;

namespace AIWhisper.Worker.Knowledge;

public sealed class CharacterKnowledgeProvider : ICharacterKnowledgeProvider
{
    private readonly Dictionary<string, CharacterKnowledge> _characters = new(StringComparer.OrdinalIgnoreCase);

    public CharacterKnowledgeProvider(string knowledgeDirectory)
    {
        if (!Directory.Exists(knowledgeDirectory))
        {
            throw new DirectoryNotFoundException($"Character knowledge directory was not found: {knowledgeDirectory}");
        }

        foreach (var path in Directory.EnumerateFiles(knowledgeDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            CharacterKnowledge? character;
            try
            {
                character = JsonSerializer.Deserialize<CharacterKnowledge>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Invalid character knowledge file '{Path.GetFileName(path)}'.", ex);
            }

            if (character is null || string.IsNullOrWhiteSpace(character.Name) ||
                string.IsNullOrWhiteSpace(character.Category) ||
                string.IsNullOrWhiteSpace(character.Race) || string.IsNullOrWhiteSpace(character.Class) ||
                string.IsNullOrWhiteSpace(character.Role) || string.IsNullOrWhiteSpace(character.Summary) ||
                character.Personality is null)
            {
                throw new InvalidOperationException($"Character knowledge file '{Path.GetFileName(path)}' is missing required fields.");
            }

            if (!_characters.TryAdd(character.Name.Trim(), character))
            {
                throw new InvalidOperationException($"Duplicate character knowledge name '{character.Name}' in '{Path.GetFileName(path)}'.");
            }
        }
    }

    public CharacterKnowledge? Find(string? speakerName)
        => string.IsNullOrWhiteSpace(speakerName) ? null : _characters.GetValueOrDefault(speakerName.Trim());
}

public static class CharacterKnowledgeFormatter
{
    public static string Format(CharacterKnowledge character)
    {
        var text = $"{character.Name} — {character.Race} {character.Class}, {character.Role}.";
        if (character.Personality.Count > 0) text += $" Personality: {string.Join(", ", character.Personality)}.";
        return text + $" Background: {character.Summary}";
    }
}
