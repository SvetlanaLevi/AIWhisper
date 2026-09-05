namespace AIWhisper.Worker.Configuration;

public sealed class MemoryOptions
{
    public int MaxImportantEvents { get; set; } = 100;
    public int MaxPlayerTraits { get; set; } = 30;
    public int MaxRunningJokes { get; set; } = 30;
    public int MaxSummaryCharacters { get; set; } = 2_000;
}
