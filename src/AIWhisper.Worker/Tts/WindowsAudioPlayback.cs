using NAudio.Wave;

namespace AIWhisper.Worker.Tts;

public interface IAudioPlayback
{
    Task PlayAsync(string filePath, CancellationToken cancellationToken);
}

/// <summary>
/// Plays a local audio file through Windows' current default output device.
/// NAudio uses Windows' default audio output device and supports MP3 without
/// relying on a separate media-player process or the legacy MCI driver.
/// </summary>
public sealed class WindowsAudioPlayback : IAudioPlayback
{
    public Task PlayAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows audio playback is only available on Windows.");
        }

        return Task.Run(() => Play(filePath, cancellationToken), cancellationToken);
    }

    private static void Play(string filePath, CancellationToken cancellationToken)
    {
        using var audioFile = new AudioFileReader(filePath);
        using var output = new WaveOutEvent();
        output.Init(audioFile);
        output.Play();

        while (output.PlaybackState == PlaybackState.Playing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(100);
        }
    }
}
