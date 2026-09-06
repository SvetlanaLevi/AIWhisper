using System.Text.Json.Serialization;

namespace AIWhisper.Worker.Configuration;

public sealed class ParasiteDevelopmentOptions
{
    public bool Enabled { get; set; } = true;
    public string InitialPhase { get; set; } = "Instinctive";
    public List<string> PhaseSequence { get; set; } = [];
    public Dictionary<string, string> Regions { get; set; } = new();
    public Dictionary<string, string> PromptPaths { get; set; } = new();

    [JsonIgnore]
    public Dictionary<string, string> PhasePrompts { get; } = new(StringComparer.OrdinalIgnoreCase);
}
