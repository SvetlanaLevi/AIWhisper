namespace AIWhisper.Worker.Configuration;

public sealed class AiRequestLoggingOptions
{
    public bool Enabled { get; set; }
    public string FileName { get; set; } = "ai-requests.ndjson";
}
