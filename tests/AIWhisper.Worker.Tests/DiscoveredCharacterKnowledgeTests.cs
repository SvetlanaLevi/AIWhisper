using AIWhisper.Worker.Conversation;
using AIWhisper.Worker.Memory;
using Xunit;

namespace AIWhisper.Worker.Tests;

public sealed class DiscoveredCharacterKnowledgeTests
{
    [Fact]
    public void Apply_AddsReplacesCapsAndRemovesTrackedKnowledge()
    {
        var memory = new CampaignMemory();

        var added = DiscoveredCharacterKnowledgeApplier.Apply(memory, [new()
        {
            CharacterName = " ketheric thorm ",
            KnownFacts = ["First", "First", "Second", "Third"],
        }], ["Ketheric Thorm"], 2);

        Assert.True(added.Changed);
        Assert.Equal("Ketheric Thorm", memory.CharacterKnowledge.Single().CharacterName);
        Assert.Equal(["First", "Second"], memory.CharacterKnowledge.Single().KnownFacts);

        var replaced = DiscoveredCharacterKnowledgeApplier.Apply(memory, [new()
        {
            CharacterName = "Ketheric Thorm",
            KnownFacts = ["Revised"],
        }], ["Ketheric Thorm"], 2);
        Assert.True(replaced.Changed);
        Assert.Equal(["Revised"], memory.CharacterKnowledge.Single().KnownFacts);

        var removed = DiscoveredCharacterKnowledgeApplier.Apply(memory, [new()
        {
            CharacterName = "Ketheric Thorm",
            KnownFacts = [],
        }], ["Ketheric Thorm"], 2);
        Assert.True(removed.Changed);
        Assert.Empty(memory.CharacterKnowledge);
    }

    [Fact]
    public void Apply_RejectsUntrackedCharacters()
    {
        var memory = new CampaignMemory();

        var result = DiscoveredCharacterKnowledgeApplier.Apply(memory, [new()
        {
            CharacterName = "Random Guard",
            KnownFacts = ["A secret."],
        }], ["Ketheric Thorm"], 8);

        Assert.False(result.Changed);
        Assert.Equal(["Random Guard"], result.RejectedCharacters);
        Assert.Empty(memory.CharacterKnowledge);
    }
}
