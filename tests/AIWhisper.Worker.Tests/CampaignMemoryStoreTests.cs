using AIWhisper.Worker.Conversation;
using AIWhisper.Worker.Memory;
using AIWhisper.Worker.Persistence;
using Xunit;

namespace AIWhisper.Worker.Tests;

public sealed class CampaignMemoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public CampaignMemoryStoreTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Load_WhenFileMissing_ReturnsEmptyMemory()
    {
        var memory = await new CampaignMemoryStore(Path.Combine(_directory, "memory.json"))
            .LoadAsync(CancellationToken.None);

        Assert.Empty(memory.LongTermMemory);
        Assert.Empty(memory.CharacterKnowledge);
    }

    [Fact]
    public async Task SaveThenLoad_PreservesMemoryAcrossStoreInstances()
    {
        var path = Path.Combine(_directory, "memory.json");
        var original = new CampaignMemory
        {
            LongTermMemory = [new ParasiteMemoryItem
            {
                Id = Guid.NewGuid(),
                Summary = "The host distrusts Gale's proposed cure.",
                Category = MemoryCategory.RemovalThreat,
                CharacterName = "Gale",
                Tags = ["cure", "tadpole"],
            }],
            CharacterKnowledge = [new DiscoveredCharacterKnowledge
            {
                CharacterName = "Ketheric Thorm",
                KnownFacts = ["He survived a fatal wound."],
            }],
        };

        await new CampaignMemoryStore(path).SaveAsync(original, CancellationToken.None);
        var loaded = await new CampaignMemoryStore(path).LoadAsync(CancellationToken.None);

        var item = Assert.Single(loaded.LongTermMemory);
        Assert.Equal(original.LongTermMemory[0].Id, item.Id);
        Assert.Equal(original.LongTermMemory[0].Summary, item.Summary);
        Assert.Equal(MemoryCategory.RemovalThreat, item.Category);
        Assert.Equal("Gale", item.CharacterName);
        Assert.Equal(["cure", "tadpole"], item.Tags);
        var character = Assert.Single(loaded.CharacterKnowledge);
        Assert.Equal("Ketheric Thorm", character.CharacterName);
        Assert.Equal(["He survived a fatal wound."], character.KnownFacts);
    }

    [Fact]
    public async Task Load_WhenJsonIsCorrupt_ReturnsEmptyWithoutDeletingFile()
    {
        var path = Path.Combine(_directory, "memory.json");
        await File.WriteAllTextAsync(path, "{ not valid json");

        var memory = await new CampaignMemoryStore(path).LoadAsync(CancellationToken.None);

        Assert.Empty(memory.LongTermMemory);
        Assert.Empty(memory.CharacterKnowledge);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Load_LegacyCampaignSummary_IsAcceptedAsEmptySubjectiveMemory()
    {
        var path = Path.Combine(_directory, "memory.json");
        await File.WriteAllTextAsync(path, """
            {
              "Summary": "Legacy save",
              "ImportantEvents": ["Met Gale"],
              "Relationships": { "Gale": "Ally" },
              "PlayerTraits": ["Curious"],
              "RunningJokes": []
            }
            """);

        var memory = await new CampaignMemoryStore(path).LoadAsync(CancellationToken.None);

        Assert.Empty(memory.LongTermMemory);
        Assert.Empty(memory.CharacterKnowledge);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
