using System.Text.Json;

namespace AIWhisper.Worker.Persistence;

/// <summary>
/// Loads/saves a campaign's worker-state.json. Writes are atomic (write to a
/// temp file, then replace) so a crash mid-write cannot corrupt the checkpoint.
/// </summary>
public sealed class CheckpointStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;

    public CheckpointStore(string path)
    {
        _path = path;
    }

    public async Task<WorkerCheckpoint> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new WorkerCheckpoint();
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            var checkpoint = await JsonSerializer.DeserializeAsync<WorkerCheckpoint>(stream, cancellationToken: cancellationToken);
            return checkpoint ?? new WorkerCheckpoint();
        }
        catch (JsonException)
        {
            // A corrupt checkpoint must never crash the worker; start fresh
            // (at-least-once semantics mean some events may be re-processed).
            return new WorkerCheckpoint();
        }
    }

    public async Task SaveAsync(WorkerCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, checkpoint, SerializerOptions, cancellationToken);
        }

        File.Move(tempPath, _path, overwrite: true);
    }
}
