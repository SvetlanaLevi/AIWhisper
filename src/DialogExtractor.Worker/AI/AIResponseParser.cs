using System.Text.Json;

namespace DialogExtractor.Worker.AI;

/// <summary>
/// Parses the AI's structured JSON output. Never attempts to guess intent
/// from free-form text - an unexpected shape is a parse failure, not a
/// best-effort guess.
/// </summary>
public static class AIResponseParser
{
    public static bool TryParse(string json, out AIDecision? decision, out string? error)
    {
        decision = null;
        error = null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("action", out var actionProp) || actionProp.ValueKind != JsonValueKind.String)
            {
                error = "missing or invalid 'action' field";
                return false;
            }

            switch (actionProp.GetString())
            {
                case "speak":
                    if (!root.TryGetProperty("text", out var textProp) ||
                        textProp.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(textProp.GetString()))
                    {
                        error = "'speak' action requires a non-empty 'text' field";
                        return false;
                    }
                    decision = new AIDecision(AIDecisionAction.Speak, textProp.GetString());
                    return true;

                case "silent":
                    decision = new AIDecision(AIDecisionAction.Silent, null);
                    return true;

                default:
                    error = $"unknown action '{actionProp.GetString()}'";
                    return false;
            }
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
