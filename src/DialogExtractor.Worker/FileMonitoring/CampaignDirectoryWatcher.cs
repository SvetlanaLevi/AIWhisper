namespace DialogExtractor.Worker.FileMonitoring;

/// <summary>
/// Discovers first-level subdirectories of the DialogExtractor root
/// directory, each one a separate campaign. Uses a FileSystemWatcher as a
/// hint plus a periodic re-scan, since notifications are not guaranteed.
/// </summary>
public sealed class CampaignDirectoryWatcher : IDisposable
{
    readonly string _rootDirectory;
    readonly TimeSpan _pollInterval;
    readonly FileSystemWatcher? _watcher;
    readonly HashSet<string> _known = new(StringComparer.Ordinal);
    readonly object _gate = new();

    public event Action<string>? CampaignDiscovered;

    public CampaignDirectoryWatcher(string rootDirectory, TimeSpan pollInterval)
    {
        _rootDirectory = rootDirectory;
        _pollInterval = pollInterval;

        Directory.CreateDirectory(_rootDirectory);

        _watcher = new FileSystemWatcher(_rootDirectory)
        {
            NotifyFilter = NotifyFilters.DirectoryName,
            IncludeSubdirectories = false,
        };
        _watcher.Created += (_, _) => ScanNow();
        _watcher.Renamed += (_, _) => ScanNow();
        _watcher.EnableRaisingEvents = true;
    }

    public void ScanNow()
    {
        List<string> discovered = new();
        lock (_gate)
        {
            foreach (var dir in Directory.EnumerateDirectories(_rootDirectory))
            {
                var campaignId = Path.GetFileName(dir);
                if (_known.Add(campaignId))
                {
                    discovered.Add(campaignId);
                }
            }
        }

        foreach (var campaignId in discovered)
        {
            CampaignDiscovered?.Invoke(campaignId);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ScanNow();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            ScanNow();
        }
    }

    public void Dispose() => _watcher?.Dispose();
}
