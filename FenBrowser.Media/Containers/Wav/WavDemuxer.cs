using System.Buffers.Binary;
using System.Globalization;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Containers.Wav;

/// <summary>
/// RIFF/WAVE (Multimedia Programming Interface and Data Specification 1.0, "WAVE Form";
/// WAVE_FORMAT_EXTENSIBLE per Microsoft's "Multiple Channel Audio Data and WAVE Files").
/// The only chunks that matter are <c>fmt </c> and <c>data</c>; everything else is skipped
/// by its declared size. PCM integer and IEEE float samples are supported.
/// </summary>
public sealed class WavDemuxerFactory : IDemuxerFactory
{
    public static readonly WavDemuxerFactory Instance = new();

    public string Name => "wav";

    public IReadOnlyList<string> MimeTypes { get; } = ["audio/wav", "audio/wave", "audio/x-wav", "audio/vnd.wave"];

    /// <summary>"RIFF" .... "WAVE" is the whole signature (MIME Sniffing §6.2 pattern table).</summary>
    public int Probe(ReadOnlySpan<byte> header)
    {
        if (header.Length < 12)
            return 0;
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8))
            return 0;
        return 100;
    }

    public IDemuxer Create(IByteSource source, MediaPipelineContext context) => new WavDemuxer(source, context);
}

public sealed class WavDemuxer : IDemuxer
{
    // Header parsing reads at most this much; a chunk list longer than this is not a
    // WAV file anyone plays, and it bounds hostile chunk walks.
    private const int MaxHeaderBytes = 1 << 20;

    // Packets of about 20 ms keep the audio queue fine-grained without a packet per sample.
    private const int FramesPerPacket = 1024;

    private const ushort FormatPcm = 0x0001;
    private const ushort FormatIeeeFloat = 0x0003;
    private const ushort FormatExtensible = 0xFFFE;

    private readonly IByteSource _source;
    private readonly MediaPipelineContext _context;
    private readonly byte[] _scratch = new byte[12];

    private CodecConfig? _config;
    private int _blockAlign;
    private long _dataStart;
    private long _dataLength;
    private long _nextFrame;
    private long _totalFrames;

    public WavDemuxer(IByteSource source, MediaPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);
        _source = source;
        _context = context;
    }

    public async ValueTask<DemuxerInfo> InitializeAsync(CancellationToken cancellationToken)
    {
        // RIFF header: "RIFF" <size> "WAVE".
        if (await _source.ReadAtLeastAsync(0, _scratch.AsMemory(0, 12), cancellationToken).ConfigureAwait(false) < 12 ||
            !_scratch.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !_scratch.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new MediaFormatException("Not a RIFF/WAVE resource.");
        }

        long riffEnd = 8L + BinaryPrimitives.ReadUInt32LittleEndian(_scratch.AsSpan(4));
        long limit = _source.Length is { } length ? Math.Min(length, riffEnd) : riffEnd;
        long position = 12;
        bool haveFormat = false;

        while (position + 8 <= limit && position < MaxHeaderBytes)
        {
            if (await _source.ReadAtLeastAsync(position, _scratch.AsMemory(0, 8), cancellationToken).ConfigureAwait(false) < 8)
                break;

            var chunkId = _scratch.AsSpan(0, 4);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(_scratch.AsSpan(4));
            long chunkStart = position + 8;

            if (chunkId.SequenceEqual("fmt "u8))
            {
                if (chunkSize < 16 || chunkSize > 1024)
                    throw new MediaFormatException($"The fmt chunk is {chunkSize} bytes; expected 16 to 1024.");
                await ParseFormatAsync(chunkStart, (int)chunkSize, cancellationToken).ConfigureAwait(false);
                haveFormat = true;
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                if (!haveFormat)
                    throw new MediaFormatException("The data chunk precedes the fmt chunk.");

                _dataStart = chunkStart;
                // A streamed WAV may declare 0 or 0xFFFFFFFF; the resource length then decides.
                long available = _source.Length is { } total ? Math.Max(0, total - chunkStart) : long.MaxValue;
                _dataLength = chunkSize is 0 or uint.MaxValue ? available : Math.Min(chunkSize, available);
                break;
            }

            // Chunks are word-aligned: an odd size is followed by a pad byte.
            position = chunkStart + chunkSize + (chunkSize & 1);
        }

        if (!haveFormat || _config is null)
            throw new MediaFormatException("No fmt chunk.");
        if (_dataStart == 0)
            throw new MediaFormatException("No data chunk.");

        _totalFrames = _dataLength / _blockAlign;
        var duration = MediaTime.FromTimescale(_totalFrames, _config.SampleRate);
        var track = new MediaTrackInfo(0, _config, duration, IsDefault: true);
        _context.Log.Emit(_context.Player, MediaEventKind.TrackAdded, MediaLogLevel.Info,
            $"WAV: {_config.PcmFormat} {_config.SampleRate} Hz {_config.Channels} ch, {_totalFrames} frames.",
            ("track", "0"), ("codec", "pcm"), ("format", _config.PcmFormat.ToString()),
            ("sampleRate", _config.SampleRate.ToString(CultureInfo.InvariantCulture)),
            ("channels", _config.Channels.ToString(CultureInfo.InvariantCulture)));
        return new DemuxerInfo([track], duration, IsSeekable: true);
    }

    private async ValueTask ParseFormatAsync(long start, int size, CancellationToken cancellationToken)
    {
        byte[] fmt = new byte[size];
        if (await _source.ReadAtLeastAsync(start, fmt, cancellationToken).ConfigureAwait(false) < size)
            throw new MediaFormatException("The fmt chunk is truncated.");

        ushort formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(0));
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
        int sampleRate = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4)));
        int blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(12));
        int bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));

        if (formatTag == FormatExtensible)
        {
            // WAVEFORMATEXTENSIBLE: cbSize >= 22, then wValidBitsPerSample, dwChannelMask,
            // SubFormat GUID whose first two bytes are the classic format tag.
            if (size < 40)
                throw new MediaFormatException("WAVE_FORMAT_EXTENSIBLE fmt chunk is too short.");
            int validBits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(18));
            formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(24));
            if (validBits > 0 && validBits < bitsPerSample)
            {
                // Samples are container-sized; the decoder reads the container width.
                _context.Log.Emit(_context.Player, MediaEventKind.TrackAdded, MediaLogLevel.Debug,
                    $"WAV: {validBits} valid bits in {bitsPerSample}-bit containers.");
            }
        }

        _context.Limits.CheckAudioFormat(sampleRate, channels);

        var pcmFormat = (formatTag, bitsPerSample) switch
        {
            (FormatPcm, 8) => PcmSampleFormat.U8,
            (FormatPcm, 16) => PcmSampleFormat.S16,
            (FormatPcm, 24) => PcmSampleFormat.S24,
            (FormatPcm, 32) => PcmSampleFormat.S32,
            (FormatIeeeFloat, 32) => PcmSampleFormat.F32,
            (FormatIeeeFloat, 64) => PcmSampleFormat.F64,
            _ => throw new MediaFormatException($"Unsupported WAVE format tag 0x{formatTag:X4} with {bitsPerSample} bits per sample."),
        };

        int expectedAlign = channels * (bitsPerSample / 8);
        if (blockAlign != expectedAlign)
            throw new MediaFormatException($"Block align {blockAlign} does not match {channels} channels of {bitsPerSample} bits.");

        _blockAlign = blockAlign;
        _config = new CodecConfig(
            MediaTrackKind.Audio,
            MediaCodec.Pcm,
            CodecString: "1",
            SampleRate: sampleRate,
            Channels: channels,
            PcmFormat: pcmFormat);
    }

    public async ValueTask<EncodedPacket?> ReadPacketAsync(CancellationToken cancellationToken)
    {
        var config = _config ?? throw new InvalidOperationException("InitializeAsync has not run.");
        if (_nextFrame >= _totalFrames)
            return null;

        long frames = Math.Min(FramesPerPacket, _totalFrames - _nextFrame);
        int bytes = checked((int)(frames * _blockAlign));
        var packet = EncodedPacket.Rent(
            _context.Limits,
            MediaTrackKind.Audio,
            trackId: 0,
            bytes,
            pts: MediaTime.FromTimescale(_nextFrame, config.SampleRate),
            dts: MediaTime.FromTimescale(_nextFrame, config.SampleRate),
            duration: MediaTime.FromTimescale(frames, config.SampleRate),
            isKeyframe: true);
        try
        {
            int read = await _source.ReadAtLeastAsync(_dataStart + _nextFrame * _blockAlign, packet.Memory, cancellationToken).ConfigureAwait(false);
            if (read < bytes)
            {
                // The resource ended early: play what is whole and stop.
                long wholeFrames = read / _blockAlign;
                _totalFrames = _nextFrame + wholeFrames;
                if (wholeFrames == 0)
                {
                    packet.Dispose();
                    return null;
                }

                var shorter = EncodedPacket.Copy(
                    _context.Limits, MediaTrackKind.Audio, 0, packet.Span[..(int)(wholeFrames * _blockAlign)],
                    packet.Pts, packet.Dts, MediaTime.FromTimescale(wholeFrames, config.SampleRate), isKeyframe: true);
                packet.Dispose();
                _nextFrame += wholeFrames;
                return shorter;
            }
        }
        catch
        {
            packet.Dispose();
            throw;
        }

        _nextFrame += frames;
        return packet;
    }

    /// <summary>Every PCM frame is a keyframe, so a seek lands exactly on the target frame.</summary>
    public ValueTask SeekAsync(MediaTime target, CancellationToken cancellationToken)
    {
        var config = _config ?? throw new InvalidOperationException("InitializeAsync has not run.");
        cancellationToken.ThrowIfCancellationRequested();
        long frame = target <= MediaTime.Zero ? 0 : target.ToTimescale(config.SampleRate);
        _nextFrame = Math.Clamp(frame, 0, _totalFrames);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
