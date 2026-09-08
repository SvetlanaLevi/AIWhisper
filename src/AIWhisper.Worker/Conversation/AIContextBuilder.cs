using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIWhisper.Worker.Configuration;
using AIWhisper.Worker.EventProcessing;
using AIWhisper.Worker.Knowledge;
using AIWhisper.Worker.Memory;

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
    private readonly MemoryOptions _memoryOptions;
    private readonly ActiveMemorySelector _activeMemorySelector;

    public AIContextBuilder(ICharacterKnowledgeProvider? characterKnowledge = null, MemoryOptions? memoryOptions = null)
    {
        _characterKnowledge = characterKnowledge;
        _memoryOptions = memoryOptions ?? new MemoryOptions();
        _activeMemorySelector = new ActiveMemorySelector(
            new ParasiteMemoryPhasePolicy(),
            _memoryOptions.MaxActiveItems);
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

        AppendActiveMemory(sb, campaign, dialogue);
        AppendCharacterKnowledge(sb, campaign, dialogue);

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

    private void AppendActiveMemory(StringBuilder sb, CampaignContext campaign, DialogueState dialogue)
    {
        var characters = dialogue.Events
            .Where(evt => evt.Type == "dialogue.line")
            .Select(evt => GetString(evt.Data, "speaker"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToList();
        var active = _activeMemorySelector.Select(
            campaign.Memory.LongTermMemory,
            campaign.Development.CurrentPhase,
            BuildTranscript(dialogue),
            characters);
        if (active.Count == 0) return;

        sb.AppendLine("ACTIVE MEMORY");
        sb.AppendLine("Relevant memories:");
        foreach (var item in active) sb.AppendLine("- " + item.Summary);
        sb.AppendLine();
    }

    private void AppendCharacterKnowledge(StringBuilder sb, CampaignContext campaign, DialogueState dialogue)
    {
        var names = dialogue.Events
            .Where(evt => evt.Type == "dialogue.line")
            .Select(evt => GetString(evt.Data, "speaker"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var matches = _characterKnowledge is null
            ? []
            : names.Select(_characterKnowledge.Find).Where(character => character is not null).Cast<CharacterKnowledge>().ToList();

        if (matches.Count > 0)
        {
            sb.AppendLine("CHARACTER KNOWLEDGE");
            foreach (var character in matches) sb.AppendLine(CharacterKnowledgeFormatter.Format(character));
            sb.AppendLine();
        }

        var discovered = campaign.Memory.CharacterKnowledge.Where(item =>
            names.Contains(item.CharacterName, StringComparer.OrdinalIgnoreCase) && item.KnownFacts.Count > 0).ToList();
        if (discovered.Count == 0) return;
        sb.AppendLine("DISCOVERED CHARACTER KNOWLEDGE");
        foreach (var item in discovered)
            sb.AppendLine($"{item.CharacterName}: {string.Join("; ", item.KnownFacts)}");
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
