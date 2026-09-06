using AIWhisper.Worker.Configuration;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AIWhisper.Worker.Tts;

public interface IVoiceEffectProcessor
{
    ISampleProvider Apply(ISampleProvider source);
}

/// <summary>
/// Keeps the original signal intact and layers a quiet, single delayed copy
/// underneath it. The result is a restrained psychic double, not a repeating
/// echo.
/// </summary>
public sealed class PsychicDoubleVoiceEffectProcessor : IVoiceEffectProcessor
{
    private readonly VoiceEffectsOptions _options;

    public PsychicDoubleVoiceEffectProcessor(VoiceEffectsOptions options)
    {
        _options = options;
    }

    public ISampleProvider Apply(ISampleProvider source)
    {
        if (!_options.Enabled)
        {
            return source;
        }

        var sharedSource = new SharedSampleSource(source);
        ISampleProvider originalVoice = sharedSource.CreateReader();
        ISampleProvider shadowVoice = sharedSource.CreateReader();

        var distance = Math.Clamp(_options.Distance, 0f, 1f);
        if (distance > 0)
        {
            // Distant sound loses direct energy and high frequencies before it
            // reaches the listener. Keep the wet shadow unchanged so the room
            // reflection becomes relatively more prominent with distance.
            var distantLowPassHz = 12_000f + (2_200f - 12_000f) * distance;
            originalVoice = new FilterSampleProvider(originalVoice, 0, distantLowPassHz);
            originalVoice = new VolumeSampleProvider(originalVoice)
            {
                Volume = 1f - 0.45f * distance,
            };
        }

        if (Math.Abs(_options.PitchShiftSemitones) > 0.001f)
        {
            shadowVoice = new SmbPitchShiftingSampleProvider(shadowVoice)
            {
                PitchFactor = (float)Math.Pow(2, _options.PitchShiftSemitones / 12d),
            };
        }

        if (_options.HighPassHz > 0 || _options.LowPassHz > 0)
        {
            shadowVoice = new FilterSampleProvider(shadowVoice, _options.HighPassHz, _options.LowPassHz);
        }

        if (_options.ReverbMix > 0)
        {
            shadowVoice = new FeedbackReverbSampleProvider(shadowVoice, _options.ReverbMix, _options.ReverbDecay);
        }

        var delayMs = Math.Clamp(_options.DelayMs, 1, 2_000);
        var delayMix = Math.Clamp(_options.DelayMix, 0f, 1f);
        if (delayMix == 0)
        {
            return originalVoice;
        }

        shadowVoice = new DelayedShadowSampleProvider(shadowVoice, delayMs, delayMix);
        return new MixingSampleProvider([originalVoice, shadowVoice]);
    }

    private sealed class FilterSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly BiQuadFilter[]? _highPassFilters;
        private readonly BiQuadFilter[]? _lowPassFilters;

        public FilterSampleProvider(ISampleProvider source, float highPassHz, float lowPassHz)
        {
            _source = source;
            var sampleRate = source.WaveFormat.SampleRate;
            var channels = source.WaveFormat.Channels;
            var nyquist = sampleRate / 2f - 1f;

            if (highPassHz > 0 && highPassHz < nyquist)
            {
                _highPassFilters = Enumerable.Range(0, channels)
                    .Select(_ => BiQuadFilter.HighPassFilter(sampleRate, highPassHz, 1f))
                    .ToArray();
            }

            if (lowPassHz > 0 && lowPassHz < nyquist)
            {
                _lowPassFilters = Enumerable.Range(0, channels)
                    .Select(_ => BiQuadFilter.LowPassFilter(sampleRate, lowPassHz, 1f))
                    .ToArray();
            }
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var samplesRead = _source.Read(buffer, offset, count);
            var channels = WaveFormat.Channels;
            for (var i = 0; i < samplesRead; i++)
            {
                var channel = i % channels;
                var sample = buffer[offset + i];
                if (_highPassFilters is not null) sample = _highPassFilters[channel].Transform(sample);
                if (_lowPassFilters is not null) sample = _lowPassFilters[channel].Transform(sample);
                buffer[offset + i] = sample;
            }

            return samplesRead;
        }
    }

    private sealed class FeedbackReverbSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly float[] _reverbBuffer;
        private readonly float _mix;
        private readonly float _decay;
        private int _position;
        private int _remainingTailSamples;

        public FeedbackReverbSampleProvider(ISampleProvider source, float mix, float decay)
        {
            _source = source;
            _mix = Math.Clamp(mix, 0f, 1f);
            _decay = Math.Clamp(decay, 0f, 0.95f);
            var sampleCount = (int)Math.Ceiling(source.WaveFormat.SampleRate * source.WaveFormat.Channels * 0.045d);
            _reverbBuffer = new float[Math.Max(1, sampleCount)];
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var samplesRead = _source.Read(buffer, offset, count);
            if (samplesRead > 0)
            {
                // Enough time for a natural decay, while HasAudibleTail stops
                // playback earlier once the tail has faded to silence.
                _remainingTailSamples = WaveFormat.SampleRate * WaveFormat.Channels * 2;
            }

            if (samplesRead < count && _remainingTailSamples > 0 && (samplesRead > 0 || HasAudibleTail()))
            {
                var tailSamples = Math.Min(count - samplesRead, _remainingTailSamples);
                Array.Clear(buffer, offset + samplesRead, tailSamples);
                samplesRead += tailSamples;
                _remainingTailSamples -= tailSamples;
            }

            for (var i = 0; i < samplesRead; i++)
            {
                var index = (_position + i) % _reverbBuffer.Length;
                var original = buffer[offset + i];
                var tail = _reverbBuffer[index];
                buffer[offset + i] = original + tail * _mix;
                _reverbBuffer[index] = original + tail * _decay;
            }

            _position = (_position + samplesRead) % _reverbBuffer.Length;
            return samplesRead;
        }

        private bool HasAudibleTail()
            => _reverbBuffer.Any(sample => Math.Abs(sample) > 0.0001f);
    }

    private sealed class DelayedShadowSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly float[] _delayedSamples;
        private readonly float _mix;
        private int _position;
        private int _remainingTailSamples;

        public DelayedShadowSampleProvider(ISampleProvider source, int delayMs, float mix)
        {
            _source = source;
            _mix = mix;
            var sampleCount = (int)Math.Ceiling(source.WaveFormat.SampleRate * source.WaveFormat.Channels * delayMs / 1_000d);
            _delayedSamples = new float[Math.Max(1, sampleCount)];
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var samplesRead = _source.Read(buffer, offset, count);
            if (samplesRead > 0)
            {
                _remainingTailSamples = _delayedSamples.Length;
            }

            if (samplesRead < count && _remainingTailSamples > 0)
            {
                var tailSamples = Math.Min(count - samplesRead, _remainingTailSamples);
                Array.Clear(buffer, offset + samplesRead, tailSamples);
                samplesRead += tailSamples;
                _remainingTailSamples -= tailSamples;
            }

            for (var i = 0; i < samplesRead; i++)
            {
                var index = (_position + i) % _delayedSamples.Length;
                var sourceSample = buffer[offset + i];
                buffer[offset + i] = _delayedSamples[index] * _mix;
                _delayedSamples[index] = sourceSample;
            }

            _position = (_position + samplesRead) % _delayedSamples.Length;
            return samplesRead;
        }
    }

    /// <summary>
    /// Lets the untouched main voice and the processed shadow consume the same
    /// stream independently without decoding the entire file into memory.
    /// </summary>
    private sealed class SharedSampleSource
    {
        private readonly ISampleProvider _source;
        private readonly List<float> _samples = [];
        private long _firstSampleIndex;
        private bool _ended;
        private Reader? _firstReader;
        private Reader? _secondReader;

        public SharedSampleSource(ISampleProvider source) => _source = source;

        public Reader CreateReader()
        {
            var reader = new Reader(this);
            if (_firstReader is null)
            {
                _firstReader = reader;
            }
            else if (_secondReader is null)
            {
                _secondReader = reader;
            }
            else
            {
                throw new InvalidOperationException("Only two readers are supported.");
            }

            return reader;
        }

        private int Read(Reader reader, float[] buffer, int offset, int count)
        {
            EnsureAvailable(reader.Position + count);
            var available = (int)Math.Min(count, _firstSampleIndex + _samples.Count - reader.Position);
            if (available <= 0)
            {
                return 0;
            }

            _samples.CopyTo((int)(reader.Position - _firstSampleIndex), buffer, offset, available);
            reader.Position += available;
            DiscardConsumedSamples();
            return available;
        }

        private void EnsureAvailable(long exclusiveEnd)
        {
            while (!_ended && _firstSampleIndex + _samples.Count < exclusiveEnd)
            {
                var readBuffer = new float[Math.Min(4_096, (int)Math.Max(1, exclusiveEnd - (_firstSampleIndex + _samples.Count)))];
                var samplesRead = _source.Read(readBuffer, 0, readBuffer.Length);
                if (samplesRead == 0)
                {
                    _ended = true;
                    break;
                }

                _samples.AddRange(readBuffer.AsSpan(0, samplesRead).ToArray());
            }
        }

        private void DiscardConsumedSamples()
        {
            if (_firstReader is null || _secondReader is null)
            {
                return;
            }

            var consumedThrough = Math.Min(_firstReader.Position, _secondReader.Position);
            var discardCount = (int)Math.Min(consumedThrough - _firstSampleIndex, _samples.Count);
            if (discardCount <= 0)
            {
                return;
            }

            _samples.RemoveRange(0, discardCount);
            _firstSampleIndex += discardCount;
        }

        public sealed class Reader : ISampleProvider
        {
            private readonly SharedSampleSource _owner;

            internal Reader(SharedSampleSource owner)
            {
                _owner = owner;
            }

            public WaveFormat WaveFormat => _owner._source.WaveFormat;
            internal long Position { get; set; }

            public int Read(float[] buffer, int offset, int count)
                => _owner.Read(this, buffer, offset, count);
        }
    }
}
