namespace DialogExtractor.Worker.Configuration;

public sealed class TtsOptions
{
    public string Provider { get; set; } = "EventLab";
    public string? BaseUrl { get; set; }
    public string? Endpoint { get; set; } = "/v1/tts";
    public string? Voice { get; set; }
    public string Format { get; set; } = "wav";
    public string? ApiKeyEnvironmentVariable { get; set; } = "EVENTLAB_API_KEY";
    public int TimeoutSeconds { get; set; } = 30;
}
