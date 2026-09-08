using System.Text.Json;
using AIWhisper.Worker.Conversation;

namespace AIWhisper.Worker.Persistence;

/// <summary>
/// Atomic JSON storage for one campaign's long-term memory. Like the worker
/// checkpoint, malformed files recover to an empty in-memory value without
/// overwriting the original file during loading.
/// </summary>
public sealed class CampaignMemoryStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly string _path;

    public CampaignMemoryStore(string path) => _path = path;

    public bool Exists => File.Exists(_path);

    public async Task<CampaignMemory> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return new CampaignMemory();

        try
        {
            await using var stream = File.OpenRead(_path);
            var memory = await JsonSerializer.DeserializeAsync<CampaignMemory>(stream, SerializerOptions, cancellationToken)
                ?? new CampaignMemory();
            NormalizeCollections(memory);
            return memory;
        }
        catch (JsonException)
        {
            return new CampaignMemory();
        }
    }

    public async Task SaveAsync(CampaignMemory memory, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var tempPath = _path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, memory, SerializerOptions, cancellationToken);
        }

        File.Move(tempPath, _path, overwrite: true);
    }

    internal static void NormalizeCollections(CampaignMemory memory)
    {
        memory.LongTermMemory ??= [];
        memory.CharacterKnowledge ??= [];
        foreach (var item in memory.LongTermMemory)
        {
            item.Summary ??= string.Empty;
            item.Tags ??= [];
        }
        foreach (var item in memory.CharacterKnowledge)
        {
            item.CharacterName ??= string.Empty;
            item.KnownFacts ??= [];
        }
    }
}
