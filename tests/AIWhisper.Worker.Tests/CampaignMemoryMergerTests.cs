using AIWhisper.Worker.Configuration;
using AIWhisper.Worker.Conversation;
using Xunit;

namespace AIWhisper.Worker.Tests;

public sealed class CampaignMemoryMergerTests
{
    private static readonly MemoryOptions Options = new()
    {
        MaxImportantEvents = 2,
        MaxPlayerTraits = 2,
        MaxRunningJokes = 2,
        MaxSummaryCharacters = 100,
    };

    [Fact]
    public void Apply_UpdatesRelationshipsAndDoesNotDuplicateLists()
    {
        var memory = new CampaignMemory
        {
            ImportantEvents = ["Met Astarion"],
            Relationships = { ["Astarion"] = "Wary allies" },
        };
        var update = new CampaignMemoryUpdate
        {
            ImportantEventsToAdd = ["met astarion", "Promised to help Astarion"],
            RelationshipUpdates = [new RelationshipMemoryUpdate
            {
                Name = "astarion",
                Description = "Flirtatious trust is growing",
            }],
        };

        Assert.True(CampaignMemoryMerger.Apply(memory, update, Options));
        Assert.Equal(["Met Astarion", "Promised to help Astarion"], memory.ImportantEvents);
        Assert.Single(memory.Relationships);
        Assert.Equal("Flirtatious trust is growing", memory.Relationships["astarion"]);
    }

    [Fact]
    public void Apply_NoOpDoesNotChangeMemory()
    {
        var memory = new CampaignMemory { Summary = "Existing summary" };

        Assert.False(CampaignMemoryMerger.Apply(memory, new CampaignMemoryUpdate { UpdatedSummary = "  " }, Options));
        Assert.Equal("Existing summary", memory.Summary);
    }

    [Fact]
    public void Apply_ReplacesNonEmptySummaryAndTrimsOldestItemsAtLimit()
    {
        var memory = new CampaignMemory { Summary = "Old", ImportantEvents = ["one", "two"] };
        var update = new CampaignMemoryUpdate
        {
            UpdatedSummary = " New summary ",
            ImportantEventsToAdd = ["three"],
        };

        Assert.True(CampaignMemoryMerger.Apply(memory, update, Options));
        Assert.Equal("New summary", memory.Summary);
        Assert.Equal(["two", "three"], memory.ImportantEvents);
    }
}
