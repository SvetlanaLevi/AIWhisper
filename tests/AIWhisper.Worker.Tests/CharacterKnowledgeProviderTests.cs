using AIWhisper.Worker.Knowledge;
using Xunit;

namespace AIWhisper.Worker.Tests;

public sealed class CharacterKnowledgeProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public CharacterKnowledgeProviderTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "astarion.json"), """
        { "name":"Astarion", "category":"companion", "race":"High Elf", "class":"Rogue", "role":"Origin companion", "summary":"A theatrical survivor.", "personality":["sarcastic","charming"] }
        """);
    }

    [Fact]
    public void LoadsOnceAndFindsNamesCaseInsensitively()
    {
        var provider = new CharacterKnowledgeProvider(_directory);
        File.Delete(Path.Combine(_directory, "astarion.json"));

        Assert.Equal("Astarion", provider.Find(" astarion ")?.Name);
        Assert.Equal("Astarion", provider.Find("ASTARION")?.Name);
        Assert.Null(provider.Find("Kagha"));
    }

    [Fact]
    public void FormatsCompactCharacterContext()
    {
        var character = new CharacterKnowledgeProvider(_directory).Find("Astarion")!;

        Assert.Equal("Astarion — High Elf Rogue, Origin companion. Personality: sarcastic, charming. Background: A theatrical survivor.", CharacterKnowledgeFormatter.Format(character));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
