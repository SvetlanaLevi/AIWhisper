namespace AIWhisper.Worker.Knowledge;

public sealed class CharacterKnowledge
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? Race { get; set; }
    public string? Class { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public List<string> Personality { get; set; } = [];
}

public interface ICharacterKnowledgeProvider
{
    CharacterKnowledge? Find(string? speakerName);
}
