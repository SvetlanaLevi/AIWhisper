namespace AIWhisper.Worker.EventProcessing;

/// <summary>
/// In-memory state for a single dialogue, keyed by (campaignId, dialogueId).
/// Exists only until the dialogue is finalized and handed off for AI/TTS processing.
/// </summary>
public sealed class DialogueState
{
    public required string CampaignId { get; init; }
    public required string DialogueId { get; init; }
    public long Generation { get; init; }
    public string? DialogueResource { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public IReadOnlyList<SpeakerInfo> Speakers { get; set; } = Array.Empty<SpeakerInfo>();
    public List<WorkerEvent> Events { get; } = new();
    public DialogueStatus Status { get; set; } = DialogueStatus.Active;

    public DialogueKey Key => new(CampaignId, DialogueId);
}
