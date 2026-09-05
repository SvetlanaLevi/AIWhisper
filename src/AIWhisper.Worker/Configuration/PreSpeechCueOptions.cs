namespace AIWhisper.Worker.Configuration;

/// <summary>
/// A short local sound played immediately before an AI voice line.
/// </summary>
public sealed class PreSpeechCueOptions
{
    public bool Enabled { get; set; }
    public string? FilePath { get; set; }
    public float Volume { get; set; } = 1.0f;
    /// <summary>
    /// Maximum amount of the cue to play before speech starts. Set to zero to
    /// play the entire file.
    /// </summary>
    public int DurationMs { get; set; } = 750;
}
