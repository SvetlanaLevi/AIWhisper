using AIWhisper.Worker.Conversation;
using AIWhisper.Worker.EventProcessing;
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
}
