namespace AIWhisper.Worker.Configuration;

public sealed class VoiceEffectsOptions
{
    public bool Enabled { get; set; } = true;
    public float Distance { get; set; } = 0f;
    public int DelayMs { get; set; } = 100;
    public float DelayMix { get; set; } = 0.20f;
    public float ReverbMix { get; set; } = 0.20f;
    public float ReverbDecay { get; set; } = 0.72f;
    public float HighPassHz { get; set; } = 120f;
    public float LowPassHz { get; set; } = 6_500f;
    public float PitchShiftSemitones { get; set; } = -0.30f;
}
