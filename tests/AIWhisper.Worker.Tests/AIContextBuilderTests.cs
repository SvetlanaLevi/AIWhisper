using AIWhisper.Worker.Conversation;
using AIWhisper.Worker.Configuration;
using AIWhisper.Worker.EventProcessing;
using AIWhisper.Worker.Knowledge;
using AIWhisper.Worker.Memory;
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
    public void BuildUserPrompt_IncludesPlayerAndHistoryButNotTechnicalRegion()
    {
        var campaign = new CampaignContext { CampaignId = "C1", Directory = "/tmp/C1" };
        campaign.Session.Player = "Victoria";
        campaign.Session.Region = "WLD_Main_A";
        campaign.History.Add(new ConversationHistoryEntry("D0", "Res0", null, null, "some earlier chat", "silent", null));

        var dialogue = new DialogueState { CampaignId = "C1", DialogueId = "D1" };
        dialogue.Events.Add(MakeEvent("dialogue.line", DateTime.UtcNow, """{"dialogueId":"D1","speaker":"Gale","text":"hello"}"""));

        var prompt = new AIContextBuilder().BuildUserPrompt(campaign, dialogue, maxHistoryEntries: 5);

        Assert.Contains("Victoria", prompt);
        Assert.DoesNotContain("WLD_Main_A", prompt);
        Assert.Contains("D0", prompt);
        Assert.Contains("Gale: hello", prompt);
    }

    [Fact]
    public void BuildUserPrompt_IncludesDialogueNameWithoutTrailingGuid()
    {
        const string guid = "12345678-1234-1234-1234-123456789abc";
        var campaign = new CampaignContext { CampaignId = "C1", Directory = "/tmp/C1" };
        var dialogue = new DialogueState
        {
            CampaignId = "C1",
            DialogueId = "D1",
            DialogueResource = $"DEN_GoblinAttack_{guid}",
        };
        dialogue.Events.Add(MakeEvent("dialogue.line", DateTime.UtcNow,
            """{"dialogueId":"D1","speaker":"Gale","text":"hello"}"""));

        var prompt = new AIContextBuilder().BuildUserPrompt(campaign, dialogue, maxHistoryEntries: 0);

        Assert.Contains("Dialogue name: DEN_GoblinAttack", prompt);
        Assert.DoesNotContain(guid, prompt);
    }

    [Fact]
    public void BuildUserPrompt_IncludesPersistentMemoryBeforeRecentContext()
    {
        var campaign = new CampaignContext { CampaignId = "C1", Directory = "/tmp/C1" };
        campaign.Memory.LongTermMemory.Add(new ParasiteMemoryItem
        {
            Id = Guid.NewGuid(),
            Summary = "Astarion is flirtatious but cautious with the host.",
            Category = MemoryCategory.CharacterRelationship,
            CharacterName = "Astarion",
        });
        campaign.History.Add(new ConversationHistoryEntry("D0", null, null, null, "recent exchange", "silent", null));

        var dialogue = new DialogueState { CampaignId = "C1", DialogueId = "D1" };
        dialogue.Events.Add(MakeEvent("dialogue.line", DateTime.UtcNow, """{"dialogueId":"D1","speaker":"Astarion","text":"We should go."}"""));

        var prompt = new AIContextBuilder().BuildUserPrompt(campaign, dialogue, maxHistoryEntries: 5);

        Assert.Contains("ACTIVE MEMORY", prompt);
        Assert.Contains("Astarion is flirtatious but cautious with the host.", prompt);
        Assert.Contains("RECENT CONTEXT", prompt);
        Assert.True(prompt.IndexOf("ACTIVE MEMORY", StringComparison.Ordinal) < prompt.IndexOf("RECENT CONTEXT", StringComparison.Ordinal));
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
        campaign.Memory.LongTermMemory.AddRange([
            new() { Id = Guid.NewGuid(), Summary = "Astarion is an old acquaintance.", Category = MemoryCategory.CharacterRelationship, CharacterName = "Astarion" },
            new() { Id = Guid.NewGuid(), Summary = "Zevlor may protect the host.", Category = MemoryCategory.CharacterOpinion, CharacterName = "Zevlor" },
            new() { Id = Guid.NewGuid(), Summary = "The host once trusted Zevlor.", Category = MemoryCategory.Trust, CharacterName = "Zevlor" },
        ]);
        var dialogue = new DialogueState { CampaignId = "C1", DialogueId = "D1" };
        dialogue.Events.Add(MakeEvent("dialogue.line", DateTime.UtcNow, """{"dialogueId":"D1","speaker":"Zevlor","text":"Listen."}"""));
        var builder = new AIContextBuilder(memoryOptions: new MemoryOptions
        {
            MaxActiveItems = 1,
        });

        var prompt = builder.BuildUserPrompt(campaign, dialogue, maxHistoryEntries: 0);

        Assert.Contains("The host once trusted Zevlor.", prompt);
        Assert.DoesNotContain("Zevlor may protect the host.", prompt);
        Assert.DoesNotContain("Astarion is an old acquaintance.", prompt);
    }

    [Fact]
    public void BuildUserPrompt_IncludesDiscoveredKnowledgeOnlyForCurrentSpeakers()
    {
        var campaign = new CampaignContext { CampaignId = "C1", Directory = "/tmp/C1" };
        campaign.Memory.CharacterKnowledge.AddRange([
            new() { CharacterName = "Ketheric Thorm", KnownFacts = ["He survived a fatal wound."] },
            new() { CharacterName = "Raphael", KnownFacts = ["He wants the Crown."] },
        ]);
        var dialogue = new DialogueState { CampaignId = "C1", DialogueId = "D1" };
        dialogue.Events.Add(MakeEvent("dialogue.line", DateTime.UtcNow,
            """{"dialogueId":"D1","speaker":"Ketheric Thorm","text":"Bow."}"""));

        var prompt = new AIContextBuilder().BuildUserPrompt(campaign, dialogue, 0);

        Assert.Contains("DISCOVERED CHARACTER KNOWLEDGE", prompt);
        Assert.Contains("Ketheric Thorm: He survived a fatal wound.", prompt);
        Assert.DoesNotContain("He wants the Crown.", prompt);
    }

    [Fact]
    public void BuildUserPrompt_SeparatesRecentRemarksAndRequestsNovelty()
    {
        var campaign = new CampaignContext { CampaignId = "C1", Directory = "/tmp/C1" };
        campaign.History.Add(new ConversationHistoryEntry(
            "D0", null, null, null, "A dangerous choice", "speak", "Stay alert. This endangers us both."));
        var dialogue = new DialogueState { CampaignId = "C1", DialogueId = "D1" };
        dialogue.Events.Add(MakeEvent("dialogue.line", DateTime.UtcNow,
            "{\"dialogueId\":\"D1\",\"speaker\":\"Gale\",\"text\":\"Another dangerous choice.\"}"));

        var prompt = new AIContextBuilder().BuildUserPrompt(campaign, dialogue, 5);

        Assert.Contains("RECENT PARASITE VOICE", prompt);
        Assert.DoesNotContain("Do not repeat the same central thought", prompt);
        Assert.Equal(1, prompt.Split("Stay alert. This endangers us both.").Length - 1);
    }
}
