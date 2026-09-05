namespace AIWhisper.Worker.Persistence;

public sealed class WorkerCheckpoint
{
    public string CampaignId { get; set; } = string.Empty;
    public Dictionary<string, FileCheckpoint> Files { get; set; } = new();
}

public sealed class FileCheckpoint
{
    /// <summary>Byte offset immediately past the last confirmed complete line.</summary>
    public long Position { get; set; }

    /// <summary>File length as observed when this checkpoint was taken. Diagnostic only.</summary>
    public long Length { get; set; }
}
