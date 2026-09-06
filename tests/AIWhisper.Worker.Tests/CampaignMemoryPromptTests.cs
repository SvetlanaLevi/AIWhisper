using AIWhisper.Worker.AI;
using AIWhisper.Worker.Conversation;
using Xunit;

namespace AIWhisper.Worker.Tests;

public sealed class CampaignMemoryPromptTests
{
    [Fact]
    public void Create_MakesAnEmptyDeltaTheDefaultForMinorDialogue()
    {
        var template = "Most ordinary dialogues must produce no memory changes\n\"UpdatedSummary\": null\n" +
            CampaignMemoryPrompt.CurrentMemoryPlaceholder + "\n" + CampaignMemoryPrompt.DialoguePlaceholder;
        var prompt = CampaignMemoryPrompt.Render(template, new CampaignMemory { Summary = "Existing state" }, "Okta offers food and warns about gnolls.");

        Assert.Contains("Most ordinary dialogues must produce no memory changes", prompt);
        Assert.Contains("\"UpdatedSummary\": null", prompt);
        Assert.Contains("Existing state", prompt);
    }

    [Fact]
    public void Create_RequiresMaterialEvidenceAndPreservesUncertainty()
    {
        const string template = "Would this still matter if the next 10 dialogues were unrelated? Never turn speculation into fact. Normally require evidence from at least two separate events. Ordinary commerce is not relationships. Running jokes appear multiple times. {{NEWLY_PROCESSED_DIALOGUE}}";
        var prompt = CampaignMemoryPrompt.Render(template, new CampaignMemory(), "Aradin guesses that Halsin may be lost.");

        Assert.Contains("next 10 dialogues were unrelated", prompt);
        Assert.Contains("Never turn speculation into fact", prompt);
        Assert.Contains("at least two separate events", prompt);
        Assert.Contains("not relationships", prompt);
        Assert.Contains("multiple times", prompt);
        Assert.Contains("Aradin guesses that Halsin may be lost.", prompt);
    }
}
