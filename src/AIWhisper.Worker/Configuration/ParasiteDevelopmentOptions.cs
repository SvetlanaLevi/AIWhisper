using System.Text.Json.Serialization;

namespace AIWhisper.Worker.Configuration;

public sealed class ParasiteDevelopmentOptions
{
    public bool Enabled { get; set; } = true;
    public List<string> PhaseSequence { get; set; } = [];
    public Dictionary<string, string> Regions { get; set; } = new();
    public Dictionary<string, string> PromptPaths { get; set; } = new();
    public Dictionary<string, PhaseIntroductionOptions> PhaseIntroductions { get; set; } = new();

    [JsonIgnore]
    public Dictionary<string, string> PhasePrompts { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PhaseIntroductionOptions
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}
