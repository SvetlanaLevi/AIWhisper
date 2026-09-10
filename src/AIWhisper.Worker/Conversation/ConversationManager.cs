using AIWhisper.Worker.AI;
using AIWhisper.Worker.Configuration;
using AIWhisper.Worker.Development;
using AIWhisper.Worker.EventProcessing;
using AIWhisper.Worker.Logging;
using AIWhisper.Worker.Memory;
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
    private readonly IMemoryEvaluator? _memoryEvaluator;
    private readonly ITextToSpeech _textToSpeech;
    private readonly IWorkerLog _log;
    private readonly string _systemPrompt;
    private readonly int _maxHistoryEntries;
    private readonly CampaignMemoryStore _memoryStore;
    private readonly MemoryOptions _memoryOptions;
    private readonly ParasiteDevelopmentPolicy? _developmentPolicy;
    private readonly string _systemPromptId;
    private readonly Func<CancellationToken, Task>? _saveCheckpoint;
    private readonly HashSet<string> _ignoredDialogueResources;
    private readonly IDialogueAnalysisLog _dialogueAnalysisLog;
    private readonly string _runId = Guid.NewGuid().ToString("N");
    private static readonly string Version = typeof(ConversationManager).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion
        ?? typeof(ConversationManager).Assembly.GetName().Version?.ToString()
        ?? "unknown";

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
        string systemPromptId = "base:unspecified",
        Func<CancellationToken, Task>? saveCheckpoint = null,
        IMemoryEvaluator? memoryEvaluator = null,
        IEnumerable<string>? ignoredDialogueResources = null,
        IDialogueAnalysisLog? dialogueAnalysisLog = null)
    {
        _campaign = campaign;
        _contextBuilder = contextBuilder;
        _aiDecisionService = aiDecisionService;
        _memoryEvaluator = memoryEvaluator;
        _textToSpeech = textToSpeech;
        _log = log;
        _systemPrompt = systemPrompt;
        _maxHistoryEntries = maxHistoryEntries;
        _memoryStore = memoryStore;
        _memoryOptions = memoryOptions;
        _developmentPolicy = developmentPolicy;
        _systemPromptId = systemPromptId;
        _saveCheckpoint = saveCheckpoint;
        _ignoredDialogueResources = new HashSet<string>(
            ignoredDialogueResources?.Where(name => !string.IsNullOrWhiteSpace(name)) ?? [],
            StringComparer.OrdinalIgnoreCase);
        _dialogueAnalysisLog = dialogueAnalysisLog ?? NullDialogueAnalysisLog.Instance;
    }

    public async Task ProcessAsync(DialogueState dialogue, CancellationToken cancellationToken)
    {
        await _campaign.ProcessingGate.WaitAsync(cancellationToken);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        string? fingerprint = null;
        try
        {
            if (dialogue.Generation != _campaign.Generation)
            {
                WriteAnalysis(dialogue, _contextBuilder.BuildTranscript(dialogue), "skipped", "stale-generation", stopwatch);
                return;
            }
            var resourceName = DialogueResourceName.Normalize(dialogue.DialogueResource);
            if (resourceName is not null && _ignoredDialogueResources.Contains(resourceName))
            {
                _log.Info($"dialogue {dialogue.DialogueId}: resource '{resourceName}' is configured to be ignored - skipping memory and AI");
                WriteAnalysis(dialogue, _contextBuilder.BuildTranscript(dialogue), "skipped", "ignored-resource", stopwatch);
                return;
            }
            fingerprint = DialogueFingerprint.Create(dialogue);
            if (!_campaign.ProcessedDialogueFingerprints.TryAdd(fingerprint, 0))
            {
                _log.Info($"dialogue {dialogue.DialogueId}: exact dialogue branch already processed - skipping memory and AI");
                WriteAnalysis(dialogue, _contextBuilder.BuildTranscript(dialogue), "skipped", "duplicate", stopwatch);
                return;
            }
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _campaign.DialogueCancellation.Token);
            await ProcessCurrentAsync(dialogue, linked, stopwatch);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (fingerprint is not null) _campaign.ProcessedDialogueFingerprints.TryRemove(fingerprint, out _);
            _log.Info($"dialogue {dialogue.DialogueId}: cancelled by save loading");
            WriteAnalysis(dialogue, _contextBuilder.BuildTranscript(dialogue), "cancelled", "save-loading", stopwatch);
        }
        catch (OperationCanceledException)
        {
            if (fingerprint is not null) _campaign.ProcessedDialogueFingerprints.TryRemove(fingerprint, out _);
            throw;
        }
        finally { _campaign.ProcessingGate.Release(); }
    }

    private async Task ProcessCurrentAsync(DialogueState dialogue, CancellationTokenSource linked, System.Diagnostics.Stopwatch stopwatch)
    {
        var cancellationToken = linked.Token;
        Task memoryEvaluationTask = Task.CompletedTask;
        var memoryEvaluationStarted = false;
        var transcript = _contextBuilder.BuildTranscript(dialogue);
        if (string.IsNullOrWhiteSpace(transcript))
        {
            _log.Info($"dialogue {dialogue.DialogueId} completed with no line/choice content - skipping AI");
            WriteAnalysis(dialogue, transcript, "skipped", "empty-transcript", stopwatch);
            linked.Dispose();
            return;
        }
        var hasPendingIntroduction = _developmentPolicy?.TryGetPendingIntroduction(
            _campaign.Development,
            out _) == true;
        if (!hasPendingIntroduction && IsShortNpcOnlyChatter(dialogue))
        {
            _log.Info($"dialogue {dialogue.DialogueId} contains only short NPC chatter - skipping memory and AI");
            WriteAnalysis(dialogue, transcript, "skipped", "short-npc-chatter", stopwatch);
            linked.Dispose();
            return;
        }

        // Freeze reaction context before this batch can update memory.
        var userPrompt = _contextBuilder.BuildUserPrompt(_campaign, dialogue, _maxHistoryEntries);
        try
        {
            if (_developmentPolicy?.TryGetPendingIntroduction(
                    _campaign.Development,
                    out var introduction) == true)
            {
                var ttsSucceeded = await PlayPhaseIntroductionAsync(dialogue, transcript, introduction, cancellationToken);
                WriteAnalysis(dialogue, transcript, "phase-introduction", null, stopwatch, introduction.Text, ttsSucceeded);
                return;
            }

            string? developmentPhase = null;
            string? developmentPrompt = null;
            var hasDevelopmentInstruction = _developmentPolicy?.TryGetInstruction(
                _campaign.Development,
                out developmentPhase,
                out developmentPrompt) == true;
            var requestContext = new AIRequestContext(
                _campaign.CampaignId,
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
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Error($"AI decision failed for dialogue {dialogue.DialogueId} after retries - dialogue left unanswered", ex);
                RecordHistory(dialogue, transcript, "error", null);
                WriteAnalysis(dialogue, transcript, "error", "ai-decision-failed", stopwatch);
                return;
            }

            // The evaluator may retain an important intent expressed by the parasite.
            // It remains background work and does not hold up the reaction queue.
            memoryEvaluationTask = EvaluateMemoryAsync(
                dialogue,
                transcript,
                decision.Action == AIDecisionAction.Speak ? decision.Text : null,
                cancellationToken);
            memoryEvaluationStarted = true;

            if (decision.Action == AIDecisionAction.Silent)
            {
                _log.Info($"dialogue {dialogue.DialogueId}: AI decided to stay silent");
                RecordHistory(dialogue, transcript, "silent", null);
                WriteAnalysis(dialogue, transcript, "silent", null, stopwatch);
                return;
            }

            var aiTextForLog = decision.Text?.ReplaceLineEndings(" ") ?? string.Empty;
            _log.Highlight($">>> [AI SPEAK] dialogue {dialogue.DialogueId}: {aiTextForLog}");
            RecordHistory(dialogue, transcript, "speak", decision.Text);

            try
            {
                var speaker = dialogue.Speakers.Count > 0 ? dialogue.Speakers[0].Name : null;
                var voiceContext = new VoiceContext(dialogue.CampaignId, dialogue.DialogueId, speaker, null);
                await _textToSpeech.SynthesizeAsync(decision.Text ?? string.Empty, voiceContext, cancellationToken);
                WriteAnalysis(dialogue, transcript, "speak", null, stopwatch, decision.Text, true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Error($"TTS failed for dialogue {dialogue.DialogueId} - AI text was produced but not spoken", ex);
                WriteAnalysis(dialogue, transcript, "speak", "tts-failed", stopwatch, decision.Text, false);
            }
        }
        finally
        {
            // Introductions and failed decisions still allow the game event itself
            // to update memory, but do not invent a parasite intent.
            if (!memoryEvaluationStarted)
                memoryEvaluationTask = EvaluateMemoryAsync(dialogue, transcript, null, cancellationToken);
            _ = ObserveMemoryEvaluationAsync(memoryEvaluationTask, linked);
        }
    }

    private bool IsShortNpcOnlyChatter(DialogueState dialogue)
    {
        var contentEvents = dialogue.Events
            .Where(evt => evt.Type is "dialogue.line" or "dialogue.choice")
            .ToList();
        if (contentEvents.Count is 0 or > 2 || contentEvents.Any(evt => evt.Type == "dialogue.choice"))
            return false;

        return contentEvents.All(evt =>
        {
            if (evt.Data.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !evt.Data.TryGetProperty("speaker", out var speaker))
                return false;
            var name = speaker.GetString();
            return !string.IsNullOrWhiteSpace(name) &&
                !string.Equals(name, "Narrator", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "Player", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, _campaign.Session.Player, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static async Task ObserveMemoryEvaluationAsync(
        Task memoryEvaluationTask,
        CancellationTokenSource linked)
    {
        try
        {
            await memoryEvaluationTask;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // Save loading and application shutdown intentionally cancel stale memory work.
        }
        finally
        {
            linked.Dispose();
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

    private async Task<bool> PlayPhaseIntroductionAsync(
        DialogueState dialogue,
        string transcript,
        PhaseIntroductionOptions introduction,
        CancellationToken cancellationToken)
    {
        try
        {
            var speaker = dialogue.Speakers.Count > 0 ? dialogue.Speakers[0].Name : null;
            var voiceContext = new VoiceContext(dialogue.CampaignId, dialogue.DialogueId, speaker, null);
            await _textToSpeech.SynthesizeAsync(introduction.Text, voiceContext, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            _campaign.Development.DeliveredOneShots.Add(introduction.Id);
            RecordHistory(dialogue, transcript, "speak", introduction.Text);
            if (_saveCheckpoint is not null) await _saveCheckpoint(cancellationToken);
            _log.Info($"campaign {_campaign.CampaignId}: delivered phase introduction '{introduction.Id}'");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error($"campaign {_campaign.CampaignId}: failed to deliver phase introduction '{introduction.Id}' - it will be retried", ex);
            return false;
        }
    }

    private void WriteAnalysis(
        DialogueState dialogue,
        string transcript,
        string outcome,
        string? skipReason,
        System.Diagnostics.Stopwatch stopwatch,
        string? aiText = null,
        bool? ttsSucceeded = null)
    {
        var contentEvents = dialogue.Events.Where(evt => evt.Type is "dialogue.line" or "dialogue.choice").ToList();
        var systemInstructions = outcome is "silent" or "speak" or "error"
            ? _campaign.LastAppliedSystemInstructions.ToList()
            : [];
        _dialogueAnalysisLog.Write(new DialogueAnalysisEntry(
            DateTimeOffset.UtcNow, _runId, Version, _campaign.CampaignId, dialogue.DialogueId, "dialogue",
            DialogueResourceName.Normalize(dialogue.DialogueResource), _campaign.Development.CurrentPhase,
            _campaign.Session.Region, dialogue.Events.Count,
            contentEvents.Count(evt => evt.Type == "dialogue.line"),
            contentEvents.Count(evt => evt.Type == "dialogue.choice"), transcript, outcome, skipReason,
            aiText, systemInstructions, stopwatch.ElapsedMilliseconds, ttsSucceeded));
    }

    private async Task EvaluateMemoryAsync(
        DialogueState dialogue,
        string transcript,
        string? parasiteRemark,
        CancellationToken cancellationToken)
    {
        if (_memoryEvaluator is null) return;

        await _campaign.MemoryGate.WaitAsync(cancellationToken);
        try
        {
            var characters = dialogue.Events
                .Where(evt => evt.Type == "dialogue.line" && evt.Data.ValueKind == System.Text.Json.JsonValueKind.Object)
                .Select(evt => evt.Data.TryGetProperty("speaker", out var speaker) ? speaker.GetString() : null)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var eligibleTrackedCharacters = _memoryOptions.TrackedCharacters
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Where(name => characters.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                    transcript.Contains(name, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var existingCharacterKnowledge = _campaign.Memory.CharacterKnowledge
                .Where(item => eligibleTrackedCharacters.Contains(item.CharacterName, StringComparer.OrdinalIgnoreCase))
                .ToList();
            var result = await _memoryEvaluator.EvaluateAsync(new MemoryEvaluationRequest(
                _campaign.CampaignId,
                _campaign.Memory.LongTermMemory.ToList(),
                existingCharacterKnowledge,
                eligibleTrackedCharacters,
                transcript,
                dialogue.DialogueId,
                _campaign.Development.CurrentPhase,
                _campaign.Session.Region,
                characters,
                parasiteRemark), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var acceptedOperations = result.Operations
                .Where(operation => operation.Category != MemoryCategory.ParasiteIntent ||
                    ParasiteIntentPolicy.IsDurable(parasiteRemark))
                .ToList();
            var rejectedIntents = result.Operations.Count - acceptedOperations.Count;
            if (rejectedIntents > 0)
                _log.Info($"campaign {_campaign.CampaignId}: rejected {rejectedIntents} non-committal parasite intent memory operation(s)");
            var applied = MemoryOperationApplier.Apply(
                _campaign.Memory,
                acceptedOperations,
                _memoryOptions.MaxLongTermItems);
            var characterKnowledgeApplied = DiscoveredCharacterKnowledgeApplier.Apply(
                _campaign.Memory,
                result.CharacterKnowledgeUpdates,
                eligibleTrackedCharacters,
                _memoryOptions.MaxKnownFactsPerCharacter);
            var changed = applied.Changed || characterKnowledgeApplied.Changed;
            foreach (var target in applied.UnknownTargets)
                _log.Warn($"campaign {_campaign.CampaignId}: memory operation references unknown target {target}; ignored");
            foreach (var character in characterKnowledgeApplied.RejectedCharacters)
                _log.Warn($"campaign {_campaign.CampaignId}: discovered knowledge update for untracked character '{character}' was ignored");
            if (!changed)
            {
                WriteMemoryAnalysis(dialogue, result, acceptedOperations.Count, rejectedIntents, false);
                _log.Info($"campaign {_campaign.CampaignId}: memory evaluation for dialogue {dialogue.DialogueId} contained no changes");
                return;
            }

            await _memoryStore.SaveAsync(_campaign.Memory, cancellationToken);
            WriteMemoryAnalysis(dialogue, result, acceptedOperations.Count, rejectedIntents, true);
            _log.Info($"campaign {_campaign.CampaignId}: memory updated and saved after dialogue {dialogue.DialogueId}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error($"campaign {_campaign.CampaignId}: memory evaluation failed after dialogue {dialogue.DialogueId}; reaction pipeline continues", ex);
            _dialogueAnalysisLog.WriteMemory(new DialogueMemoryAnalysisEntry(
                DateTimeOffset.UtcNow, _runId, Version, _campaign.CampaignId, dialogue.DialogueId, "memory",
                0, 0, 0, 0, false, ex.GetType().Name + ": " + ex.Message));
        }
        finally
        {
            _campaign.MemoryGate.Release();
        }
    }

    private void WriteMemoryAnalysis(
        DialogueState dialogue,
        MemoryEvaluationResult result,
        int acceptedOperations,
        int rejectedOperations,
        bool changed) =>
        _dialogueAnalysisLog.WriteMemory(new DialogueMemoryAnalysisEntry(
            DateTimeOffset.UtcNow, _runId, Version, _campaign.CampaignId, dialogue.DialogueId, "memory",
            result.Operations.Count, acceptedOperations, rejectedOperations,
            result.CharacterKnowledgeUpdates.Count, changed, null));
}
