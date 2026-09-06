using System.Text;

namespace AIWhisper.Worker.Logging;

/// <summary>
/// Writes technical (non-gameplay) log lines to a campaign's worker.log file.
/// Callers must never pass secrets (API keys, tokens) into any message.
/// </summary>
public sealed class CampaignFileLog : IWorkerLog, IDisposable
{
    private static readonly object ConsoleGate = new();
    private readonly string _filePath;
    private readonly WorkerLogLevel _minimumLevel;
    private readonly bool _alsoWriteToConsole;
    private readonly object _gate = new();
    private StreamWriter? _writer;

    public CampaignFileLog(string filePath, WorkerLogLevel minimumLevel = WorkerLogLevel.Information, bool alsoWriteToConsole = false)
    {
        _filePath = filePath;
        _minimumLevel = minimumLevel;
        _alsoWriteToConsole = alsoWriteToConsole;

        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public void Debug(string message) => Write(WorkerLogLevel.Debug, message);
    public void Info(string message) => Write(WorkerLogLevel.Information, message);
    public void Highlight(string message) => Write(WorkerLogLevel.Information, message, highlightInConsole: true);
    public void Warn(string message) => Write(WorkerLogLevel.Warning, message);

    public void Error(string message, Exception? exception = null)
    {
        var full = exception is null ? message : $"{message} :: {exception.GetType().Name}: {exception.Message}";
        Write(WorkerLogLevel.Error, full);
    }

    private void Write(WorkerLogLevel level, string message, bool highlightInConsole = false)
    {
        if (level < _minimumLevel) return;

        var line = $"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";

        lock (_gate)
        {
            try
            {
                _writer ??= new StreamWriter(new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
                {
                    AutoFlush = true,
                };
                _writer.WriteLine(line);
            }
            catch (IOException)
            {
                // Logging must never crash the worker. Best-effort only.
            }
        }

        if (_alsoWriteToConsole)
        {
            lock (ConsoleGate)
            {
                if (highlightInConsole)
                {
                    var previousColor = Console.ForegroundColor;
                    try
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine();
                        Console.WriteLine(line);
                        Console.WriteLine();
                    }
                    finally
                    {
                        Console.ForegroundColor = previousColor;
                    }
                }
                else
                {
                    Console.WriteLine(line);
                }
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
