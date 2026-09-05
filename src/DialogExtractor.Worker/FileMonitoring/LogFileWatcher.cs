using System.Threading.Channels;
using DialogExtractor.Worker.Persistence;

namespace DialogExtractor.Worker.FileMonitoring;

/// <summary>
/// Combines a <see cref="LogFileReader"/> with a FileSystemWatcher and a
/// periodic poll fallback. FileSystemWatcher is treated only as a hint that
/// "the file changed" - duplicate, missed, or out-of-order notifications are
/// all safe, because every ping (or timer tick) simply triggers another
/// PollOnceAsync, which is itself idempotent.
/// </summary>
public sealed class LogFileWatcher : IAsyncDisposable
{
    private readonly LogFileReader _reader;
    private readonly TimeSpan _pollInterval;
    private readonly FileSystemWatcher? _watcher;
    private readonly Channel<byte> _pings = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public event Action<Exception>? PollFailed
    {
        add => _reader.PollFailed += value;
        remove => _reader.PollFailed -= value;
    }

    public LogFileWatcher(string filePath, TimeSpan pollInterval)
    {
        _reader = new LogFileReader(filePath);
        _pollInterval = pollInterval;

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        var fileName = Path.GetFileName(filePath);

        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            _watcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName,
            };
            _watcher.Changed += (_, _) => Ping();
            _watcher.Created += (_, _) => Ping();
            _watcher.Renamed += (_, _) => Ping();
            _watcher.Error += (_, _) => Ping(); // fall back to polling if the watcher itself misbehaves
            _watcher.EnableRaisingEvents = true;
        }
    }

    public ChannelReader<string> Lines => _reader.Lines;

    public void RestoreCheckpoint(FileCheckpoint checkpoint) => _reader.RestoreCheckpoint(checkpoint);

    public FileCheckpoint CurrentCheckpoint() => _reader.CurrentCheckpoint();

    private void Ping() => _pings.Writer.TryWrite(0);

    public void Start(CancellationToken outerToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        _loopTask = RunLoopAsync(_cts.Token);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _reader.PollOnceAsync(cancellationToken);

            var pingWaitTask = _pings.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var timeoutTask = Task.Delay(_pollInterval, cancellationToken);
            try
            {
                await Task.WhenAny(pingWaitTask, timeoutTask);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            while (_pings.Reader.TryRead(out _))
            {
                // drain any coalesced pings
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _watcher?.Dispose();
        _cts?.Cancel();
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask;
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
        }

        _reader.Complete();
        _cts?.Dispose();
    }
}
