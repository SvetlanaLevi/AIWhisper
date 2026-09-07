using AIWhisper.Worker.AI;
using AIWhisper.Worker.Configuration;
using AIWhisper.Worker.Conversation;
using AIWhisper.Worker.Development;
using AIWhisper.Worker.EventProcessing;
using AIWhisper.Worker.Logging;
using AIWhisper.Worker.Persistence;
using AIWhisper.Worker.Tts;
using Xunit;

namespace AIWhisper.Worker.Tests;

public sealed class ConversationManagerPhaseIntroductionTests : IDisposable
{
    private const string IntroductionId = "awakening-intro";
    private const string IntroductionText =
        "[confused] Something is wrong. [long pause] The change should have begun by now.";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public ConversationManagerPhaseIntroductionTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task PendingIntroduction_BypassesDecisionPersistsImmediatelyAndDoesNotRepeat()
    {
        var campaign = CreateCampaign("Awakening");
        var ai = new RecordingAiService();
        var tts = new RecordingTextToSpeech();
        var checkpointSaves = 0;
        var manager = CreateManager(campaign, ai, tts, _ =>
        {
            checkpointSaves++;
            return Task.CompletedTask;
        });

        await manager.ProcessAsync(CreateDialogue("D1"), CancellationToken.None);

        Assert.Equal(0, ai.DecisionCalls);
        Assert.Equal(1, ai.MemoryUpdateCalls);
        Assert.Equal([IntroductionText], tts.Texts);
        Assert.Contains(IntroductionId, campaign.Development.DeliveredOneShots);
        Assert.Equal(1, checkpointSaves);
        Assert.Equal(IntroductionText, campaign.History.Single().AiText);

        await manager.ProcessAsync(CreateDialogue("D2"), CancellationToken.None);

        Assert.Equal(1, ai.DecisionCalls);
        Assert.Single(tts.Texts);
        Assert.Equal(1, checkpointSaves);
    }

    [Fact]
    public async Task FailedIntroductionTts_IsNotMarkedAndRetriesOnNextDialogue()
    {
        var campaign = CreateCampaign("Awakening");
        var ai = new RecordingAiService();
        var tts = new FailOnceTextToSpeech();
        var checkpointSaves = 0;
        var manager = CreateManager(campaign, ai, tts, _ =>
        {
            checkpointSaves++;
            return Task.CompletedTask;
        });

        await manager.ProcessAsync(CreateDialogue("D1"), CancellationToken.None);

        Assert.DoesNotContain(IntroductionId, campaign.Development.DeliveredOneShots);
        Assert.Equal(0, checkpointSaves);
        Assert.Empty(campaign.History);
        Assert.Equal(1, ai.MemoryUpdateCalls);

        await manager.ProcessAsync(CreateDialogue("D2"), CancellationToken.None);

        Assert.Contains(IntroductionId, campaign.Development.DeliveredOneShots);
        Assert.Equal(2, tts.Calls);
        Assert.Equal(1, checkpointSaves);
        Assert.Equal(0, ai.DecisionCalls);
    }

    [Fact]
    public async Task DeliveredIntroductionLoadedFromCheckpoint_DoesNotRepeat()
    {
        var checkpointPath = Path.Combine(_directory, "worker-state.json");
        var checkpointStore = new CheckpointStore(checkpointPath);
        await checkpointStore.SaveAsync(new WorkerCheckpoint
        {
            Development = new ParasiteDevelopmentState
            {
                CurrentPhase = "Awakening",
                DeliveredOneShots = [IntroductionId],
            },
        }, CancellationToken.None);
        var restored = await checkpointStore.LoadAsync(CancellationToken.None);
        var campaign = CreateCampaign("Awakening");
        campaign.Development = restored.Development!;
        var ai = new RecordingAiService();
        var tts = new RecordingTextToSpeech();

        await CreateManager(campaign, ai, tts).ProcessAsync(CreateDialogue("D1"), CancellationToken.None);

        Assert.Equal(1, ai.DecisionCalls);
        Assert.Empty(tts.Texts);
    }

    [Fact]
    public async Task PhaseWithoutIntroduction_UsesNormalDecisionPath()
    {
        var campaign = CreateCampaign("Established");
        var ai = new RecordingAiService();
        var tts = new RecordingTextToSpeech();

        await CreateManager(campaign, ai, tts).ProcessAsync(CreateDialogue("D1"), CancellationToken.None);

        Assert.Equal(1, ai.DecisionCalls);
        Assert.Empty(tts.Texts);
    }

    private ConversationManager CreateManager(
        CampaignContext campaign,
        RecordingAiService ai,
        ITextToSpeech tts,
        Func<CancellationToken, Task>? saveCheckpoint = null)
    {
        var options = new ParasiteDevelopmentOptions
        {
            PhaseSequence = ["Instinctive", "Awakening", "Established"],
            PhaseIntroductions =
            {
                ["Awakening"] = new PhaseIntroductionOptions
                {
                    Id = IntroductionId,
                    Text = IntroductionText,
                },
            },
        };
        options.PhasePrompts["Awakening"] = "awakening prompt";
        options.PhasePrompts["Established"] = "established prompt";

        return new ConversationManager(
            campaign,
            new AIContextBuilder(),
            ai,
            tts,
            new NullLog(),
            "system prompt",
            20,
            new CampaignMemoryStore(Path.Combine(_directory, "memory.json")),
            new MemoryOptions(),
            new ParasiteDevelopmentPolicy(options),
            saveCheckpoint: saveCheckpoint);
    }

    private CampaignContext CreateCampaign(string phase) => new()
    {
        CampaignId = "C1",
        Directory = _directory,
        Development = new ParasiteDevelopmentState { CurrentPhase = phase },
    };

    private static DialogueState CreateDialogue(string dialogueId)
    {
        var json =
            $"{{\"schemaVersion\":1,\"campaignId\":\"C1\",\"timestamp\":\"2026-01-01 10:00:00.0000000\",\"source\":\"client\",\"type\":\"dialogue.line\",\"data\":{{\"dialogueId\":\"{dialogueId}\",\"speaker\":\"Gale\",\"text\":\"The grove needs help.\"}}}}";
        Assert.True(EventParser.TryParse(json, "C1", out var evt, out var error), error?.Message);
        var dialogue = new DialogueState { CampaignId = "C1", DialogueId = dialogueId };
        dialogue.Events.Add(evt!);
        return dialogue;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class RecordingAiService : IAIDecisionService
    {
        public int DecisionCalls { get; private set; }
        public int MemoryUpdateCalls { get; private set; }

        public Task<AIDecision> DecideAsync(AIRequestContext context, CancellationToken cancellationToken)
        {
            DecisionCalls++;
            return Task.FromResult(new AIDecision(AIDecisionAction.Silent, null));
        }

        public Task<CampaignMemoryUpdate> UpdateCampaignMemoryAsync(
            string campaignId,
            CampaignMemory currentMemory,
            string transcript,
            string dialogueId,
            CancellationToken cancellationToken)
        {
            MemoryUpdateCalls++;
            return Task.FromResult(new CampaignMemoryUpdate());
        }
    }

    private sealed class RecordingTextToSpeech : ITextToSpeech
    {
        public List<string> Texts { get; } = [];

        public Task<AudioResult> SynthesizeAsync(string text, VoiceContext context, CancellationToken cancellationToken)
        {
            Texts.Add(text);
            return Task.FromResult(new AudioResult("audio.mp3", "mp3"));
        }
    }

    private sealed class FailOnceTextToSpeech : ITextToSpeech
    {
        public int Calls { get; private set; }

        public Task<AudioResult> SynthesizeAsync(string text, VoiceContext context, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls == 1) throw new InvalidOperationException("TTS failed");
            return Task.FromResult(new AudioResult("audio.mp3", "mp3"));
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
