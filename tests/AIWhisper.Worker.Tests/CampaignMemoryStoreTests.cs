using AIWhisper.Worker.Conversation;
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

        Assert.Empty(memory.Summary);
        Assert.Empty(memory.ImportantEvents);
        Assert.Empty(memory.Relationships);
    }

    [Fact]
    public async Task SaveThenLoad_PreservesMemoryAcrossStoreInstances()
    {
        var path = Path.Combine(_directory, "memory.json");
        var original = new CampaignMemory
        {
            Summary = "The player is travelling with Gale.",
            CurrentSituation = "The party is looking for a healer.",
            ImportantEvents = ["Defeated the goblin leaders."],
            Relationships = { ["Gale"] = "A cautious but growing trust." },
            PlayerTraits = ["Often helps strangers."],
            RunningJokes = ["Keeps collecting useless spoons."],
            ImportantEventSources = { ["Defeated the goblin leaders."] = ["D42"] },
        };

        await new CampaignMemoryStore(path).SaveAsync(original, CancellationToken.None);
        var loaded = await new CampaignMemoryStore(path).LoadAsync(CancellationToken.None);

        Assert.Equal(original.Summary, loaded.Summary);
        Assert.Equal(original.CurrentSituation, loaded.CurrentSituation);
        Assert.Equal(original.ImportantEvents, loaded.ImportantEvents);
        Assert.Equal(original.Relationships, loaded.Relationships);
        Assert.Equal(original.PlayerTraits, loaded.PlayerTraits);
        Assert.Equal(original.RunningJokes, loaded.RunningJokes);
        Assert.Equal(["D42"], loaded.ImportantEventSources["Defeated the goblin leaders."]);
    }

    [Fact]
    public async Task Load_WhenJsonIsCorrupt_ReturnsEmptyWithoutDeletingFile()
    {
        var path = Path.Combine(_directory, "memory.json");
        await File.WriteAllTextAsync(path, "{ not valid json");

        var memory = await new CampaignMemoryStore(path).LoadAsync(CancellationToken.None);

        Assert.Empty(memory.Summary);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Load_LegacyMemoryWithoutProvenance_MigratesWithEmptyMetadata()
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

        Assert.Equal("Legacy save", memory.Summary);
        Assert.Equal(["Met Gale"], memory.ImportantEvents);
        Assert.Empty(memory.ImportantEventSources);
        Assert.Empty(memory.PlayerTraitCandidates);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
