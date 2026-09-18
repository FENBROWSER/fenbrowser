using System.Buffers.Binary;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Codecs;

/// <summary>
/// Linear PCM "decoding": integer and float sample formats become the pipeline's
/// interleaved float blocks. Managed, allocation-free apart from the pooled output.
/// </summary>
public sealed class PcmDecoderFactory : IDecoderFactory<AudioBlock>
{
    public static readonly PcmDecoderFactory Instance = new();

    public string Name => "pcm";

    public bool IsHardwareAccelerated => false;

    public int Priority => 0;

    public DecoderSupport Supports(CodecConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Kind != MediaTrackKind.Audio || config.Codec != MediaCodec.Pcm)
            return DecoderSupport.Unsupported;

        // A canPlayType probe carries only the codecs value (WAVE format tag 1 or 3, RFC
        // 2361), never a sample format: the demuxer fills that in for a real track and
        // refuses any tag or width it cannot name, so the probe can answer for the family.
        bool isProbe = config.SampleRate == 0 && config.PcmFormat == PcmSampleFormat.None;
        return isProbe || config.PcmFormat != PcmSampleFormat.None
            ? DecoderSupport.Supported
            : DecoderSupport.Unsupported;
    }

    public IMediaDecoder<AudioBlock> Create(MediaPipelineContext context) => new PcmDecoder(context);
}

public sealed class PcmDecoder : IMediaDecoder<AudioBlock>
{
    private readonly MediaPipelineContext _context;
    private CodecConfig? _config;
    private int _bytesPerSample;

    public PcmDecoder(MediaPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    public string Name => "pcm";

    public ValueTask ConfigureAsync(CodecConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();
        _bytesPerSample = config.PcmFormat switch
        {
            PcmSampleFormat.U8 => 1,
            PcmSampleFormat.S16 => 2,
            PcmSampleFormat.S24 => 3,
            PcmSampleFormat.S32 => 4,
            PcmSampleFormat.F32 => 4,
            PcmSampleFormat.F64 => 8,
            _ => throw new MediaDecoderException($"Unsupported PCM format {config.PcmFormat}."),
        };
        _context.Limits.CheckAudioFormat(config.SampleRate, config.Channels);
        _config = config;
        return ValueTask.CompletedTask;
    }

    public ValueTask DecodeAsync(EncodedPacket packet, IDecodeOutput<AudioBlock> output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(output);
        var config = _config ?? throw new InvalidOperationException("ConfigureAsync has not run.");
        cancellationToken.ThrowIfCancellationRequested();

        int frameBytes = _bytesPerSample * config.Channels;
        int frames = packet.Length / frameBytes;
        if (frames == 0)
            return ValueTask.CompletedTask;

        var block = AudioBlock.Allocate(_context.Limits, config.SampleRate, config.Channels, frames, packet.Pts);
        try
        {
            Convert(packet.Span[..(frames * frameBytes)], block.Samples, config.PcmFormat);
        }
        catch
        {
            block.Dispose();
            throw;
        }

        output.Emit(block);
        return ValueTask.CompletedTask;
    }

    /// <summary>Little-endian samples to floats in [-1, 1] (WAVE stores integer PCM little-endian).</summary>
    internal static void Convert(ReadOnlySpan<byte> input, Span<float> output, PcmSampleFormat format)
    {
        switch (format)
        {
            case PcmSampleFormat.U8:
                for (int i = 0; i < output.Length; i++)
                    output[i] = (input[i] - 128) / 128f;
                break;
            case PcmSampleFormat.S16:
                for (int i = 0; i < output.Length; i++)
                    output[i] = BinaryPrimitives.ReadInt16LittleEndian(input[(i * 2)..]) / 32768f;
                break;
            case PcmSampleFormat.S24:
                for (int i = 0; i < output.Length; i++)
                {
                    int o = i * 3;
                    int sample = (input[o] | (input[o + 1] << 8) | (input[o + 2] << 16)) << 8 >> 8;
                    output[i] = sample / 8388608f;
                }

                break;
            case PcmSampleFormat.S32:
                for (int i = 0; i < output.Length; i++)
                    output[i] = BinaryPrimitives.ReadInt32LittleEndian(input[(i * 4)..]) / 2147483648f;
                break;
            case PcmSampleFormat.F32:
                for (int i = 0; i < output.Length; i++)
                    output[i] = Math.Clamp(BinaryPrimitives.ReadSingleLittleEndian(input[(i * 4)..]), -1f, 1f);
                break;
            case PcmSampleFormat.F64:
                for (int i = 0; i < output.Length; i++)
                    output[i] = (float)Math.Clamp(BinaryPrimitives.ReadDoubleLittleEndian(input[(i * 8)..]), -1.0, 1.0);
                break;
            default:
                throw new MediaDecoderException($"Unsupported PCM format {format}.");
        }
    }

    public ValueTask DrainAsync(IDecodeOutput<AudioBlock> output, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask ResetAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
