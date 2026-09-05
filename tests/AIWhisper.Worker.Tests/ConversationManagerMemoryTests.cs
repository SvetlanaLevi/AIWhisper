using AIWhisper.Worker.AI;
using AIWhisper.Worker.Configuration;
using AIWhisper.Worker.Conversation;
using AIWhisper.Worker.EventProcessing;
using AIWhisper.Worker.Logging;
using AIWhisper.Worker.Persistence;
using AIWhisper.Worker.Tts;
using Xunit;

namespace AIWhisper.Worker.Tests;

public sealed class ConversationManagerMemoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public ConversationManagerMemoryTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ProcessAsync_WhenAiIsSilent_UpdatesAndPersistsCampaignMemory()
    {
        var campaign = new CampaignContext { CampaignId = "C1", Directory = _directory };
        var store = new CampaignMemoryStore(Path.Combine(_directory, "memory.json"));
        var ai = new FakeAiService
        {
            MemoryUpdate = new CampaignMemoryUpdate
            {
                ImportantEventsToAdd = ["The player promised to help the grove."],
                RelationshipUpdates = [new RelationshipMemoryUpdate
                {
                    Name = "Astarion",
                    Description = "The player defended him.",
                }],
            },
        };
        var manager = new ConversationManager(
            campaign,
            new AIContextBuilder(),
            ai,
            new UnusedTextToSpeech(),
            new NullLog(),
            "system prompt",
            20,
            store,
            new MemoryOptions());
        var dialogue = CreateDialogue();

        await manager.ProcessAsync(dialogue, CancellationToken.None);

        var restoredMemory = await new CampaignMemoryStore(Path.Combine(_directory, "memory.json"))
            .LoadAsync(CancellationToken.None);
        Assert.Equal(1, ai.MemoryUpdateCalls);
        Assert.Contains("The player promised to help the grove.", restoredMemory.ImportantEvents);
        Assert.Equal("The player defended him.", restoredMemory.Relationships["Astarion"]);
    }

    [Fact]
    public async Task ProcessAsync_WhenAiSpeaks_StartsTtsBeforeMemoryUpdateFinishes()
    {
        var campaign = new CampaignContext { CampaignId = "C1", Directory = _directory };
        var memoryStore = new CampaignMemoryStore(Path.Combine(_directory, "memory.json"));
        var ai = new BlockingMemoryAi();
        var tts = new RecordingTextToSpeech();
        var manager = new ConversationManager(
            campaign,
            new AIContextBuilder(),
            ai,
            tts,
            new NullLog(),
            "system prompt",
            20,
            memoryStore,
            new MemoryOptions());

        var processing = manager.ProcessAsync(CreateDialogue(), CancellationToken.None);
        await tts.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(processing.IsCompleted);
        ai.AllowMemoryUpdate.TrySetResult();
        await processing;
    }

    private static DialogueState CreateDialogue()
    {
        const string json = """
        {"schemaVersion":1,"campaignId":"C1","timestamp":"2026-01-01 10:00:00.0000000","source":"client","type":"dialogue.line","data":{"dialogueId":"D1","speaker":"Gale","text":"The grove needs help."}}
        """;
        Assert.True(EventParser.TryParse(json, "C1", out var evt, out var error), error?.Message);
        var dialogue = new DialogueState { CampaignId = "C1", DialogueId = "D1" };
        dialogue.Events.Add(evt!);
        return dialogue;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeAiService : IAIDecisionService
    {
        public CampaignMemoryUpdate MemoryUpdate { get; init; } = new();
        public int MemoryUpdateCalls { get; private set; }

        public Task<AIDecision> DecideAsync(AIRequestContext context, CancellationToken cancellationToken)
            => Task.FromResult(new AIDecision(AIDecisionAction.Silent, null));

        public Task<CampaignMemoryUpdate> UpdateCampaignMemoryAsync(
            CampaignMemory currentMemory,
            string transcript,
            CancellationToken cancellationToken)
        {
            MemoryUpdateCalls++;
            return Task.FromResult(MemoryUpdate);
        }
    }

    private sealed class UnusedTextToSpeech : ITextToSpeech
    {
        public Task<AudioResult> SynthesizeAsync(string text, VoiceContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("TTS must not run when the AI is silent.");
    }

    private sealed class RecordingTextToSpeech : ITextToSpeech
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AudioResult> SynthesizeAsync(string text, VoiceContext context, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return Task.FromResult(new AudioResult("audio.mp3", "mp3"));
        }
    }

    private sealed class BlockingMemoryAi : IAIDecisionService
    {
        public TaskCompletionSource AllowMemoryUpdate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AIDecision> DecideAsync(AIRequestContext context, CancellationToken cancellationToken)
            => Task.FromResult(new AIDecision(AIDecisionAction.Speak, "A comment."));

        public async Task<CampaignMemoryUpdate> UpdateCampaignMemoryAsync(
            CampaignMemory currentMemory,
            string transcript,
            CancellationToken cancellationToken)
        {
            await AllowMemoryUpdate.Task.WaitAsync(cancellationToken);
            return new CampaignMemoryUpdate();
        }
    }

    private sealed class NullLog : IWorkerLog
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
