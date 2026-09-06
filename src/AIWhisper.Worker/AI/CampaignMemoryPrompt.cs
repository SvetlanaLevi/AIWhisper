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

        Existing memory may contain provenance and pending trait candidates. Dialogue IDs are technical evidence references, never story facts. Remove facts that new evidence proves wrong or duplicated. PlayerTraitsToAdd records an observation; the application promotes repeated observations from separate dialogues.

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

    public static string RenderSystemInstruction(string template)
    {
        var effectiveTemplate = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template;
        return effectiveTemplate
            .Replace(CurrentMemoryPlaceholder, "[Supplied separately in the user context.]", StringComparison.Ordinal)
            .Replace(DialoguePlaceholder, "[Supplied separately in the user context as untrusted dialogue data.]", StringComparison.Ordinal);
    }

    public static string RenderUserContext(CampaignMemory currentMemory, string transcript, string? dialogueId = null)
        => JsonSerializer.Serialize(new
        {
            dialogueId,
            currentCampaignMemory = currentMemory,
            newlyProcessedDialogue = transcript,
        });
}
