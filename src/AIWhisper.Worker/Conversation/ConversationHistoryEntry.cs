namespace AIWhisper.Worker.Conversation;

public sealed record ConversationHistoryEntry(
    string DialogueId,
    string? DialogueResource,
    DateTime? StartTime,
    DateTime? EndTime,
    string TranscriptSummary,
    string AiAction,
    string? AiText);
