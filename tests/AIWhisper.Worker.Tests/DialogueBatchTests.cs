using AIWhisper.Worker.AI;
using AIWhisper.Worker.Configuration;
using AIWhisper.Worker.Conversation;
using AIWhisper.Worker.EventProcessing;
using AIWhisper.Worker.Logging;
using AIWhisper.Worker.Persistence;
using AIWhisper.Worker.Tts;
using Xunit;

namespace AIWhisper.Worker.Tests;

public sealed class DialogueBatchTests
{
    private readonly ManualClock _clock = new();
    private readonly RecordingAi _ai = new();

    private DialogueAggregator CreateAggregator() =>
        new(TimeSpan.FromMilliseconds(500), new NullLog(), timeProvider: _clock);

    private void Send(DialogueAggregator aggregator, string type, string id, string text = "")
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            campaignId = "C1", source = "server", type,
            timestamp = new DateTime(2026, 1, 1).AddTicks(_clock.GetTimestamp()).ToString("yyyy-MM-dd HH:mm:ss.fffffff"),
            data = new { dialogueId = id, speaker = "Gale", text },
        });
        Assert.True(EventParser.TryParse(json, "C1", out var evt, out var error), error?.Message);
        aggregator.Handle(evt!);
    }

    [Fact]
    public async Task StartsExtendFromArrival_WholeChainMakesOneDecisionRequest()
    {
        using var aggregator = CreateAggregator();
        Send(aggregator, "dialogue.start", "A");
        Send(aggregator, "dialogue.line", "A", "First line");
        Send(aggregator, "dialogue.end", "A");
        _clock.Advance(350);
        Send(aggregator, "dialogue.start", "B");
        Send(aggregator, "dialogue.line", "B", "Second line");
        _clock.Advance(200); // Past the original deadline.
        Assert.False(aggregator.Completed.TryRead(out _));
        Send(aggregator, "dialogue.start", "C");
        _clock.Advance(450);
        Send(aggregator, "dialogue.choice", "C", "Third choice");
        Send(aggregator, "dialogue.end", "B");
        Send(aggregator, "dialogue.end", "C");
        _clock.Advance(49);
        Assert.False(aggregator.Completed.TryRead(out _));
        _clock.Advance(1);
        Assert.True(aggregator.Completed.TryRead(out var batch));
        Assert.Equal(new[] { "A", "B", "C" }, batch!.Events.Select(e => e.DialogueId).Distinct());
        var campaign = new CampaignContext { CampaignId = "C1", Directory = Path.GetTempPath() };
        var manager = new ConversationManager(campaign, new AIContextBuilder(), _ai, new UnusedTts(),
            new NullLog(), "system", 20, new CampaignMemoryStore(Path.Combine(Path.GetTempPath(),
                Guid.NewGuid() + ".json")), new MemoryOptions());
        await manager.ProcessAsync(batch, default);
        Assert.Equal(1, _ai.DecisionCalls);
        Assert.Contains("First line", _ai.Prompt);
        Assert.Contains("Second line", _ai.Prompt);
        Assert.Contains("Third choice", _ai.Prompt);
        _clock.Advance(2000);
        Assert.False(aggregator.Completed.TryRead(out _));
    }

    [Fact]
    public void ContentSpeakersAndRepeatedEnd_DoNotExtendInitialWindow()
    {
        using var aggregator = CreateAggregator();
        Send(aggregator, "dialogue.end", "A");
        _clock.Advance(450);
        Send(aggregator, "dialogue.line", "A", "Late line");
        Send(aggregator, "dialogue.choice", "A", "Late choice");
        Send(aggregator, "dialogue.speakers", "A");
        Send(aggregator, "dialogue.end", "A");
        _clock.Advance(50);
        Assert.True(aggregator.Completed.TryRead(out var batch));
        Assert.Equal(5, batch!.Events.Count);
    }

    [Fact]
    public void ContentFromAnotherActiveDialogue_JoinsBatchWithoutExtendingWindow()
    {
        using var aggregator = CreateAggregator();
        Send(aggregator, "dialogue.start", "B");
        Send(aggregator, "dialogue.end", "A");
        _clock.Advance(450);
        Send(aggregator, "dialogue.line", "B", "Concurrent line");
        _clock.Advance(50);
        Assert.True(aggregator.Completed.TryRead(out var batch));
        Assert.Contains("Concurrent line", new AIContextBuilder().BuildTranscript(batch!));
    }

    [Fact]
    public void LongChain_HasNoHardLimit()
    {
        using var aggregator = CreateAggregator();
        Send(aggregator, "dialogue.end", "A");
        for (var i = 0; i < 100; i++)
        {
            _clock.Advance(499);
            Assert.False(aggregator.Completed.TryRead(out _));
            Send(aggregator, "dialogue.start", $"D{i}");
        }
        _clock.Advance(500);
        Assert.True(aggregator.Completed.TryRead(out var batch));
        Assert.Equal(101, batch!.Events.Count);
        Assert.False(aggregator.Completed.TryRead(out _));
    }

    [Fact]
    public void OngoingDialogue_RemainsOpen_AndDoesNotRepeatSentEvents()
    {
        using var aggregator = CreateAggregator();
        Send(aggregator, "dialogue.end", "A");
        _clock.Advance(350);
        Send(aggregator, "dialogue.start", "B");
        Send(aggregator, "dialogue.line", "B", "Before snapshot");
        _clock.Advance(500);
        Assert.True(aggregator.Completed.TryRead(out var first));
        Send(aggregator, "dialogue.line", "B", "After snapshot");
        Send(aggregator, "dialogue.end", "B");
        _clock.Advance(500);
        Assert.True(aggregator.Completed.TryRead(out var second));
        var builder = new AIContextBuilder();
        Assert.Contains("Before snapshot", builder.BuildTranscript(first!));
        Assert.DoesNotContain("After snapshot", builder.BuildTranscript(first!));
        Assert.Equal("Gale: After snapshot" + Environment.NewLine, builder.BuildTranscript(second!));
    }

    [Fact]
    public void ResetDuringExtendedWindow_DropsOldBatchAndDeadline()
    {
        using var aggregator = CreateAggregator();
        Send(aggregator, "dialogue.end", "A");
        _clock.Advance(350);
        Send(aggregator, "dialogue.start", "B");
        aggregator.Reset(1);
        _clock.Advance(300);
        Send(aggregator, "dialogue.end", "A");
        _clock.Advance(200); // Old extended deadline.
        Assert.False(aggregator.Completed.TryRead(out _));
        _clock.Advance(300);
        Assert.True(aggregator.Completed.TryRead(out var batch));
        Assert.Equal(1, batch!.Generation);
        Assert.Single(batch.Events);
    }

    [Fact]
    public void CompleteDuringWindow_CancelsPendingBatch()
    {
        using var aggregator = CreateAggregator();
        Send(aggregator, "dialogue.end", "A");
        aggregator.Complete();
        _clock.Advance(500);
        Assert.False(aggregator.Completed.TryRead(out _));
        Assert.True(aggregator.Completed.Completion.IsCompletedSuccessfully);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        private ManualTimer? _timer;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _timer = new ManualTimer(this, callback, state);
            _timer.Change(dueTime, period);
            return _timer;
        }
        public void Advance(int milliseconds)
        {
            _ticks += TimeSpan.FromMilliseconds(milliseconds).Ticks;
            _timer?.FireIfDue();
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private long _due = long.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks;
                return true;
            }
            public void FireIfDue()
            {
                if (clock._ticks < _due) return;
                _due = long.MaxValue;
                callback(state);
            }
            public void Dispose() => _due = long.MaxValue;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class RecordingAi : IAIDecisionService
    {
        public int DecisionCalls { get; private set; }
        public string Prompt { get; private set; } = "";
        public Task<AIDecision> DecideAsync(AIRequestContext context, CancellationToken cancellationToken)
        {
            DecisionCalls++;
            Prompt = context.UserPrompt;
            return Task.FromResult(new AIDecision(AIDecisionAction.Silent, null));
        }
        public Task<CampaignMemoryUpdate> UpdateCampaignMemoryAsync(string campaignId, CampaignMemory currentMemory,
            string transcript, string dialogueId, CancellationToken cancellationToken) => Task.FromResult(new CampaignMemoryUpdate());
    }
    private sealed class UnusedTts : ITextToSpeech
    {
        public Task<AudioResult> SynthesizeAsync(string text, VoiceContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Silent decision must not invoke TTS.");
    }
    private sealed class NullLog : IWorkerLog
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
