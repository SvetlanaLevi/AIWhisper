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
    void Write(string operation, string model, string userContent, string? responseContent, int attempts, Stopwatch stopwatch, Exception? exception = null, IReadOnlyList<string>? systemInstructions = null, string? commentFrequency = null, string? minimumReactionLevel = null);
}

public sealed class AiRequestFileLog : IAiRequestLog, IDisposable
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private StreamWriter? _writer;

    public AiRequestFileLog(string filePath)
    {
        _filePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
    }

    public void Write(string operation, string model, string userContent, string? responseContent, int attempts, Stopwatch stopwatch, Exception? exception = null, IReadOnlyList<string>? systemInstructions = null, string? commentFrequency = null, string? minimumReactionLevel = null)
    {
        var entry = new
        {
            timestampUtc = DateTimeOffset.UtcNow,
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
                _writer ??= new StreamWriter(new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
                {
                    AutoFlush = true,
                };
                _writer.WriteLine(JsonSerializer.Serialize(entry));
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
            _writer?.Dispose();
            _writer = null;
        }
    }
}

public sealed class NullAiRequestLog : IAiRequestLog
{
    public static readonly NullAiRequestLog Instance = new();
    private NullAiRequestLog() { }

    public void Write(string operation, string model, string userContent, string? responseContent, int attempts, Stopwatch stopwatch, Exception? exception = null, IReadOnlyList<string>? systemInstructions = null, string? commentFrequency = null, string? minimumReactionLevel = null) { }
    public void Dispose() { }
}
