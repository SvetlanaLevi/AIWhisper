namespace AIWhisper.Worker.Configuration;

public sealed class MemoryOptions
{
    public int MaxImportantEvents { get; set; } = 60;
    public int MaxPlayerTraits { get; set; } = 30;
    public int MaxRunningJokes { get; set; } = 30;
    public int MaxSummaryCharacters { get; set; } = 2_000;
    public int MaxCurrentSituationCharacters { get; set; } = 800;
    public int MinimumTraitEvidenceDialogues { get; set; } = 2;
    public int MaxTraitCandidates { get; set; } = 30;
    public int MaxContextImportantEvents { get; set; } = 20;
    public int MaxContextPlayerTraits { get; set; } = 12;
    public int MaxContextRelationships { get; set; } = 12;
    public int MaxContextRunningJokes { get; set; } = 10;
}
