using AIWhisper.Worker.FileMonitoring;
using Xunit;

namespace AIWhisper.Worker.Tests;

public class LogFileReaderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "dx-worker-tests-" + Guid.NewGuid() + ".log");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static async Task<List<string>> DrainAsync(LogFileReader reader)
    {
        var lines = new List<string>();
        while (reader.Lines.TryRead(out var line)) lines.Add(line);
        return await Task.FromResult(lines);
    }

    [Fact]
    public async Task NonExistentFile_ProducesNoLines()
    {
        var reader = new LogFileReader(_path);

        await reader.PollOnceAsync(CancellationToken.None);

        Assert.Empty(await DrainAsync(reader));
    }

    [Fact]
    public async Task PartialTrailingLine_IsWithheldUntilNewline()
    {
        await File.WriteAllTextAsync(_path, "{\"a\":1}\n{\"a\":2");
        var reader = new LogFileReader(_path);

        await reader.PollOnceAsync(CancellationToken.None);
        var lines = await DrainAsync(reader);

        Assert.Single(lines);
        Assert.Equal("{\"a\":1}", lines[0]);
    }

    [Fact]
    public async Task AppendingMultipleLines_EmitsEachNewLineExactlyOnce()
    {
        await File.WriteAllTextAsync(_path, "{\"a\":1}\n{\"a\":2");
        var reader = new LogFileReader(_path);
        await reader.PollOnceAsync(CancellationToken.None);
        await DrainAsync(reader); // discard the first line

        await File.AppendAllTextAsync(_path, "}\n{\"a\":3}\n");
        await reader.PollOnceAsync(CancellationToken.None);
        var lines = await DrainAsync(reader);

        Assert.Equal(new[] { "{\"a\":2}", "{\"a\":3}" }, lines);
    }

    [Fact]
    public async Task RestartFromCheckpoint_OnlySeesContentWrittenAfterIt()
    {
        await File.WriteAllTextAsync(_path, "{\"a\":1}\n{\"a\":2}\n");
        var original = new LogFileReader(_path);
        await original.PollOnceAsync(CancellationToken.None);
        await DrainAsync(original);
        var checkpoint = original.CurrentCheckpoint();

        await File.AppendAllTextAsync(_path, "{\"a\":3}\n");

        var afterRestart = new LogFileReader(_path);
        afterRestart.RestoreCheckpoint(checkpoint);
        await afterRestart.PollOnceAsync(CancellationToken.None);
        var lines = await DrainAsync(afterRestart);

        Assert.Equal(new[] { "{\"a\":3}" }, lines);
    }

    [Fact]
    public async Task FileShrinking_IsTreatedAsReplacementAndReadFromScratch()
    {
        await File.WriteAllTextAsync(_path, "{\"a\":1}\n{\"a\":2}\n{\"a\":3}\n");
        var reader = new LogFileReader(_path);
        await reader.PollOnceAsync(CancellationToken.None);
        await DrainAsync(reader);

        File.Delete(_path);
        await File.WriteAllTextAsync(_path, "{\"a\":\"fresh\"}\n");

        await reader.PollOnceAsync(CancellationToken.None);
        var lines = await DrainAsync(reader);

        Assert.Equal(new[] { "{\"a\":\"fresh\"}" }, lines);
    }
}
