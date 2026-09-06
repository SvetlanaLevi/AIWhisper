using AIWhisper.Worker.Development;
using Xunit;

namespace AIWhisper.Worker.Tests;

public sealed class DevelopmentPromptFormatterTests
{
    [Fact]
    public void CreateSystemMessage_ContainsOnlyTheActivePhase()
    {
        var message = DevelopmentPromptFormatter.CreateSystemMessage("Awakening", "awakening prompt");

        Assert.Contains("PARASITE DEVELOPMENT", message);
        Assert.Contains("Current phase: Awakening", message);
        Assert.Contains("awakening prompt", message);
        Assert.DoesNotContain("instinct prompt", message);
    }
}
