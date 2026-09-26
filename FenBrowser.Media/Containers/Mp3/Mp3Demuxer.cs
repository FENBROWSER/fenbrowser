using System.Buffers.Binary;
using System.Globalization;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Containers.Mp3;

/// <summary>
/// MPEG audio elementary streams (ISO/IEC 11172-3, 13818-3), the "MP3 file": an optional
/// ID3v2 tag (id3v2.4.0-structure §3), a run of frames, an optional ID3v1 tag at the end.
/// A Xing/Info header in the first frame gives the frame count (so the duration) and a
/// seek table for VBR streams; otherwise the stream is treated as constant bitrate.
/// </summary>
public sealed class Mp3DemuxerFactory : IDemuxerFactory
{
    public static readonly Mp3DemuxerFactory Instance = new();

    public string Name => "mp3";

    public IReadOnlyList<string> MimeTypes { get; } = ["audio/mpeg", "audio/mp3", "audio/mpa", "audio/x-mpeg", "audio/x-mp3"];

    public int Probe(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 10 && header[..3].SequenceEqual("ID3"u8))
            return 90;
        // Two consecutive compatible frame headers at the start: MIME Sniffing's "MP3 without ID3".
        if (MpegAudioFrameHeader.TryParse(header, out var first) && first.Layer == 3 &&
            header.Length >= first.FrameLength + 4 &&
            MpegAudioFrameHeader.TryParse(header[first.FrameLength..], out var second) && first.IsCompatibleWith(second))
        {
            return 80;
        }

        return 0;
    }

    public IDemuxer Create(IByteSource source, MediaPipelineContext context) => new Mp3Demuxer(source, context);
}

public sealed class Mp3Demuxer : IDemuxer
{
    private const int ReadChunk = 16 * 1024;
    private const int MaxResyncBytes = 64 * 1024;
    private const int MaxId3Bytes = 64 * 1024 * 1024;

    private readonly IByteSource _source;
    private readonly MediaPipelineContext _context;
    private readonly byte[] _window = new byte[ReadChunk];
    private long _windowStart = -1;
    private int _windowLength;

    private MpegAudioFrameHeader _first;
    private CodecConfig? _config;
    private long _audioStart;
    private long _audioEnd;
    private long _position;
    private long _samplesEmitted;
    private long _totalFrames;         // from Xing, or estimated
    private byte[]? _toc;              // Xing TOC: 100 bytes, percent of stream → byte offset (0..255 scale)
    private MediaTime _duration = MediaTime.PositiveInfinity;

    public Mp3Demuxer(IByteSource source, MediaPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);
        _source = source;
        _context = context;
    }

    public async ValueTask<DemuxerInfo> InitializeAsync(CancellationToken cancellationToken)
    {
        long length = _source.Length ?? throw new MediaFormatException("MP3 needs a resource of known length.");
        _audioStart = await SkipId3v2Async(0, cancellationToken).ConfigureAwait(false);
        _audioEnd = await TrimId3v1Async(length, cancellationToken).ConfigureAwait(false);
        if (_audioEnd - _audioStart < 4)
            throw new MediaFormatException("No MPEG audio frames after the tags.");

        // Find the first frame: a header with a compatible successor, within the resync window.
        long firstFrame = await FindNextFrameAsync(_audioStart, cancellationToken).ConfigureAwait(false)
            ?? throw new MediaFormatException("No MPEG audio frame sync found.");
        var frameBytes = await ReadAtAsync(firstFrame, 4, cancellationToken).ConfigureAwait(false);
        MpegAudioFrameHeader.TryParse(frameBytes.Span, out _first);
        if (_first.Layer != 3)
            throw new MediaFormatException($"MPEG Layer {_first.Layer} audio is not supported; only Layer III is.");
        _context.Limits.CheckAudioFormat(_first.SampleRate, _first.Channels);
        _audioStart = firstFrame;
        _position = firstFrame;

        long audioBytes = _audioEnd - _audioStart;
        if (await TryReadXingAsync(firstFrame, cancellationToken).ConfigureAwait(false))
        {
            // The Xing frame itself carries no audio.
            _position = firstFrame + _first.FrameLength;
            _audioStart = _position;
        }
        else
        {
            _totalFrames = audioBytes / _first.FrameLength;
        }

        if (_totalFrames > 0)
            _duration = MediaTime.FromTimescale(_totalFrames * _first.SamplesPerFrame, _first.SampleRate);

        _config = CodecConfig.Audio(MediaCodec.Mp3, _first.SampleRate, _first.Channels, "mp3");
        var track = new MediaTrackInfo(0, _config, _duration, IsDefault: true);
        _context.Log.Emit(_context.Player, MediaEventKind.TrackAdded, MediaLogLevel.Info,
            $"MP3: MPEG-{(_first.Version == 25 ? "2.5" : _first.Version.ToString(CultureInfo.InvariantCulture))} Layer III {_first.SampleRate} Hz {_first.Channels} ch, {(_toc is null ? "no" : "a")} Xing TOC.",
            ("track", "0"), ("codec", "mp3"),
            ("sampleRate", _first.SampleRate.ToString(CultureInfo.InvariantCulture)),
            ("channels", _first.Channels.ToString(CultureInfo.InvariantCulture)),
            ("frames", _totalFrames.ToString(CultureInfo.InvariantCulture)));
        return new DemuxerInfo([track], _duration, IsSeekable: true);
    }

    /// <summary>ID3v2 header: "ID3", version, flags, syncsafe size; the footer flag adds 10 bytes.</summary>
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

        bool footer = (header.Span[5] & 0x10) != 0;
        long total = 10L + size + (footer ? 10 : 0);
        if (total > MaxId3Bytes)
            throw new MediaFormatException($"ID3v2 tag of {total} bytes exceeds the {MaxId3Bytes} byte ceiling.");
        // Tags can be chained; a second tag directly after the first is skipped too.
        return await SkipId3v2Async(at + total, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<long> TrimId3v1Async(long length, CancellationToken cancellationToken)
    {
        if (length < 128)
            return length;
        var tail = await ReadAtAsync(length - 128, 3, cancellationToken).ConfigureAwait(false);
        return tail.Length == 3 && tail.Span.SequenceEqual("TAG"u8) ? length - 128 : length;
    }

    /// <summary>The Xing/Info tag in the first frame: flags, then frame count, byte count, TOC, quality.</summary>
    private async ValueTask<bool> TryReadXingAsync(long frame, CancellationToken cancellationToken)
    {
        int offset = _first.XingOffset;
        var data = await ReadAtAsync(frame + offset, 8 + 4 + 4 + 100, cancellationToken).ConfigureAwait(false);
        if (data.Length < 8)
            return false;
        var span = data.Span;
        if (!span[..4].SequenceEqual("Xing"u8) && !span[..4].SequenceEqual("Info"u8))
            return false;

        uint flags = BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
        int at = 8;
        if ((flags & 1) != 0)
        {
            if (span.Length < at + 4)
                return false;
            _totalFrames = BinaryPrimitives.ReadUInt32BigEndian(span[at..]);
            at += 4;
        }

        if ((flags & 2) != 0)
            at += 4; // byte count; the resource length is more reliable
        if ((flags & 4) != 0 && span.Length >= at + 100)
        {
            _toc = span.Slice(at, 100).ToArray();
        }

        return true;
    }

    public async ValueTask<EncodedPacket?> ReadPacketAsync(CancellationToken cancellationToken)
    {
        var config = _config ?? throw new InvalidOperationException("InitializeAsync has not run.");
        while (true)
        {
            if (_position + 4 > _audioEnd)
                return null;

            var headerBytes = await ReadAtAsync(_position, 4, cancellationToken).ConfigureAwait(false);
            if (headerBytes.Length < 4)
                return null;

            if (!MpegAudioFrameHeader.TryParse(headerBytes.Span, out var header) || !_first.IsCompatibleWith(header))
            {
                // Garbage or a tag mid-stream: resync forward, bounded.
                long? next = await FindNextFrameAsync(_position + 1, cancellationToken).ConfigureAwait(false);
                if (next is null)
                    return null;
                _position = next.Value;
                continue;
            }

            int length = (int)Math.Min(header.FrameLength, _audioEnd - _position);
            var frame = await ReadAtAsync(_position, length, cancellationToken).ConfigureAwait(false);
            if (frame.Length < header.FrameLength)
            {
                // A truncated last frame is dropped; decoders cannot use half a frame.
                return null;
            }

            var packet = EncodedPacket.Copy(
                _context.Limits,
                MediaTrackKind.Audio,
                trackId: 0,
                frame.Span,
                pts: MediaTime.FromTimescale(_samplesEmitted, config.SampleRate),
                dts: MediaTime.FromTimescale(_samplesEmitted, config.SampleRate),
                duration: MediaTime.FromTimescale(header.SamplesPerFrame, config.SampleRate),
                isKeyframe: true);
            _samplesEmitted += header.SamplesPerFrame;
            _position += header.FrameLength;
            return packet;
        }
    }

    /// <summary>
    /// Seeks by the Xing TOC when there is one, else by bytes as if constant bitrate, then
    /// resyncs to the next frame. Timestamps after a seek are estimates on VBR streams
    /// without a TOC, as they are in every MP3 player.
    /// </summary>
    public async ValueTask SeekAsync(MediaTime target, CancellationToken cancellationToken)
    {
        var config = _config ?? throw new InvalidOperationException("InitializeAsync has not run.");
        if (target <= MediaTime.Zero || _duration.IsInfinite)
        {
            _position = _audioStart;
            _samplesEmitted = 0;
            return;
        }

        double fraction = Math.Clamp(target.TotalSeconds / _duration.TotalSeconds, 0.0, 1.0);
        long audioBytes = _audioEnd - _audioStart;
        long guess;
        if (_toc is not null)
        {
            double percent = fraction * 100.0;
            int index = Math.Clamp((int)percent, 0, 99);
            double a = _toc[index];
            double b = index < 99 ? _toc[index + 1] : 256.0;
            double interpolated = a + (b - a) * (percent - index);
            guess = _audioStart + (long)(interpolated / 256.0 * audioBytes);
        }
        else
        {
            guess = _audioStart + (long)(fraction * audioBytes);
        }

        long? frame = await FindNextFrameAsync(Math.Max(_audioStart, guess), cancellationToken).ConfigureAwait(false);
        _position = frame ?? _audioEnd;
        // Position in samples follows the byte fraction actually reached.
        double reached = audioBytes == 0 ? 0 : (double)(_position - _audioStart) / audioBytes;
        _samplesEmitted = (long)(reached * _totalFrames) * _first.SamplesPerFrame;
        if (frame is null)
            _samplesEmitted = _totalFrames * _first.SamplesPerFrame;
        _ = config;
    }

    /// <summary>The next byte offset at or after <paramref name="from"/> holding a frame header whose successor agrees with it.</summary>
    private async ValueTask<long?> FindNextFrameAsync(long from, CancellationToken cancellationToken)
    {
        long limit = Math.Min(_audioEnd, from + MaxResyncBytes);
        for (long at = from; at + 4 <= limit; at++)
        {
            var bytes = await ReadAtAsync(at, 4, cancellationToken).ConfigureAwait(false);
            if (bytes.Length < 4 || bytes.Span[0] != 0xFF)
            {
                if (bytes.Length >= 1 && bytes.Span[0] != 0xFF)
                {
                    // Skip to the next 0xFF quickly within the window.
                    var window = await ReadAtAsync(at, (int)Math.Min(ReadChunk, limit - at), cancellationToken).ConfigureAwait(false);
                    int ff = window.Span.IndexOf((byte)0xFF);
                    if (ff < 0)
                    {
                        at += window.Length - 1;
                        continue;
                    }

                    at += ff - 1;
                }

                continue;
            }

            if (!MpegAudioFrameHeader.TryParse(bytes.Span, out var header) || header.Layer != 3)
                continue;
            if (_config is not null && !_first.IsCompatibleWith(header))
                continue;

            long nextAt = at + header.FrameLength;
            if (nextAt + 4 > _audioEnd)
                return at; // last frame in the stream
            var nextBytes = await ReadAtAsync(nextAt, 4, cancellationToken).ConfigureAwait(false);
            if (nextBytes.Length == 4 && MpegAudioFrameHeader.TryParse(nextBytes.Span, out var next) && header.IsCompatibleWith(next))
                return at;
        }

        return null;
    }

    /// <summary>Reads through a sliding window so byte-by-byte resyncs do not hit the source per byte.</summary>
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
