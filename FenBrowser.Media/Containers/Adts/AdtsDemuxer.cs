using System.Buffers.Binary;
using System.Globalization;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Containers.Adts;

/// <summary>An ADTS frame header (ISO/IEC 13818-7 §6.2 and ISO/IEC 14496-3 §1.A.2.2).</summary>
public readonly record struct AdtsFrameHeader(
    bool Mpeg2,
    int Profile,            // audio_object_type - 1
    int SampleRateIndex,
    int SampleRate,
    int ChannelConfiguration,
    int FrameLength,        // whole frame including the header
    int HeaderLength,       // 7, or 9 with the CRC
    int RawDataBlocks)
{
    private static readonly int[] s_sampleRates =
        [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350, 0, 0, 0];

    public const int MaxHeaderLength = 9;

    /// <summary>Samples per frame: 1024 per raw data block.</summary>
    public int Samples => 1024 * RawDataBlocks;

    public int Channels => ChannelConfiguration switch
    {
        0 => 2, // carried in the PCE; assume stereo for the track shape
        7 => 8,
        _ => ChannelConfiguration,
    };

    public static bool TryParse(ReadOnlySpan<byte> bytes, out AdtsFrameHeader header)
    {
        header = default;
        if (bytes.Length < 7 || bytes[0] != 0xFF || (bytes[1] & 0xF6) != 0xF0)
            return false; // 12-bit sync and layer 00

        bool mpeg2 = (bytes[1] & 0x08) != 0;
        bool protectionAbsent = (bytes[1] & 0x01) != 0;
        int profile = bytes[2] >> 6;
        int sampleRateIndex = (bytes[2] >> 2) & 0x0F;
        int channelConfiguration = ((bytes[2] & 0x01) << 2) | (bytes[3] >> 6);
        int frameLength = ((bytes[3] & 0x03) << 11) | (bytes[4] << 3) | (bytes[5] >> 5);
        int rawDataBlocks = (bytes[6] & 0x03) + 1;
        int headerLength = protectionAbsent ? 7 : 9;

        if (sampleRateIndex >= 13 || frameLength < headerLength)
            return false;

        header = new AdtsFrameHeader(mpeg2, profile, sampleRateIndex, s_sampleRates[sampleRateIndex], channelConfiguration, frameLength, headerLength, rawDataBlocks);
        return true;
    }

    public bool IsCompatibleWith(in AdtsFrameHeader other) =>
        Mpeg2 == other.Mpeg2 && Profile == other.Profile && SampleRateIndex == other.SampleRateIndex && ChannelConfiguration == other.ChannelConfiguration;

    /// <summary>The two-byte AudioSpecificConfig (ISO/IEC 14496-3 §1.6.2.1) a decoder is configured with.</summary>
    public byte[] AudioSpecificConfig()
    {
        int objectType = Profile + 1;
        ushort packed = (ushort)((objectType << 11) | (SampleRateIndex << 7) | (ChannelConfiguration << 3));
        var config = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(config, packed);
        return config;
    }
}

/// <summary>
/// AAC in ADTS transport: a run of frames, each with its own header and length. Frames are
/// emitted whole, timestamps count 1024 samples per raw data block, and seeks are by byte
/// fraction (ADTS carries no index).
/// </summary>
public sealed class AdtsDemuxerFactory : IDemuxerFactory
{
    public static readonly AdtsDemuxerFactory Instance = new();

    public string Name => "adts";

    public IReadOnlyList<string> MimeTypes { get; } = ["audio/aac", "audio/aacp", "audio/x-aac", "audio/mp4a-latm"];

    /// <summary>Two consecutive compatible frame headers; ADTS has no other signature.</summary>
    public int Probe(ReadOnlySpan<byte> header)
    {
        if (!AdtsFrameHeader.TryParse(header, out var first))
            return 0;
        if (header.Length < first.FrameLength + AdtsFrameHeader.MaxHeaderLength)
            return 20;
        return AdtsFrameHeader.TryParse(header[first.FrameLength..], out var second) && first.IsCompatibleWith(second) ? 70 : 0;
    }

    public IDemuxer Create(IByteSource source, MediaPipelineContext context) => new AdtsDemuxer(source, context);
}

public sealed class AdtsDemuxer : IDemuxer
{
    private const int ReadChunk = 16 * 1024;
    private const int MaxResyncBytes = 64 * 1024;
    private const int MaxId3Bytes = 64 * 1024 * 1024;

    private readonly IByteSource _source;
    private readonly MediaPipelineContext _context;
    private readonly byte[] _window = new byte[ReadChunk];
    private long _windowStart = -1;
    private int _windowLength;

    private AdtsFrameHeader _first;
    private CodecConfig? _config;
    private long _audioStart;
    private long _position;
    private long _samplesEmitted;
    private long _estimatedFrames;
    private MediaTime _duration = MediaTime.PositiveInfinity;

    public AdtsDemuxer(IByteSource source, MediaPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);
        _source = source;
        _context = context;
    }

    public async ValueTask<DemuxerInfo> InitializeAsync(CancellationToken cancellationToken)
    {
        long start = await SkipId3v2Async(0, cancellationToken).ConfigureAwait(false);
        long? firstFrame = await FindNextFrameAsync(start, cancellationToken).ConfigureAwait(false)
            ?? throw new MediaFormatException("No ADTS frame sync found.");
        var bytes = await ReadAtAsync(firstFrame.Value, AdtsFrameHeader.MaxHeaderLength, cancellationToken).ConfigureAwait(false);
        AdtsFrameHeader.TryParse(bytes.Span, out _first);
        if (_first.SampleRate == 0)
            throw new MediaFormatException("ADTS frame has a reserved sampling frequency index.");
        _context.Limits.CheckAudioFormat(_first.SampleRate, _first.Channels);

        _audioStart = firstFrame.Value;
        _position = _audioStart;
        if (_source.Length is { } length)
        {
            _estimatedFrames = Math.Max(1, (length - _audioStart) / _first.FrameLength);
            _duration = MediaTime.FromTimescale(_estimatedFrames * _first.Samples, _first.SampleRate);
        }

        _config = new CodecConfig(
            MediaTrackKind.Audio,
            MediaCodec.Aac,
            "mp4a.40." + (_first.Profile + 1).ToString(CultureInfo.InvariantCulture),
            SampleRate: _first.SampleRate,
            Channels: _first.Channels,
            Extradata: _first.AudioSpecificConfig());
        var track = new MediaTrackInfo(0, _config, _duration, IsDefault: true);
        _context.Log.Emit(_context.Player, MediaEventKind.TrackAdded, MediaLogLevel.Info,
            $"ADTS: AAC {_config.CodecString} {_first.SampleRate} Hz {_first.Channels} ch (duration estimated from the first frame).",
            ("track", "0"), ("codec", "aac"), ("codecString", _config.CodecString ?? string.Empty),
            ("sampleRate", _first.SampleRate.ToString(CultureInfo.InvariantCulture)),
            ("channels", _first.Channels.ToString(CultureInfo.InvariantCulture)));
        return new DemuxerInfo([track], _duration, IsSeekable: _source.Length is not null);
    }

    private async ValueTask<long> SkipId3v2Async(long at, CancellationToken cancellationToken)
    {
        var header = await ReadAtAsync(at, 10, cancellationToken).ConfigureAwait(false);
        if (header.Length < 10 || !header.Span[..3].SequenceEqual("ID3"u8))
            return at;
        int size = 0;
        for (int i = 6; i < 10; i++)
        {
            if ((header.Span[i] & 0x80) != 0)
                throw new MediaFormatException("ID3v2 size is not syncsafe.");
            size = (size << 7) | header.Span[i];
        }

        long total = 10L + size + ((header.Span[5] & 0x10) != 0 ? 10 : 0);
        if (total > MaxId3Bytes)
            throw new MediaFormatException($"ID3v2 tag of {total} bytes exceeds the {MaxId3Bytes} byte ceiling.");
        return await SkipId3v2Async(at + total, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<EncodedPacket?> ReadPacketAsync(CancellationToken cancellationToken)
    {
        var config = _config ?? throw new InvalidOperationException("InitializeAsync has not run.");
        while (true)
        {
            var headerBytes = await ReadAtAsync(_position, AdtsFrameHeader.MaxHeaderLength, cancellationToken).ConfigureAwait(false);
            if (headerBytes.Length < 7)
                return null;
            if (!AdtsFrameHeader.TryParse(headerBytes.Span, out var header) || !_first.IsCompatibleWith(header))
            {
                long? next = await FindNextFrameAsync(_position + 1, cancellationToken).ConfigureAwait(false);
                if (next is null)
                    return null;
                _position = next.Value;
                continue;
            }

            var frame = await ReadAtAsync(_position, header.FrameLength, cancellationToken).ConfigureAwait(false);
            if (frame.Length < header.FrameLength)
                return null;

            var packet = EncodedPacket.Copy(
                _context.Limits,
                MediaTrackKind.Audio,
                trackId: 0,
                frame.Span,
                pts: MediaTime.FromTimescale(_samplesEmitted, config.SampleRate),
                dts: MediaTime.FromTimescale(_samplesEmitted, config.SampleRate),
                duration: MediaTime.FromTimescale(header.Samples, config.SampleRate),
                isKeyframe: true);
            _samplesEmitted += header.Samples;
            _position += header.FrameLength;
            return packet;
        }
    }

    public async ValueTask SeekAsync(MediaTime target, CancellationToken cancellationToken)
    {
        if (_config is null)
            throw new InvalidOperationException("InitializeAsync has not run.");
        long length = _source.Length ?? throw new MediaFormatException("Cannot seek an unsized ADTS stream.");
        if (target <= MediaTime.Zero || _duration.IsInfinite)
        {
            _position = _audioStart;
            _samplesEmitted = 0;
            return;
        }

        double fraction = Math.Clamp(target.TotalSeconds / _duration.TotalSeconds, 0.0, 1.0);
        long guess = _audioStart + (long)(fraction * (length - _audioStart));
        long? frame = await FindNextFrameAsync(guess, cancellationToken).ConfigureAwait(false);
        _position = frame ?? length;
        double reached = length == _audioStart ? 0 : (double)(_position - _audioStart) / (length - _audioStart);
        _samplesEmitted = (long)(reached * _estimatedFrames) * _first.Samples;
    }

    private async ValueTask<long?> FindNextFrameAsync(long from, CancellationToken cancellationToken)
    {
        long limit = Math.Min(_source.Length ?? long.MaxValue, from + MaxResyncBytes);
        for (long at = from; at + 7 <= limit; at++)
        {
            var bytes = await ReadAtAsync(at, AdtsFrameHeader.MaxHeaderLength, cancellationToken).ConfigureAwait(false);
            if (bytes.Length < 7)
                return null;
            if (bytes.Span[0] != 0xFF)
            {
                var window = await ReadAtAsync(at, (int)Math.Min(ReadChunk, limit - at), cancellationToken).ConfigureAwait(false);
                int ff = window.Span.IndexOf((byte)0xFF);
                if (ff < 0)
                {
                    at += window.Length - 1;
                    continue;
                }

                at += ff - 1;
                continue;
            }

            if (!AdtsFrameHeader.TryParse(bytes.Span, out var header) || header.SampleRate == 0)
                continue;
            if (_config is not null && !_first.IsCompatibleWith(header))
                continue;

            long nextAt = at + header.FrameLength;
            if (_source.Length is { } length && nextAt + 7 > length)
                return nextAt <= length ? at : null;
            var nextBytes = await ReadAtAsync(nextAt, AdtsFrameHeader.MaxHeaderLength, cancellationToken).ConfigureAwait(false);
            if (nextBytes.Length >= 7 && AdtsFrameHeader.TryParse(nextBytes.Span, out var next) && header.IsCompatibleWith(next))
                return at;
        }

        return null;
    }

    private async ValueTask<ReadOnlyMemory<byte>> ReadAtAsync(long position, int count, CancellationToken cancellationToken)
    {
        if (count > ReadChunk)
        {
            var big = new byte[count];
            int got = await _source.ReadAtLeastAsync(position, big, cancellationToken).ConfigureAwait(false);
            return big.AsMemory(0, got);
        }

        if (_windowStart < 0 || position < _windowStart || position + count > _windowStart + _windowLength)
        {
            _windowStart = position;
            _windowLength = await _source.ReadAtLeastAsync(position, _window, cancellationToken).ConfigureAwait(false);
        }

        int offset = (int)(position - _windowStart);
        int available = Math.Max(0, Math.Min(count, _windowLength - offset));
        return _window.AsMemory(offset, available);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
