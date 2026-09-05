using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIWhisper.Worker.EventProcessing;

namespace AIWhisper.Worker.Conversation;

/// <summary>
/// Turns a completed DialogueState plus campaign context into a compact,
/// semantic prompt for the AI. Internal/technical fields never reach the
/// model; only speaker/text content does. Markup is stripped only here -
/// storage always keeps it intact (see DialogueState.Events).
/// </summary>
public sealed class AIContextBuilder
{
    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled);

    public string BuildUserPrompt(CampaignContext campaign, DialogueState dialogue, int maxHistoryEntries)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(campaign.Session.Player) || !string.IsNullOrEmpty(campaign.Session.Region))
        {
            sb.AppendLine("Game context:");
            if (!string.IsNullOrEmpty(campaign.Session.Player)) sb.AppendLine($"- Player: {campaign.Session.Player}");
            if (!string.IsNullOrEmpty(campaign.Session.Region)) sb.AppendLine($"- Region: {campaign.Session.Region}");
            sb.AppendLine();
        }

        if (campaign.History.Count > 0)
        {
            sb.AppendLine("Recent conversation history:");
            foreach (var entry in campaign.History.TakeLast(maxHistoryEntries))
            {
                var reaction = entry.AiAction == "speak" ? $"responded: \"{entry.AiText}\"" : "stayed silent";
                sb.AppendLine($"- Dialogue {entry.DialogueId}: {entry.TranscriptSummary} -> you {reaction}");
            }
            sb.AppendLine();
        }

        if (dialogue.Speakers.Count > 0)
        {
            sb.AppendLine("Speakers in this dialogue: " + string.Join(", ", dialogue.Speakers.Select(s => s.Name)));
        }

        sb.AppendLine("Current dialogue:");
        sb.AppendLine(BuildTranscript(dialogue));

        return sb.ToString();
    }

    public string BuildTranscript(DialogueState dialogue)
    {
        var sb = new StringBuilder();
        foreach (var evt in dialogue.Events.OrderBy(e => e.Timestamp))
        {
            switch (evt.Type)
            {
                case "dialogue.line":
                {
                    var speaker = GetString(evt.Data, "speaker") ?? "?";
                    var text = CleanText(GetString(evt.Data, "text"));
                    if (!string.IsNullOrEmpty(text)) sb.AppendLine($"{speaker}: {text}");
                    break;
                }
                case "dialogue.choice":
                {
                    var text = CleanText(GetString(evt.Data, "text"));
                    if (!string.IsNullOrEmpty(text)) sb.AppendLine($"Player chose: {text}");
                    break;
                }
            }
        }
        return sb.ToString();
    }

    public static string CleanText(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var withoutTags = TagRegex.Replace(raw, string.Empty);
        return WebUtility.HtmlDecode(withoutTags).Trim();
    }

    private static string? GetString(JsonElement data, string property)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        return data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
