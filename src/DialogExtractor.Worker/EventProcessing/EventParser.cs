using System.Globalization;
using System.Text.Json;

namespace DialogExtractor.Worker.EventProcessing;

public enum EventParseErrorKind
{
    MalformedJson,
    MissingRequiredField,
    InvalidTimestamp,
    CampaignIdMismatch,
}

public sealed record EventParseError(EventParseErrorKind Kind, string Message, string RawLine);

/// <summary>
/// Parses a single JSONL line into a <see cref="WorkerEvent"/>. Never throws:
/// malformed input is reported as an <see cref="EventParseError"/> so the
/// caller can log it and keep processing subsequent lines/events.
/// </summary>
public static class EventParser
{
    public static bool TryParse(string rawLine, string expectedCampaignId, out WorkerEvent? workerEvent, out EventParseError? error)
    {
        workerEvent = null;
        error = null;

        if (string.IsNullOrWhiteSpace(rawLine))
        {
            error = new EventParseError(EventParseErrorKind.MalformedJson, "empty line", rawLine);
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawLine);
        }
        catch (JsonException ex)
        {
            error = new EventParseError(EventParseErrorKind.MalformedJson, ex.Message, rawLine);
            return false;
        }

        using (document)
        {
            var root = document.RootElement;

            if (!root.TryGetProperty("campaignId", out var campaignIdProp) || campaignIdProp.ValueKind != JsonValueKind.String)
            {
                error = new EventParseError(EventParseErrorKind.MissingRequiredField, "missing 'campaignId'", rawLine);
                return false;
            }

            var campaignId = campaignIdProp.GetString()!;
            if (!string.Equals(campaignId, expectedCampaignId, StringComparison.Ordinal))
            {
                error = new EventParseError(
                    EventParseErrorKind.CampaignIdMismatch,
                    $"event campaignId '{campaignId}' does not match directory campaignId '{expectedCampaignId}'",
                    rawLine);
                return false;
            }

            if (!root.TryGetProperty("source", out var sourceProp) || sourceProp.ValueKind != JsonValueKind.String)
            {
                error = new EventParseError(EventParseErrorKind.MissingRequiredField, "missing 'source'", rawLine);
                return false;
            }

            if (!root.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
            {
                error = new EventParseError(EventParseErrorKind.MissingRequiredField, "missing 'type'", rawLine);
                return false;
            }

            if (!root.TryGetProperty("timestamp", out var timestampProp) || timestampProp.ValueKind != JsonValueKind.String)
            {
                error = new EventParseError(EventParseErrorKind.MissingRequiredField, "missing 'timestamp'", rawLine);
                return false;
            }

            if (!DateTime.TryParse(
                    timestampProp.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var timestamp))
            {
                error = new EventParseError(EventParseErrorKind.InvalidTimestamp, $"unparseable timestamp '{timestampProp.GetString()}'", rawLine);
                return false;
            }

            var schemaVersion = 0;
            if (root.TryGetProperty("schemaVersion", out var schemaVersionProp) && schemaVersionProp.ValueKind == JsonValueKind.Number)
            {
                schemaVersionProp.TryGetInt32(out schemaVersion);
            }

            string? dialogueId = null;
            var hasData = root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Object;
            if (hasData && dataProp.TryGetProperty("dialogueId", out var dialogueIdProp) && dialogueIdProp.ValueKind == JsonValueKind.String)
            {
                dialogueId = dialogueIdProp.GetString();
            }

            var dataClone = hasData ? dataProp.Clone() : default;
            var originalClone = root.Clone();

            workerEvent = new WorkerEvent
            {
                CampaignId = campaignId,
                Source = sourceProp.GetString()!,
                Type = typeProp.GetString()!,
                Timestamp = timestamp,
                DialogueId = dialogueId,
                SchemaVersion = schemaVersion,
                Data = dataClone,
                OriginalEvent = originalClone,
                ReceivedAt = DateTimeOffset.UtcNow,
            };
            return true;
        }
    }
}
