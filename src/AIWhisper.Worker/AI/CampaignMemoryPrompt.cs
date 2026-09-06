using System.Text.Json;
using AIWhisper.Worker.Conversation;

namespace AIWhisper.Worker.AI;

public static class CampaignMemoryPrompt
{
    public const string CurrentMemoryPlaceholder = "{{CURRENT_CAMPAIGN_MEMORY_JSON}}";
    public const string DialoguePlaceholder = "{{NEWLY_PROCESSED_DIALOGUE}}";

    // Used only when the configured text file cannot be found.
    public const string DefaultTemplate = """
        You maintain conservative long-term memory for a character observing a Baldur's Gate 3 campaign. Prefer an empty delta when uncertain.

        CURRENT CAMPAIGN MEMORY
        {{CURRENT_CAMPAIGN_MEMORY_JSON}}

        NEWLY PROCESSED DIALOGUE
        {{NEWLY_PROCESSED_DIALOGUE}}
        """;

    public static string Render(string template, CampaignMemory currentMemory, string transcript)
    {
        var effectiveTemplate = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template;
        return effectiveTemplate
            .Replace(CurrentMemoryPlaceholder, JsonSerializer.Serialize(currentMemory), StringComparison.Ordinal)
            .Replace(DialoguePlaceholder, transcript, StringComparison.Ordinal);
    }
}
