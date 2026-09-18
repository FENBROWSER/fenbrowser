using System.Globalization;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Containers.Ogg;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Containers.Matroska;

/// <summary>
/// Matroska and WebM (RFC 8794 EBML; the Matroska specification; the WebM container
/// guidelines). Reads the EBML header, the Segment's Info and Tracks, then walks the
/// Clusters emitting one <see cref="EncodedPacket"/> per frame of every track with a
/// codec the engine can name. Seeks use the Cues (found through the SeekHead or in the
/// Segment walk) and fall back to scanning Cluster timestamps.
/// </summary>
/// <remarks>
/// Only the elements this engine acts on are parsed; everything else is skipped by size.
/// Every element that is read into memory is bounded first, and a Block's size is checked
/// against the packet ceiling for its track's kind before any allocation (design §4).
/// Unknown-size Segments and Clusters (live streams) are followed until a sibling element
/// or the end of the resource.
/// </remarks>
public sealed class MatroskaDemuxerFactory : IDemuxerFactory
{
    public static readonly MatroskaDemuxerFactory Instance = new();

    public string Name => "webm";

    public IReadOnlyList<string> MimeTypes { get; } = ["video/webm", "audio/webm", "video/x-matroska", "audio/x-matroska", "video/matroska"];

    /// <summary>WHATWG MIME Sniffing §6.2: an EBML header starts a WebM file.</summary>
    public int Probe(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && header[..4].SequenceEqual((ReadOnlySpan<byte>)[0x1A, 0x45, 0xDF, 0xA3]) ? 100 : 0;

    public IDemuxer Create(IByteSource source, MediaPipelineContext context) => new MatroskaDemuxer(source, context);
}

public sealed class MatroskaDemuxer : IDemuxer
{
    /// <summary>Largest Info, TrackEntry, BlockGroup wrapper or SeekHead this demuxer will read whole.</summary>
    private const int MaxHeaderElementBytes = 4 * 1024 * 1024;

    /// <summary>Largest Cues element read whole; a bigger one is ignored and seeks scan Clusters.</summary>
    private const int MaxCuesBytes = 32 * 1024 * 1024;

    private const int MaxCodecPrivateBytes = 1024 * 1024;
    private const int MaxLacedFrames = 256;
    private const int MaxSkippedElementsPerRead = 4096;
    private const long DefaultTimestampScale = 1_000_000;

    private readonly IByteSource _source;
    private readonly MediaPipelineContext _context;
    private readonly byte[] _scratch = new byte[Ebml.MaxHeaderLength + 16];
    private readonly Dictionary<ulong, TrackState> _tracks = [];
    private readonly List<CuePoint> _cues = [];
    private readonly List<(long Position, MediaTime Timestamp)> _clusterIndex = [];
    private readonly Queue<EncodedPacket> _pending = new();

    private long _timestampScale = DefaultTimestampScale;
    private long _segmentDataStart;
    private long? _segmentEnd;
    private long _firstCluster;
    private long? _cuesPosition;
    private bool _cuesParsed;
    private MediaTime _duration = MediaTime.PositiveInfinity;
    private MediaTrackInfo[] _trackInfos = [];
    private int _nextTrackId;

    // Cluster walk.
    private long _position;
    private bool _inCluster;
    private long? _clusterEnd;
    private long _clusterTimestamp;
    private bool _endOfStream;

    public MatroskaDemuxer(IByteSource source, MediaPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);
        _source = source;
        _context = context;
    }

    private sealed class TrackState
    {
        public int Id;
        public ulong Number;
        public MediaTrackKind Kind;
        public CodecConfig Config = null!;
        public long DefaultDurationNs;
        public long CodecDelayNs;
        public byte[] StrippedHeader = [];
        public string Language = "";
        public string Name = "";
        public bool IsDefault;
    }

    private readonly record struct CuePoint(MediaTime Time, ulong Track, long ClusterPosition);

    public async ValueTask<DemuxerInfo> InitializeAsync(CancellationToken cancellationToken)
    {
        // 1. The EBML header (RFC 8794 §11.2.1) names the document type.
        var header = await ReadHeaderAsync(0, null, cancellationToken).ConfigureAwait(false)
            ?? throw new MediaFormatException("The resource is empty.");
        if (header.Id != EbmlId.EbmlHeader)
            throw new MediaFormatException("The resource does not start with an EBML header.");
        var headerData = await ReadDataAsync(header, MaxHeaderElementBytes, cancellationToken).ConfigureAwait(false);
        ParseEbmlHeader(headerData);
        long position = header.End!.Value;

        // 2. The Segment holds everything else (Matroska §8.1). Unknown size means "to the end".
        var segment = await ReadHeaderAsync(position, null, cancellationToken).ConfigureAwait(false)
            ?? throw new MediaFormatException("The resource ends after its EBML header.");
        if (segment.Id != EbmlId.Segment)
            throw new MediaFormatException($"Expected a Segment after the EBML header, found element 0x{segment.Id:X}.");
        _segmentDataStart = segment.DataStart;
        _segmentEnd = segment.End ?? _source.Length;
        if (_segmentEnd is { } declaredEnd && _source.Length is { } length && declaredEnd > length)
            _segmentEnd = length; // truncated download: read what is there

        // 3. Top-level children up to the first Cluster.
        position = _segmentDataStart;
        bool haveTracks = false;
        bool reachedClusters = false;
        for (int guard = 0; guard < MaxSkippedElementsPerRead && !reachedClusters; guard++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var child = await ReadHeaderAsync(position, _segmentEnd, cancellationToken).ConfigureAwait(false);
            if (child is null || child.Value.Id == EbmlId.Cluster)
            {
                _firstCluster = position;
                reachedClusters = true;
                break;
            }

            var element = child.Value;

            if (element.DataLength is null)
                throw new MediaFormatException($"Element 0x{element.Id:X} may not have an unknown size.");

            switch (element.Id)
            {
                case EbmlId.SeekHead:
                    ParseSeekHead(await ReadDataAsync(element, MaxHeaderElementBytes, cancellationToken).ConfigureAwait(false));
                    break;
                case EbmlId.Info:
                    ParseInfo(await ReadDataAsync(element, MaxHeaderElementBytes, cancellationToken).ConfigureAwait(false));
                    break;
                case EbmlId.Tracks:
                    ParseTracks(await ReadDataAsync(element, MaxHeaderElementBytes, cancellationToken).ConfigureAwait(false));
                    haveTracks = true;
                    break;
                case EbmlId.Cues:
                    await ParseCuesAtAsync(element, cancellationToken).ConfigureAwait(false);
                    break;
            }

            position = element.End!.Value;
        }

        if (!reachedClusters)
            throw new MediaFormatException("Too many elements before the first Cluster.");
        if (!haveTracks)
            throw new MediaFormatException("The Segment has no Tracks element before its first Cluster.");
        if (_tracks.Count == 0)
            throw new MediaFormatException("No track uses a codec this engine can name.");

        // 4. Cues that live after the Clusters are reached through the SeekHead.
        if (!_cuesParsed && _cuesPosition is { } cuesPosition)
        {
            var cues = await ReadHeaderAsync(cuesPosition, _segmentEnd, cancellationToken).ConfigureAwait(false);
            if (cues is { Id: EbmlId.Cues, DataLength: not null })
                await ParseCuesAtAsync(cues.Value, cancellationToken).ConfigureAwait(false);
        }

        _position = _firstCluster;
        _inCluster = false;
        _trackInfos = _tracks.Values.OrderBy(t => t.Id)
            .Select(t => new MediaTrackInfo(t.Id, t.Config, _duration, t.Language, t.Name, t.IsDefault))
            .ToArray();
        foreach (var track in _trackInfos)
        {
            _context.Log.Emit(_context.Player, MediaEventKind.TrackAdded, MediaLogLevel.Info,
                $"Matroska: track {track.Id} {track.Config.Codec} {Describe(track.Config)}.",
                ("track", track.Id.ToString(CultureInfo.InvariantCulture)),
                ("kind", track.Kind.ToString().ToLowerInvariant()),
                ("codec", track.Config.Codec.ToString().ToLowerInvariant()));
        }

        bool seekable = _source.Length is not null;
        return new DemuxerInfo(_trackInfos, _duration, seekable);
    }

    private static string Describe(CodecConfig config) => config.Kind == MediaTrackKind.Video
        ? $"{config.Width}x{config.Height}"
        : $"{config.SampleRate} Hz {config.Channels} ch";

    // ---- header parsing (all from memory) ----

    private static void ParseEbmlHeader(ReadOnlyMemory<byte> data)
    {
        string docType = "matroska";
        ulong readVersion = 1;
        foreach (var (child, body) in Children(data))
        {
            if (child.Id == EbmlId.DocType)
                docType = Ebml.ReadString(body.Span);
            else if (child.Id == EbmlId.DocTypeReadVersion)
                readVersion = Ebml.ReadUnsigned(body.Span);
        }

        if (docType is not ("webm" or "matroska"))
            throw new MediaFormatException($"EBML document type \"{docType}\" is not Matroska or WebM.");
        if (readVersion > 4)
            throw new MediaFormatException($"DocTypeReadVersion {readVersion} is newer than this demuxer understands.");
    }

    private void ParseSeekHead(ReadOnlyMemory<byte> data)
    {
        foreach (var (seek, seekBody) in Children(data))
        {
            if (seek.Id != EbmlId.Seek)
                continue;
            uint id = 0;
            long position = -1;
            foreach (var (child, body) in Children(seekBody))
            {
                if (child.Id == EbmlId.SeekId)
                    id = (uint)Math.Min(uint.MaxValue, Ebml.ReadUnsigned(body.Span));
                else if (child.Id == EbmlId.SeekPosition)
                    position = (long)Math.Min(long.MaxValue, Ebml.ReadUnsigned(body.Span));
            }

            if (id == EbmlId.Cues && position >= 0)
                _cuesPosition = _segmentDataStart + position;
        }
    }

    /// <summary>Matroska §8.1.2: TimestampScale (ns per tick, default 1 ms) and the Segment Duration in ticks.</summary>
    private void ParseInfo(ReadOnlyMemory<byte> data)
    {
        double? durationTicks = null;
        foreach (var (child, body) in Children(data))
        {
            if (child.Id == EbmlId.TimestampScale)
            {
                ulong scale = Ebml.ReadUnsigned(body.Span);
                if (scale == 0 || scale > 1_000_000_000_000UL)
                    throw new MediaFormatException($"TimestampScale {scale} is out of range.");
                _timestampScale = (long)scale;
            }
            else if (child.Id == EbmlId.Duration)
            {
                durationTicks = Ebml.ReadFloat(body.Span);
            }
        }

        if (durationTicks is { } ticks)
        {
            if (double.IsNaN(ticks) || ticks < 0 || ticks > 1e15)
                throw new MediaFormatException($"Segment Duration {ticks} is out of range.");
            _duration = TicksToTime((long)Math.Round(ticks));
        }
    }

    private MediaTime TicksToTime(long ticks) => MediaTime.FromTimescale(ToNanoseconds((Int128)ticks * _timestampScale), 1_000_000_000);

    private void ParseTracks(ReadOnlyMemory<byte> data)
    {
        int entries = 0;
        foreach (var (entry, body) in Children(data))
        {
            if (entry.Id != EbmlId.TrackEntry)
                continue;
            if (++entries > _context.Limits.MaxTracks)
                throw new MediaLimitExceededException(nameof(MediaLimits.MaxTracks), entries, _context.Limits.MaxTracks);
            var track = ParseTrackEntry(body);
            if (track is null)
                continue;
            if (_tracks.ContainsKey(track.Number))
                throw new MediaFormatException($"Track number {track.Number} appears twice.");
            track.Id = _nextTrackId++;
            _tracks[track.Number] = track;
        }
    }

    /// <summary>Matroska §8.1.4.1: one TrackEntry. Returns null for a track the engine cannot decode or play.</summary>
    private TrackState? ParseTrackEntry(ReadOnlyMemory<byte> data)
    {
        ulong number = 0;
        ulong type = 0;
        string codecId = "";
        ReadOnlyMemory<byte> codecPrivate = default;
        long defaultDuration = 0;
        long codecDelay = 0;
        bool isDefault = true;
        string language = "";
        string name = "";
        int width = 0, height = 0, channels = 0, bitDepth = 0;
        double samplingFrequency = 0;
        byte[] stripped = [];
        bool unsupportedEncoding = false;

        foreach (var (child, body) in Children(data))
        {
            switch (child.Id)
            {
                case EbmlId.TrackNumber: number = Ebml.ReadUnsigned(body.Span); break;
                case EbmlId.TrackType: type = Ebml.ReadUnsigned(body.Span); break;
                case EbmlId.CodecId: codecId = Ebml.ReadString(body.Span); break;
                case EbmlId.CodecPrivate:
                    if (body.Length > MaxCodecPrivateBytes)
                        throw new MediaLimitExceededException("CodecPrivate", body.Length, MaxCodecPrivateBytes);
                    codecPrivate = body;
                    break;
                case EbmlId.DefaultDuration: defaultDuration = (long)Math.Min(long.MaxValue, Ebml.ReadUnsigned(body.Span)); break;
                case EbmlId.CodecDelay: codecDelay = (long)Math.Min(long.MaxValue, Ebml.ReadUnsigned(body.Span)); break;
                case EbmlId.FlagDefault: isDefault = Ebml.ReadUnsigned(body.Span) != 0; break;
                case EbmlId.Language: language = Ebml.ReadString(body.Span); break;
                case EbmlId.LanguageBcp47: language = Ebml.ReadString(body.Span); break;
                case EbmlId.Name: name = Ebml.ReadString(body.Span); break;
                case EbmlId.Video:
                    foreach (var (v, vBody) in Children(body))
                    {
                        if (v.Id == EbmlId.PixelWidth) width = (int)Math.Min(int.MaxValue, Ebml.ReadUnsigned(vBody.Span));
                        else if (v.Id == EbmlId.PixelHeight) height = (int)Math.Min(int.MaxValue, Ebml.ReadUnsigned(vBody.Span));
                    }
                    break;
                case EbmlId.Audio:
                    foreach (var (a, aBody) in Children(body))
                    {
                        if (a.Id == EbmlId.SamplingFrequency) samplingFrequency = Ebml.ReadFloat(aBody.Span);
                        else if (a.Id == EbmlId.Channels) channels = (int)Math.Min(int.MaxValue, Ebml.ReadUnsigned(aBody.Span));
                        else if (a.Id == EbmlId.BitDepth) bitDepth = (int)Math.Min(int.MaxValue, Ebml.ReadUnsigned(aBody.Span));
                    }
                    break;
                case EbmlId.ContentEncodings:
                    (stripped, unsupportedEncoding) = ParseContentEncodings(body);
                    break;
            }
        }

        if (number == 0)
            throw new MediaFormatException("A TrackEntry has no TrackNumber.");
        if (unsupportedEncoding)
            throw new MediaFormatException($"Track {number} uses a content encoding (compression or encryption) this engine does not support.");

        // Matroska §8.1.4.1.3: 1 video, 2 audio, 17 subtitle; others are skipped.
        MediaTrackKind kind;
        switch (type)
        {
            case 1: kind = MediaTrackKind.Video; break;
            case 2: kind = MediaTrackKind.Audio; break;
            default:
                return null;
        }

        var (codec, codecString) = MapCodec(codecId, kind);
        if (codec == MediaCodec.Unknown)
        {
            _context.Log.Emit(_context.Player, MediaEventKind.TrackAdded, MediaLogLevel.Info,
                $"Matroska: skipping track {number} with codec \"{codecId}\".",
                ("track", number.ToString(CultureInfo.InvariantCulture)), ("codecId", codecId));
            return null;
        }

        CodecConfig config;
        if (kind == MediaTrackKind.Video)
        {
            _context.Limits.CheckVideoDimensions(width, height);
            config = new CodecConfig(kind, codec, codecString, Width: width, Height: height, Extradata: codecPrivate);
        }
        else
        {
            if (double.IsNaN(samplingFrequency) || samplingFrequency <= 0 || samplingFrequency > int.MaxValue)
                throw new MediaFormatException($"Track {number} has an invalid SamplingFrequency.");
            int sampleRate = codec == MediaCodec.Opus ? 48000 : (int)Math.Round(samplingFrequency);
            if (channels == 0)
                channels = 1; // the Matroska default
            _context.Limits.CheckAudioFormat(sampleRate, channels);
            var pcmFormat = codec == MediaCodec.Pcm ? PcmFormatFor(codecId, bitDepth) : PcmSampleFormat.None;
            config = new CodecConfig(kind, codec, codecString, SampleRate: sampleRate, Channels: channels, PcmFormat: pcmFormat, Extradata: codecPrivate);
        }

        return new TrackState
        {
            Number = number,
            Kind = kind,
            Config = config,
            DefaultDurationNs = defaultDuration,
            CodecDelayNs = kind == MediaTrackKind.Audio ? codecDelay : 0,
            StrippedHeader = stripped,
            Language = language,
            Name = name,
            IsDefault = isDefault,
        };
    }

    /// <summary>
    /// Matroska §8.1.4.1.30: only header stripping (ContentCompAlgo 3) is accepted; the bytes
    /// it removed are put back in front of every frame. Anything else marks the track unsupported.
    /// </summary>
    private static (byte[] Stripped, bool Unsupported) ParseContentEncodings(ReadOnlyMemory<byte> data)
    {
        byte[] stripped = [];
        bool unsupported = false;
        foreach (var (encoding, encodingBody) in Children(data))
        {
            if (encoding.Id != EbmlId.ContentEncoding)
                continue;
            ulong scope = 1;
            ulong encodingType = 0;
            ulong algo = 0;
            ReadOnlyMemory<byte> settings = default;
            bool hasCompression = false;
            foreach (var (child, body) in Children(encodingBody))
            {
                switch (child.Id)
                {
                    case EbmlId.ContentEncodingScope: scope = Ebml.ReadUnsigned(body.Span); break;
                    case EbmlId.ContentEncodingType: encodingType = Ebml.ReadUnsigned(body.Span); break;
                    case EbmlId.ContentCompression:
                        hasCompression = true;
                        foreach (var (c, cBody) in Children(body))
                        {
                            if (c.Id == EbmlId.ContentCompAlgo) algo = Ebml.ReadUnsigned(cBody.Span);
                            else if (c.Id == EbmlId.ContentCompSettings) settings = cBody;
                        }
                        break;
                }
            }

            if (encodingType != 0 || !hasCompression || algo != 3 || (scope & 1) == 0 || settings.Length > 1024)
                unsupported = true;
            else
                stripped = settings.ToArray();
        }

        return (stripped, unsupported);
    }

    /// <summary>Matroska codec IDs (the "Codec Mappings" registry) to the engine's names.</summary>
    internal static (MediaCodec Codec, string? CodecString) MapCodec(string codecId, MediaTrackKind kind)
    {
        if (kind == MediaTrackKind.Video)
        {
            return codecId switch
            {
                "V_VP8" => (MediaCodec.Vp8, "vp8"),
                "V_VP9" => (MediaCodec.Vp9, "vp9"),
                "V_AV1" => (MediaCodec.Av1, "av01"),
                "V_MPEG4/ISO/AVC" => (MediaCodec.H264, "avc1"),
                "V_MPEGH/ISO/HEVC" => (MediaCodec.Hevc, "hev1"),
                _ => (MediaCodec.Unknown, null),
            };
        }

        if (codecId.StartsWith("A_AAC", StringComparison.Ordinal))
            return (MediaCodec.Aac, "mp4a.40.2");
        return codecId switch
        {
            "A_OPUS" => (MediaCodec.Opus, "opus"),
            "A_VORBIS" => (MediaCodec.Vorbis, "vorbis"),
            "A_FLAC" => (MediaCodec.Flac, "flac"),
            "A_MPEG/L3" => (MediaCodec.Mp3, "mp3"),
            "A_PCM/INT/LIT" or "A_PCM/FLOAT/IEEE" => (MediaCodec.Pcm, "1"),
            _ => (MediaCodec.Unknown, null),
        };
    }

    private static PcmSampleFormat PcmFormatFor(string codecId, int bitDepth) => (codecId, bitDepth) switch
    {
        ("A_PCM/INT/LIT", 8) => PcmSampleFormat.U8,
        ("A_PCM/INT/LIT", 16) => PcmSampleFormat.S16,
        ("A_PCM/INT/LIT", 24) => PcmSampleFormat.S24,
        ("A_PCM/INT/LIT", 32) => PcmSampleFormat.S32,
        ("A_PCM/FLOAT/IEEE", 32) => PcmSampleFormat.F32,
        ("A_PCM/FLOAT/IEEE", 64) => PcmSampleFormat.F64,
        _ => throw new MediaFormatException($"PCM track with {bitDepth} bits per sample ({codecId}) is not supported."),
    };

    private async ValueTask ParseCuesAtAsync(EbmlElement cues, CancellationToken cancellationToken)
    {
        if (cues.DataLength > MaxCuesBytes)
        {
            _context.Log.Emit(_context.Player, MediaEventKind.LimitExceeded, MediaLogLevel.Warn,
                $"Cues of {cues.DataLength} bytes are too large to index; seeks will scan.",
                ("limit", "MaxCuesBytes"), ("value", cues.DataLength.Value.ToString(CultureInfo.InvariantCulture)),
                ("max", MaxCuesBytes.ToString(CultureInfo.InvariantCulture)));
            _cuesParsed = true;
            return;
        }

        var data = await ReadDataAsync(cues, MaxCuesBytes, cancellationToken).ConfigureAwait(false);
        ParseCues(data);
        _cuesParsed = true;
    }

    /// <summary>Matroska §8.1.5: CuePoint (CueTime, CueTrackPositions (CueTrack, CueClusterPosition)).</summary>
    private void ParseCues(ReadOnlyMemory<byte> data)
    {
        foreach (var (point, pointBody) in Children(data))
        {
            if (point.Id != EbmlId.CuePoint)
                continue;
            long time = -1;
            foreach (var (child, body) in Children(pointBody))
            {
                if (child.Id == EbmlId.CueTime)
                {
                    time = (long)Math.Min(long.MaxValue / 2, Ebml.ReadUnsigned(body.Span));
                }
                else if (child.Id == EbmlId.CueTrackPositions && time >= 0)
                {
                    ulong track = 0;
                    long cluster = -1;
                    foreach (var (p, pBody) in Children(body))
                    {
                        if (p.Id == EbmlId.CueTrack) track = Ebml.ReadUnsigned(pBody.Span);
                        else if (p.Id == EbmlId.CueClusterPosition) cluster = (long)Math.Min(long.MaxValue / 2, Ebml.ReadUnsigned(pBody.Span));
                    }

                    if (cluster < 0)
                        continue;
                    if (_cues.Count >= _context.Limits.MaxSampleTableEntries)
                        throw new MediaLimitExceededException(nameof(MediaLimits.MaxSampleTableEntries), _cues.Count + 1, _context.Limits.MaxSampleTableEntries);
                    _cues.Add(new CuePoint(TicksToTime(time), track, _segmentDataStart + cluster));
                }
            }
        }

        _cues.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    /// <summary>The child elements of a master element held in memory, in order; a malformed tail ends the sequence.</summary>
    private static IEnumerable<(EbmlElement Element, ReadOnlyMemory<byte> Body)> Children(ReadOnlyMemory<byte> data)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            if (!Ebml.TryReadElement(data.Span[offset..], offset, out var element))
                throw new MediaFormatException("A master element contains a malformed child header.");
            if (element.DataLength is not { } length)
                throw new MediaFormatException($"Element 0x{element.Id:X} may not have an unknown size.");
            long end = element.DataStart + length;
            if (end > data.Length)
                throw new MediaFormatException($"Element 0x{element.Id:X} runs past its parent.");
            yield return (element, data.Slice((int)element.DataStart, (int)length));
            offset = (int)end;
        }
    }

    // ---- cluster walk ----

    public async ValueTask<EncodedPacket?> ReadPacketAsync(CancellationToken cancellationToken)
    {
        if (_trackInfos.Length == 0)
            throw new InvalidOperationException("InitializeAsync has not run.");

        for (int guard = 0; guard < MaxSkippedElementsPerRead; guard++)
        {
            if (_pending.TryDequeue(out var ready))
                return ready;
            if (_endOfStream)
                return null;

            long? limit = _inCluster ? _clusterEnd ?? _segmentEnd : _segmentEnd;
            var next = await ReadHeaderAsync(_position, limit, cancellationToken).ConfigureAwait(false);
            if (next is null)
            {
                if (_inCluster && _clusterEnd is { } clusterEnd && clusterEnd < (_segmentEnd ?? long.MaxValue))
                {
                    // The Cluster ended exactly at its declared size: continue with the Segment.
                    _inCluster = false;
                    _position = clusterEnd;
                    continue;
                }

                _endOfStream = true;
                return null;
            }

            var element = next.Value;
            if (element.Id == EbmlId.Cluster)
            {
                _inCluster = true;
                _clusterEnd = element.End;
                _position = element.DataStart;
                continue;
            }

            if (!_inCluster || IsSegmentLevel(element.Id))
            {
                // A Segment-level element (Cues, Tags, another Cluster) also ends an unknown-size Cluster.
                _inCluster = false;
                if (element.DataLength is not { } skip)
                    throw new MediaFormatException($"Element 0x{element.Id:X} may not have an unknown size.");
                _position = element.DataStart + skip;
                continue;
            }

            if (element.DataLength is not { } length)
                throw new MediaFormatException($"Element 0x{element.Id:X} inside a Cluster may not have an unknown size.");

            switch (element.Id)
            {
                case EbmlId.Timestamp:
                    var ts = await ReadDataAsync(element, 8, cancellationToken).ConfigureAwait(false);
                    _clusterTimestamp = (long)Math.Min(long.MaxValue / 4, Ebml.ReadUnsigned(ts.Span));
                    break;
                case EbmlId.SimpleBlock:
                    await ReadBlockAsync(element, isSimpleBlock: true, groupDurationTicks: null, hasReference: false, cancellationToken).ConfigureAwait(false);
                    break;
                case EbmlId.BlockGroup:
                    await ReadBlockGroupAsync(element, cancellationToken).ConfigureAwait(false);
                    break;
            }

            _position = element.DataStart + length;
        }

        throw new MediaFormatException("Too many elements without a frame in a row.");
    }

    private static bool IsSegmentLevel(uint id) => id is EbmlId.Cluster or EbmlId.Cues or EbmlId.Tags or EbmlId.Attachments
        or EbmlId.Chapters or EbmlId.SeekHead or EbmlId.Info or EbmlId.Tracks or EbmlId.EbmlHeader;

    /// <summary>Matroska §8.1.6.3: Block, BlockDuration and ReferenceBlock (whose presence means "not a keyframe").</summary>
    private async ValueTask ReadBlockGroupAsync(EbmlElement group, CancellationToken cancellationToken)
    {
        int max = Math.Max(_context.Limits.MaxVideoPacketBytes, _context.Limits.MaxAudioPacketBytes) + 1024;
        if (group.DataLength > max)
            throw new MediaLimitExceededException(nameof(MediaLimits.MaxVideoPacketBytes), group.DataLength.Value, max);
        var data = await ReadDataAsync(group, max, cancellationToken).ConfigureAwait(false);

        EbmlElement? block = null;
        ReadOnlyMemory<byte> blockBody = default;
        long? duration = null;
        bool hasReference = false;
        foreach (var (child, body) in Children(data))
        {
            switch (child.Id)
            {
                case EbmlId.Block: block = child; blockBody = body; break;
                case EbmlId.BlockDuration: duration = (long)Math.Min(long.MaxValue / 4, Ebml.ReadUnsigned(body.Span)); break;
                case EbmlId.ReferenceBlock: hasReference = true; break;
            }
        }

        if (block is null)
            return;
        EmitFrames(blockBody.Span, isSimpleBlock: false, duration, hasReference);
    }

    private async ValueTask ReadBlockAsync(EbmlElement block, bool isSimpleBlock, long? groupDurationTicks, bool hasReference, CancellationToken cancellationToken)
    {
        long length = block.DataLength!.Value;
        int max = Math.Max(_context.Limits.MaxVideoPacketBytes, _context.Limits.MaxAudioPacketBytes) + 16;
        if (length > max)
            throw new MediaLimitExceededException(nameof(MediaLimits.MaxVideoPacketBytes), length, max);

        // Read the block head first so an unlaced frame can land straight in its packet.
        int headLength = (int)Math.Min(length, 12);
        int got = await _source.ReadAtLeastAsync(block.DataStart, _scratch.AsMemory(0, headLength), cancellationToken).ConfigureAwait(false);
        if (got < headLength)
            throw new MediaFormatException("A Block is truncated.");
        if (!TryParseBlockHead(_scratch.AsSpan(0, headLength), out var head))
            throw new MediaFormatException("A Block has a malformed head.");
        if (!_tracks.TryGetValue(head.TrackNumber, out var track))
            return; // a track we do not play

        if (head.Lacing == 0)
        {
            if (length < head.Length)
                throw new MediaFormatException("A Block is shorter than its head.");
            int frameLength = (int)(length - head.Length);
            // The bytes of the frame already in the scratch buffer carry the Opus TOC.
            var (pts, frameDuration) = FrameTiming(track, head.RelativeTimestamp, groupDurationTicks, 1, 0, _scratch.AsSpan(head.Length, headLength - head.Length));
            var packet = RentFrame(track, frameLength, pts, frameDuration, isSimpleBlock ? head.IsKeyframe : !hasReference);
            var destination = packet.Memory[track.StrippedHeader.Length..];
            int read = await _source.ReadAtLeastAsync(block.DataStart + head.Length, destination, cancellationToken).ConfigureAwait(false);
            if (read < destination.Length)
            {
                packet.Dispose();
                throw new MediaFormatException("A Block is truncated.");
            }

            _pending.Enqueue(packet);
            return;
        }

        var data = await ReadDataAsync(block, max, cancellationToken).ConfigureAwait(false);
        EmitFrames(data.Span, isSimpleBlock, groupDurationTicks, hasReference);
    }

    public readonly record struct BlockHead(ulong TrackNumber, int RelativeTimestamp, bool IsKeyframe, int Lacing, int Length);

    /// <summary>Matroska §8.1.6.2 "Block Structure": track number VINT, int16 timestamp, flags.</summary>
    public static bool TryParseBlockHead(ReadOnlySpan<byte> bytes, out BlockHead head)
    {
        head = default;
        if (!Ebml.TryReadSize(bytes, out long? number, out int numberLength) || number is null)
            return false;
        if (bytes.Length < numberLength + 3)
            return false;
        int relative = (short)((bytes[numberLength] << 8) | bytes[numberLength + 1]);
        byte flags = bytes[numberLength + 2];
        head = new BlockHead((ulong)number.Value, relative, (flags & 0x80) != 0, (flags >> 1) & 3, numberLength + 3);
        return true;
    }

    /// <summary>Splits a (possibly laced) block body into frames and queues them.</summary>
    private void EmitFrames(ReadOnlySpan<byte> block, bool isSimpleBlock, long? groupDurationTicks, bool hasReference)
    {
        if (!TryParseBlockHead(block, out var head))
            throw new MediaFormatException("A Block has a malformed head.");
        if (!_tracks.TryGetValue(head.TrackNumber, out var track))
            return;
        bool keyframe = isSimpleBlock ? head.IsKeyframe : !hasReference;
        var body = block[head.Length..];

        Span<int> sizes = stackalloc int[MaxLacedFrames];
        int count = SplitLacing(head.Lacing, ref body, sizes);
        int offset = 0;
        long elapsedNs = 0;
        for (int i = 0; i < count; i++)
        {
            var frame = body.Slice(offset, sizes[i]);
            offset += sizes[i];
            var (pts, duration) = FrameTiming(track, head.RelativeTimestamp, groupDurationTicks, count, elapsedNs, frame);
            elapsedNs += duration.IsInfinite ? 0 : duration.Microseconds * 1000;
            var packet = RentFrame(track, frame.Length, pts, duration, keyframe);
            frame.CopyTo(packet.Memory.Span[track.StrippedHeader.Length..]);
            _pending.Enqueue(packet);
        }
    }

    /// <summary>
    /// Matroska §8.1.6.2.1 lacing: 0 none, 1 Xiph, 2 fixed-size, 3 EBML. Fills
    /// <paramref name="sizes"/>, advances <paramref name="body"/> past the lacing head and
    /// returns the frame count. Every size is checked against the bytes that remain.
    /// </summary>
    private static int SplitLacing(int lacing, scoped ref ReadOnlySpan<byte> body, scoped Span<int> sizes)
    {
        if (lacing == 0)
        {
            sizes[0] = body.Length;
            return 1;
        }

        if (body.Length < 1)
            throw new MediaFormatException("A laced Block has no frame count.");
        int count = body[0] + 1;
        if (count > MaxLacedFrames)
            throw new MediaFormatException($"A Block laces {count} frames.");
        int at = 1;
        long total = 0;
        switch (lacing)
        {
            case 1: // Xiph: each of the first count-1 sizes is a run of 255s ended by a smaller byte
                for (int i = 0; i < count - 1; i++)
                {
                    int size = 0;
                    while (true)
                    {
                        if (at >= body.Length)
                            throw new MediaFormatException("Xiph lacing runs past the Block.");
                        int b = body[at++];
                        size += b;
                        if (b < 255)
                            break;
                    }

                    sizes[i] = size;
                    total += size;
                }
                break;

            case 2: // fixed: the remaining bytes divide evenly
                int remaining = body.Length - at;
                if (remaining % count != 0)
                    throw new MediaFormatException("Fixed-size lacing does not divide the Block evenly.");
                for (int i = 0; i < count - 1; i++)
                {
                    sizes[i] = remaining / count;
                    total += sizes[i];
                }
                break;

            case 3: // EBML: first size as an unsigned VINT, then signed VINT differences
                long previous = 0;
                for (int i = 0; i < count - 1; i++)
                {
                    if (!Ebml.TryReadSize(body[at..], out long? raw, out int vintLength) || raw is null)
                        throw new MediaFormatException("EBML lacing has a malformed size.");
                    at += vintLength;
                    long size = i == 0 ? raw.Value : previous + (raw.Value - ((1L << (7 * vintLength - 1)) - 1));
                    if (size < 0 || size > int.MaxValue)
                        throw new MediaFormatException("EBML lacing has a negative frame size.");
                    sizes[i] = (int)size;
                    previous = size;
                    total += size;
                }
                break;
        }

        long last = body.Length - at - total;
        if (last < 0)
            throw new MediaFormatException("Laced frame sizes exceed the Block.");
        sizes[count - 1] = (int)last;
        body = body[at..];
        return count;
    }

    /// <summary>
    /// The frame's presentation time: Cluster Timestamp plus the Block's relative timestamp,
    /// in TimestampScale ticks, less the track's CodecDelay (Matroska §8.1.4.1.24); laced
    /// frames follow one another by their durations.
    /// </summary>
    private (MediaTime Pts, MediaTime Duration) FrameTiming(TrackState track, int relativeTicks, long? groupDurationTicks, int laceCount, long elapsedNs, ReadOnlySpan<byte> frame)
    {
        Int128 ns = (Int128)(_clusterTimestamp + relativeTicks) * _timestampScale - track.CodecDelayNs + elapsedNs;
        MediaTime pts = MediaTime.FromTimescale(ToNanoseconds(ns), 1_000_000_000);

        MediaTime duration;
        if (track.Config.Codec == MediaCodec.Opus && !frame.IsEmpty)
            duration = MediaTime.FromTimescale(OggDemuxer.OpusPacketSamples(frame), 48000);
        else if (groupDurationTicks is { } group)
            duration = MediaTime.FromTimescale(ToNanoseconds((Int128)group * _timestampScale / laceCount), 1_000_000_000);
        else if (track.DefaultDurationNs > 0)
            duration = MediaTime.FromTimescale(track.DefaultDurationNs, 1_000_000_000);
        else
            duration = MediaTime.Zero;
        return (pts, duration);
    }

    /// <summary>Matroska timestamps are bounded to what a 64-bit nanosecond count can hold (about 292 years).</summary>
    private static long ToNanoseconds(Int128 value)
    {
        if (value > long.MaxValue / 2 || value < long.MinValue / 2)
            throw new MediaFormatException("A Block timestamp is out of range.");
        return (long)value;
    }

    private EncodedPacket RentFrame(TrackState track, int frameLength, MediaTime pts, MediaTime duration, bool keyframe)
    {
        int total = checked(frameLength + track.StrippedHeader.Length);
        var packet = EncodedPacket.Rent(_context.Limits, track.Kind, track.Id, total, pts, pts, duration, track.Kind == MediaTrackKind.Audio || keyframe);
        track.StrippedHeader.CopyTo(packet.Memory.Span);
        return packet;
    }

    // ---- seeking ----

    /// <summary>
    /// Positions on the Cluster the Cues name for the video track (or any track) at or
    /// before the target; without Cues, on the last Cluster whose Timestamp is at or before
    /// it. Frames before the target are the caller's to discard.
    /// </summary>
    public async ValueTask SeekAsync(MediaTime target, CancellationToken cancellationToken)
    {
        if (_trackInfos.Length == 0)
            throw new InvalidOperationException("InitializeAsync has not run.");
        DrainPending();
        _endOfStream = false;
        _inCluster = false;
        _clusterEnd = null;

        if (target <= MediaTime.Zero)
        {
            _position = _firstCluster;
            return;
        }

        var videoTrack = _tracks.Values.FirstOrDefault(t => t.Kind == MediaTrackKind.Video);
        if (_cues.Count > 0)
        {
            long best = -1;
            foreach (var cue in _cues)
            {
                if (videoTrack is not null && cue.Track != videoTrack.Number)
                    continue;
                if (cue.Time > target)
                    break;
                best = cue.ClusterPosition;
            }

            if (best < 0)
                best = _cues.Count > 0 ? _cues[0].ClusterPosition : _firstCluster;
            _position = best >= _firstCluster && best < (_segmentEnd ?? long.MaxValue) ? best : _firstCluster;
            return;
        }

        // No Cues: walk Cluster headers, extending the index built so far.
        long at = _clusterIndex.Count > 0 ? _clusterIndex[^1].Position : _firstCluster;
        for (int guard = 0; guard < _context.Limits.MaxSampleTableEntries; guard++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var element = await ReadHeaderAsync(at, _segmentEnd, cancellationToken).ConfigureAwait(false);
            if (element is null)
                break;
            var e = element.Value;
            if (e.Id != EbmlId.Cluster)
            {
                if (e.DataLength is not { } skip)
                    break;
                at = e.DataStart + skip;
                continue;
            }

            var timestamp = await ReadClusterTimestampAsync(e, cancellationToken).ConfigureAwait(false);
            if (_clusterIndex.Count == 0 || _clusterIndex[^1].Position < at)
                _clusterIndex.Add((at, timestamp));
            if (timestamp > target)
                break;
            if (e.End is not { } end)
                break; // an unknown-size Cluster: nothing after it can be located
            at = end;
        }

        long position = _firstCluster;
        foreach (var (clusterPosition, timestamp) in _clusterIndex)
        {
            if (timestamp > target)
                break;
            position = clusterPosition;
        }

        _position = position;
    }

    private async ValueTask<MediaTime> ReadClusterTimestampAsync(EbmlElement cluster, CancellationToken cancellationToken)
    {
        long at = cluster.DataStart;
        for (int guard = 0; guard < 16; guard++)
        {
            var child = await ReadHeaderAsync(at, cluster.End ?? _segmentEnd, cancellationToken).ConfigureAwait(false);
            if (child is null || child.Value.DataLength is not { } length)
                break;
            if (child.Value.Id == EbmlId.Timestamp)
            {
                var data = await ReadDataAsync(child.Value, 8, cancellationToken).ConfigureAwait(false);
                return TicksToTime((long)Math.Min(long.MaxValue / 4, Ebml.ReadUnsigned(data.Span)));
            }

            if (child.Value.Id is EbmlId.SimpleBlock or EbmlId.BlockGroup)
                break;
            at = child.Value.DataStart + length;
        }

        return MediaTime.Zero;
    }

    private void DrainPending()
    {
        while (_pending.TryDequeue(out var packet))
            packet.Dispose();
    }

    // ---- bounded reads ----

    /// <summary>The element header at <paramref name="position"/>, or null at the end of the resource or of <paramref name="limit"/>.</summary>
    private async ValueTask<EbmlElement?> ReadHeaderAsync(long position, long? limit, CancellationToken cancellationToken)
    {
        long available = limit is { } l ? l - position : Ebml.MaxHeaderLength;
        if (available <= 0)
            return null;
        int want = (int)Math.Min(Ebml.MaxHeaderLength, available);
        int got = await _source.ReadAtLeastAsync(position, _scratch.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
        if (got == 0)
            return null;
        if (!Ebml.TryReadElement(_scratch.AsSpan(0, got), position, out var element))
            throw new MediaFormatException($"Malformed element header at byte {position}.");
        if (element.End is { } end && limit is { } bound && end > bound)
        {
            // A truncated resource: let the caller read what exists, never past the parent.
            if (_source.Length is { } length && end > length)
                throw new MediaFormatException($"Element 0x{element.Id:X} at byte {position} runs past the end of the resource.");
            throw new MediaFormatException($"Element 0x{element.Id:X} at byte {position} runs past its parent.");
        }

        return element;
    }

    private async ValueTask<ReadOnlyMemory<byte>> ReadDataAsync(EbmlElement element, int max, CancellationToken cancellationToken)
    {
        if (element.DataLength is not { } length)
            throw new MediaFormatException($"Element 0x{element.Id:X} may not have an unknown size.");
        if (length > max)
            throw new MediaLimitExceededException($"Element0x{element.Id:X}", length, max);
        var buffer = new byte[length];
        int got = await _source.ReadAtLeastAsync(element.DataStart, buffer, cancellationToken).ConfigureAwait(false);
        if (got < length)
            throw new MediaFormatException($"Element 0x{element.Id:X} is truncated.");
        return buffer;
    }

    public ValueTask DisposeAsync()
    {
        DrainPending();
        return ValueTask.CompletedTask;
    }
}
