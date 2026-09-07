using System.Diagnostics;
using System.Text.Json;
using AIWhisper.Worker.Configuration;
using AIWhisper.Worker.Logging;
using Xunit;

namespace AIWhisper.Worker.Tests;

public sealed class AiRequestFileLogTests
{
    [Fact]
    public void Options_AreDisabledByDefault()
    {
        var options = new AiRequestLoggingOptions();

        Assert.False(options.Enabled);
        Assert.Equal("ai-requests.ndjson", options.FileName);
    }

    [Fact]
    public void Write_ProducesOneStructuredUserSideRecord()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "C1", "ai-requests.ndjson");
        try
        {
            using (var log = new AiRequestFileLog(root, "ai-requests.ndjson"))
            {
                var stopwatch = Stopwatch.StartNew();
                log.Write("C1", "decision", "gpt-4.1-mini", "CURRENT EVENT\\nGale: Hello", "{\"action\":\"silent\",\"text\":null}", 1, stopwatch,
                    systemInstructions: ["file:ai-system-prompt.txt", "minimum-reaction-level:Critical"],
                    commentFrequency: "Low",
                    minimumReactionLevel: "Critical");
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var entry = document.RootElement;
            Assert.Equal("C1", entry.GetProperty("campaignId").GetString());
            Assert.Equal("decision", entry.GetProperty("operation").GetString());
            Assert.Equal("gpt-4.1-mini", entry.GetProperty("model").GetString());
            Assert.Equal("CURRENT EVENT\\nGale: Hello", entry.GetProperty("userContent").GetString());
            Assert.Equal("{\"action\":\"silent\",\"text\":null}", entry.GetProperty("responseContent").GetString());
            Assert.Equal("Low", entry.GetProperty("commentFrequency").GetString());
            Assert.Equal("Critical", entry.GetProperty("minimumReactionLevel").GetString());
            Assert.False(entry.TryGetProperty("systemPrompt", out _));
            Assert.Equal(
                ["file:ai-system-prompt.txt", "minimum-reaction-level:Critical"],
                entry.GetProperty("systemInstructions").EnumerateArray().Select(value => value.GetString()!).ToArray());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Write_RoutesDifferentCampaignsToDifferentFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            using (var log = new AiRequestFileLog(root, "ai-requests.ndjson"))
            {
                var stopwatch = Stopwatch.StartNew();
                log.Write("C1", "decision", "model", "first", null, 1, stopwatch);
                log.Write("C2", "memory-update", "model", "second", null, 1, stopwatch);
            }

            var first = File.ReadAllText(Path.Combine(root, "C1", "ai-requests.ndjson"));
            var second = File.ReadAllText(Path.Combine(root, "C2", "ai-requests.ndjson"));
            Assert.Contains("\"campaignId\":\"C1\"", first);
            Assert.DoesNotContain("\"campaignId\":\"C2\"", first);
            Assert.Contains("\"campaignId\":\"C2\"", second);
            Assert.DoesNotContain("\"campaignId\":\"C1\"", second);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
