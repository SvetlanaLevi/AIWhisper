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
        var path = Path.GetTempFileName();
        try
        {
            using (var log = new AiRequestFileLog(path))
            {
                var stopwatch = Stopwatch.StartNew();
                log.Write("decision", "gpt-4.1-mini", "CURRENT EVENT\\nGale: Hello", "{\"action\":\"silent\",\"text\":null}", 1, stopwatch,
                    systemInstructions: ["file:ai-system-prompt.txt", "comment-frequency:Low"]);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var entry = document.RootElement;
            Assert.Equal("decision", entry.GetProperty("operation").GetString());
            Assert.Equal("gpt-4.1-mini", entry.GetProperty("model").GetString());
            Assert.Equal("CURRENT EVENT\\nGale: Hello", entry.GetProperty("userContent").GetString());
            Assert.Equal("{\"action\":\"silent\",\"text\":null}", entry.GetProperty("responseContent").GetString());
            Assert.False(entry.TryGetProperty("systemPrompt", out _));
            Assert.Equal(
                ["file:ai-system-prompt.txt", "comment-frequency:Low"],
                entry.GetProperty("systemInstructions").EnumerateArray().Select(value => value.GetString()!).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
