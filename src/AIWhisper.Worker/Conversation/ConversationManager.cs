using AIWhisper.Worker.AI;
using AIWhisper.Worker.Configuration;
using AIWhisper.Worker.Development;
using AIWhisper.Worker.EventProcessing;
using AIWhisper.Worker.Logging;
using AIWhisper.Worker.Persistence;
using AIWhisper.Worker.Tts;

namespace AIWhisper.Worker.Conversation;

/// <summary>
/// Sequentially processes completed dialogues for one campaign: builds the
/// AI context, asks for a decision, and on "speak" hands the text to TTS.
/// Multiple dialogues can complete concurrently upstream, but this class
/// processes the resulting queue one at a time - simple, and sufficient for
/// the first version (see section 38 of the brief).
/// </summary>
public sealed class ConversationManager
{
    private readonly CampaignContext _campaign;
    private readonly AIContextBuilder _contextBuilder;
    private readonly IAIDecisionService _aiDecisionService;
    private readonly ITextToSpeech _textToSpeech;
    private readonly IWorkerLog _log;
    private readonly string _systemPrompt;
    private readonly int _maxHistoryEntries;
    private readonly CampaignMemoryStore _memoryStore;
    private readonly MemoryOptions _memoryOptions;
    private readonly ParasiteDevelopmentPolicy? _developmentPolicy;
    private readonly string _systemPromptId;

    public ConversationManager(
        CampaignContext campaign,
        AIContextBuilder contextBuilder,
        IAIDecisionService aiDecisionService,
        ITextToSpeech textToSpeech,
        IWorkerLog log,
        string systemPrompt,
        int maxHistoryEntries,
        CampaignMemoryStore memoryStore,
        MemoryOptions memoryOptions,
        ParasiteDevelopmentPolicy? developmentPolicy = null,
        string systemPromptId = "base:unspecified")
    {
        _campaign = campaign;
        _contextBuilder = contextBuilder;
        _aiDecisionService = aiDecisionService;
        _textToSpeech = textToSpeech;
        _log = log;
        _systemPrompt = systemPrompt;
        _maxHistoryEntries = maxHistoryEntries;
        _memoryStore = memoryStore;
        _memoryOptions = memoryOptions;
        _developmentPolicy = developmentPolicy;
        _systemPromptId = systemPromptId;
    }

    public async Task ProcessAsync(DialogueState dialogue, CancellationToken cancellationToken)
    {
        var transcript = _contextBuilder.BuildTranscript(dialogue);
        if (string.IsNullOrWhiteSpace(transcript))
        {
            _log.Info($"dialogue {dialogue.DialogueId} completed with no line/choice content - skipping AI");
            return;
        }

        var userPrompt = _contextBuilder.BuildUserPrompt(_campaign, dialogue, _maxHistoryEntries);
        string? developmentPhase = null;
        string? developmentPrompt = null;
        var hasDevelopmentInstruction = _developmentPolicy?.TryGetInstruction(
            _campaign.Development,
            out developmentPhase,
            out developmentPrompt) == true;
        var requestContext = new AIRequestContext(
            _systemPrompt,
            userPrompt,
            hasDevelopmentInstruction ? developmentPhase : null,
            hasDevelopmentInstruction ? developmentPrompt : null,
            _systemPromptId,
            instructions => _campaign.LastAppliedSystemInstructions = instructions);
        _log.Info($"dialogue {dialogue.DialogueId}: requesting an AI decision ({dialogue.Events.Count} event(s), {transcript.Length} transcript character(s))");

        AIDecision decision;
        try
        {
            decision = await _aiDecisionService.DecideAsync(requestContext, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error($"AI decision failed for dialogue {dialogue.DialogueId} after retries - dialogue left unanswered", ex);
            RecordHistory(dialogue, transcript, "error", null);
            return;
        }

        if (decision.Action == AIDecisionAction.Silent)
        {
            _log.Info($"dialogue {dialogue.DialogueId}: AI decided to stay silent");
            RecordHistory(dialogue, transcript, "silent", null);
            await UpdateCampaignMemoryAsync(dialogue, transcript, cancellationToken);
            return;
        }

        var aiTextForLog = decision.Text?.ReplaceLineEndings(" ") ?? string.Empty;
        _log.Highlight($">>> [AI SPEAK] dialogue {dialogue.DialogueId}: {aiTextForLog}");
        RecordHistory(dialogue, transcript, "speak", decision.Text);

        // Start the independent memory request now, but do not make speech wait
        // for it. ProcessAsync still awaits it before accepting the next
        // dialogue, keeping updates sequential for this campaign.
        var memoryUpdateTask = UpdateCampaignMemoryAsync(dialogue, transcript, cancellationToken);

        try
        {
            var speaker = dialogue.Speakers.Count > 0 ? dialogue.Speakers[0].Name : null;
            var voiceContext = new VoiceContext(dialogue.CampaignId, dialogue.DialogueId, speaker, null);
            await _textToSpeech.SynthesizeAsync(decision.Text ?? string.Empty, voiceContext, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error($"TTS failed for dialogue {dialogue.DialogueId} - AI text was produced but not spoken", ex);
        }
        finally
        {
            await memoryUpdateTask;
        }
    }

    private void RecordHistory(DialogueState dialogue, string transcript, string aiAction, string? aiText)
    {
        var flattened = transcript.ReplaceLineEndings(" / ").Trim();
        var summary = flattened.Length > 240 ? flattened[..240] + "..." : flattened;
        _campaign.History.Add(new ConversationHistoryEntry(
            dialogue.DialogueId,
            dialogue.DialogueResource,
            dialogue.StartTime,
            dialogue.EndTime,
            summary,
            aiAction,
            aiText));
    }

    private async Task UpdateCampaignMemoryAsync(
        DialogueState dialogue,
        string transcript,
        CancellationToken cancellationToken)
    {
        try
        {
            var update = await _aiDecisionService.UpdateCampaignMemoryAsync(
                _campaign.Memory,
                transcript,
                cancellationToken);
            if (!CampaignMemoryMerger.Apply(_campaign.Memory, update, _memoryOptions))
            {
                _log.Info($"campaign {_campaign.CampaignId}: memory update for dialogue {dialogue.DialogueId} contained no changes");
                return;
            }

            await _memoryStore.SaveAsync(_campaign.Memory, cancellationToken);
            _log.Info($"campaign {_campaign.CampaignId}: memory updated and saved after dialogue {dialogue.DialogueId}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error($"campaign {_campaign.CampaignId}: failed to update memory after dialogue {dialogue.DialogueId}", ex);
        }
    }
}
