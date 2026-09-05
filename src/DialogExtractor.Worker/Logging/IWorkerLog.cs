namespace DialogExtractor.Worker.Logging;

/// <summary>
/// Technical logging sink for a single campaign's worker.log.
/// Deliberately independent of any DI/logging framework so the core
/// pipeline has no external package dependency.
/// </summary>
public interface IWorkerLog
{
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}
