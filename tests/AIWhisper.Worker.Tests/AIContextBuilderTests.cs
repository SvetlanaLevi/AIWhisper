using AIWhisper.Worker.Conversation;
using AIWhisper.Worker.Configuration;
using AIWhisper.Worker.EventProcessing;
using AIWhisper.Worker.Knowledge;
using Xunit;

namespace AIWhisper.Worker.Tests;

public class AIContextBuilderTests
{
    private static WorkerEvent MakeEvent(string type, DateTime timestamp, string dataJson)
    {
        var json = "{\"schemaVersion\":1,\"campaignId\":\"C1\",\"timestamp\":\"" +
            timestamp.ToString("yyyy-MM-dd HH:mm:ss.fffffff") +
            "\",\"source\":\"client\",\"type\":\"" + type + "\",\"data\":" + dataJson + "}";
        var ok = EventParser.TryParse(json, "C1", out var evt, out var error);
        Assert.True(ok, error?.Message);
        return evt!;
    }

    [Fact]
    public void BuildTranscript_StripsMarkupAndPreservesOrder()
    {
        var t0 = new DateTime(2026, 1, 1, 10, 0, 0);
        var dialogue = new DialogueState { CampaignId = "C1", DialogueId = "D1" };
        dialogue.Events.Add(MakeEvent("dialogue.line", t0, """{"dialogueId":"D1","speaker":"Gale","text":"Go ahead, I'm listening."}"""));
        dialogue.Events.Add(MakeEvent("dialogue.choice", t0.AddSeconds(1), """{"dialogueId":"D1","speaker":"Gale","text":"<i>Leave.</i>"}"""));

        var transcript = new AIContextBuilder().BuildTranscript(dialogue);

        Assert.Contains("Gale: Go ahead, I'm listening.", transcript);
        Assert.Contains("Player chose: Leave.", transcript);
        Assert.DoesNotContain("<i>", transcript);
    }

    [Fact]
    public void CleanText_DecodesHtmlEntitiesAfterStrippingTags()
    {
        var cleaned = AIContextBuilder.CleanText("<b>Rock &amp; Roll</b>");

        Assert.Equal("Rock & Roll", cleaned);
    }

    [Fact]
    public void BuildUserPrompt_IncludesSessionAndHistory()
    {
        var campaign = new CampaignContext { CampaignId = "C1", Directory = "/tmp/C1" };
        campaign.Session.Player = "Victoria";
        campaign.Session.Region = "WLD_Main_A";
        campaign.History.Add(new ConversationHistoryEntry("D0", "Res0", null, null, "some earlier chat", "silent", null));

        var dialogue = new DialogueState { CampaignId = "C1", DialogueId = "D1" };
        dialogue.Events.Add(MakeEvent("dialogue.line", DateTime.UtcNow, """{"dialogueId":"D1","speaker":"Gale","text":"hello"}"""));

        var prompt = new AIContextBuilder().BuildUserPrompt(campaign, dialogue, maxHistoryEntries: 5);

        Assert.Contains("Victoria", prompt);
        Assert.Contains("WLD_Main_A", prompt);
        Assert.Contains("D0", prompt);
        Assert.Contains("Gale: hello", prompt);
    }

    [Fact]
    public void BuildUserPrompt_IncludesPersistentMemoryBeforeRecentContext()
    {
        var campaign = new CampaignContext { CampaignId = "C1", Directory = "/tmp/C1" };
        campaign.Memory.Summary = "The player is earning Astarion's trust.";
        campaign.Memory.Relationships["Astarion"] = "Flirtatious but cautious.";
        campaign.Memory.ImportantEvents.Add("The player promised to protect the grove.");
        campaign.History.Add(new ConversationHistoryEntry("D0", null, null, null, "recent exchange", "silent", null));

        var dialogue = new DialogueState { CampaignId = "C1", DialogueId = "D1" };
        dialogue.Events.Add(MakeEvent("dialogue.line", DateTime.UtcNow, """{"dialogueId":"D1","speaker":"Gale","text":"We should go."}"""));

        var prompt = new AIContextBuilder().BuildUserPrompt(campaign, dialogue, maxHistoryEntries: 5);

        Assert.Contains("CAMPAIGN MEMORY", prompt);
        Assert.Contains("Astarion: Flirtatious but cautious.", prompt);
        Assert.Contains("RECENT CONTEXT", prompt);
        Assert.True(prompt.IndexOf("CAMPAIGN MEMORY", StringComparison.Ordinal) < prompt.IndexOf("RECENT CONTEXT", StringComparison.Ordinal));
        Assert.Contains("CURRENT EVENT", prompt);
    }

    [Fact]
    public void BuildUserPrompt_IncludesKnowledgeOnlyForKnownLineSpeakers()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"AIWhisper.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(Path.Combine(directory, "astarion.json"), """
                { "name": "Astarion", "category": "companion", "race": "High Elf", "class": "Rogue", "role": "Origin companion", "summary": "A theatrical survivor.", "personality": ["sarcastic"] }
                """);
            var builder = new AIContextBuilder(new CharacterKnowledgeProvider(directory));
            var campaign = new CampaignContext { CampaignId = "C1", Directory = "/tmp/C1" };
            var dialogue = new DialogueState { CampaignId = "C1", DialogueId = "D1" };
            dialogue.Events.Add(MakeEvent("dialogue.line", DateTime.UtcNow, """{"dialogueId":"D1","speaker":" Astarion ","text":"Hello"}"""));
            dialogue.Events.Add(MakeEvent("dialogue.line", DateTime.UtcNow, """{"dialogueId":"D1","speaker":"Unknown","text":"Hello"}"""));

            var prompt = builder.BuildUserPrompt(campaign, dialogue, maxHistoryEntries: 0);

            Assert.Contains("CHARACTER KNOWLEDGE", prompt);
            Assert.Contains("Astarion — High Elf Rogue, Origin companion.", prompt);
            Assert.DoesNotContain("Unknown —", prompt);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BuildUserPrompt_BoundsLongTermMemoryAndPrioritizesCurrentSpeakerRelationship()
    {
        var campaign = new CampaignContext { CampaignId = "C1", Directory = "/tmp/C1" };
        campaign.Memory.ImportantEvents.AddRange(["old event", "middle event", "recent event"]);
        campaign.Memory.Relationships["Astarion"] = "Old acquaintance";
        campaign.Memory.Relationships["Zevlor"] = "Current ally";
        campaign.Memory.ImportantEventSources["recent event"] = ["secret-dialogue-id"];
        var dialogue = new DialogueState { CampaignId = "C1", DialogueId = "D1" };
        dialogue.Events.Add(MakeEvent("dialogue.line", DateTime.UtcNow, """{"dialogueId":"D1","speaker":"Zevlor","text":"Listen."}"""));
        var builder = new AIContextBuilder(memoryOptions: new MemoryOptions
        {
            MaxContextImportantEvents = 2,
            MaxContextRelationships = 1,
        });

        var prompt = builder.BuildUserPrompt(campaign, dialogue, maxHistoryEntries: 0);

        Assert.DoesNotContain("old event", prompt);
        Assert.Contains("middle event", prompt);
        Assert.Contains("recent event", prompt);
        Assert.Contains("Zevlor: Current ally", prompt);
        Assert.DoesNotContain("Astarion: Old acquaintance", prompt);
        Assert.DoesNotContain("secret-dialogue-id", prompt);
    }
}
