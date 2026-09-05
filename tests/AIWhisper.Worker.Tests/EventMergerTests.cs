using AIWhisper.Worker.EventProcessing;
using Xunit;

namespace AIWhisper.Worker.Tests;

public class EventMergerTests
{
    private static WorkerEvent MakeEvent(string type, DateTime timestamp, string source = "server")
    {
        var json = "{\"schemaVersion\":1,\"campaignId\":\"C1\",\"timestamp\":\"" +
            timestamp.ToString("yyyy-MM-dd HH:mm:ss.fffffff") +
            "\",\"source\":\"" + source + "\",\"type\":\"" + type + "\",\"data\":{}}";
        var ok = EventParser.TryParse(json, "C1", out var evt, out var error);
        Assert.True(ok, error?.Message);
        return evt!;
    }

    [Fact]
    public async Task EventsPublishedOutOfOrder_AreEmittedInTimestampOrder()
    {
        var merger = new EventMerger(TimeSpan.FromMilliseconds(60));
        var t0 = new DateTime(2026, 1, 1, 10, 0, 0);

        // Publish "end" first even though its timestamp is latest, then the
        // rest out of logical order - simulating server/client notifications
        // arriving in whatever order the filesystem happens to deliver them.
        merger.Publish(MakeEvent("dialogue.end", t0.AddSeconds(3)));
        merger.Publish(MakeEvent("dialogue.start", t0));
        merger.Publish(MakeEvent("dialogue.line", t0.AddSeconds(2), "client"));
        merger.Publish(MakeEvent("dialogue.choice", t0.AddSeconds(1), "client"));

        var received = new List<string>();
        var readTask = Task.Run(async () =>
        {
            await foreach (var evt in merger.Output.ReadAllAsync())
            {
                received.Add(evt.Type);
            }
        });

        await Task.Delay(300);
        merger.Complete();
        await readTask;

        Assert.Equal(new[] { "dialogue.start", "dialogue.choice", "dialogue.line", "dialogue.end" }, received);
    }

    [Fact]
    public async Task Publish_DoesNotEmitImmediately_RespectsOrderingDelay()
    {
        var merger = new EventMerger(TimeSpan.FromMilliseconds(200));
        merger.Publish(MakeEvent("session.start", DateTime.UtcNow));

        // Immediately after publishing, nothing should be ready yet.
        var readImmediately = merger.Output.TryRead(out _);
        Assert.False(readImmediately);

        await Task.Delay(350);
        var readAfterDelay = merger.Output.TryRead(out var evt);
        Assert.True(readAfterDelay);
        Assert.Equal("session.start", evt!.Type);
    }
}
