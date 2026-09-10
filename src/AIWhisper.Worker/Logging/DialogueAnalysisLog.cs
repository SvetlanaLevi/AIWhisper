using System.Text;
using System.Text.Json;

namespace AIWhisper.Worker.Logging;

public sealed record DialogueAnalysisEntry(
    DateTimeOffset TimestampUtc,
    string RunId,
    string Version,
    string CampaignId,
    string DialogueId,
    string RecordType,
    string? DialogueResource,
    string? Phase,
    string? Region,
    int EventCount,
    int LineCount,
    int ChoiceCount,
    string Transcript,
    string Outcome,
    string? SkipReason,
    string? AiText,
    IReadOnlyList<string> SystemInstructions,
    long DurationMs,
    bool? TtsSucceeded);

public sealed record DialogueMemoryAnalysisEntry(
    DateTimeOffset TimestampUtc,
    string RunId,
    string Version,
    string CampaignId,
    string DialogueId,
    string RecordType,
    int OperationsReturned,
    int OperationsAccepted,
    int OperationsRejected,
    int CharacterKnowledgeUpdates,
    bool Changed,
    string? Error);

public interface IDialogueAnalysisLog : IDisposable
{
    void Write(DialogueAnalysisEntry entry);
    void WriteMemory(DialogueMemoryAnalysisEntry entry);
}

public sealed class DialogueAnalysisFileLog : IDialogueAnalysisLog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public DialogueAnalysisFileLog(string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? ".");
        _writer = new StreamWriter(
            new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
    }

    public void Write(DialogueAnalysisEntry entry) => WriteCore(entry);
    public void WriteMemory(DialogueMemoryAnalysisEntry entry) => WriteCore(entry);

    private void WriteCore<T>(T entry)
    {
        lock (_gate)
        {
            try { _writer.WriteLine(JsonSerializer.Serialize(entry, JsonOptions)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        lock (_gate) _writer.Dispose();
    }
}

public sealed class NullDialogueAnalysisLog : IDialogueAnalysisLog
{
    public static readonly NullDialogueAnalysisLog Instance = new();
    private NullDialogueAnalysisLog() { }
    public void Write(DialogueAnalysisEntry entry) { }
    public void WriteMemory(DialogueMemoryAnalysisEntry entry) { }
    public void Dispose() { }
}
