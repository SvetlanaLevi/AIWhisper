using DialogExtractor.Worker.EventProcessing;
using Xunit;

namespace DialogExtractor.Worker.Tests;

public class EventParserTests
{
    private const string CampaignId = "1788474414";

    [Fact]
    public void ValidLine_ParsesEnvelopeAndData()
    {
        var json = """
        {"schemaVersion":1,"campaignId":"1788474414","timestamp":"2026-09-05 17:13:06.8766646","source":"client","type":"dialogue.line","data":{"dialogueIndex":1,"text":"Go ahead, I'm listening.","dialogueId":"5392","speaker":"Gale"}}
        """;

        var ok = EventParser.TryParse(json, CampaignId, out var evt, out var error);

        Assert.True(ok, error?.Message);
        Assert.NotNull(evt);
        Assert.Equal("client", evt!.Source);
        Assert.Equal("dialogue.line", evt.Type);
        Assert.Equal("5392", evt.DialogueId);
        Assert.Equal(1, evt.SchemaVersion);
    }

    [Fact]
    public void MalformedJson_ReturnsError_DoesNotThrow()
    {
        var ok = EventParser.TryParse("{ not valid json", CampaignId, out var evt, out var error);

        Assert.False(ok);
        Assert.Null(evt);
        Assert.Equal(EventParseErrorKind.MalformedJson, error!.Kind);
    }

    [Fact]
    public void CampaignIdMismatch_IsRejected()
    {
        var json = """{"schemaVersion":1,"campaignId":"OTHER","timestamp":"2026-09-05 17:13:06.0000000","source":"client","type":"dialogue.line","data":{"dialogueId":"1"}}""";

        var ok = EventParser.TryParse(json, CampaignId, out var evt, out var error);

        Assert.False(ok);
        Assert.Null(evt);
        Assert.Equal(EventParseErrorKind.CampaignIdMismatch, error!.Kind);
    }

    [Fact]
    public void InvalidTimestamp_IsRejected()
    {
        var json = """{"schemaVersion":1,"campaignId":"1788474414","timestamp":"not-a-date","source":"server","type":"session.start","data":{}}""";

        var ok = EventParser.TryParse(json, CampaignId, out _, out var error);

        Assert.False(ok);
        Assert.Equal(EventParseErrorKind.InvalidTimestamp, error!.Kind);
    }

    [Fact]
    public void UnknownEventType_StillParsesSuccessfully()
    {
        // The parser only validates the envelope; classifying/ignoring an
        // unknown type is the DialogueAggregator's job, not the parser's.
        var json = """{"schemaVersion":1,"campaignId":"1788474414","timestamp":"2026-09-05 17:13:06.0000000","source":"server","type":"some.future.event","data":{"foo":"bar","nested":{"baz":1}}}""";

        var ok = EventParser.TryParse(json, CampaignId, out var evt, out var error);

        Assert.True(ok, error?.Message);
        Assert.Equal("some.future.event", evt!.Type);
    }

    [Fact]
    public void SessionStart_HasNoDialogueId()
    {
        var json = """{"schemaVersion":1,"campaignId":"1788474414","timestamp":"2026-09-05 17:12:00.0000000","source":"server","type":"session.start","data":{"player":"Victoria","region":"WLD_Main_A"}}""";

        var ok = EventParser.TryParse(json, CampaignId, out var evt, out var error);

        Assert.True(ok, error?.Message);
        Assert.Null(evt!.DialogueId);
    }

    [Fact]
    public void MissingCampaignId_IsRejected()
    {
        var json = """{"schemaVersion":1,"timestamp":"2026-09-05 17:12:00.0000000","source":"server","type":"session.start","data":{}}""";

        var ok = EventParser.TryParse(json, CampaignId, out _, out var error);

        Assert.False(ok);
        Assert.Equal(EventParseErrorKind.MissingRequiredField, error!.Kind);
    }
}
