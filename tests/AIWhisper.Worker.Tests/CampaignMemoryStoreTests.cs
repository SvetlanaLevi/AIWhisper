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
            ImportantEvents = ["Defeated the goblin leaders."],
            Relationships = { ["Gale"] = "A cautious but growing trust." },
            PlayerTraits = ["Often helps strangers."],
            RunningJokes = ["Keeps collecting useless spoons."],
        };

        await new CampaignMemoryStore(path).SaveAsync(original, CancellationToken.None);
        var loaded = await new CampaignMemoryStore(path).LoadAsync(CancellationToken.None);

        Assert.Equal(original.Summary, loaded.Summary);
        Assert.Equal(original.ImportantEvents, loaded.ImportantEvents);
        Assert.Equal(original.Relationships, loaded.Relationships);
        Assert.Equal(original.PlayerTraits, loaded.PlayerTraits);
        Assert.Equal(original.RunningJokes, loaded.RunningJokes);
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

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
