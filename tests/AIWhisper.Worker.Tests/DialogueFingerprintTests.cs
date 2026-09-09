using AIWhisper.Worker.Conversation;
using AIWhisper.Worker.EventProcessing;
using Xunit;

namespace AIWhisper.Worker.Tests;

public sealed class DialogueFingerprintTests
{
    [Fact]
    public void NormalizeResource_RemovesTrailingGuid()
    {
        Assert.Equal(
            "NGB_BuyFromTrader",
            DialogueResourceName.Normalize("NGB_BuyFromTrader_65b9572e-20c8-46de-a94d-4371bb5f6f85"));
    }

    [Fact]
    public void Create_IgnoresInvocationIdAndFormatting_WhenResourceAndBranchMatch()
    {
        var first = Create("runtime-1", "GLO_Companion_JoinCamp", "  Wait   for me at camp. ");
        var second = Create("runtime-2", "GLO_Companion_JoinCamp", "Wait for me at camp.");

        Assert.Equal(DialogueFingerprint.Create(first), DialogueFingerprint.Create(second));
    }

    [Fact]
    public void Create_DiffersForAnotherBranchInTheSameResource()
    {
        var first = Create("runtime-1", "GLO_Companion", "Wait for me at camp.");
        var second = Create("runtime-2", "GLO_Companion", "Join me instead.");

        Assert.NotEqual(DialogueFingerprint.Create(first), DialogueFingerprint.Create(second));
    }

    private static DialogueState Create(string dialogueId, string resource, string text)
    {
        var json = $$$"""
            {"campaignId":"C1","source":"client","type":"dialogue.choice","timestamp":"2026-01-01 10:00:00","data":{"dialogueId":"{{{dialogueId}}}","text":"{{{text}}}"}}
            """;
        Assert.True(EventParser.TryParse(json, "C1", out var evt, out var error), error?.Message);
        var dialogue = new DialogueState
        {
            CampaignId = "C1",
            DialogueId = dialogueId,
            DialogueResource = resource,
        };
        dialogue.Events.Add(evt!);
        return dialogue;
    }
}
