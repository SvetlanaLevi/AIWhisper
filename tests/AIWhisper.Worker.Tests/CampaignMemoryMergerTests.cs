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
    public void Apply_EmptyDeltaPreservesExistingSummaryAndMemory()
    {
        var memory = new CampaignMemory
        {
            Summary = "Halsin is missing.",
            ImportantEvents = ["The goblin temple is dangerous."],
        };

        Assert.False(CampaignMemoryMerger.Apply(memory, new CampaignMemoryUpdate(), Options));
        Assert.Equal("Halsin is missing.", memory.Summary);
        Assert.Equal(["The goblin temple is dangerous."], memory.ImportantEvents);
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

    [Fact]
    public void Apply_CanCorrectFactAndTracksItsSourceDialogue()
    {
        var memory = new CampaignMemory { ImportantEvents = ["Kagha killed a child named Teela."] };
        var update = new CampaignMemoryUpdate
        {
            ImportantEventsToRemove = ["Kagha killed a child named Teela."],
            ImportantEventsToAdd = ["Kagha's snake Teela killed Arabella."],
        };

        Assert.True(CampaignMemoryMerger.Apply(memory, update, Options, "733"));
        Assert.Equal(["Kagha's snake Teela killed Arabella."], memory.ImportantEvents);
        Assert.Equal(["733"], memory.ImportantEventSources["Kagha's snake Teela killed Arabella."]);
    }

    [Fact]
    public void Apply_PromotesTraitOnlyAfterEvidenceFromTwoDifferentDialogues()
    {
        var memory = new CampaignMemory();
        var observation = new CampaignMemoryUpdate { PlayerTraitsToAdd = ["Escalates failed diplomacy into violence."] };

        Assert.True(CampaignMemoryMerger.Apply(memory, observation, Options, "736"));
        Assert.Empty(memory.PlayerTraits);
        Assert.Single(memory.PlayerTraitCandidates);

        Assert.False(CampaignMemoryMerger.Apply(memory, observation, Options, "736"));
        Assert.Empty(memory.PlayerTraits);

        Assert.True(CampaignMemoryMerger.Apply(memory, observation, Options, "812"));
        Assert.Equal(["Escalates failed diplomacy into violence."], memory.PlayerTraits);
        Assert.Empty(memory.PlayerTraitCandidates);
        Assert.Equal(["736", "812"], memory.PlayerTraitSources["Escalates failed diplomacy into violence."]);
    }

    [Fact]
    public void Apply_CanRemoveRelationshipsTraitsAndJokes()
    {
        var memory = new CampaignMemory
        {
            Relationships = { ["Astarion"] = "Trusted ally" },
            PlayerTraits = ["Always cruel"],
            RunningJokes = ["Spoons"],
        };
        var update = new CampaignMemoryUpdate
        {
            RelationshipsToRemove = ["astarion"],
            PlayerTraitsToRemove = ["always cruel"],
            RunningJokesToRemove = ["spoons"],
        };

        Assert.True(CampaignMemoryMerger.Apply(memory, update, Options, "900"));
        Assert.Empty(memory.Relationships);
        Assert.Empty(memory.PlayerTraits);
        Assert.Empty(memory.RunningJokes);
    }

    [Fact]
    public void Apply_CurrentSituationCanChangeAndBeClearedWithoutReplacingSummary()
    {
        var memory = new CampaignMemory { Summary = "Durable overview", CurrentSituation = "Guards are approaching" };

        Assert.True(CampaignMemoryMerger.Apply(
            memory,
            new CampaignMemoryUpdate { UpdatedCurrentSituation = "" },
            Options,
            "901"));

        Assert.Equal("Durable overview", memory.Summary);
        Assert.Empty(memory.CurrentSituation);
    }
}
