namespace AIWhisper.Worker.Configuration;

public sealed class TtsOptions
{
    public string Provider { get; set; } = "ElevenLabs";
    public string BaseUrl { get; set; } = "https://api.elevenlabs.io";
    public string? Voice { get; set; }
    public string Model { get; set; } = "eleven_multilingual_v2";
    public string OutputFormat { get; set; } = "mp3_22050_32";
    public string ApiKeyEnvironmentVariable { get; set; } = "ELEVENLABS_API_KEY";
    public bool PlayOnWindows { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 30;
}
