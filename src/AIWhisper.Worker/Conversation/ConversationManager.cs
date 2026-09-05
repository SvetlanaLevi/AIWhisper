using AIWhisper.Worker.AI;
using AIWhisper.Worker.EventProcessing;
using AIWhisper.Worker.Logging;
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

    public ConversationManager(
        CampaignContext campaign,
        AIContextBuilder contextBuilder,
        IAIDecisionService aiDecisionService,
        ITextToSpeech textToSpeech,
        IWorkerLog log,
        string systemPrompt,
        int maxHistoryEntries)
    {
        _campaign = campaign;
        _contextBuilder = contextBuilder;
        _aiDecisionService = aiDecisionService;
        _textToSpeech = textToSpeech;
        _log = log;
        _systemPrompt = systemPrompt;
        _maxHistoryEntries = maxHistoryEntries;
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
        var requestContext = new AIRequestContext(_systemPrompt, userPrompt);

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
            return;
        }

        _log.Info($"dialogue {dialogue.DialogueId}: AI decided to speak");
        RecordHistory(dialogue, transcript, "speak", decision.Text);

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
}
