using AIWhisper.Worker.Knowledge;
using AIWhisper.Worker.Logging;
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

    [Fact]
    public void FormatsProfileWithoutClass()
    {
        var formatted = CharacterKnowledgeFormatter.Format(new CharacterKnowledge
        {
            Name = "Arabella",
            Race = "Tiefling",
            Role = "Tiefling child",
            Summary = "A bold young refugee.",
            Personality = ["bold"],
        });

        Assert.Equal("Arabella — Tiefling, Tiefling child. Personality: bold. Background: A bold young refugee.", formatted);
    }

    [Fact]
    public void FormatsProfileWithoutRaceOrClass()
    {
        var formatted = CharacterKnowledgeFormatter.Format(new CharacterKnowledge
        {
            Name = "Sceleritas Fel",
            Role = "Mysterious butler",
            Summary = "A cryptic servant.",
            Personality = ["obsequious"],
        });

        Assert.Equal("Sceleritas Fel — Mysterious butler. Personality: obsequious. Background: A cryptic servant.", formatted);
    }

    [Fact]
    public void InvalidFile_IsSkippedAndReportedAsWarning()
    {
        File.WriteAllText(Path.Combine(_directory, "broken.json"), "{ not valid json }");
        var log = new RecordingLog();

        var provider = new CharacterKnowledgeProvider(_directory, log);

        Assert.Equal("Astarion", provider.Find("Astarion")?.Name);
        Assert.Contains(log.Warnings, warning => warning.Contains("broken.json", StringComparison.Ordinal));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private sealed class RecordingLog : IWorkerLog
    {
        public List<string> Warnings { get; } = [];
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message, Exception? exception = null) { }
    }
}
