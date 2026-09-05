namespace DialogExtractor.Worker.Tts;

public sealed record VoiceContext(string CampaignId, string DialogueId, string? Speaker, string? Voice);

public sealed record AudioResult(string FilePath, string Format);

/// <summary>
/// Abstraction over the TTS provider. The dialogue/AI pipeline never depends
/// on a concrete provider, so swapping Event Lab for something else later
/// requires no change outside a new implementation of this interface.
/// </summary>
public interface ITextToSpeech
{
    Task<AudioResult> SynthesizeAsync(string text, VoiceContext context, CancellationToken cancellationToken);
}
