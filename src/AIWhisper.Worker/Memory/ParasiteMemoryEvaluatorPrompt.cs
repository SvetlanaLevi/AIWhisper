using System.Text.Json;

namespace AIWhisper.Worker.Memory;

public static class ParasiteMemoryEvaluatorPrompt
{
    // Deliberately minimal. The final evaluator prompt is a separate design task.
    public const string DefaultTemplate = """
        Evaluate the completed event batch as the parasite's subjective long-term memory.
        Return only memories whose removal could change a future reaction, expectation,
        emotion, decision, or relationship. Plot importance alone is insufficient.
        Prefer no operations when nothing has durable subjective meaning. Use create for
        a new independent memory, update to refine or generalize an existing memory, and
        remove only when an existing memory is false or fully absorbed. New IDs are
        assigned by the application; never invent a target ID.
        For create, targetId must be null.
        For update or remove, targetId must exactly match an existing memory ID.

        Development phase must not be used to discard potentially useful long-term
        memory. Phase relevance and Active Memory selection are handled separately
        by the application.
        """;

    public static string RenderUserContext(MemoryEvaluationRequest request) =>
        JsonSerializer.Serialize(new
        {
            completedEventBatch = request.Transcript,
            request.DialogueId,
            currentDevelopmentPhase = request.DevelopmentPhase,
            request.Region,
            currentCharacters = request.CurrentCharacters,
            existingLongTermMemory = request.ExistingMemory,
        });
}
