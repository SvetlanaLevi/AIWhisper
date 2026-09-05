using System.Text.Json;

namespace DialogExtractor.Worker.EventProcessing;

/// <summary>
/// Internal, source-agnostic representation of a single JSONL event emitted
/// by the DialogExtractor mod. Unknown fields are never discarded: both
/// <see cref="Data"/> and <see cref="OriginalEvent"/> are clones of the
/// parsed JSON, independent of the originating JsonDocument's lifetime.
/// </summary>
public sealed class WorkerEvent
{
    public required string CampaignId { get; init; }
    public required string Source { get; init; }
    public required string Type { get; init; }
    public required DateTime Timestamp { get; init; }
    public string? DialogueId { get; init; }
    public int SchemaVersion { get; init; }

    /// <summary>The event's "data" element, as originally received.</summary>
    public required JsonElement Data { get; init; }

    /// <summary>The full original envelope, preserved verbatim for traceability.</summary>
    public required JsonElement OriginalEvent { get; init; }

    /// <summary>
    /// Wall-clock time this event was handed to the merger. Used only for
    /// the ordering buffer, never for logical ordering.
    /// </summary>
    public DateTimeOffset ReceivedAt { get; init; }
}
