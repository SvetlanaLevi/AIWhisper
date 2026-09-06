namespace AIWhisper.Worker.Development;

public sealed class ParasiteDevelopmentState
{
    public string CurrentPhase { get; set; } = string.Empty;
    public string? ReachedInRegion { get; set; }
    public HashSet<string> DeliveredOneShots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
