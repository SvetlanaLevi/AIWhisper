using System.Text.Json;

namespace AIWhisper.Worker.Persistence;

/// <summary>Publishes memory and campaign state together in one atomic JSON file.</summary>
public sealed class CampaignSnapshotStore(string campaignDirectory)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string GetPath(Guid id) => Path.Combine(campaignDirectory, "snaphots", $"snapshot_{id:D}", "state.json");

    public async Task SaveAsync(Guid id, CampaignStateSnapshot snapshot, CancellationToken token)
    {
        var path = GetPath(id);
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, snapshot, Options, token);
        File.Move(temporary, path, overwrite: false);
    }

    public async Task<CampaignStateSnapshot?> LoadAsync(Guid id, CancellationToken token)
    {
        var path = GetPath(id);
        try
        {
            await using var stream = File.OpenRead(path);
            var snapshot = await JsonSerializer.DeserializeAsync<CampaignStateSnapshot>(stream, Options, token);
            if (snapshot?.Memory is null || snapshot.Development is null || snapshot.Session is null) return null;
            CampaignMemoryStore.NormalizeCollections(snapshot.Memory);
            snapshot.Development.DeliveredOneShots = new(snapshot.Development.DeliveredOneShots ?? [], StringComparer.OrdinalIgnoreCase);
            return snapshot;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException) { return null; }
    }
}
