using System.Buffers.Binary;
using System.Globalization;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Containers.Flac;

/// <summary>
/// Native FLAC (RFC 9639): the "fLaC" marker, metadata blocks (STREAMINFO first, SEEKTABLE
/// when present), then frames. Frame boundaries are found by the next frame sync whose
/// header passes its CRC-8 and whose preceding frame passes its CRC-16, so a sync pattern
/// inside audio data never splits a frame. Every frame header carries its sample or frame
/// number, so timestamps are exact and seeks land on the frame at or before the target.
/// </summary>
public sealed class FlacDemuxerFactory : IDemuxerFactory
{
    public static readonly FlacDemuxerFactory Instance = new();

    public string Name => "flac";

    public IReadOnlyList<string> MimeTypes { get; } = ["audio/flac", "audio/x-flac"];

    public int Probe(ReadOnlySpan<byte> header) =>
        header.Length >= 8 && header[..4].SequenceEqual("fLaC"u8) && (header[4] & 0x7F) == 0 ? 100 : 0;

    public IDemuxer Create(IByteSource source, MediaPipelineContext context) => new FlacDemuxer(source, context);
}

/// <summary>A FLAC frame header (RFC 9639 §9.1), without the subframes.</summary>
public readonly record struct FlacFrameHeader(
    int HeaderLength,
    bool VariableBlockSize,
    int BlockSize,
    int SampleRate,      // 0 = from STREAMINFO
    int Channels,
    int BitsPerSample,   // 0 = from STREAMINFO
    long Number)         // sample number (variable block size) or frame number
{
    public const int MaxHeaderLength = 16;

    /// <summary>Parses a frame header at the start of <paramref name="bytes"/>, including its CRC-8.</summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, out FlacFrameHeader header)
    {
        header = default;
        if (bytes.Length < 6 || bytes[0] != 0xFF || (bytes[1] & 0xFC) != 0xF8)
            return false;

        bool variable = (bytes[1] & 0x01) != 0;
        int blockSizeCode = bytes[2] >> 4;
        int sampleRateCode = bytes[2] & 0x0F;
        int channelCode = bytes[3] >> 4;
        int sampleSizeCode = (bytes[3] >> 1) & 0x07;
        if (blockSizeCode == 0 || sampleRateCode == 15 || channelCode >= 11 || sampleSizeCode == 3 || (bytes[3] & 0x01) != 0)
            return false;

        int at = 4;
        if (!TryReadUtf8Number(bytes, ref at, variable ? 7 : 6, out long number))
            return false;

        int blockSize;
        switch (blockSizeCode)
        {
            case 1: blockSize = 192; break;
            case >= 2 and <= 5: blockSize = 576 << (blockSizeCode - 2); break;
            case 6:
                if (at + 1 > bytes.Length) return false;
                blockSize = bytes[at++] + 1;
                break;
            case 7:
                if (at + 2 > bytes.Length) return false;
                blockSize = BinaryPrimitives.ReadUInt16BigEndian(bytes[at..]) + 1;
                at += 2;
                break;
            default: blockSize = 256 << (blockSizeCode - 8); break;
        }

        int sampleRate;
        switch (sampleRateCode)
        {
            case 0: sampleRate = 0; break;
            case 1: sampleRate = 88200; break;
            case 2: sampleRate = 176400; break;
            case 3: sampleRate = 192000; break;
            case 4: sampleRate = 8000; break;
            case 5: sampleRate = 16000; break;
            case 6: sampleRate = 22050; break;
            case 7: sampleRate = 24000; break;
            case 8: sampleRate = 32000; break;
            case 9: sampleRate = 44100; break;
            case 10: sampleRate = 48000; break;
            case 11: sampleRate = 96000; break;
            case 12:
                if (at + 1 > bytes.Length) return false;
                sampleRate = bytes[at++] * 1000;
                break;
            case 13:
                if (at + 2 > bytes.Length) return false;
                sampleRate = BinaryPrimitives.ReadUInt16BigEndian(bytes[at..]);
                at += 2;
                break;
            default:
                if (at + 2 > bytes.Length) return false;
                sampleRate = BinaryPrimitives.ReadUInt16BigEndian(bytes[at..]) * 10;
                at += 2;
                break;
        }

        int channels = channelCode <= 7 ? channelCode + 1 : 2;
        int bitsPerSample = sampleSizeCode switch
        {
            0 => 0,
            1 => 8,
            2 => 12,
            4 => 16,
            5 => 20,
            6 => 24,
            _ => 32,
        };

        if (at + 1 > bytes.Length)
            return false;
        if (Crc8(bytes[..at]) != bytes[at])
            return false;
        at++;

        header = new FlacFrameHeader(at, variable, blockSize, sampleRate, channels, bitsPerSample, number);
        return true;
    }

    /// <summary>The UTF-8-like coded number of RFC 9639 §9.1.5, up to 7 bytes (36 bits).</summary>
    private static bool TryReadUtf8Number(ReadOnlySpan<byte> bytes, ref int at, int maxBytes, out long value)
    {
        value = 0;
        if (at >= bytes.Length)
            return false;
        byte first = bytes[at];
        int extra;
        if ((first & 0x80) == 0) { extra = 0; value = first; }
        else if ((first & 0xE0) == 0xC0) { extra = 1; value = first & 0x1F; }
        else if ((first & 0xF0) == 0xE0) { extra = 2; value = first & 0x0F; }
        else if ((first & 0xF8) == 0xF0) { extra = 3; value = first & 0x07; }
        else if ((first & 0xFC) == 0xF8) { extra = 4; value = first & 0x03; }
        else if ((first & 0xFE) == 0xFC) { extra = 5; value = first & 0x01; }
        else if (first == 0xFE) { extra = 6; value = 0; }
        else return false;

        if (extra + 1 > maxBytes || at + 1 + extra > bytes.Length)
            return false;
        at++;
        for (int i = 0; i < extra; i++)
        {
            byte b = bytes[at++];
            if ((b & 0xC0) != 0x80)
                return false;
            value = (value << 6) | (uint)(b & 0x3F);
        }

        return true;
    }

    /// <summary>CRC-8, polynomial 0x07, initial 0 (RFC 9639 §9.1.8).</summary>
    public static byte Crc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
        }

        return crc;
    }

    /// <summary>CRC-16, polynomial 0x8005, initial 0 (RFC 9639 §9.3).</summary>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1);
        }

        return crc;
    }
}

public sealed class FlacDemuxer : IDemuxer
{
    private const int MaxMetadataBytes = 16 * 1024 * 1024;
    private const int MaxFrameBytes = 1024 * 1024; // a 65535-sample 32-bit 8-channel frame is ~2 MB uncompressed; FLAC frames are smaller
    private const int ReadChunk = 64 * 1024;

    private readonly IByteSource _source;
    private readonly MediaPipelineContext _context;
    private readonly List<(long Sample, long Offset)> _seekTable = [];
    private byte[] _window = new byte[ReadChunk];
    private long _windowStart = -1;
    private int _windowLength;

    private CodecConfig? _config;
    private int _sampleRate;
    private int _channels;
    private int _bitsPerSample;
    private int _minBlockSize;
    private int _maxBlockSize;
    private long _totalSamples;
    private long _framesStart;
    private long _position;
    private long _nextSample;

    public FlacDemuxer(IByteSource source, MediaPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);
        _source = source;
        _context = context;
    }

    public async ValueTask<DemuxerInfo> InitializeAsync(CancellationToken cancellationToken)
    {
        var marker = await ReadAtAsync(0, 4, cancellationToken).ConfigureAwait(false);
        if (marker.Length < 4 || !marker.Span.SequenceEqual("fLaC"u8))
            throw new MediaFormatException("Not a FLAC stream.");

        long at = 4;
        byte[]? streamInfo = null;
        bool last = false;
        while (!last)
        {
            var blockHeader = await ReadAtAsync(at, 4, cancellationToken).ConfigureAwait(false);
            if (blockHeader.Length < 4)
                throw new MediaFormatException("FLAC metadata is truncated.");
            last = (blockHeader.Span[0] & 0x80) != 0;
            int type = blockHeader.Span[0] & 0x7F;
            int length = (blockHeader.Span[1] << 16) | (blockHeader.Span[2] << 8) | blockHeader.Span[3];
            if (at + 4 + length > MaxMetadataBytes)
                throw new MediaFormatException("FLAC metadata exceeds the 16 MiB ceiling.");
            if (type == 127)
                throw new MediaFormatException("Invalid FLAC metadata block type 127.");

            if (type == 0)
            {
                if (length != 34)
                    throw new MediaFormatException($"STREAMINFO is {length} bytes; expected 34.");
                var info = await ReadAtAsync(at + 4, 34, cancellationToken).ConfigureAwait(false);
                if (info.Length < 34)
                    throw new MediaFormatException("STREAMINFO is truncated.");
                streamInfo = info.ToArray();
                ParseStreamInfo(streamInfo);
            }
            else if (type == 3 && streamInfo is not null)
            {
                await ReadSeekTableAsync(at + 4, length, cancellationToken).ConfigureAwait(false);
            }

            at += 4 + length;
        }

        if (streamInfo is null)
            throw new MediaFormatException("No STREAMINFO block.");
        if (_source.Length is { } resourceLength && at > resourceLength)
            throw new MediaFormatException("FLAC metadata runs past the end of the resource.");
        _framesStart = at;
        _position = at;
        _nextSample = 0;

        var duration = _totalSamples > 0 ? MediaTime.FromTimescale(_totalSamples, _sampleRate) : MediaTime.PositiveInfinity;
        _config = new CodecConfig(MediaTrackKind.Audio, MediaCodec.Flac, "flac", SampleRate: _sampleRate, Channels: _channels, Extradata: streamInfo);
        var track = new MediaTrackInfo(0, _config, duration, IsDefault: true);
        _context.Log.Emit(_context.Player, MediaEventKind.TrackAdded, MediaLogLevel.Info,
            $"FLAC: {_sampleRate} Hz {_channels} ch {_bitsPerSample} bit, {_totalSamples} samples, {_seekTable.Count} seek points.",
            ("track", "0"), ("codec", "flac"),
            ("sampleRate", _sampleRate.ToString(CultureInfo.InvariantCulture)),
            ("channels", _channels.ToString(CultureInfo.InvariantCulture)),
            ("bits", _bitsPerSample.ToString(CultureInfo.InvariantCulture)));
        return new DemuxerInfo([track], duration, IsSeekable: _source.Length is not null);
    }

    /// <summary>RFC 9639 §8.2: block sizes, frame sizes, then rate (20), channels-1 (3), bps-1 (5), total samples (36).</summary>
    private void ParseStreamInfo(ReadOnlySpan<byte> info)
    {
        _minBlockSize = BinaryPrimitives.ReadUInt16BigEndian(info);
        _maxBlockSize = BinaryPrimitives.ReadUInt16BigEndian(info[2..]);
        ulong packed = BinaryPrimitives.ReadUInt64BigEndian(info[10..]);
        _sampleRate = (int)(packed >> 44);
        _channels = (int)((packed >> 41) & 0x7) + 1;
        _bitsPerSample = (int)((packed >> 36) & 0x1F) + 1;
        _totalSamples = (long)(packed & 0xFFFFFFFFFUL);
        if (_sampleRate == 0)
            throw new MediaFormatException("STREAMINFO declares a zero sample rate.");
        if (_minBlockSize < 16 || _maxBlockSize < _minBlockSize)
            throw new MediaFormatException("STREAMINFO block sizes are invalid.");
        _context.Limits.CheckAudioFormat(_sampleRate, _channels);
    }

    /// <summary>RFC 9639 §8.5: 18-byte points of sample number, byte offset from the first frame, samples.</summary>
    private async ValueTask ReadSeekTableAsync(long at, int length, CancellationToken cancellationToken)
    {
        int points = Math.Min(length / 18, 100_000);
        var table = await ReadAtAsync(at, points * 18, cancellationToken).ConfigureAwait(false);
        for (int i = 0; i + 18 <= table.Length; i += 18)
        {
            long sample = BinaryPrimitives.ReadInt64BigEndian(table.Span[i..]);
            long offset = BinaryPrimitives.ReadInt64BigEndian(table.Span[(i + 8)..]);
            if (sample == -1 || sample < 0 || offset < 0)
                continue; // placeholder point
            _seekTable.Add((sample, offset));
        }

        _seekTable.Sort((a, b) => a.Sample.CompareTo(b.Sample));
    }

    public async ValueTask<EncodedPacket?> ReadPacketAsync(CancellationToken cancellationToken)
    {
        var config = _config ?? throw new InvalidOperationException("InitializeAsync has not run.");
        while (true)
        {
            long? start = await FindFrameAsync(_position, cancellationToken).ConfigureAwait(false);
            if (start is null)
                return null;

            var headerBytes = await ReadAtAsync(start.Value, FlacFrameHeader.MaxHeaderLength, cancellationToken).ConfigureAwait(false);
            if (!FlacFrameHeader.TryParse(headerBytes.Span, out var header))
            {
                _position = start.Value + 2;
                continue;
            }

            long? end = await FindFrameEndAsync(start.Value, header, cancellationToken).ConfigureAwait(false);
            if (end is null)
            {
                // No confirmed frame: the last frame ends at the resource end, if its CRC holds.
                long length = _source.Length ?? 0;
                if (length > start.Value && length - start.Value <= MaxFrameBytes &&
                    await FrameCrcHoldsAsync(start.Value, length, cancellationToken).ConfigureAwait(false))
                {
                    end = length;
                }
                else
                {
                    return null;
                }
            }

            int frameLength = (int)(end.Value - start.Value);
            var frame = await ReadAtAsync(start.Value, frameLength, cancellationToken).ConfigureAwait(false);
            long sample = header.VariableBlockSize ? header.Number : header.Number * _minBlockSize;
            if (!header.VariableBlockSize && _minBlockSize != _maxBlockSize)
                sample = _nextSample; // fixed-size frame numbers only work for constant block sizes
            var packet = EncodedPacket.Copy(
                _context.Limits,
                MediaTrackKind.Audio,
                trackId: 0,
                frame.Span,
                pts: MediaTime.FromTimescale(sample, config.SampleRate),
                dts: MediaTime.FromTimescale(sample, config.SampleRate),
                duration: MediaTime.FromTimescale(header.BlockSize, config.SampleRate),
                isKeyframe: true);
            _nextSample = sample + header.BlockSize;
            _position = end.Value;
            return packet;
        }
    }

    /// <summary>The next byte offset at or after <paramref name="from"/> with a frame sync and a valid header.</summary>
    private async ValueTask<long?> FindFrameAsync(long from, CancellationToken cancellationToken)
    {
        long length = _source.Length ?? long.MaxValue;
        long at = from;
        while (at + 2 <= length)
        {
            var window = await ReadAtAsync(at, ReadChunk, cancellationToken).ConfigureAwait(false);
            if (window.Length < 2)
                return null;
            var span = window.Span;
            for (int i = 0; i + 1 < span.Length; i++)
            {
                if (span[i] != 0xFF || (span[i + 1] & 0xFC) != 0xF8)
                    continue;
                var candidate = await ReadAtAsync(at + i, FlacFrameHeader.MaxHeaderLength, cancellationToken).ConfigureAwait(false);
                if (FlacFrameHeader.TryParse(candidate.Span, out var header) && HeaderAgrees(header))
                    return at + i;
                window = await ReadAtAsync(at, ReadChunk, cancellationToken).ConfigureAwait(false);
                span = window.Span;
            }

            at += span.Length - 1;
        }

        return null;
    }

    private bool HeaderAgrees(in FlacFrameHeader header) =>
        (header.SampleRate == 0 || header.SampleRate == _sampleRate) &&
        header.Channels == _channels &&
        (header.BitsPerSample == 0 || header.BitsPerSample == _bitsPerSample) &&
        header.BlockSize <= _maxBlockSize;

    /// <summary>
    /// The end of the frame at <paramref name="start"/>: the next sync whose header agrees
    /// and for which the bytes in between end with their own CRC-16.
    /// </summary>
    private async ValueTask<long?> FindFrameEndAsync(long start, FlacFrameHeader header, CancellationToken cancellationToken)
    {
        long at = start + header.HeaderLength;
        long limit = Math.Min(_source.Length ?? long.MaxValue, start + MaxFrameBytes);
        while (at < limit)
        {
            long? next = await FindFrameAsync(at, cancellationToken).ConfigureAwait(false);
            if (next is null || next.Value >= limit)
                return null;
            if (await FrameCrcHoldsAsync(start, next.Value, cancellationToken).ConfigureAwait(false))
                return next.Value;
            at = next.Value + 2;
        }

        return null;
    }

    private async ValueTask<bool> FrameCrcHoldsAsync(long start, long end, CancellationToken cancellationToken)
    {
        int length = (int)(end - start);
        if (length < 4)
            return false;
        var frame = await ReadAtAsync(start, length, cancellationToken).ConfigureAwait(false);
        if (frame.Length < length)
            return false;
        ushort expected = BinaryPrimitives.ReadUInt16BigEndian(frame.Span[(length - 2)..]);
        return FlacFrameHeader.Crc16(frame.Span[..(length - 2)]) == expected;
    }

    /// <summary>Seeks by the SEEKTABLE when present, else by sample fraction, then reads on from the frame at or before the target.</summary>
    public async ValueTask SeekAsync(MediaTime target, CancellationToken cancellationToken)
    {
        var config = _config ?? throw new InvalidOperationException("InitializeAsync has not run.");
        long length = _source.Length ?? throw new MediaFormatException("Cannot seek an unsized FLAC stream.");
        long targetSample = target <= MediaTime.Zero ? 0 : target.ToTimescale(config.SampleRate);
        if (_totalSamples > 0)
            targetSample = Math.Min(targetSample, _totalSamples);

        long guess = _framesStart;
        if (_seekTable.Count > 0)
        {
            foreach (var (sample, offset) in _seekTable)
            {
                if (sample <= targetSample)
                    guess = _framesStart + offset;
                else
                    break;
            }
        }
        else if (_totalSamples > 0)
        {
            guess = _framesStart + (long)((double)targetSample / _totalSamples * (length - _framesStart));
        }

        // Walk back if the guess landed past the target, then forward to the last frame at or before it.
        long at = Math.Clamp(guess, _framesStart, length);
        for (int back = 0; back < 64; back++)
        {
            long? frame = await FindFrameAsync(at, cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                at = Math.Max(_framesStart, at - ReadChunk);
                if (at == _framesStart)
                    break;
                continue;
            }

            long sample = await FrameSampleAsync(frame.Value, cancellationToken).ConfigureAwait(false);
            if (sample <= targetSample || at == _framesStart)
            {
                at = frame.Value;
                break;
            }

            at = Math.Max(_framesStart, at - ReadChunk);
        }

        _position = at;
        _nextSample = await FrameSampleAsync(at, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            long? frame = await FindFrameAsync(_position, cancellationToken).ConfigureAwait(false);
            if (frame is null)
                break;
            var headerBytes = await ReadAtAsync(frame.Value, FlacFrameHeader.MaxHeaderLength, cancellationToken).ConfigureAwait(false);
            if (!FlacFrameHeader.TryParse(headerBytes.Span, out var header))
                break;
            long sample = header.VariableBlockSize ? header.Number : header.Number * _minBlockSize;
            if (sample + header.BlockSize > targetSample)
            {
                _position = frame.Value;
                _nextSample = sample;
                return;
            }

            long? end = await FindFrameEndAsync(frame.Value, header, cancellationToken).ConfigureAwait(false);
            if (end is null)
                break;
            _position = end.Value;
            _nextSample = sample + header.BlockSize;
        }
    }

    private async ValueTask<long> FrameSampleAsync(long frame, CancellationToken cancellationToken)
    {
        var bytes = await ReadAtAsync(frame, FlacFrameHeader.MaxHeaderLength, cancellationToken).ConfigureAwait(false);
        if (!FlacFrameHeader.TryParse(bytes.Span, out var header))
            return 0;
        return header.VariableBlockSize ? header.Number : header.Number * _minBlockSize;
    }

    private async ValueTask<ReadOnlyMemory<byte>> ReadAtAsync(long position, int count, CancellationToken cancellationToken)
    {
        if (count > _window.Length)
            _window = new byte[Math.Max(count, _window.Length * 2)];
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
