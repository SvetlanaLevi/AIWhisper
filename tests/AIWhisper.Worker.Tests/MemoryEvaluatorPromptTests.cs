using System.Text.Json;
using AIWhisper.Worker.Memory;
using Xunit;

namespace AIWhisper.Worker.Tests;

public sealed class MemoryEvaluatorPromptTests
{
    [Fact]
    public void DefaultPrompt_SeparatesOperationIdsAndPhaseRelevance()
    {
        var prompt = ParasiteMemoryEvaluatorPrompt.DefaultTemplate;

        Assert.Contains("For create, targetId must be null", prompt);
        Assert.Contains("targetId must exactly match an existing memory ID", prompt);
        Assert.Contains("Development phase must not be used to discard", prompt);
        Assert.Contains("Active Memory selection", prompt);
    }

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
        var json = ParasiteMemoryEvaluatorPrompt.RenderUserContext(new MemoryEvaluationRequest(
            "C1", [item], "Nettie: I can treat you.", "D1", "Awakening", "WLD_Main_A", ["Nettie"]));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("Nettie: I can treat you.", root.GetProperty("completedEventBatch").GetString());
        Assert.Equal("Awakening", root.GetProperty("currentDevelopmentPhase").GetString());
        Assert.Equal(item.Id, root.GetProperty("existingLongTermMemory")[0].GetProperty("Id").GetGuid());
    }
}
