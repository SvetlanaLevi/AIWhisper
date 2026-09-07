using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AIWhisper.Worker.Logging;

/// <summary>
/// Diagnostic record of AI calls. System prompt text and secrets must never
/// be written here; only non-sensitive identifiers of applied instructions
/// may be recorded.
/// </summary>
public interface IAiRequestLog : IDisposable
{
    void Write(string campaignId, string operation, string model, string userContent, string? responseContent, int attempts, Stopwatch stopwatch, Exception? exception = null, IReadOnlyList<string>? systemInstructions = null, string? commentFrequency = null, string? minimumReactionLevel = null);
}

public sealed class AiRequestFileLog : IAiRequestLog, IDisposable
{
    private readonly string _rootDirectory;
    private readonly string _fileName;
    private readonly object _gate = new();
    private readonly Dictionary<string, StreamWriter> _writers = new(StringComparer.OrdinalIgnoreCase);

    public AiRequestFileLog(string rootDirectory, string fileName)
    {
        _rootDirectory = rootDirectory;
        _fileName = fileName;
    }

    public void Write(string campaignId, string operation, string model, string userContent, string? responseContent, int attempts, Stopwatch stopwatch, Exception? exception = null, IReadOnlyList<string>? systemInstructions = null, string? commentFrequency = null, string? minimumReactionLevel = null)
    {
        var entry = new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            campaignId,
            operation,
            model,
            attempts,
            durationMs = stopwatch.ElapsedMilliseconds,
            systemInstructions = systemInstructions ?? [],
            commentFrequency,
            minimumReactionLevel,
            userContent,
            responseContent,
            error = exception is null ? null : new { type = exception.GetType().Name, message = exception.Message },
        };

        lock (_gate)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(campaignId) ||
                    Path.IsPathRooted(campaignId) ||
                    !string.Equals(Path.GetFileName(campaignId), campaignId, StringComparison.Ordinal))
                {
                    return;
                }

                if (!_writers.TryGetValue(campaignId, out var writer))
                {
                    var campaignDirectory = Path.Combine(_rootDirectory, campaignId);
                    Directory.CreateDirectory(campaignDirectory);
                    var filePath = Path.Combine(campaignDirectory, _fileName);
                    writer = new StreamWriter(new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
                    {
                        AutoFlush = true,
                    };
                    _writers[campaignId] = writer;
                }

                writer.WriteLine(JsonSerializer.Serialize(entry));
            }
            catch (IOException)
            {
                // Diagnostic logging is best-effort and must not stop the worker.
            }
            catch (UnauthorizedAccessException)
            {
                // Diagnostic logging is best-effort and must not stop the worker.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var writer in _writers.Values) writer.Dispose();
            _writers.Clear();
        }
    }
}

public sealed class NullAiRequestLog : IAiRequestLog
{
    public static readonly NullAiRequestLog Instance = new();
    private NullAiRequestLog() { }

    public void Write(string campaignId, string operation, string model, string userContent, string? responseContent, int attempts, Stopwatch stopwatch, Exception? exception = null, IReadOnlyList<string>? systemInstructions = null, string? commentFrequency = null, string? minimumReactionLevel = null) { }
    public void Dispose() { }
}
