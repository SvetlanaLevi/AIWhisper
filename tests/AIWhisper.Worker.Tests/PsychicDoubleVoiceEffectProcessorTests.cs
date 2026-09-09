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
    public void Apply_WithDistance_AttenuatesTheDirectVoice()
    {
        var source = new ArraySampleProvider(
            Enumerable.Repeat(1f, 1_000).ToArray(),
            WaveFormat.CreateIeeeFloatWaveFormat(44_100, 1));
        var processor = new PsychicDoubleVoiceEffectProcessor(new VoiceEffectsOptions
        {
            Enabled = true,
            Distance = 1f,
            DelayMix = 0,
        });
        var output = new float[1_000];

        Assert.Equal(output.Length, processor.Apply(source).Read(output, 0, output.Length));
        Assert.InRange(output[^1], 0.54f, 0.56f);
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

    [Fact]
    public void Apply_ReverbTail_RemainsAudibleAfterOneSecondAndThenEnds()
    {
        var source = new ArraySampleProvider([1f], WaveFormat.CreateIeeeFloatWaveFormat(1_000, 1));
        var processor = new PsychicDoubleVoiceEffectProcessor(new VoiceEffectsOptions
        {
            Enabled = true,
            DelayMs = 100,
            DelayMix = 0.20f,
            ReverbMix = 0.20f,
            ReverbDecay = 0.72f,
            HighPassHz = 0,
            LowPassHz = 0,
            PitchShiftSemitones = 0,
        });
        var output = processor.Apply(source);
        var samples = new List<float>();
        var buffer = new float[128];
        int read;
        while ((read = output.Read(buffer, 0, buffer.Length)) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        Assert.True(samples.Count > 1_000, $"Expected a tail longer than one second, got {samples.Count} samples.");
        Assert.True(Math.Abs(samples[1_000]) > 0.00005f);
        Assert.Equal(0f, samples[^1]);
    }

    [Fact]
    public void Apply_AfterEffectTail_AddsDeviceDrainSilence()
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
        var samples = new List<float>();
        var buffer = new float[128];
        int read;
        while ((read = output.Read(buffer, 0, buffer.Length)) > 0)
            samples.AddRange(buffer.AsSpan(0, read).ToArray());

        Assert.Equal(351, samples.Count);
        Assert.Equal(0.20f, samples[100], precision: 5);
        Assert.All(samples.Skip(101), sample => Assert.Equal(0f, sample));
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
