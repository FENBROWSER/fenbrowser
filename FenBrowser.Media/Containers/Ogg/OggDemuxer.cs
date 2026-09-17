using System.Buffers.Binary;
using System.Globalization;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Containers.Ogg;

/// <summary>
/// Ogg (RFC 3533) carrying Opus (RFC 7845) or Vorbis (Vorbis I specification §A). The first
/// audio logical stream is played; other streams are skipped by serial number. Packets are
/// reassembled from the lacing values across page boundaries, pages with a bad CRC are
/// skipped, and seeking bisects the file on granule positions.
/// </summary>
/// <remarks>
/// Timestamps: Opus packets have exact durations from their TOC byte, so every packet's
/// PTS is known. A Vorbis packet's length depends on the setup header's mode table, so
/// only the first new packet on each page is stamped (with the previous page's granule
/// position); the rest carry <see cref="MediaTime.NegativeInfinity"/>, "continues from
/// the previous packet", which the decoder resolves by counting output samples.
/// </remarks>
public sealed class OggDemuxerFactory : IDemuxerFactory
{
    public static readonly OggDemuxerFactory Instance = new();

    public string Name => "ogg";

    public IReadOnlyList<string> MimeTypes { get; } = ["audio/ogg", "application/ogg", "audio/opus", "audio/vorbis", "video/ogg"];

    public int Probe(ReadOnlySpan<byte> header) =>
        header.Length >= 5 && header[..4].SequenceEqual("OggS"u8) && header[4] == 0 ? 100 : 0;

    public IDemuxer Create(IByteSource source, MediaPipelineContext context) => new OggDemuxer(source, context);
}

public enum OggCodec
{
    None,
    Opus,
    Vorbis,
}

public sealed class OggDemuxer : IDemuxer
{
    private const int MaxPageLength = OggPageHeader.FixedHeaderLength + 255 + 255 * 255;
    private const int MaxHeaderPages = 64;
    private const int ScanWindow = 64 * 1024;

    private readonly IByteSource _source;
    private readonly MediaPipelineContext _context;
    private readonly byte[] _pageBuffer = new byte[MaxPageLength];
    private readonly Queue<EncodedPacket> _pending = new();
    private readonly List<byte> _partial = [];

    private uint _serial;
    private OggCodec _codec;
    private int _sampleRate;      // granule rate: 48000 for Opus, the stream rate for Vorbis
    private int _channels;
    private int _preSkip;
    private CodecConfig? _config;
    private long _firstAudioPage;
    private long _position;
    private long _lastGranule = -1;   // granule of the last page read (end of its last complete packet)
    private long _opusSamples;        // running 48 kHz position for Opus packets
    private bool _partialPending;
    private bool _endOfStream;
    private MediaTime _duration = MediaTime.PositiveInfinity;

    public OggDemuxer(IByteSource source, MediaPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);
        _source = source;
        _context = context;
    }

    public async ValueTask<DemuxerInfo> InitializeAsync(CancellationToken cancellationToken)
    {
        // Header pages: every logical stream's beginning-of-stream page comes first (RFC 3533 §4).
        var headers = new List<byte[]>();
        long position = 0;
        bool chosen = false;
        for (int pages = 0; pages < MaxHeaderPages; pages++)
        {
            var page = await ReadPageAsync(position, cancellationToken).ConfigureAwait(false);
            if (page is null)
                throw new MediaFormatException(chosen ? "The Ogg stream ends inside its headers." : "No Opus or Vorbis stream in the Ogg container.");

            var (header, body, pageStart) = page.Value;
            position = pageStart + header.TotalLength;

            if (!chosen)
            {
                if (!header.IsBeginningOfStream)
                    throw new MediaFormatException("No Opus or Vorbis stream in the Ogg container.");
                var packets = SplitPackets(header, body, [], out _);
                if (packets.Count == 0)
                    continue;
                var first = packets[0];
                if (first.Length >= 8 && first.AsSpan(0, 8).SequenceEqual("OpusHead"u8))
                {
                    ParseOpusHead(first);
                    _codec = OggCodec.Opus;
                }
                else if (first.Length >= 7 && first[0] == 1 && first.AsSpan(1, 6).SequenceEqual("vorbis"u8))
                {
                    ParseVorbisIdentification(first);
                    _codec = OggCodec.Vorbis;
                }
                else
                {
                    continue; // another kind of stream (Theora, Skeleton, ...): keep looking
                }

                _serial = header.Serial;
                chosen = true;
                headers.AddRange(packets);
            }
            else if (header.Serial == _serial)
            {
                headers.AddRange(SplitPackets(header, body, _partial, out _));
            }

            int needed = _codec == OggCodec.Opus ? 2 : 3;
            if (chosen && headers.Count >= needed)
            {
                _firstAudioPage = position;
                break;
            }
        }

        if (!chosen)
            throw new MediaFormatException("No Opus or Vorbis stream in the Ogg container.");

        _context.Limits.CheckAudioFormat(_sampleRate, _channels);
        _config = _codec == OggCodec.Opus
            ? new CodecConfig(MediaTrackKind.Audio, MediaCodec.Opus, "opus", SampleRate: 48000, Channels: _channels, Extradata: headers[0])
            : new CodecConfig(MediaTrackKind.Audio, MediaCodec.Vorbis, "vorbis", SampleRate: _sampleRate, Channels: _channels, Extradata: XiphLacing(headers[0], headers[1], headers[2]));

        _position = _firstAudioPage;
        _lastGranule = 0;
        _opusSamples = 0;
        _duration = await ReadDurationAsync(cancellationToken).ConfigureAwait(false);

        var track = new MediaTrackInfo(0, _config, _duration, IsDefault: true);
        _context.Log.Emit(_context.Player, MediaEventKind.TrackAdded, MediaLogLevel.Info,
            $"Ogg: {_codec} {_config.SampleRate} Hz {_channels} ch, serial {_serial}.",
            ("track", "0"), ("codec", _codec.ToString().ToLowerInvariant()),
            ("sampleRate", _config.SampleRate.ToString(CultureInfo.InvariantCulture)),
            ("channels", _channels.ToString(CultureInfo.InvariantCulture)),
            ("preSkip", _preSkip.ToString(CultureInfo.InvariantCulture)));
        return new DemuxerInfo([track], _duration, IsSeekable: _source.Length is not null);
    }

    /// <summary>RFC 7845 §5.1: version, channel count, pre-skip, input sample rate, output gain, mapping family.</summary>
    private void ParseOpusHead(byte[] head)
    {
        if (head.Length < 19)
            throw new MediaFormatException("OpusHead is too short.");
        if ((head[8] & 0xF0) != 0)
            throw new MediaFormatException($"Unsupported OpusHead version {head[8]}.");
        _channels = head[9];
        _preSkip = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10));
        _sampleRate = 48000;
        if (_channels == 0)
            throw new MediaFormatException("OpusHead declares no channels.");
        int mappingFamily = head[18];
        if (mappingFamily == 0 && _channels > 2)
            throw new MediaFormatException("Opus mapping family 0 allows at most two channels.");
    }

    /// <summary>Vorbis I §4.2.2: version, channels, rate, bitrates, blocksizes, framing bit.</summary>
    private void ParseVorbisIdentification(byte[] id)
    {
        if (id.Length < 30)
            throw new MediaFormatException("The Vorbis identification header is too short.");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(id.AsSpan(7));
        if (version != 0)
            throw new MediaFormatException($"Unsupported Vorbis version {version}.");
        _channels = id[11];
        _sampleRate = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(id.AsSpan(12)));
        int blocksize0 = 1 << (id[28] & 0x0F);
        int blocksize1 = 1 << (id[28] >> 4);
        if (_channels == 0 || _sampleRate == 0 || blocksize0 > blocksize1 || (id[29] & 1) == 0)
            throw new MediaFormatException("The Vorbis identification header is invalid.");
    }

    /// <summary>The three Vorbis headers in the lacing layout codec libraries expect as extradata.</summary>
    private static byte[] XiphLacing(byte[] a, byte[] b, byte[] c)
    {
        var lacing = new List<byte> { 2 };
        foreach (int length in (int[])[a.Length, b.Length])
        {
            int remaining = length;
            while (remaining >= 255)
            {
                lacing.Add(255);
                remaining -= 255;
            }

            lacing.Add((byte)remaining);
        }

        return [.. lacing, .. a, .. b, .. c];
    }

    /// <summary>The stream's last granule position, from the last pages of the resource.</summary>
    private async ValueTask<MediaTime> ReadDurationAsync(CancellationToken cancellationToken)
    {
        if (_source.Length is not { } length)
            return MediaTime.PositiveInfinity;

        long from = Math.Max(_firstAudioPage, length - ScanWindow);
        long best = -1;
        long at = from;
        while (at < length)
        {
            long? next = await FindPageAsync(at, length, cancellationToken).ConfigureAwait(false);
            if (next is null)
                break;
            var page = await ReadPageAsync(next.Value, cancellationToken).ConfigureAwait(false);
            if (page is null)
                break;
            var (header, _, start) = page.Value;
            if (header.Serial == _serial && header.GranulePosition >= 0)
                best = header.GranulePosition;
            at = start + header.TotalLength;
        }

        if (best < 0)
            return MediaTime.PositiveInfinity;
        long samples = _codec == OggCodec.Opus ? Math.Max(0, best - _preSkip) : best;
        return MediaTime.FromTimescale(samples, _sampleRate);
    }

    public async ValueTask<EncodedPacket?> ReadPacketAsync(CancellationToken cancellationToken)
    {
        if (_config is null)
            throw new InvalidOperationException("InitializeAsync has not run.");

        while (_pending.Count == 0)
        {
            if (_endOfStream)
                return null;
            var page = await ReadPageAsync(_position, cancellationToken).ConfigureAwait(false);
            if (page is null)
            {
                _endOfStream = true;
                return null;
            }

            var (header, body, start) = page.Value;
            _position = start + header.TotalLength;
            if (header.Serial != _serial)
                continue;

            bool stampFirst = true;
            var packets = SplitPackets(header, body, _partial, out bool endsWithPartial);
            foreach (var packet in packets)
                _pending.Enqueue(MakePacket(packet, ref stampFirst));

            if (header.GranulePosition >= 0)
                _lastGranule = header.GranulePosition;
            _partialPending = endsWithPartial;
            if (header.IsEndOfStream)
                _endOfStream = true;
        }

        return _pending.Dequeue();
    }

    private EncodedPacket MakePacket(byte[] data, ref bool stampFirst)
    {
        MediaTime pts;
        MediaTime duration;
        if (_codec == OggCodec.Opus)
        {
            int samples = OpusPacketSamples(data);
            pts = MediaTime.FromTimescale(_opusSamples - _preSkip, 48000);
            duration = MediaTime.FromTimescale(samples, 48000);
            _opusSamples += samples;
        }
        else
        {
            // The first packet that starts on this page begins where the previous page's
            // last completed packet ended; later ones follow on without a known stamp.
            pts = stampFirst && !_partialPending && _lastGranule >= 0
                ? MediaTime.FromTimescale(_lastGranule, _sampleRate)
                : MediaTime.NegativeInfinity;
            duration = MediaTime.Zero;
        }

        stampFirst = false;
        return EncodedPacket.Copy(_context.Limits, MediaTrackKind.Audio, 0, data, pts, pts, duration, isKeyframe: true);
    }

    /// <summary>RFC 6716 §3.1: frame size from the TOC configuration and the frame count from the code.</summary>
    internal static int OpusPacketSamples(ReadOnlySpan<byte> packet)
    {
        if (packet.Length == 0)
            return 0;
        int toc = packet[0];
        int config = toc >> 3;
        int frameSamples = config switch
        {
            < 12 => (config & 3) switch { 0 => 480, 1 => 960, 2 => 1920, _ => 2880 },
            < 16 => (config & 1) == 0 ? 480 : 960,
            _ => (config & 3) switch { 0 => 120, 1 => 240, 2 => 480, _ => 960 },
        };
        int frames = (toc & 3) switch
        {
            0 => 1,
            1 or 2 => 2,
            _ => packet.Length > 1 ? packet[1] & 0x3F : 0,
        };
        // A packet may not exceed 120 ms (RFC 6716 §3.2.5).
        return Math.Min(frames * frameSamples, 5760);
    }

    /// <summary>Splits a page body by its lacing values, joining a packet continued from the previous page.</summary>
    private List<byte[]> SplitPackets(OggPageHeader header, ReadOnlyMemory<byte> body, List<byte> partial, out bool endsWithPartial)
    {
        var packets = new List<byte[]>();
        var table = _pageBuffer.AsSpan(OggPageHeader.FixedHeaderLength, header.SegmentCount);
        int offset = 0;
        endsWithPartial = false;
        if (!header.IsContinued && partial.Count > 0)
        {
            // The continuation never arrived (a dropped page): the partial packet is lost.
            partial.Clear();
        }

        for (int i = 0; i < table.Length; i++)
        {
            int lacing = table[i];
            partial.AddRange(body.Span.Slice(offset, lacing));
            offset += lacing;
            if (lacing < 255)
            {
                packets.Add([.. partial]);
                partial.Clear();
            }
            else if (i == table.Length - 1)
            {
                endsWithPartial = true;
            }
        }

        return packets;
    }

    /// <summary>
    /// Bisects the resource for the last page of this stream whose granule position is at or
    /// before the target, then reads on from the page after it, whose first packet starts
    /// at that granule. The decoder handles the pre-roll its codec needs.
    /// </summary>
    public async ValueTask SeekAsync(MediaTime target, CancellationToken cancellationToken)
    {
        if (_config is null)
            throw new InvalidOperationException("InitializeAsync has not run.");
        DrainPending();
        _endOfStream = false;

        long length = _source.Length ?? throw new MediaFormatException("Cannot seek an unsized Ogg stream.");
        long targetGranule = target <= MediaTime.Zero
            ? 0
            : target.ToTimescale(_sampleRate) + (_codec == OggCodec.Opus ? _preSkip : 0);
        if (targetGranule <= 0)
        {
            RewindToStart();
            return;
        }

        long lo = _firstAudioPage;
        long hi = length;
        long bestPageStart = -1;
        long bestGranule = -1;
        long bestEnd = _firstAudioPage;
        for (int iteration = 0; iteration < 64 && hi - lo > MaxPageLength / 4; iteration++)
        {
            long mid = lo + (hi - lo) / 2;
            var probe = await FindStreamPageAsync(mid, hi, cancellationToken).ConfigureAwait(false);
            if (probe is null)
            {
                hi = mid;
                continue;
            }

            var (header, start) = probe.Value;
            if (header.GranulePosition <= targetGranule)
            {
                bestPageStart = start;
                bestGranule = header.GranulePosition;
                bestEnd = start + header.TotalLength;
                lo = bestEnd;
            }
            else
            {
                hi = start;
            }
        }

        // Walk forward from the best lower bound to the last page still at or before the target.
        long at = bestPageStart < 0 ? _firstAudioPage : bestEnd;
        long granule = bestGranule;
        long resume = bestPageStart < 0 ? _firstAudioPage : bestEnd;
        while (true)
        {
            var probe = await FindStreamPageAsync(at, length, cancellationToken).ConfigureAwait(false);
            if (probe is null)
                break;
            var (header, start) = probe.Value;
            if (header.GranulePosition > targetGranule)
                break;
            granule = header.GranulePosition;
            resume = start + header.TotalLength;
            at = resume;
        }

        if (granule < 0)
        {
            RewindToStart();
            return;
        }

        _position = resume;
        _lastGranule = granule;
        _opusSamples = granule;
        _partial.Clear();
        _partialPending = false;
    }

    private void RewindToStart()
    {
        _position = _firstAudioPage;
        _lastGranule = 0;
        _opusSamples = 0;
        _partial.Clear();
        _partialPending = false;
    }

    private void DrainPending()
    {
        while (_pending.TryDequeue(out var packet))
            packet.Dispose();
    }

    /// <summary>The next page of this stream at or after <paramref name="from"/> that carries a granule position.</summary>
    private async ValueTask<(OggPageHeader Header, long Start)?> FindStreamPageAsync(long from, long limit, CancellationToken cancellationToken)
    {
        long at = from;
        for (int guard = 0; guard < 4096 && at < limit; guard++)
        {
            long? next = await FindPageAsync(at, limit, cancellationToken).ConfigureAwait(false);
            if (next is null)
                return null;
            var page = await ReadPageAsync(next.Value, cancellationToken).ConfigureAwait(false);
            if (page is null)
                return null;
            var (header, _, start) = page.Value;
            if (header.Serial == _serial && header.GranulePosition >= 0)
                return (header, start);
            at = start + header.TotalLength;
        }

        return null;
    }

    /// <summary>The byte offset of the next "OggS" capture pattern at or after <paramref name="from"/>.</summary>
    private async ValueTask<long?> FindPageAsync(long from, long limit, CancellationToken cancellationToken)
    {
        long at = from;
        var window = new byte[ScanWindow];
        while (at < limit)
        {
            int read = await _source.ReadAtLeastAsync(at, window.AsMemory(0, (int)Math.Min(window.Length, limit - at)), cancellationToken).ConfigureAwait(false);
            if (read < 4)
                return null;
            int index = window.AsSpan(0, read).IndexOf("OggS"u8);
            if (index >= 0)
                return at + index;
            at += read - 3;
        }

        return null;
    }

    /// <summary>
    /// Reads the page at <paramref name="position"/>, or the next valid one when the bytes
    /// there are not a page or its CRC fails. Null at the end of the resource.
    /// </summary>
    private async ValueTask<(OggPageHeader Header, ReadOnlyMemory<byte> Body, long Start)?> ReadPageAsync(long position, CancellationToken cancellationToken)
    {
        long at = position;
        for (int attempts = 0; attempts < 1024; attempts++)
        {
            long? start = await FindPageAsync(at, _source.Length ?? long.MaxValue, cancellationToken).ConfigureAwait(false);
            if (start is null)
                return null;

            int got = await _source.ReadAtLeastAsync(start.Value, _pageBuffer.AsMemory(0, OggPageHeader.FixedHeaderLength + 255), cancellationToken).ConfigureAwait(false);
            if (!OggPageHeader.TryParse(_pageBuffer.AsSpan(0, got), out var header, out _))
            {
                at = start.Value + 4;
                continue;
            }

            int total = header.TotalLength;
            if (got < total)
            {
                int more = await _source.ReadAtLeastAsync(start.Value + got, _pageBuffer.AsMemory(got, total - got), cancellationToken).ConfigureAwait(false);
                got += more;
            }

            if (got < total)
                return null; // truncated final page

            if (OggCrc.Compute(_pageBuffer.AsSpan(0, total)) != header.Crc)
            {
                _context.Log.Emit(_context.Player, MediaEventKind.Stall, MediaLogLevel.Debug, "Skipping an Ogg page with a bad CRC.",
                    ("offset", start.Value.ToString(CultureInfo.InvariantCulture)));
                at = start.Value + 4;
                continue;
            }

            return (header, _pageBuffer.AsMemory(header.HeaderLength, header.BodyLength), start.Value);
        }

        return null;
    }

    public ValueTask DisposeAsync()
    {
        DrainPending();
        return ValueTask.CompletedTask;
    }
}
