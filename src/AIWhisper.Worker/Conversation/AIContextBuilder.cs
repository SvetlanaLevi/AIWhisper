using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIWhisper.Worker.EventProcessing;
using AIWhisper.Worker.Knowledge;

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
    private readonly ICharacterKnowledgeProvider? _characterKnowledge;

    public AIContextBuilder(ICharacterKnowledgeProvider? characterKnowledge = null)
    {
        _characterKnowledge = characterKnowledge;
    }

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

        AppendCampaignMemory(sb, campaign.Memory);
        AppendCharacterKnowledge(sb, dialogue);

        if (campaign.History.Count > 0)
        {
            sb.AppendLine("RECENT CONTEXT");
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

        sb.AppendLine("CURRENT EVENT");
        sb.AppendLine(BuildTranscript(dialogue));

        return sb.ToString();
    }

    private static void AppendCampaignMemory(StringBuilder sb, CampaignMemory memory)
    {
        if (string.IsNullOrWhiteSpace(memory.Summary) &&
            memory.ImportantEvents.Count == 0 &&
            memory.Relationships.Count == 0 &&
            memory.PlayerTraits.Count == 0 &&
            memory.RunningJokes.Count == 0)
        {
            return;
        }

        sb.AppendLine("CAMPAIGN MEMORY");
        if (!string.IsNullOrWhiteSpace(memory.Summary)) sb.AppendLine($"Summary: {memory.Summary}");
        AppendList(sb, "Relationships", memory.Relationships.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}: {pair.Value}"));
        AppendList(sb, "Player tendencies", memory.PlayerTraits);
        AppendList(sb, "Important past events", memory.ImportantEvents);
        AppendList(sb, "Running jokes", memory.RunningJokes);
        sb.AppendLine();
    }

    private static void AppendList(StringBuilder sb, string heading, IEnumerable<string> items)
    {
        var values = items.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        if (values.Count == 0) return;

        sb.AppendLine(heading + ":");
        foreach (var value in values) sb.AppendLine("- " + value);
    }

    private void AppendCharacterKnowledge(StringBuilder sb, DialogueState dialogue)
    {
        if (_characterKnowledge is null) return;

        var names = dialogue.Events
            .Where(evt => evt.Type == "dialogue.line")
            .Select(evt => GetString(evt.Data, "speaker"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var matches = names.Select(_characterKnowledge.Find).Where(character => character is not null).Cast<CharacterKnowledge>().ToList();
        if (matches.Count == 0) return;

        sb.AppendLine("CHARACTER KNOWLEDGE");
        foreach (var character in matches) sb.AppendLine(CharacterKnowledgeFormatter.Format(character));
        sb.AppendLine();
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
