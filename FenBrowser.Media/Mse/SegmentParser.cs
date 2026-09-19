using System.Buffers.Binary;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Containers.Matroska;
using FenBrowser.Media.Containers.Mp4;
using FenBrowser.Media.Pipeline;
using FenBrowser.Media.Types;

namespace FenBrowser.Media.Mse;

/// <summary>What the segment parser found in the appended bytes so far.</summary>
public enum SegmentParseStatus
{
    /// <summary>Everything complete was consumed; the rest waits for more bytes.</summary>
    Ok,

    /// <summary>The bytes are not the format this byte stream carries (MSE "append error").</summary>
    Error,
}

/// <summary>
/// The MSE "segment parser loop" for one byte stream format (MSE §3.5.9): the appended
/// bytes are held until a complete initialization segment or media segment is there, then
/// handed to the container demuxer in the format's own units. Partial data stays pending;
/// bytes that cannot belong to the format are an append error.
/// </summary>
public abstract class SegmentParser
{
    private readonly List<byte> _pending = [];

    protected SegmentParser(MediaPipelineContext context)
    {
        Context = context;
    }

    protected MediaPipelineContext Context { get; }

    /// <summary>The last initialization segment, as bytes the demuxer can be run over.</summary>
    protected byte[]? InitializationSegment { get; set; }

    public bool HasInitializationSegment => InitializationSegment is not null;

    /// <summary>False when the container's frame durations are nominal rather than timed, so the coded frame processing measures them from the next frame instead.</summary>
    public virtual bool FrameDurationsAreReliable => true;

    /// <summary>The initialization segment's tracks; set again whenever a new one arrives.</summary>
    public DemuxerInfo? Info { get; private set; }

    /// <summary>The byte stream format for a MIME type, or null when it is not one MSE supports.</summary>
    public static SegmentParser? Create(string mimeType, MediaPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(mimeType);
        ArgumentNullException.ThrowIfNull(context);
        var essence = MimeType.Parse(mimeType)?.Essence;
        return essence switch
        {
            "video/mp4" or "audio/mp4" => new Mp4SegmentParser(context),
            "video/webm" or "audio/webm" => new WebmSegmentParser(context),
            _ => null,
        };
    }

    /// <summary>
    /// Appends bytes and runs the loop. Every complete initialization segment is reported
    /// through <paramref name="onInitializationSegment"/> (with its tracks) and every complete
    /// media segment's packets through <paramref name="onPacket"/>, in the order they appear.
    /// Packets handed out are owned by the receiver.
    /// </summary>
    public SegmentParseStatus Append(ReadOnlySpan<byte> bytes, Action<DemuxerInfo> onInitializationSegment, Action<EncodedPacket> onPacket)
    {
        ArgumentNullException.ThrowIfNull(onInitializationSegment);
        ArgumentNullException.ThrowIfNull(onPacket);
        _pending.AddRange(bytes);
        var data = _pending.ToArray();
        int consumed;
        SegmentParseStatus status;
        try
        {
            status = Parse(data, out consumed, onInitializationSegment, onPacket);
        }
        catch (MediaFormatException ex)
        {
            LastError = ex.Message;
            status = SegmentParseStatus.Error;
            consumed = data.Length;
        }
        catch (MediaLimitExceededException ex)
        {
            LastError = ex.Message;
            status = SegmentParseStatus.Error;
            consumed = data.Length;
        }

        _pending.RemoveRange(0, Math.Min(consumed, _pending.Count));
        return status;
    }

    /// <summary>Why the last append was an error, for the media log.</summary>
    public string? LastError { get; private set; }

    /// <summary>Drops pending bytes (MSE "reset parser state"); the initialization segment is kept.</summary>
    public virtual void ResetParserState() => _pending.Clear();

    public int PendingBytes => _pending.Count;

    /// <summary>
    /// §3.5.1 append state PARSING_MEDIA_SEGMENT: bytes of a media segment arrived without
    /// its end, so mode and timestampOffset cannot change until it completes or the parser
    /// is reset.
    /// </summary>
    public bool ParsingMediaSegment => _pending.Count > 0 && PendingIsMediaSegment(_pending);

    /// <summary>Whether the pending bytes begin a media segment rather than an initialization segment.</summary>
    protected abstract bool PendingIsMediaSegment(List<byte> pending);

    /// <summary>
    /// Parses as many complete units as <paramref name="data"/> holds, reporting how many bytes
    /// they took. Throws <see cref="MediaFormatException"/> for bytes that break the format.
    /// </summary>
    protected abstract SegmentParseStatus Parse(byte[] data, out int consumed, Action<DemuxerInfo> onInitializationSegment, Action<EncodedPacket> onPacket);

    /// <summary>Runs <paramref name="factory"/>'s demuxer over the initialization segment alone for its tracks.</summary>
    protected DemuxerInfo ReadInitialization(IDemuxerFactory factory, byte[] initialization)
    {
        var demuxer = factory.Create(new MemoryByteSource(initialization), Context);
        try
        {
            var info = demuxer.InitializeAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            Info = info;
            return info;
        }
        finally
        {
            demuxer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Runs the demuxer over the initialization segment followed by one media segment and hands out its packets.</summary>
    protected void ReadMediaSegment(IDemuxerFactory factory, ReadOnlySpan<byte> segment, Action<EncodedPacket> onPacket)
    {
        var initialization = InitializationSegment ?? throw new MediaFormatException("A media segment arrived before an initialization segment.");
        var composed = new byte[initialization.Length + segment.Length];
        initialization.CopyTo(composed, 0);
        segment.CopyTo(composed.AsSpan(initialization.Length));
        var demuxer = factory.Create(new MemoryByteSource(composed), Context);
        try
        {
            _ = demuxer.InitializeAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            while (demuxer.ReadPacketAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult() is { } packet)
                onPacket(packet);
        }
        finally
        {
            demuxer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}

/// <summary>
/// ISO BMFF byte stream format (https://w3c.github.io/mse-byte-stream-format-isobmff/):
/// an initialization segment is ftyp+moov (with mvex), a media segment is moof+mdat; styp,
/// sidx, prft, emsg, free and skip boxes are allowed and ignored in between.
/// </summary>
public sealed class Mp4SegmentParser : SegmentParser
{
    private static readonly uint Styp = BoxType.Of("styp");
    private static readonly uint Sidx = BoxType.Of("sidx");
    private static readonly uint Prft = BoxType.Of("prft");
    private static readonly uint Emsg = BoxType.Of("emsg");
    private static readonly uint Free = BoxType.Of("free");
    private static readonly uint Skip = BoxType.Of("skip");
    private static readonly uint Mdat = BoxType.Of("mdat");
    private static readonly uint Mfra = BoxType.Of("mfra");
    private static readonly uint Pdin = BoxType.Of("pdin");
    private static readonly uint Meta = BoxType.Of("meta");
    private static readonly uint Uuid = BoxType.Of("uuid");

    public Mp4SegmentParser(MediaPipelineContext context)
        : base(context)
    {
    }

    protected override bool PendingIsMediaSegment(List<byte> pending)
    {
        // A box header names the segment: ftyp/moov start an initialization segment,
        // anything else (styp, sidx, moof, mdat) belongs to a media segment. Too few bytes
        // to tell: after the initialization segment a media segment is what comes.
        if (pending.Count >= 8 && Box.TryReadHeader(pending.ToArray().AsSpan(0, 8), 0, long.MaxValue, out var box))
            return box.Type != BoxType.Ftyp && box.Type != BoxType.Moov;
        return InitializationSegment is not null;
    }

    protected override SegmentParseStatus Parse(byte[] data, out int consumed, Action<DemuxerInfo> onInitializationSegment, Action<EncodedPacket> onPacket)
    {
        consumed = 0;
        int position = 0;
        List<byte>? initialization = null;
        int? moofStart = null;
        while (data.Length - position >= 8)
        {
            if (!Box.TryReadHeader(data.AsSpan(position), position, long.MaxValue, out var box))
                throw new MediaFormatException("A box header is malformed.");
            if (box.Size > int.MaxValue)
                throw new MediaFormatException("A box is too large for a byte stream segment.");
            var type = box.Type;
            if (!IsTopLevel(type))
                throw new MediaFormatException($"Box '{FourCc(type)}' is not part of the ISO BMFF byte stream format.");
            if (position + box.Size > data.Length)
                break; // the box is not complete yet

            int end = position + (int)box.Size;
            if (type == BoxType.Ftyp)
            {
                initialization = [.. data.AsSpan(position, end - position)];
            }
            else if (type == BoxType.Moov)
            {
                initialization ??= [];
                initialization.AddRange(data.AsSpan(position, end - position));
                var bytes = initialization.ToArray();
                var info = ReadInitialization(Mp4DemuxerFactory.Instance, bytes);
                InitializationSegment = bytes;
                initialization = null;
                onInitializationSegment(info);
            }
            else if (type == BoxType.Moof)
            {
                if (moofStart is not null)
                    throw new MediaFormatException("A moof box follows another without an mdat.");
                moofStart = position;
            }
            else if (type == Mdat)
            {
                if (moofStart is not { } start)
                    throw new MediaFormatException("An mdat box arrived without a moof.");
                ReadMediaSegment(Mp4DemuxerFactory.Instance, data.AsSpan(start, end - start), onPacket);
                moofStart = null;
            }
            else
            {
                // styp, sidx, prft, emsg, free, skip, pdin, meta, uuid and the mfra a muxer
                // leaves at the end of a file: allowed between segments, nothing to do with them.
            }

            position = end;
            if (moofStart is null && initialization is null)
                consumed = position;
        }

        return SegmentParseStatus.Ok;
    }

    /// <summary>The top-level boxes the byte stream format allows (§3-4); anything else is an append error, which also catches non-MP4 bytes whose first four bytes happen to look like a box size.</summary>
    private static bool IsTopLevel(uint type) =>
        type == BoxType.Ftyp || type == BoxType.Moov || type == BoxType.Moof || type == Mdat
        || type == Styp || type == Sidx || type == Prft || type == Emsg || type == Free || type == Skip
        || type == Mfra || type == Pdin || type == Meta || type == Uuid;

    private static string FourCc(uint type) =>
        new([(char)(type >> 24), (char)((type >> 16) & 0xFF), (char)((type >> 8) & 0xFF), (char)(type & 0xFF)]);
}

/// <summary>
/// WebM byte stream format (https://w3c.github.io/mse-byte-stream-format-webm/): an
/// initialization segment is the EBML header plus the Segment header with its Info and
/// Tracks; a media segment is one Cluster. Other Segment children are skipped.
/// </summary>
public sealed class WebmSegmentParser : SegmentParser
{
    private const int MaxHeaderElementBytes = 16 * 1024 * 1024;

    public WebmSegmentParser(MediaPipelineContext context)
        : base(context)
    {
    }

    public override bool FrameDurationsAreReliable => false;

    protected override bool PendingIsMediaSegment(List<byte> pending)
    {
        // A Cluster starts a media segment; the EBML header or a Segment starts an
        // initialization segment. A header too short to read: after the initialization
        // segment a media segment is what comes.
        var head = pending.Count > Ebml.MaxHeaderLength ? pending.GetRange(0, Ebml.MaxHeaderLength).ToArray() : pending.ToArray();
        if (Ebml.TryReadElement(head, 0, out var element))
            return element.Id == EbmlId.Cluster;
        return InitializationSegment is not null;
    }

    protected override SegmentParseStatus Parse(byte[] data, out int consumed, Action<DemuxerInfo> onInitializationSegment, Action<EncodedPacket> onPacket)
    {
        consumed = 0;
        int position = 0;
        List<byte>? initialization = null;
        bool sawTracks = false;
        while (position < data.Length)
        {
            if (!Ebml.TryReadElement(data.AsSpan(position), position, out var element))
            {
                if (data.Length - position < Ebml.MaxHeaderLength)
                    break; // maybe a header split across appends
                throw new MediaFormatException("An EBML element header is malformed.");
            }

            uint id = element.Id;
            int headerEnd = position + element.HeaderLength;
            if (id == EbmlId.EbmlHeader)
            {
                if (element.DataLength is not { } headerLength || headerLength > MaxHeaderElementBytes)
                    throw new MediaFormatException("The EBML header must have a known size.");
                int end = checked(headerEnd + (int)headerLength);
                if (end > data.Length)
                    break;
                initialization = [.. data.AsSpan(position, end - position)];
                position = end;
                continue;
            }

            if (id == EbmlId.Segment)
            {
                // Only the header: the Segment's children follow at the top level of the stream.
                if (initialization is null)
                    throw new MediaFormatException("A Segment arrived before the EBML header.");
                initialization.AddRange(data.AsSpan(position, element.HeaderLength));
                position = headerEnd;
                continue;
            }

            if (id == EbmlId.Cluster)
            {
                if (initialization is not null)
                    throw new MediaFormatException("A Cluster arrived inside an initialization segment.");
                int? clusterEnd = null;
                if (element.DataLength is { } clusterLength)
                {
                    if (clusterLength > int.MaxValue - headerEnd)
                        throw new MediaFormatException("A Cluster is too large for a byte stream segment.");
                    clusterEnd = headerEnd + (int)clusterLength;
                }
                else
                {
                    // Unknown size: the Cluster ends where the next Segment-level element starts.
                    clusterEnd = FindNextTopLevel(data, headerEnd);
                }

                if (clusterEnd is not { } clusterEndValue || clusterEndValue > data.Length)
                    break;
                ReadMediaSegment(MatroskaDemuxerFactory.Instance, data.AsSpan(position, clusterEndValue - position), onPacket);
                position = clusterEndValue;
                consumed = position;
                continue;
            }

            // Every other Segment child (SeekHead, Info, Tracks, Void, Cues, Tags, ...).
            if (element.DataLength is not { } length)
                throw new MediaFormatException($"Element 0x{id:X} may not have an unknown size in a byte stream.");
            if (length > MaxHeaderElementBytes)
                throw new MediaFormatException($"Element 0x{id:X} is too large for a byte stream segment.");
            int elementEnd = checked(headerEnd + (int)length);
            if (elementEnd > data.Length)
                break;
            if (initialization is not null)
            {
                initialization.AddRange(data.AsSpan(position, elementEnd - position));
                if (id == EbmlId.Tracks)
                    sawTracks = true;
                position = elementEnd;
                // The initialization segment is complete once Info and Tracks are in and the
                // next element is not another header-level element - or when a Cluster comes.
                if (sawTracks && NextIsClusterOrEnd(data, position))
                {
                    var bytes = initialization.ToArray();
                    var info = ReadInitialization(MatroskaDemuxerFactory.Instance, bytes);
                    InitializationSegment = bytes;
                    initialization = null;
                    sawTracks = false;
                    onInitializationSegment(info);
                    consumed = position;
                }

                continue;
            }

            position = elementEnd;
            consumed = position;
        }

        return SegmentParseStatus.Ok;
    }

    /// <summary>True when a complete Cluster header follows, or nothing follows at all in this append.</summary>
    private static bool NextIsClusterOrEnd(byte[] data, int position)
    {
        if (position >= data.Length)
            return true;
        return Ebml.TryReadElement(data.AsSpan(position), position, out var next) && next.Id == EbmlId.Cluster;
    }

    /// <summary>The offset of the next Segment-level element after <paramref name="from"/>, scanning Cluster children; null when the Cluster is still open.</summary>
    private static int? FindNextTopLevel(byte[] data, int from)
    {
        int position = from;
        while (position < data.Length)
        {
            if (!Ebml.TryReadElement(data.AsSpan(position), position, out var element))
                return null;
            if (element.Id is EbmlId.Cluster or EbmlId.Cues or EbmlId.Info or EbmlId.Tracks or EbmlId.SeekHead or EbmlId.Tags or EbmlId.EbmlHeader or EbmlId.Segment)
                return position;
            if (element.DataLength is not { } length)
                throw new MediaFormatException($"Element 0x{element.Id:X} inside a Cluster may not have an unknown size.");
            if (length > int.MaxValue - position)
                throw new MediaFormatException("A Cluster child is too large.");
            position = position + element.HeaderLength + (int)length;
        }

        return null;
    }
}
