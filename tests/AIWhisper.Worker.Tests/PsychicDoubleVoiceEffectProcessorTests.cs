using AIWhisper.Worker.Configuration;
using AIWhisper.Worker.Tts;
using NAudio.Wave;
using Xunit;

namespace AIWhisper.Worker.Tests;

public sealed class PsychicDoubleVoiceEffectProcessorTests
{
    [Fact]
    public void Apply_KeepsOriginalAndAddsQuietDelayedCopy()
    {
        var sourceSamples = new float[101];
        sourceSamples[0] = 1f;
        var source = new ArraySampleProvider(sourceSamples, WaveFormat.CreateIeeeFloatWaveFormat(1_000, 1));
        var processor = new PsychicDoubleVoiceEffectProcessor(new VoiceEffectsOptions
        {
            Enabled = true,
            DelayMs = 100,
            DelayMix = 0.20f,
            ReverbMix = 0,
            HighPassHz = 0,
            LowPassHz = 0,
            PitchShiftSemitones = 0,
        });

        var output = new float[sourceSamples.Length];
        var samplesRead = processor.Apply(source).Read(output, 0, output.Length);

        Assert.Equal(sourceSamples.Length, samplesRead);
        Assert.Equal(1f, output[0]);
        Assert.Equal(0.20f, output[100], precision: 5);
    }

    [Fact]
    public void Apply_WhenDisabled_ReturnsOriginalProvider()
    {
        var source = new ArraySampleProvider([1f], WaveFormat.CreateIeeeFloatWaveFormat(1_000, 1));
        var processor = new PsychicDoubleVoiceEffectProcessor(new VoiceEffectsOptions { Enabled = false });

        Assert.Same(source, processor.Apply(source));
    }

    [Fact]
    public void Apply_WithPitchShift_LeavesMainVoiceUnchanged()
    {
        var source = new ArraySampleProvider([1f], WaveFormat.CreateIeeeFloatWaveFormat(1_000, 1));
        var processor = new PsychicDoubleVoiceEffectProcessor(new VoiceEffectsOptions
        {
            Enabled = true,
            DelayMs = 100,
            DelayMix = 0.20f,
            ReverbMix = 0,
            HighPassHz = 0,
            LowPassHz = 0,
            PitchShiftSemitones = -1f,
        });

        var output = new float[1];
        processor.Apply(source).Read(output, 0, output.Length);

        Assert.Equal(1f, output[0]);
    }

    [Fact]
    public void Apply_AfterSourceEnds_FlushesDelayedShadow()
    {
        var source = new ArraySampleProvider([1f], WaveFormat.CreateIeeeFloatWaveFormat(1_000, 1));
        var processor = new PsychicDoubleVoiceEffectProcessor(new VoiceEffectsOptions
        {
            Enabled = true,
            DelayMs = 100,
            DelayMix = 0.20f,
            ReverbMix = 0,
            HighPassHz = 0,
            LowPassHz = 0,
            PitchShiftSemitones = 0,
        });
        var output = processor.Apply(source);

        var first = new float[1];
        Assert.Equal(1, output.Read(first, 0, first.Length));

        var tail = new float[100];
        Assert.Equal(tail.Length, output.Read(tail, 0, tail.Length));
        Assert.Equal(0.20f, tail[^1], precision: 5);
    }

    private sealed class ArraySampleProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public ArraySampleProvider(float[] samples, WaveFormat waveFormat)
        {
            _samples = samples;
            WaveFormat = waveFormat;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var available = Math.Min(count, _samples.Length - _position);
            Array.Copy(_samples, _position, buffer, offset, available);
            _position += available;
            return available;
        }
    }
}
