using System.Text.Json;

namespace AIWhisper.Worker.Memory;

public static class ParasiteMemoryEvaluatorPrompt
{
    public static string RenderUserContext(MemoryEvaluationRequest request) =>
        JsonSerializer.Serialize(new
        {
            completedEventBatch = request.Transcript,
            request.DialogueId,
            currentDevelopmentPhase = request.DevelopmentPhase,
            request.Region,
            currentCharacters = request.CurrentCharacters,
            existingLongTermMemory = request.ExistingMemory,
            trackedCharacterNames = request.TrackedCharacterNames,
            existingCharacterKnowledge = request.ExistingCharacterKnowledge,
        });
}
