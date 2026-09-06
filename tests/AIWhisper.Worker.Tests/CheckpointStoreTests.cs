using AIWhisper.Worker.Persistence;
using AIWhisper.Worker.Development;
using AIWhisper.Worker.Conversation;
using Xunit;

namespace AIWhisper.Worker.Tests;

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
            Session = new SessionContext { Player = "Lana", Region = "SYS_CC_I" },
            Development = new ParasiteDevelopmentState { CurrentPhase = "Awakening", ReachedInRegion = "WLD_Main_A" },
            LastAppliedSystemInstructions = ["file:ai-system-prompt.txt", "comment-frequency:Low"],
        };

        await store.SaveAsync(checkpoint, CancellationToken.None);
        var reloaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("C1", reloaded.CampaignId);
        Assert.Equal(123, reloaded.Files["server.log"].Position);
        Assert.Equal("Lana", reloaded.Session.Player);
        Assert.Equal("SYS_CC_I", reloaded.Session.Region);
        Assert.Equal("Awakening", reloaded.Development?.CurrentPhase);
        Assert.Equal("WLD_Main_A", reloaded.Development?.ReachedInRegion);
        Assert.Equal(["file:ai-system-prompt.txt", "comment-frequency:Low"], reloaded.LastAppliedSystemInstructions);
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
