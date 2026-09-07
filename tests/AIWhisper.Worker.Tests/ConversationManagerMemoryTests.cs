using AIWhisper.Worker.AI;
using AIWhisper.Worker.Configuration;
using AIWhisper.Worker.Conversation;
using AIWhisper.Worker.EventProcessing;
using AIWhisper.Worker.Logging;
using AIWhisper.Worker.Memory;
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
        var ai = new FakeAiService(AIDecisionAction.Silent);
        var memoryEvaluator = new FakeMemoryEvaluator
        {
            Result = new MemoryEvaluationResult
            {
                Operations = [new MemoryOperation
                {
                    Kind = MemoryOperationKind.Create,
                    Summary = "The host rejected treatment that could endanger the parasite.",
                    Category = MemoryCategory.RemovalThreat,
                    Tags = ["treatment", "parasite"],
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
            new MemoryOptions(),
            memoryEvaluator: memoryEvaluator);
        var dialogue = CreateDialogue();

        await manager.ProcessAsync(dialogue, CancellationToken.None);

        var restoredMemory = await new CampaignMemoryStore(Path.Combine(_directory, "memory.json"))
            .LoadAsync(CancellationToken.None);
        Assert.Equal(1, memoryEvaluator.Calls);
        Assert.Contains("Gale: The grove needs help.", memoryEvaluator.LastRequest!.Transcript);
        Assert.Contains("Player chose: We should help.", memoryEvaluator.LastRequest.Transcript);
        Assert.DoesNotContain("The host rejected treatment", ai.LastContext!.UserPrompt);
        Assert.Equal("silent", campaign.History.Single().AiAction);
        Assert.Equal("The host rejected treatment that could endanger the parasite.",
            restoredMemory.LongTermMemory.Single().Summary);
    }

    [Fact]
    public async Task ProcessAsync_WhenAiSpeaks_StartsTtsBeforeMemoryUpdateFinishes()
    {
        var campaign = new CampaignContext { CampaignId = "C1", Directory = _directory };
        var memoryStore = new CampaignMemoryStore(Path.Combine(_directory, "memory.json"));
        var ai = new FakeAiService(AIDecisionAction.Speak);
        var memoryEvaluator = new BlockingMemoryEvaluator();
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
            new MemoryOptions(),
            memoryEvaluator: memoryEvaluator);

        var processing = manager.ProcessAsync(CreateDialogue(), CancellationToken.None);
        await tts.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(processing.IsCompleted);
        memoryEvaluator.AllowEvaluation.TrySetResult();
        await processing;
        Assert.Empty(campaign.Memory.LongTermMemory);
        Assert.Equal("speak", campaign.History.Single().AiAction);
    }

    [Fact]
    public async Task MemoryFailure_DoesNotBreakReaction()
    {
        var campaign = new CampaignContext { CampaignId = "C1", Directory = _directory };
        var log = new RecordingLog();
        var manager = new ConversationManager(
            campaign,
            new AIContextBuilder(),
            new FakeAiService(AIDecisionAction.Silent),
            new UnusedTextToSpeech(),
            log,
            "system",
            20,
            new CampaignMemoryStore(Path.Combine(_directory, "memory.json")),
            new MemoryOptions(),
            memoryEvaluator: new ThrowingMemoryEvaluator());

        await manager.ProcessAsync(CreateDialogue(), default);

        Assert.Equal("silent", campaign.History.Single().AiAction);
        Assert.Single(log.Errors);
        Assert.Empty(campaign.Memory.LongTermMemory);
    }

    private static DialogueState CreateDialogue()
    {
        return CreateDialogueForGeneration(0);
    }

    [Fact]
    public async Task Load_CancelsOldWorkAndSkipsQueuedOldDialogue()
    {
        var campaign = new CampaignContext { CampaignId = "C1", Directory = _directory };
        var store = new CampaignMemoryStore(Path.Combine(_directory, "memory.json"));
        var ai = new FakeAiService(AIDecisionAction.Speak);
        var memoryEvaluator = new BlockingMemoryEvaluator();
        var tts = new RecordingTextToSpeech();
        var manager = new ConversationManager(campaign, new AIContextBuilder(), ai, tts,
            new NullLog(), "system", 20, store, new MemoryOptions(), memoryEvaluator: memoryEvaluator);
        var processing = manager.ProcessAsync(CreateDialogue(), default);
        await tts.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(EventParser.TryParse("""
            {"campaignId":"C1","source":"server","type":"memory.load","timestamp":"2026-09-07 12:00:00","data":{}}
            """, "C1", out var evt, out _));
        await new MemoryEventHandler(campaign, store, new NullLog()).HandleAsync(evt!, default)
            .WaitAsync(TimeSpan.FromSeconds(2));
        await processing;
        await manager.ProcessAsync(CreateDialogue(), default);
        Assert.Empty(campaign.History);
        Assert.Empty(campaign.Memory.LongTermMemory);
        Assert.Equal(1, campaign.Generation);
        memoryEvaluator.AllowEvaluation.TrySetResult();
        await manager.ProcessAsync(CreateDialogueForGeneration(1), default);
        Assert.Single(campaign.History);
    }

    private static DialogueState CreateDialogueForGeneration(long generation)
    {
        const string json = """
        {"schemaVersion":1,"campaignId":"C1","timestamp":"2026-01-01 10:00:00.0000000","source":"client","type":"dialogue.line","data":{"dialogueId":"D1","speaker":"Gale","text":"The grove needs help."}}
        """;
        Assert.True(EventParser.TryParse(json, "C1", out var evt, out var error), error?.Message);
        var dialogue = new DialogueState { CampaignId = "C1", DialogueId = "D1", Generation = generation };
        dialogue.Events.Add(evt!);
        const string choiceJson = """
        {"schemaVersion":1,"campaignId":"C1","timestamp":"2026-01-01 10:00:01.0000000","source":"client","type":"dialogue.choice","data":{"dialogueId":"D1","speaker":"Gale","text":"We should help."}}
        """;
        Assert.True(EventParser.TryParse(choiceJson, "C1", out var choice, out error), error?.Message);
        dialogue.Events.Add(choice!);
        return dialogue;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeAiService(AIDecisionAction action) : IAIDecisionService
    {
        public AIRequestContext? LastContext { get; private set; }

        public Task<AIDecision> DecideAsync(AIRequestContext context, CancellationToken cancellationToken)
        {
            LastContext = context;
            return Task.FromResult(new AIDecision(action, action == AIDecisionAction.Speak ? "A comment." : null));
        }
    }

    private sealed class FakeMemoryEvaluator : IMemoryEvaluator
    {
        public MemoryEvaluationResult Result { get; init; } = new();
        public int Calls { get; private set; }
        public MemoryEvaluationRequest? LastRequest { get; private set; }

        public Task<MemoryEvaluationResult> EvaluateAsync(
            MemoryEvaluationRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(Result);
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

    private sealed class BlockingMemoryEvaluator : IMemoryEvaluator
    {
        public TaskCompletionSource AllowEvaluation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<MemoryEvaluationResult> EvaluateAsync(
            MemoryEvaluationRequest request,
            CancellationToken cancellationToken)
        {
            await AllowEvaluation.Task.WaitAsync(cancellationToken);
            return new MemoryEvaluationResult();
        }
    }

    private sealed class ThrowingMemoryEvaluator : IMemoryEvaluator
    {
        public Task<MemoryEvaluationResult> EvaluateAsync(
            MemoryEvaluationRequest request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("network unavailable");
    }

    private sealed class RecordingLog : IWorkerLog
    {
        public List<string> Errors { get; } = [];
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) => Errors.Add(message);
    }

    private sealed class NullLog : IWorkerLog
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
