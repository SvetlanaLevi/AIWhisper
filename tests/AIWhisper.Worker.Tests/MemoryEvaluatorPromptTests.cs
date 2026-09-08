using System.Text.Json;
using AIWhisper.Worker.Memory;
using Xunit;

namespace AIWhisper.Worker.Tests;

public sealed class MemoryEvaluatorPromptTests
{
    [Fact]
    public void UserContext_ContainsCompletedBatchExistingMemoryAndDevelopmentContext()
    {
        var item = new ParasiteMemoryItem
        {
            Id = Guid.NewGuid(),
            Summary = "The host previously refused a dangerous cure.",
            Category = MemoryCategory.RemovalThreat,
            Tags = ["cure"],
        };
        var known = new DiscoveredCharacterKnowledge
        {
            CharacterName = "Ketheric Thorm",
            KnownFacts = ["He survived a fatal wound."],
        };
        var json = ParasiteMemoryEvaluatorPrompt.RenderUserContext(new MemoryEvaluationRequest(
            "C1", [item], [known], ["Ketheric Thorm"], "Nettie: I can treat you.", "D1", "Awakening", "WLD_Main_A", ["Nettie"]));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("Nettie: I can treat you.", root.GetProperty("completedEventBatch").GetString());
        Assert.Equal("Awakening", root.GetProperty("currentDevelopmentPhase").GetString());
        Assert.Equal(item.Id, root.GetProperty("existingLongTermMemory")[0].GetProperty("Id").GetGuid());
        Assert.Equal("Ketheric Thorm", root.GetProperty("trackedCharacterNames")[0].GetString());
        Assert.Equal("He survived a fatal wound.", root.GetProperty("existingCharacterKnowledge")[0].GetProperty("KnownFacts")[0].GetString());
    }
}
