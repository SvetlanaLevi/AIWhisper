using System.Text.Json;
using AIWhisper.Worker.Conversation;
using AIWhisper.Worker.EventProcessing;
using AIWhisper.Worker.Logging;
using AIWhisper.Worker.Persistence;
using Xunit;

namespace AIWhisper.Worker.Tests;

public sealed class MemoryEventHandlerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly CampaignContext _campaign;
    private readonly CampaignMemoryStore _working;
    private readonly TestLog _log = new();
    private readonly MemoryEventHandler _handler;

    public MemoryEventHandlerTests()
    {
        _campaign = new CampaignContext { CampaignId = "campaign", Directory = _directory };
        _working = new CampaignMemoryStore(Path.Combine(_directory, "memory.json"));
        _handler = new MemoryEventHandler(_campaign, _working, _log);
    }

    [Fact]
    public async Task SaveLoadAndBranch_LeavesOriginalSnapshotUnchanged()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        _campaign.Memory.Summary = "First save";
        _campaign.Memory.ImportantEvents.Add("Met Gale");
        _campaign.Development.CurrentPhase = "first";
        _campaign.Development.DeliveredOneShots.Add("intro-first");
        _campaign.Session.Region = "grove";
        await Send("save.start", new { snapshotId = first });
        var original = await File.ReadAllTextAsync(SnapshotPath(first));
        _campaign.Memory.Summary = "Later";
        _campaign.Development.CurrentPhase = "third";
        _campaign.Development.DeliveredOneShots.Add("intro-third");
        _campaign.Session.Region = "city";
        _campaign.History.Add(new("old", null, null, null, "future", "silent", null));
        await Send("memory.load", new { snapshotId = first });
        Assert.Equal("First save", _campaign.Memory.Summary);
        Assert.Equal("first", _campaign.Development.CurrentPhase);
        Assert.Equal(new[] { "intro-first" }, _campaign.Development.DeliveredOneShots);
        Assert.Equal("grove", _campaign.Session.Region);
        Assert.Empty(_campaign.History);
        Assert.Equal(new[] { "Met Gale" }, _campaign.Memory.ImportantEvents);
        _campaign.Memory.Summary = "New branch";
        await _working.SaveAsync(_campaign.Memory, default);
        Assert.Equal(original, await File.ReadAllTextAsync(SnapshotPath(first)));
        Assert.False(File.Exists(SnapshotPath(second)));
        await Send("save.start", new { snapshotId = second });
        Assert.Equal("New branch", (await new CampaignSnapshotStore(_directory).LoadAsync(second, default))!.Memory.Summary);
        await Send("save.start", new { snapshotId = first });
        Assert.Equal(original, await File.ReadAllTextAsync(SnapshotPath(first)));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"snapshotId\":null}")]
    public async Task LoadWithoutSnapshot_ResetsAndPersistsEmptyWorkingMemory(string data)
    {
        _campaign.Memory.Summary = "Old timeline";
        _campaign.Memory.Relationships["Gale"] = "Ally";
        await SendJson("memory.load", data);
        Assert.Empty(_campaign.Memory.Summary);
        Assert.Empty(_campaign.Memory.Relationships);
        Assert.Empty((await _working.LoadAsync(default)).Summary);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task MissingSnapshot_WarnsAndResetsWithoutCreatingSnapshot()
    {
        var id = Guid.NewGuid();
        _campaign.Memory.Summary = "Old timeline";
        await Send("memory.load", new { snapshotId = id });
        Assert.Empty(_campaign.Memory.Summary);
        Assert.Single(_log.Warnings);
        Assert.Contains(id.ToString(), _log.Warnings[0]);
        Assert.False(File.Exists(SnapshotPath(id)));
    }

    [Theory]
    [InlineData("save.start", "{}")]
    [InlineData("memory.load", "{\"snapshotId\":false}")]
    [InlineData("memory.load", "{\"snapshotId\":\"invalid\"}")]
    public async Task InvalidData_WarnsWithoutChangingMemory(string type, string data)
    {
        _campaign.Memory.Summary = "Keep";
        await SendJson(type, data);
        Assert.Equal("Keep", _campaign.Memory.Summary);
        Assert.Single(_log.Warnings);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task SaveEnd_IsIgnored()
    {
        await Send("save.end", new { snapshotId = Guid.NewGuid() });
        Assert.False(Directory.Exists(_directory));
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public async Task Load_WaitsForInFlightMemoryUpdate()
    {
        await _campaign.ProcessingGate.WaitAsync();
        var load = SendJson("memory.load", "{}");
        Assert.True(_campaign.DialogueCancellation.IsCancellationRequested);
        Assert.False(load.IsCompleted);
        _campaign.Memory.Summary = "In-flight update";
        _campaign.ProcessingGate.Release();
        await load;
        Assert.Empty(_campaign.Memory.Summary);
    }

    [Fact]
    public async Task CorruptSnapshot_ResetsBothStatesAndPreservesFile()
    {
        var id = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath(id))!);
        await File.WriteAllTextAsync(SnapshotPath(id), "{broken");
        _campaign.Development.CurrentPhase = "third";
        await Send("memory.load", new { snapshotId = id });
        Assert.Empty(_campaign.Memory.Summary);
        Assert.Empty(_campaign.Development.CurrentPhase);
        Assert.Single(_log.Warnings);
        Assert.Equal("{broken", await File.ReadAllTextAsync(SnapshotPath(id)));
    }

    private string SnapshotPath(Guid id) => new CampaignSnapshotStore(_directory).GetPath(id);
    private Task Send(string type, object data) => SendJson(type, JsonSerializer.Serialize(data));
    private async Task SendJson(string type, string data)
    {
        var raw = "{\"campaignId\":\"campaign\",\"source\":\"server\",\"timestamp\":\"2026-09-07 18:27:06\",\"type\":\"" + type + "\",\"data\":" + data + "}";
        Assert.True(EventParser.TryParse(raw, "campaign", out var evt, out _));
        Assert.True(await _handler.HandleAsync(evt!, default));
    }

    public void Dispose()
    {
        _campaign.ProcessingGate.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class TestLog : IWorkerLog
    {
        public List<string> Warnings { get; } = [];
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message, Exception? exception = null) { }
    }
}

