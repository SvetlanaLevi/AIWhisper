using DialogExtractor.Worker.Persistence;
using Xunit;

namespace DialogExtractor.Worker.Tests;

public class CheckpointStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dx-worker-ckpt-" + Guid.NewGuid());

    public CheckpointStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string StorePath => Path.Combine(_dir, "worker-state.json");

    [Fact]
    public async Task Load_WhenFileMissing_ReturnsEmptyCheckpoint()
    {
        var store = new CheckpointStore(StorePath);

        var checkpoint = await store.LoadAsync(CancellationToken.None);

        Assert.Empty(checkpoint.Files);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsCorrectly()
    {
        var store = new CheckpointStore(StorePath);
        var checkpoint = new WorkerCheckpoint
        {
            CampaignId = "C1",
            Files = { ["server.log"] = new FileCheckpoint { Position = 123, Length = 123 } },
        };

        await store.SaveAsync(checkpoint, CancellationToken.None);
        var reloaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("C1", reloaded.CampaignId);
        Assert.Equal(123, reloaded.Files["server.log"].Position);
    }

    [Fact]
    public async Task CorruptCheckpointFile_RecoversToEmptyInsteadOfThrowing()
    {
        await File.WriteAllTextAsync(StorePath, "{ this is not valid json");
        var store = new CheckpointStore(StorePath);

        var checkpoint = await store.LoadAsync(CancellationToken.None);

        Assert.Empty(checkpoint.Files);
    }
}
