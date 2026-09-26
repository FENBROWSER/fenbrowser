namespace FenBrowser.Media.Buffers;

/// <summary>
/// Decoded audio as interleaved 32-bit float samples in [-1, 1], in a pooled buffer.
/// </summary>
/// <remarks>
/// Decoder adapters convert every sample format and planar layout to this one shape, so
/// the renderer, mixer and time-stretcher handle a single format. The buffer is zeroed on
/// return (see <see cref="EncodedPacket"/>).
/// </remarks>
public sealed class AudioBlock : IDisposable
{
    private float[]? _samples;

    private AudioBlock(float[] samples, int sampleRate, int channels, int frameCount, MediaTime timestamp)
    {
        _samples = samples;
        SampleRate = sampleRate;
        Channels = channels;
        FrameCount = frameCount;
        Timestamp = timestamp;
    }

    public int SampleRate { get; }
    public int Channels { get; }

    /// <summary>Number of sample frames (one sample per channel).</summary>
    public int FrameCount { get; }

    public MediaTime Timestamp { get; }

    public MediaTime Duration => MediaTime.FromTimescale(FrameCount, SampleRate);

    public MediaTime EndTime => Timestamp + Duration;

    /// <summary>Interleaved samples: frame <c>f</c>, channel <c>c</c> is at <c>f * Channels + c</c>.</summary>
    public Span<float> Samples
    {
        get
        {
            var samples = _samples;
            ObjectDisposedException.ThrowIf(samples is null, this);
            return samples.AsSpan(0, FrameCount * Channels);
        }
    }

    public static AudioBlock Allocate(MediaLimits limits, int sampleRate, int channels, int frameCount, MediaTime timestamp)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.CheckAudioFormat(sampleRate, channels);
        if (frameCount < 0 || frameCount > limits.MaxAudioBlockFrames)
            throw new MediaLimitExceededException(nameof(MediaLimits.MaxAudioBlockFrames), frameCount, limits.MaxAudioBlockFrames);

        float[] samples = MediaBufferPool.Floats.Rent(frameCount * channels);
        return new AudioBlock(samples, sampleRate, channels, frameCount, timestamp);
    }

    public void Dispose()
    {
        float[]? samples = Interlocked.Exchange(ref _samples, null);
        if (samples is not null)
            MediaBufferPool.Return(samples);
    }

    public override string ToString() => $"audio({Channels}ch {SampleRate}Hz, {FrameCount} frames, ts {Timestamp})";
}
