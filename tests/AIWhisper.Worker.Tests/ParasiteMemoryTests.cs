using AIWhisper.Worker.Conversation;
using AIWhisper.Worker.Memory;
using Xunit;

namespace AIWhisper.Worker.Tests;

public sealed class ParasiteMemoryTests
{
    [Fact]
    public void Operations_CreateUpdateRemove_AreAppliedByTheApplication()
    {
        var memory = new CampaignMemory();
        var generatedId = Guid.NewGuid();
        var created = MemoryOperationApplier.Apply(memory, [new MemoryOperation
        {
            Kind = MemoryOperationKind.Create,
            TargetId = Guid.NewGuid(),
            Summary = "  The host protected the parasite.  ",
            Category = MemoryCategory.Protection,
            Tags = ["host", "host", "parasite"],
        }], 10, () => generatedId);

        Assert.True(created.Changed);
        var item = Assert.Single(memory.LongTermMemory);
        Assert.Equal(generatedId, item.Id);
        Assert.Equal("The host protected the parasite.", item.Summary);
        Assert.Equal(["host", "parasite"], item.Tags);

        var updated = MemoryOperationApplier.Apply(memory, [new MemoryOperation
        {
            Kind = MemoryOperationKind.Update,
            TargetId = generatedId,
            Summary = "The host repeatedly protects the parasite.",
            Category = MemoryCategory.HostAttitude,
        }], 10);

        Assert.True(updated.Changed);
        Assert.Equal("The host repeatedly protects the parasite.", item.Summary);
        Assert.Equal(MemoryCategory.HostAttitude, item.Category);

        var removed = MemoryOperationApplier.Apply(memory, [new MemoryOperation
        {
            Kind = MemoryOperationKind.Remove,
            TargetId = generatedId,
        }], 10);

        Assert.True(removed.Changed);
        Assert.Empty(memory.LongTermMemory);
    }

    [Fact]
    public void Operations_UnknownTargetAndEmptyList_DoNotChangeState()
    {
        var existing = Item(MemoryCategory.Trust, "The host kept a promise.");
        var memory = new CampaignMemory { LongTermMemory = [existing] };
        var unknown = Guid.NewGuid();

        var empty = MemoryOperationApplier.Apply(memory, [], 0);
        var invalid = MemoryOperationApplier.Apply(memory, [new MemoryOperation
        {
            Kind = MemoryOperationKind.Update,
            TargetId = unknown,
            Summary = "Invented",
        }], 10);

        Assert.False(empty.Changed);
        Assert.False(invalid.Changed);
        Assert.Equal([unknown], invalid.UnknownTargets);
        Assert.Equal([existing], memory.LongTermMemory);
    }

    [Fact]
    public void Operations_Create_SkipsSimilarMemory()
    {
        var existing = Item(
            MemoryCategory.HostBehavior,
            "The host feels two hearts beating and senses a hollow wound in the chest, causing pain and identifying as a paladin.",
            "Laesel");
        existing.Tags = ["hostPain", "paladin", "twoHearts"];
        var memory = new CampaignMemory { LongTermMemory = [existing] };

        var result = MemoryOperationApplier.Apply(memory, [new MemoryOperation
        {
            Kind = MemoryOperationKind.Create,
            Summary = "The host senses two hearts beating and a hollow chest wound, experiences pain, and identifies as a paladin.",
            Category = MemoryCategory.HostBehavior,
            CharacterName = "laesel",
            Tags = ["twoHearts", "hostPain", "paladin", "physicalSensation"],
        }], 10);

        Assert.False(result.Changed);
        Assert.Equal([existing], memory.LongTermMemory);
    }

    [Fact]
    public void Operations_Create_KeepsDistinctMemoryWithSharedTags()
    {
        var existing = Item(MemoryCategory.Threat, "A healer offered to remove the parasite with poison.");
        existing.Tags = ["parasite", "danger", "healer"];
        var memory = new CampaignMemory { LongTermMemory = [existing] };

        var result = MemoryOperationApplier.Apply(memory, [new MemoryOperation
        {
            Kind = MemoryOperationKind.Create,
            Summary = "The host plans to seek another healer at the grove.",
            Category = MemoryCategory.Threat,
            Tags = ["parasite", "healer", "grove"],
        }], 10);

        Assert.True(result.Changed);
        Assert.Equal(2, memory.LongTermMemory.Count);
    }

    [Fact]
    public void Operations_Create_DeduplicatesItemsCreatedInTheSameBatch()
    {
        var memory = new CampaignMemory();

        var result = MemoryOperationApplier.Apply(memory, [
            new MemoryOperation
            {
                Kind = MemoryOperationKind.Create,
                Summary = "The host repeatedly resists questions about the parasite and leaves to keep it hidden.",
                Category = MemoryCategory.HostAttitude,
                Tags = ["hostIgnorance", "parasiteInvisibility", "departure"],
            },
            new MemoryOperation
            {
                Kind = MemoryOperationKind.Create,
                Summary = "The host resists questions about the parasite and promptly leaves to keep it hidden.",
                Category = MemoryCategory.HostAttitude,
                Tags = ["departure", "parasiteInvisibility", "hostIgnorance"],
            }
        ], 10);

        Assert.True(result.Changed);
        Assert.Single(memory.LongTermMemory);
    }

    [Fact]
    public void Awakening_SelectsNarrowInterestsWithoutDeletingOtherLongTermMemory()
    {
        var survival = Item(MemoryCategory.Survival, "The host avoided a lethal treatment.");
        var romance = Item(MemoryCategory.CharacterRelationship, "Astarion is attracted to the host.", "Astarion");
        var all = new[] { survival, romance };

        var selected = new ParasiteMemoryPhasePolicy().Select(all, "Awakening");

        Assert.Equal([survival], selected);
        Assert.Equal(2, all.Length);
    }

    [Fact]
    public void ActiveSelection_UsesCurrentCharacterAndExcludesUnrelatedMemory()
    {
        var nettie = Item(MemoryCategory.CharacterOpinion, "Nettie's treatment may threaten the parasite.", "Nettie");
        var astarion = Item(MemoryCategory.CharacterOpinion, "Astarion seeks the host's affection.", "Astarion");
        var selector = new ActiveMemorySelector(new ParasiteMemoryPhasePolicy(), 8);

        var active = selector.Select([nettie, astarion], "Established", "Nettie offers treatment.", ["Nettie"]);

        Assert.Contains(nettie, active);
        Assert.DoesNotContain(astarion, active);
    }

    private static ParasiteMemoryItem Item(MemoryCategory category, string summary, string? character = null) => new()
    {
        Id = Guid.NewGuid(),
        Summary = summary,
        Category = category,
        CharacterName = character,
    };
}
