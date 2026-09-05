namespace DialogExtractor.Worker.Configuration;

public sealed class OpenAIOptions
{
    public string Model { get; set; } = "gpt-4.1-mini";
    public int MaxRetries { get; set; } = 3;
    public int RetryBaseDelayMs { get; set; } = 1000;
    public int TimeoutSeconds { get; set; } = 60;
}
