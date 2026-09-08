namespace AIWhisper.Worker.Configuration;

public sealed class MemoryOptions
{
    public int MaxLongTermItems { get; set; } = 100;
    public int MaxActiveItems { get; set; } = 8;
    public int MaxKnownFactsPerCharacter { get; set; } = 8;
    public List<string> TrackedCharacters { get; set; } = [];
}
