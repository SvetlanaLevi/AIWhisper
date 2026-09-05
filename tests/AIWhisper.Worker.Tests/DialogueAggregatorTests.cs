using AIWhisper.Worker.EventProcessing;
using AIWhisper.Worker.Logging;
using Xunit;

namespace AIWhisper.Worker.Tests;

file sealed class NullLog : IWorkerLog
{
    public List<string> Warnings { get; } = new();
    public void Debug(string message) { }
    public void Info(string message) { }
    public void Warn(string message) => Warnings.Add(message);
    public void Error(string message, Exception? exception = null) { }
}

public class DialogueAggregatorTests
{
    private static WorkerEvent MakeEvent(string type, string dialogueId, DateTime timestamp, string dataJson = "{}")
    {
        var data = dataJson == "{}" ? "{\"dialogueId\":\"" + dialogueId + "\"}" : dataJson;
        var json = "{\"schemaVersion\":1,\"campaignId\":\"C1\",\"timestamp\":\"" +
            timestamp.ToString("yyyy-MM-dd HH:mm:ss.fffffff") +
            "\",\"source\":\"server\",\"type\":\"" + type + "\",\"data\":" + data + "}";
        var ok = EventParser.TryParse(json, "C1", out var evt, out var error);
        Assert.True(ok, error?.Message);
        return evt!;
    }

    [Fact]
    public async Task SingleDialogue_CompletesAfterEndDelay()
    {
        var log = new NullLog();
        var aggregator = new DialogueAggregator(TimeSpan.FromMilliseconds(80), log);
        var t0 = new DateTime(2026, 1, 1, 10, 0, 0);

        var completed = new List<Worker.EventProcessing.DialogueState>();
        var pump = Task.Run(async () =>
        {
            await foreach (var d in aggregator.Completed.ReadAllAsync()) completed.Add(d);
        });

        aggregator.Handle(MakeEvent("dialogue.start", "D1", t0));
        aggregator.Handle(MakeEvent("dialogue.line", "D1", t0.AddSeconds(1), """{"dialogueId":"D1","speaker":"Gale","text":"hi"}"""));
        aggregator.Handle(MakeEvent("dialogue.end", "D1", t0.AddSeconds(2)));

        await Task.Delay(250);
        aggregator.Complete();
        await pump;

        Assert.Single(completed);
        Assert.Equal("D1", completed[0].DialogueId);
        Assert.Equal(DialogueStatus.Completed, completed[0].Status);
        Assert.Equal(3, completed[0].Events.Count);
    }

    [Fact]
    public async Task TwoConcurrentDialogues_AreTrackedIndependently()
    {
        var log = new NullLog();
        var aggregator = new DialogueAggregator(TimeSpan.FromMilliseconds(80), log);
        var t0 = new DateTime(2026, 1, 1, 10, 0, 0);

        var completedIds = new List<string>();
        var pump = Task.Run(async () =>
        {
            await foreach (var d in aggregator.Completed.ReadAllAsync()) completedIds.Add(d.DialogueId);
        });

        aggregator.Handle(MakeEvent("dialogue.start", "A", t0));
        aggregator.Handle(MakeEvent("dialogue.start", "B", t0.AddSeconds(1)));
        aggregator.Handle(MakeEvent("dialogue.line", "A", t0.AddSeconds(2), """{"dialogueId":"A","speaker":"X","text":"a"}"""));
        aggregator.Handle(MakeEvent("dialogue.line", "B", t0.AddSeconds(2), """{"dialogueId":"B","speaker":"Y","text":"b"}"""));
        aggregator.Handle(MakeEvent("dialogue.end", "A", t0.AddSeconds(3)));
        aggregator.Handle(MakeEvent("dialogue.end", "B", t0.AddSeconds(3)));

        await Task.Delay(250);
        aggregator.Complete();
        await pump;

        Assert.Equal(new[] { "A", "B" }, completedIds.OrderBy(x => x));
    }

    [Fact]
    public async Task LateEventAfterCompletion_IsIgnoredNotReopened()
    {
        var log = new NullLog();
        var aggregator = new DialogueAggregator(TimeSpan.FromMilliseconds(60), log);
        var t0 = new DateTime(2026, 1, 1, 10, 0, 0);

        var completed = new List<Worker.EventProcessing.DialogueState>();
        var pump = Task.Run(async () =>
        {
            await foreach (var d in aggregator.Completed.ReadAllAsync()) completed.Add(d);
        });

        aggregator.Handle(MakeEvent("dialogue.start", "D1", t0));
        aggregator.Handle(MakeEvent("dialogue.end", "D1", t0.AddSeconds(1)));
        await Task.Delay(200);

        aggregator.Handle(MakeEvent("dialogue.line", "D1", t0.AddSeconds(2), """{"dialogueId":"D1","speaker":"X","text":"too late"}"""));
        await Task.Delay(100);

        aggregator.Complete();
        await pump;

        Assert.Single(completed);
        Assert.Contains(log.Warnings, w => w.Contains("late", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ContentWithoutDialogueStart_IsStillAggregated()
    {
        var log = new NullLog();
        var aggregator = new DialogueAggregator(TimeSpan.FromMilliseconds(60), log);
        var t0 = new DateTime(2026, 1, 1, 10, 0, 0);

        var completed = new List<Worker.EventProcessing.DialogueState>();
        var pump = Task.Run(async () =>
        {
            await foreach (var d in aggregator.Completed.ReadAllAsync()) completed.Add(d);
        });

        // No dialogue.start for "D2" - the mod's start event was lost/missed.
        aggregator.Handle(MakeEvent("dialogue.line", "D2", t0, """{"dialogueId":"D2","speaker":"X","text":"hi"}"""));
        aggregator.Handle(MakeEvent("dialogue.end", "D2", t0.AddSeconds(1)));

        await Task.Delay(200);
        aggregator.Complete();
        await pump;

        Assert.Single(completed);
        Assert.Equal("D2", completed[0].DialogueId);
    }

    [Fact]
    public void UnknownEventType_DoesNotThrow()
    {
        var log = new NullLog();
        var aggregator = new DialogueAggregator(TimeSpan.FromMilliseconds(60), log);

        var evt = MakeEvent("some.future.event", "irrelevant", DateTime.UtcNow, """{"foo":"bar"}""");

        var exception = Record.Exception(() => aggregator.Handle(evt));

        Assert.Null(exception);
    }
}
