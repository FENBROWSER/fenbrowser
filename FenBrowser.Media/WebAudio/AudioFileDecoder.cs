using FenBrowser.Media.Buffers;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.WebAudio;

/// <summary>A whole audio resource decoded to planar samples.</summary>
public sealed record DecodedAudio(float[][] Channels, float SampleRate)
{
    public int Length => Channels.Length == 0 ? 0 : Channels[0].Length;
}

/// <summary>
/// WA 1.1.5 decodeAudioData (design WA-D7): the first audio track of an in-memory resource,
/// demuxed and decoded through the same registries (and the same fuzzed parsers) the media
/// element uses, then resampled to the context's rate.
/// </summary>
public static class AudioFileDecoder
{
    /// <summary>Why a decode failed; the realm rejects with EncodingError either way.</summary>
    public sealed class DecodeFailedException : Exception
    {
        public DecodeFailedException(string message)
            : base(message)
        {
        }

        public DecodeFailedException()
        {
        }

        public DecodeFailedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    public static async Task<DecodedAudio> DecodeAsync(
        ReadOnlyMemory<byte> data,
        float targetSampleRate,
        DemuxerRegistry demuxers,
        DecoderRegistry decoders,
        MediaPipelineContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(demuxers);
        ArgumentNullException.ThrowIfNull(decoders);
        ArgumentNullException.ThrowIfNull(context);

        var factory = demuxers.Select(data.Span[..Math.Min(data.Length, 4096)], null, context)
            ?? throw new DecodeFailedException("The data is not in a container format this engine reads.");

        try
        {
            await using var demuxer = factory.Create(new MemoryByteSource(data), context);
            var info = await demuxer.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var track = info.Tracks.FirstOrDefault(t => t.Kind == MediaTrackKind.Audio)
                ?? throw new DecodeFailedException("The resource has no audio track.");

            var decoderFactory = decoders.GetAudioCandidates(track.Config).FirstOrDefault()
                ?? throw new DecodeFailedException("No decoder handles the audio track's codec.");
            await using var decoder = decoderFactory.Create(context);
            await decoder.ConfigureAsync(track.Config, cancellationToken).ConfigureAwait(false);

            var sink = new Sink();
            while (await demuxer.ReadPacketAsync(cancellationToken).ConfigureAwait(false) is { } packet)
            {
                using (packet)
                {
                    if (packet.TrackId == track.Id)
                        await decoder.DecodeAsync(packet, sink, cancellationToken).ConfigureAwait(false);
                }

                sink.ThrowIfTooLarge();
            }

            await decoder.DrainAsync(sink, cancellationToken).ConfigureAwait(false);
            sink.ThrowIfTooLarge();
            if (sink.Frames == 0)
                throw new DecodeFailedException("The audio track decoded to nothing.");

            var decoded = sink.ToPlanar();
            if (sink.SampleRate == targetSampleRate)
                return new DecodedAudio(decoded, targetSampleRate);

            long resampledLength = (long)Math.Ceiling(decoded[0].Length * (double)targetSampleRate / sink.SampleRate);
            if (resampledLength * decoded.Length > WebAudioLimits.MaxBufferSamples)
                throw new DecodeFailedException("The decoded audio is larger than the engine allows.");

            var resampled = new float[decoded.Length][];
            for (int c = 0; c < decoded.Length; c++)
                resampled[c] = Resampler.Resample(decoded[c], sink.SampleRate, targetSampleRate);
            return new DecodedAudio(resampled, targetSampleRate);
        }
        catch (Exception ex) when (ex is MediaFormatException or MediaDecoderException or MediaLimitExceededException)
        {
            throw new DecodeFailedException(ex.Message, ex);
        }
    }

    // Collects interleaved decoder output; the channel count and rate come from the first block.
    private sealed class Sink : IDecodeOutput<AudioBlock>
    {
        private readonly List<float[]> _chunks = [];

        public int Channels { get; private set; }

        public int SampleRate { get; private set; }

        public long Frames { get; private set; }

        public void Emit(AudioBlock item)
        {
            using (item)
            {
                if (Channels == 0)
                {
                    Channels = item.Channels;
                    SampleRate = item.SampleRate;
                }

                if (item.Channels != Channels || item.FrameCount == 0)
                    return;
                _chunks.Add(item.Samples[..(item.FrameCount * item.Channels)].ToArray());
                Frames += item.FrameCount;
            }
        }

        public void ThrowIfTooLarge()
        {
            if (Frames * Math.Max(1, Channels) > WebAudioLimits.MaxBufferSamples)
                throw new DecodeFailedException("The decoded audio is larger than the engine allows.");
        }

        public float[][] ToPlanar()
        {
            var planar = new float[Channels][];
            for (int c = 0; c < Channels; c++)
                planar[c] = new float[Frames];
            long frame = 0;
            foreach (var chunk in _chunks)
            {
                int frames = chunk.Length / Channels;
                for (int i = 0; i < frames; i++)
                {
                    for (int c = 0; c < Channels; c++)
                        planar[c][frame + i] = chunk[i * Channels + c];
                }

                frame += frames;
            }

            return planar;
        }
    }
}

/// <summary>
/// Sample-rate conversion by windowed sinc: 32 zero crossings each side under a Blackman
/// window, with the cut-off lowered to the target's Nyquist when converting down so nothing
/// above it folds back.
/// </summary>
public static class Resampler
{
    private const int ZeroCrossings = 32;

    public static float[] Resample(float[] input, double fromRate, double toRate)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (fromRate == toRate)
            return (float[])input.Clone();

        double ratio = toRate / fromRate;
        int outputLength = (int)Math.Ceiling(input.Length * ratio);
        var output = new float[outputLength];
        double cutoff = Math.Min(1.0, ratio);
        double halfWidth = ZeroCrossings / cutoff;

        for (int n = 0; n < outputLength; n++)
        {
            double center = n / ratio;
            int first = (int)Math.Ceiling(center - halfWidth);
            int last = (int)Math.Floor(center + halfWidth);
            double sum = 0;
            for (int k = Math.Max(0, first); k <= Math.Min(input.Length - 1, last); k++)
            {
                double x = k - center;
                double t = x * cutoff;
                double sinc = t == 0 ? 1 : Math.Sin(Math.PI * t) / (Math.PI * t);
                double w = (x + halfWidth) / (2 * halfWidth);
                double window = 0.42 - 0.5 * Math.Cos(2 * Math.PI * w) + 0.08 * Math.Cos(4 * Math.PI * w);
                sum += input[k] * sinc * window * cutoff;
            }

            output[n] = (float)sum;
        }

        return output;
    }
}
