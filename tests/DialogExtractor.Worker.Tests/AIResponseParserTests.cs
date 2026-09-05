using DialogExtractor.Worker.AI;
using Xunit;

namespace DialogExtractor.Worker.Tests;

public class AIResponseParserTests
{
    [Fact]
    public void Silent_ParsesWithNoText()
    {
        var ok = AIResponseParser.TryParse("""{"action":"silent"}""", out var decision, out _);

        Assert.True(ok);
        Assert.Equal(AIDecisionAction.Silent, decision!.Action);
        Assert.Null(decision.Text);
    }

    [Fact]
    public void Speak_ParsesWithText()
    {
        var ok = AIResponseParser.TryParse("""{"action":"speak","text":"Well, that was unexpected."}""", out var decision, out _);

        Assert.True(ok);
        Assert.Equal(AIDecisionAction.Speak, decision!.Action);
        Assert.Equal("Well, that was unexpected.", decision.Text);
    }

    [Fact]
    public void Speak_WithoutText_IsRejected()
    {
        var ok = AIResponseParser.TryParse("""{"action":"speak"}""", out var decision, out var error);

        Assert.False(ok);
        Assert.Null(decision);
        Assert.NotNull(error);
    }

    [Fact]
    public void UnknownAction_IsRejected()
    {
        var ok = AIResponseParser.TryParse("""{"action":"shrug"}""", out var decision, out var error);

        Assert.False(ok);
        Assert.Null(decision);
        Assert.Contains("shrug", error);
    }

    [Fact]
    public void MalformedJson_IsRejectedNotThrown()
    {
        var exception = Record.Exception(() => AIResponseParser.TryParse("I think I'll stay silent...", out _, out _));

        Assert.Null(exception);
        Assert.False(AIResponseParser.TryParse("I think I'll stay silent...", out _, out var error));
        Assert.NotNull(error);
    }
}
