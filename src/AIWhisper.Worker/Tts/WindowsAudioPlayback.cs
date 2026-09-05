using NAudio.Wave;

namespace AIWhisper.Worker.Tts;

public interface IAudioPlayback
{
    Task PlayAsync(string filePath, CancellationToken cancellationToken);
    Task PlayUnprocessedAsync(string filePath, float volume, int durationMs, CancellationToken cancellationToken);
}

/// <summary>
/// Plays a local audio file through Windows' current default output device.
/// NAudio uses Windows' default audio output device and supports MP3 without
/// relying on a separate media-player process or the legacy MCI driver.
/// </summary>
public sealed class WindowsAudioPlayback : IAudioPlayback
{
    private readonly IVoiceEffectProcessor _voiceEffectProcessor;

    public WindowsAudioPlayback(IVoiceEffectProcessor? voiceEffectProcessor = null)
    {
        _voiceEffectProcessor = voiceEffectProcessor ?? new PsychicDoubleVoiceEffectProcessor(new());
    }

    public Task PlayAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows audio playback is only available on Windows.");
        }

        return Task.Run(() => Play(filePath, applyVoiceEffects: true, volume: 1.0f, cancellationToken), cancellationToken);
    }

    public Task PlayUnprocessedAsync(string filePath, float volume, int durationMs, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows audio playback is only available on Windows.");
        }

        return Task.Run(() => Play(filePath, applyVoiceEffects: false, volume, durationMs, cancellationToken), cancellationToken);
    }

    private void Play(string filePath, bool applyVoiceEffects, float volume, CancellationToken cancellationToken)
        => Play(filePath, applyVoiceEffects, volume, durationMs: 0, cancellationToken);

    private void Play(string filePath, bool applyVoiceEffects, float volume, int durationMs, CancellationToken cancellationToken)
    {
        using var audioFile = new AudioFileReader(filePath);
        using var output = new WaveOutEvent();
        audioFile.Volume = Math.Clamp(volume, 0.0f, 1.0f);
        output.Init(applyVoiceEffects ? _voiceEffectProcessor.Apply(audioFile) : audioFile);
        output.Play();

        var startedAt = Environment.TickCount64;
        while (output.PlaybackState == PlaybackState.Playing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (durationMs > 0 && Environment.TickCount64 - startedAt >= durationMs)
            {
                output.Stop();
                break;
            }
            Thread.Sleep(100);
        }
    }
}
