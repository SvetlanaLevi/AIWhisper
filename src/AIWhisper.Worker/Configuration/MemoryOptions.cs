namespace AIWhisper.Worker.Configuration;

public sealed class MemoryOptions
{
    public int MaxLongTermItems { get; set; } = 100;
    public int MaxActiveItems { get; set; } = 8;
}
