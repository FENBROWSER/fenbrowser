using System.Buffers.Binary;
using System.Globalization;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Containers.Mp4;

/// <summary>
/// ISO base media file format (ISO/IEC 14496-12), MP4 (14496-14) and the AVC/HEVC (14496-15),
/// VP9, AV1, Opus and FLAC sample entries. Progressive files are indexed from the sample
/// tables; fragmented files from every <c>moof</c> in the file. Packets come out in decode
/// order, the tracks interleaved by decode time, and seeks land on the last sync sample at
/// or before the target.
/// </summary>
/// <remarks>
/// The whole index is built when the demuxer opens: the resource is already fetched, and an
/// index bounded by <see cref="MediaLimits.MaxSampleTableEntries"/> makes seeks a binary
/// search. Every box read into memory is bounded first, every table count is checked
/// against its box, and no sample is read past the resource.
/// </remarks>
public sealed class Mp4DemuxerFactory : IDemuxerFactory
{
    public static readonly Mp4DemuxerFactory Instance = new();

    public string Name => "mp4";

    public IReadOnlyList<string> MimeTypes { get; } = ["video/mp4", "audio/mp4", "audio/x-m4a", "video/x-m4v", "audio/m4a"];

    /// <summary>WHATWG MIME Sniffing §6.2.1 "signature for MP4": a <c>ftyp</c> box first; a bare <c>moov</c> or <c>mdat</c> is a weaker sign.</summary>
    public int Probe(ReadOnlySpan<byte> header)
    {
        if (header.Length < 8)
            return 0;
        uint type = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        if (type == BoxType.Ftyp)
            return 100;
        if (type == BoxType.Moov || type == BoxType.Mdat || type == BoxType.Free || type == BoxType.Skip)
            return 60;
        return 0;
    }

    public IDemuxer Create(IByteSource source, MediaPipelineContext context) => new Mp4Demuxer(source, context);
}

public sealed class Mp4Demuxer : IDemuxer
{
    private const int MaxMoovBytes = 64 * 1024 * 1024;
    private const int MaxMoofBytes = 16 * 1024 * 1024;
    private const int MaxTopLevelBoxes = 1_000_000;
    private const int MaxSampleEntryBytes = 1024 * 1024;

    private readonly IByteSource _source;
    private readonly MediaPipelineContext _context;
    private readonly byte[] _scratch = new byte[32];
    private readonly List<Track> _tracks = [];
    private readonly Dictionary<uint, Track> _tracksById = [];
    private long _movieTimescale = 1000;
    private long _movieDurationTicks;
    private long _fragmentDurationTicks;
    private bool _fragmented;
    private bool _open;
    private MediaTrackInfo[] _trackInfos = [];

    public Mp4Demuxer(IByteSource source, MediaPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);
        _source = source;
        _context = context;
    }

    /// <summary>One sample: where it is, how long it is, and when it decodes and shows (track ticks).</summary>
    private readonly record struct Sample(long Offset, int Size, long Dts, int CtsOffset, int Duration, bool IsSync);

    private sealed class Track
    {
        public int Id;
        public uint TrackId;
        public MediaTrackKind Kind;
        public long Timescale = 1;
        public long DurationTicks;
        public CodecConfig Config = null!;
        public string Language = "";
        public long EditOffsetTicks;          // subtracted from every timestamp (elst)
        public readonly List<Sample> Samples = [];
        public int Next;

        // Fragment defaults (trex, then tfhd), and the running decode time.
        public uint DefaultSampleDuration;
        public uint DefaultSampleSize;
        public uint DefaultSampleFlags;
        public long FragmentDts;

        public MediaTime Time(long ticks) => MediaTime.FromTimescale(Clamp((Int128)ticks - EditOffsetTicks), Timescale);

        /// <summary>Tick counts stay well inside a long so later arithmetic on them cannot overflow.</summary>
        public static long Clamp(Int128 ticks) => (long)Int128.Clamp(ticks, long.MinValue / 4, long.MaxValue / 4);
    }

    public async ValueTask<DemuxerInfo> InitializeAsync(CancellationToken cancellationToken)
    {
        if (_open)
            throw new InvalidOperationException("InitializeAsync has already run.");

        long length = _source.Length ?? throw new MediaFormatException("An MP4 resource must have a known length.");
        long position = 0;
        bool haveMoov = false;
        for (int boxes = 0; position + 8 <= length; boxes++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (boxes >= MaxTopLevelBoxes)
                throw new MediaLimitExceededException("MaxTopLevelBoxes", boxes, MaxTopLevelBoxes);

            int got = await _source.ReadAtLeastAsync(position, _scratch.AsMemory(0, (int)Math.Min(32, length - position)), cancellationToken).ConfigureAwait(false);
            if (!Box.TryReadHeader(_scratch.AsSpan(0, got), position, length, out var header))
                throw new MediaFormatException($"Malformed box header at byte {position}.");

            if (header.Type == BoxType.Moov)
            {
                if (haveMoov)
                    throw new MediaFormatException("The file has more than one moov box.");
                ParseMoov(await ReadBoxAsync(header, MaxMoovBytes, cancellationToken).ConfigureAwait(false), header.DataStart);
                haveMoov = true;
            }
            else if (header.Type == BoxType.Moof)
            {
                if (!haveMoov)
                    throw new MediaFormatException("A moof box precedes the moov box.");
                _fragmented = true;
                ParseMoof(await ReadBoxAsync(header, MaxMoofBytes, cancellationToken).ConfigureAwait(false), header, length);
            }

            position = header.End;
        }

        if (!haveMoov)
            throw new MediaFormatException("The file has no moov box.");
        if (_tracks.Count == 0)
            throw new MediaFormatException("No track uses a codec this engine can name.");

        foreach (var track in _tracks)
        {
            if (track.Samples.Count > _context.Limits.MaxSampleTableEntries)
                throw new MediaLimitExceededException(nameof(MediaLimits.MaxSampleTableEntries), track.Samples.Count, _context.Limits.MaxSampleTableEntries);
            foreach (var sample in track.Samples)
            {
                if (sample.Offset < 0 || sample.Offset > length - sample.Size)
                    throw new MediaFormatException($"A sample of track {track.TrackId} lies outside the resource.");
            }
        }

        var duration = Duration();
        _trackInfos = _tracks.Select(t => new MediaTrackInfo(t.Id, t.Config, TrackDuration(t, duration), t.Language, "", IsDefault: t.Id == _tracks.First(x => x.Kind == t.Kind).Id)).ToArray();
        foreach (var track in _trackInfos)
        {
            _context.Log.Emit(_context.Player, MediaEventKind.TrackAdded, MediaLogLevel.Info,
                $"MP4: track {track.Id} {track.Config.Codec} {Describe(track.Config)}{(_fragmented ? " (fragmented)" : "")}.",
                ("track", track.Id.ToString(CultureInfo.InvariantCulture)),
                ("kind", track.Kind.ToString().ToLowerInvariant()),
                ("codec", track.Config.Codec.ToString().ToLowerInvariant()),
                ("samples", _tracks[track.Id].Samples.Count.ToString(CultureInfo.InvariantCulture)));
        }

        _open = true;
        return new DemuxerInfo(_trackInfos, duration, IsSeekable: true);
    }

    private static string Describe(CodecConfig config) => config.Kind == MediaTrackKind.Video
        ? $"{config.Width}x{config.Height}"
        : $"{config.SampleRate} Hz {config.Channels} ch";

    /// <summary>The mvhd duration (what the other engines report), else mehd's, else the last sample's end.</summary>
    private MediaTime Duration()
    {
        if (_movieDurationTicks > 0)
            return MediaTime.FromTimescale(_movieDurationTicks, _movieTimescale);
        if (_fragmentDurationTicks > 0)
            return MediaTime.FromTimescale(_fragmentDurationTicks, _movieTimescale);

        // Neither header says: the decode timeline's extent, as ffmpeg reports it (a
        // composition offset on the first picture does not lengthen the movie).
        MediaTime longest = MediaTime.Zero;
        foreach (var track in _tracks)
        {
            if (track.Samples.Count == 0)
                continue;
            var last = track.Samples[^1];
            var end = track.Time(Track.Clamp((Int128)last.Dts + last.Duration));
            if (end > longest)
                longest = end;
        }

        return longest > MediaTime.Zero ? longest : MediaTime.PositiveInfinity;
    }

    private static MediaTime TrackDuration(Track track, MediaTime movieDuration) =>
        track.DurationTicks > 0 ? MediaTime.FromTimescale(track.DurationTicks, track.Timescale) : movieDuration;

    private async ValueTask<byte[]> ReadBoxAsync(BoxHeader header, int max, CancellationToken cancellationToken)
    {
        if (header.DataLength > max)
            throw new MediaLimitExceededException($"Max{BoxType.Name(header.Type)}Bytes", header.DataLength, max);
        var buffer = new byte[header.DataLength];
        int got = await _source.ReadAtLeastAsync(header.DataStart, buffer, cancellationToken).ConfigureAwait(false);
        if (got < buffer.Length)
            throw new MediaFormatException($"The {BoxType.Name(header.Type)} box is truncated.");
        return buffer;
    }

    // ---- moov ----

    private void ParseMoov(ReadOnlyMemory<byte> data, long dataPosition)
    {
        foreach (var (box, body) in Box.Children(data, dataPosition))
        {
            if (box.Type == BoxType.Mvhd)
                ParseMvhd(body.Span);
            else if (box.Type == BoxType.Trak)
                ParseTrak(body, box.DataStart);
            else if (box.Type == BoxType.Mvex)
                ParseMvex(body, box.DataStart);
        }
    }

    /// <summary>§8.2.2 MovieHeaderBox: timescale and duration (version 1 is 64-bit).</summary>
    private void ParseMvhd(ReadOnlySpan<byte> data)
    {
        var (version, _) = Box.FullBox(data);
        if (version == 1)
        {
            _movieTimescale = Box.U32(data, 20);
            _movieDurationTicks = Track.Clamp((Int128)Box.U64(data, 24));
        }
        else
        {
            _movieTimescale = Box.U32(data, 12);
            _movieDurationTicks = Box.U32(data, 16);
        }

        if (_movieTimescale <= 0)
            throw new MediaFormatException("The movie timescale is zero.");
        if (_movieDurationTicks == uint.MaxValue)
            _movieDurationTicks = 0; // "unknown"
    }

    private void ParseMvex(ReadOnlyMemory<byte> data, long dataPosition)
    {
        foreach (var (box, body) in Box.Children(data, dataPosition))
        {
            if (box.Type == BoxType.Mehd)
            {
                // §8.8.2 MovieExtendsHeaderBox: the whole movie's duration including fragments.
                var (version, _) = Box.FullBox(body.Span);
                _fragmentDurationTicks = version == 1 ? Track.Clamp((Int128)Box.U64(body.Span, 4)) : Box.U32(body.Span, 4);
                continue;
            }

            if (box.Type != BoxType.Trex)
                continue;
            var span = body.Span;
            uint trackId = Box.U32(span, 4);
            if (_tracksById.TryGetValue(trackId, out var track))
            {
                track.DefaultSampleDuration = Box.U32(span, 12);
                track.DefaultSampleSize = Box.U32(span, 16);
                track.DefaultSampleFlags = Box.U32(span, 20);
            }
        }
    }

    private void ParseTrak(ReadOnlyMemory<byte> data, long dataPosition)
    {
        if (_tracks.Count >= _context.Limits.MaxTracks)
            throw new MediaLimitExceededException(nameof(MediaLimits.MaxTracks), _tracks.Count + 1, _context.Limits.MaxTracks);

        var track = new Track();
        ReadOnlyMemory<byte> mdia = default;
        long mdiaPosition = 0;
        ReadOnlyMemory<byte> elst = default;
        bool haveTkhd = false;
        foreach (var (box, body) in Box.Children(data, dataPosition))
        {
            if (box.Type == BoxType.Tkhd)
            {
                var span = body.Span;
                var (version, _) = Box.FullBox(span);
                track.TrackId = version == 1 ? Box.U32(span, 20) : Box.U32(span, 12);
                haveTkhd = true;
            }
            else if (box.Type == BoxType.Mdia)
            {
                mdia = body;
                mdiaPosition = box.DataStart;
            }
            else if (box.Type == BoxType.Edts)
            {
                foreach (var (child, childBody) in Box.Children(body, box.DataStart))
                {
                    if (child.Type == BoxType.Elst)
                        elst = childBody;
                }
            }
        }

        if (!haveTkhd || mdia.IsEmpty)
            throw new MediaFormatException("A trak box lacks its tkhd or mdia.");
        if (_tracksById.ContainsKey(track.TrackId))
            throw new MediaFormatException($"Track ID {track.TrackId} appears twice.");

        if (!ParseMdia(track, mdia, mdiaPosition))
            return; // a track this engine does not play (hint, subtitle, unknown codec)

        if (!elst.IsEmpty)
            ApplyEditList(track, elst.Span);

        track.Id = _tracks.Count;
        _tracks.Add(track);
        _tracksById[track.TrackId] = track;
    }

    /// <summary>
    /// §8.6.6 EditListBox: an initial empty edit delays the track, and the first real edit's
    /// media time is where the presentation starts (the AAC priming and B-frame delay
    /// cases). Anything more elaborate is treated as that first edit.
    /// </summary>
    private void ApplyEditList(Track track, ReadOnlySpan<byte> data)
    {
        var (version, _) = Box.FullBox(data);
        int entrySize = version == 1 ? 20 : 12;
        int count = Box.Count(data, 4, entrySize, 1024, "elst");
        long emptyTicks = 0;
        for (int i = 0; i < count; i++)
        {
            int at = 8 + i * entrySize;
            long segmentDuration = version == 1 ? (long)Box.U64(data, at) : Box.U32(data, at);
            long mediaTime = version == 1 ? Box.S64(data, at + 8) : Box.S32(data, at + 4);
            if (mediaTime == -1)
            {
                emptyTicks = Track.Clamp((Int128)emptyTicks + (Int128)segmentDuration * track.Timescale / _movieTimescale);
                continue;
            }

            track.EditOffsetTicks = Track.Clamp((Int128)mediaTime - emptyTicks);
            return;
        }

        track.EditOffsetTicks = -emptyTicks;
    }

    private bool ParseMdia(Track track, ReadOnlyMemory<byte> data, long dataPosition)
    {
        uint handler = 0;
        ReadOnlyMemory<byte> stbl = default;
        long stblPosition = 0;
        foreach (var (box, body) in Box.Children(data, dataPosition))
        {
            var span = body.Span;
            if (box.Type == BoxType.Mdhd)
            {
                var (version, _) = Box.FullBox(span);
                track.Timescale = version == 1 ? Box.U32(span, 20) : Box.U32(span, 12);
                track.DurationTicks = version == 1 ? Track.Clamp((Int128)Box.U64(span, 24)) : Box.U32(span, 16);
                if (track.DurationTicks == uint.MaxValue)
                    track.DurationTicks = 0;
                ushort language = Box.U16(span, version == 1 ? 32 : 20);
                track.Language = DecodeLanguage(language);
                if (track.Timescale <= 0)
                    throw new MediaFormatException("A track timescale is zero.");
            }
            else if (box.Type == BoxType.Hdlr)
            {
                handler = Box.U32(span, 8);
            }
            else if (box.Type == BoxType.Minf)
            {
                foreach (var (child, childBody) in Box.Children(body, box.DataStart))
                {
                    if (child.Type == BoxType.Stbl)
                    {
                        stbl = childBody;
                        stblPosition = child.DataStart;
                    }
                }
            }
        }

        if (handler == BoxType.HandlerVideo)
            track.Kind = MediaTrackKind.Video;
        else if (handler == BoxType.HandlerAudio)
            track.Kind = MediaTrackKind.Audio;
        else
            return false;
        if (stbl.IsEmpty)
            throw new MediaFormatException("A track lacks its sample table.");

        return ParseStbl(track, stbl, stblPosition);
    }

    /// <summary>§8.4.2.3: three five-bit letters packed above 0x60.</summary>
    private static string DecodeLanguage(ushort packed)
    {
        if (packed == 0 || packed == 0x7FFF)
            return "";
        Span<char> letters =
        [
            (char)(((packed >> 10) & 0x1F) + 0x60),
            (char)(((packed >> 5) & 0x1F) + 0x60),
            (char)((packed & 0x1F) + 0x60),
        ];
        foreach (char c in letters)
        {
            if (c is < 'a' or > 'z')
                return "";
        }

        return new string(letters);
    }

    // ---- stbl ----

    private bool ParseStbl(Track track, ReadOnlyMemory<byte> data, long dataPosition)
    {
        ReadOnlyMemory<byte> stts = default, ctts = default, stss = default, stsc = default, stsz = default, stz2 = default, stco = default, co64 = default;
        bool haveConfig = false;
        foreach (var (box, body) in Box.Children(data, dataPosition))
        {
            if (box.Type == BoxType.Stsd)
                haveConfig = ParseStsd(track, body, box.DataStart);
            else if (box.Type == BoxType.Stts) stts = body;
            else if (box.Type == BoxType.Ctts) ctts = body;
            else if (box.Type == BoxType.Stss) stss = body;
            else if (box.Type == BoxType.Stsc) stsc = body;
            else if (box.Type == BoxType.Stsz) stsz = body;
            else if (box.Type == BoxType.Stz2) stz2 = body;
            else if (box.Type == BoxType.Stco) stco = body;
            else if (box.Type == BoxType.Co64) co64 = body;
        }

        if (!haveConfig)
            return false;
        if (stts.IsEmpty || stsc.IsEmpty || (stsz.IsEmpty && stz2.IsEmpty) || (stco.IsEmpty && co64.IsEmpty))
            throw new MediaFormatException($"Track {track.TrackId} lacks a sample table box (stts, stsc, stsz or stco).");

        BuildSampleTable(track, stts.Span, ctts.Span, stss.Span, stsc.Span, stsz.IsEmpty ? stz2.Span : stsz.Span, !stsz.IsEmpty, stco.IsEmpty ? co64.Span : stco.Span, stco.IsEmpty);
        return true;
    }

    /// <summary>§8.7.4 stsc, §8.7.3 stsz, §8.7.5 stco: every sample's offset and size; §8.6.1.2 stts and §8.6.1.3 ctts: its times; §8.6.2 stss: sync samples.</summary>
    private void BuildSampleTable(Track track, ReadOnlySpan<byte> stts, ReadOnlySpan<byte> ctts, ReadOnlySpan<byte> stss, ReadOnlySpan<byte> stsc, ReadOnlySpan<byte> sizes, bool isStsz, ReadOnlySpan<byte> offsets, bool is64)
    {
        int max = _context.Limits.MaxSampleTableEntries;

        // Sizes.
        int sampleCount;
        uint constantSize = 0;
        int fieldSize = 0;
        if (isStsz)
        {
            constantSize = Box.U32(sizes, 4);
            sampleCount = (int)Math.Min(int.MaxValue, Box.U32(sizes, 8));
            if (sampleCount > max)
                throw new MediaLimitExceededException(nameof(MediaLimits.MaxSampleTableEntries), sampleCount, max);
            if (constantSize == 0 && (long)sampleCount * 4 > sizes.Length - 12)
                throw new MediaFormatException("The stsz box declares more samples than it holds.");
        }
        else
        {
            fieldSize = Box.U8(sizes, 7);
            if (fieldSize is not (4 or 8 or 16))
                throw new MediaFormatException($"stz2 field size {fieldSize} is invalid.");
            sampleCount = (int)Math.Min(int.MaxValue, Box.U32(sizes, 8));
            if (sampleCount > max)
                throw new MediaLimitExceededException(nameof(MediaLimits.MaxSampleTableEntries), sampleCount, max);
            if ((long)sampleCount * fieldSize / 8 > sizes.Length - 12)
                throw new MediaFormatException("The stz2 box declares more samples than it holds.");
        }

        // Chunk offsets and the chunk-to-sample map.
        int chunkCount = Box.Count(offsets, 4, is64 ? 8 : 4, max, is64 ? "co64" : "stco");
        int stscCount = Box.Count(stsc, 4, 12, max, "stsc");
        var samples = track.Samples;
        int sampleIndex = 0;
        for (int entry = 0; entry < stscCount && sampleIndex < sampleCount; entry++)
        {
            int at = 8 + entry * 12;
            uint firstChunk = Box.U32(stsc, at);
            uint perChunk = Box.U32(stsc, at + 4);
            uint nextFirst = entry + 1 < stscCount ? Box.U32(stsc, at + 12) : (uint)chunkCount + 1;
            if (firstChunk == 0 || nextFirst < firstChunk || perChunk == 0)
                throw new MediaFormatException("The stsc box is not monotonic.");
            for (uint chunk = firstChunk; chunk < nextFirst && sampleIndex < sampleCount; chunk++)
            {
                if (chunk > (uint)chunkCount)
                    throw new MediaFormatException("The stsc box names a chunk past the chunk offsets.");
                long offset = is64 ? Track.Clamp((Int128)Box.U64(offsets, 8 + (int)(chunk - 1) * 8)) : Box.U32(offsets, 8 + (int)(chunk - 1) * 4);
                for (uint s = 0; s < perChunk && sampleIndex < sampleCount; s++)
                {
                    int size = SampleSize(sizes, isStsz, constantSize, fieldSize, sampleIndex);
                    _context.Limits.CheckPacketSize(track.Kind, size);
                    samples.Add(new Sample(offset, size, 0, 0, 0, true));
                    offset += size;
                    sampleIndex++;
                }
            }
        }

        if (sampleIndex != sampleCount)
            throw new MediaFormatException("The chunk map does not cover every sample.");

        // Decode times.
        int sttsCount = Box.Count(stts, 4, 8, max, "stts");
        long dts = 0;
        sampleIndex = 0;
        for (int entry = 0; entry < sttsCount; entry++)
        {
            uint count = Box.U32(stts, 8 + entry * 8);
            uint delta = Box.U32(stts, 12 + entry * 8);
            for (uint i = 0; i < count; i++)
            {
                if (sampleIndex >= sampleCount)
                    throw new MediaFormatException("The stts box covers more samples than exist.");
                var s = samples[sampleIndex];
                samples[sampleIndex] = s with { Dts = dts, Duration = (int)Math.Min(int.MaxValue, delta) };
                dts = Track.Clamp((Int128)dts + delta);
                sampleIndex++;
            }
        }

        if (sampleIndex != sampleCount)
            throw new MediaFormatException("The stts box does not cover every sample.");

        // Composition offsets.
        if (!ctts.IsEmpty)
        {
            var (version, _) = Box.FullBox(ctts);
            int cttsCount = Box.Count(ctts, 4, 8, max, "ctts");
            sampleIndex = 0;
            for (int entry = 0; entry < cttsCount && sampleIndex < sampleCount; entry++)
            {
                uint count = Box.U32(ctts, 8 + entry * 8);
                int offset = version == 1 ? Box.S32(ctts, 12 + entry * 8) : (int)Math.Min(int.MaxValue, Box.U32(ctts, 12 + entry * 8));
                for (uint i = 0; i < count && sampleIndex < sampleCount; i++)
                {
                    samples[sampleIndex] = samples[sampleIndex] with { CtsOffset = offset };
                    sampleIndex++;
                }
            }
        }

        // Sync samples: with an stss box only the listed samples are sync; without it, all are.
        if (!stss.IsEmpty)
        {
            int syncCount = Box.Count(stss, 4, 4, max, "stss");
            for (int i = 0; i < sampleCount; i++)
                samples[i] = samples[i] with { IsSync = false };
            for (int i = 0; i < syncCount; i++)
            {
                uint number = Box.U32(stss, 8 + i * 4);
                if (number >= 1 && number <= (uint)sampleCount)
                    samples[(int)number - 1] = samples[(int)number - 1] with { IsSync = true };
            }
        }
    }

    private static int SampleSize(ReadOnlySpan<byte> sizes, bool isStsz, uint constantSize, int fieldSize, int index)
    {
        if (isStsz)
            return ToSampleSize(constantSize != 0 ? constantSize : Box.U32(sizes, 12 + index * 4));
        return fieldSize switch
        {
            4 => (Box.U8(sizes, 12 + index / 2) >> ((index & 1) == 0 ? 4 : 0)) & 0xF,
            8 => Box.U8(sizes, 12 + index),
            _ => Box.U16(sizes, 12 + index * 2),
        };
    }

    private static int ToSampleSize(uint size) =>
        size <= int.MaxValue ? (int)size : throw new MediaLimitExceededException(nameof(MediaLimits.MaxVideoPacketBytes), size, int.MaxValue);

    // ---- stsd and codec configuration ----

    /// <summary>§8.5.2 SampleDescriptionBox: the first sample entry decides the codec; a second one is not supported.</summary>
    private bool ParseStsd(Track track, ReadOnlyMemory<byte> data, long dataPosition)
    {
        int count = Box.Count(data.Span, 4, 8, 64, "stsd");
        if (count == 0)
            return false;
        var span = data.Span;
        if (!Box.TryReadHeader(span[8..], dataPosition + 8, dataPosition + data.Length, out var entry))
            throw new MediaFormatException("The stsd box has a malformed sample entry.");
        if (entry.DataLength > MaxSampleEntryBytes)
            throw new MediaLimitExceededException("MaxSampleEntryBytes", entry.DataLength, MaxSampleEntryBytes);
        var body = data.Slice(8 + entry.HeaderLength, (int)entry.DataLength);
        return track.Kind == MediaTrackKind.Video
            ? ParseVisualSampleEntry(track, entry.Type, body, entry.DataStart)
            : ParseAudioSampleEntry(track, entry.Type, body, entry.DataStart);
    }

    /// <summary>§12.1.3 VisualSampleEntry: width and height at 24, the configuration boxes after 78 bytes.</summary>
    private bool ParseVisualSampleEntry(Track track, uint type, ReadOnlyMemory<byte> data, long dataPosition)
    {
        var span = data.Span;
        int width = Box.U16(span, 24);
        int height = Box.U16(span, 26);
        if (data.Length < 78)
            throw new MediaFormatException("A visual sample entry is too short.");

        MediaCodec codec = MediaCodec.Unknown;
        string? codecString = null;
        ReadOnlyMemory<byte> extradata = default;
        foreach (var (box, body) in Box.Children(data[78..], dataPosition + 78))
        {
            if ((type == BoxType.Avc1 || type == BoxType.Avc3) && box.Type == BoxType.AvcC)
            {
                codec = MediaCodec.H264;
                codecString = $"avc1.{Box.U8(body.Span, 1):X2}{Box.U8(body.Span, 2):X2}{Box.U8(body.Span, 3):X2}";
                extradata = body;
            }
            else if ((type == BoxType.Hvc1 || type == BoxType.Hev1) && box.Type == BoxType.HvcC)
            {
                codec = MediaCodec.Hevc;
                codecString = HevcCodecString(type == BoxType.Hvc1 ? "hvc1" : "hev1", body.Span);
                extradata = body;
            }
            else if (type == BoxType.Vp09 && box.Type == BoxType.VpcC)
            {
                codec = MediaCodec.Vp9;
                var b = body.Span;
                codecString = $"vp09.{Box.U8(b, 4):D2}.{Box.U8(b, 5):D2}.{Box.U8(b, 6) >> 4:D2}";
                extradata = body;
            }
            else if (type == BoxType.Av01 && box.Type == BoxType.Av1C)
            {
                codec = MediaCodec.Av1;
                var b = body.Span;
                int profile = Box.U8(b, 1) >> 5;
                int level = Box.U8(b, 1) & 0x1F;
                int flags = Box.U8(b, 2);
                bool tier = (flags & 0x80) != 0;
                bool high = (flags & 0x40) != 0;
                bool twelve = (flags & 0x20) != 0;
                int depth = high ? (twelve ? 12 : 10) : 8;
                codecString = $"av01.{profile}.{level:D2}{(tier ? 'H' : 'M')}.{depth:D2}";
                extradata = body;
            }
        }

        if (codec == MediaCodec.Unknown)
        {
            _context.Log.Emit(_context.Player, MediaEventKind.TrackAdded, MediaLogLevel.Info,
                $"MP4: skipping video track {track.TrackId} with sample entry '{BoxType.Name(type)}'.",
                ("track", track.TrackId.ToString(CultureInfo.InvariantCulture)), ("entry", BoxType.Name(type)));
            return false;
        }

        _context.Limits.CheckVideoDimensions(width, height);
        track.Config = new CodecConfig(MediaTrackKind.Video, codec, codecString, Width: width, Height: height, Extradata: extradata);
        return true;
    }

    /// <summary>ISO/IEC 14496-15 §E.3 "hvc1.P.C.LT.B": profile, compatibility flags reversed, tier and level, constraint bytes.</summary>
    private static string HevcCodecString(string prefix, ReadOnlySpan<byte> hvcC)
    {
        int byte1 = Box.U8(hvcC, 1);
        int profileSpace = byte1 >> 6;
        bool tier = (byte1 & 0x20) != 0;
        int profile = byte1 & 0x1F;
        uint compatibility = Box.U32(hvcC, 2);
        uint reversed = 0;
        for (int i = 0; i < 32; i++)
            reversed |= ((compatibility >> i) & 1) << (31 - i);
        int level = Box.U8(hvcC, 12);
        var constraints = new List<string>();
        for (int i = 6; i < 12; i++)
        {
            int b = Box.U8(hvcC, i);
            constraints.Add(b.ToString("X", CultureInfo.InvariantCulture));
        }

        while (constraints.Count > 1 && constraints[^1] == "0")
            constraints.RemoveAt(constraints.Count - 1);
        string space = profileSpace switch { 1 => "A", 2 => "B", 3 => "C", _ => "" };
        return $"{prefix}.{space}{profile}.{reversed:X}.{(tier ? 'H' : 'L')}{level}.{string.Join(".", constraints)}";
    }

    /// <summary>§12.2.3 AudioSampleEntry (QuickTime versions 1 and 2 too): channels, rate, then the configuration boxes.</summary>
    private bool ParseAudioSampleEntry(Track track, uint type, ReadOnlyMemory<byte> data, long dataPosition)
    {
        var span = data.Span;
        int version = Box.U16(span, 8);
        int channels;
        int sampleRate;
        int childrenAt;
        if (version == 2)
        {
            sampleRate = (int)Math.Clamp(Math.Round(BitConverter.Int64BitsToDouble(Box.S64(span, 32))), 0, int.MaxValue);
            channels = (int)Math.Min(int.MaxValue, Box.U32(span, 40));
            childrenAt = 72;
        }
        else
        {
            channels = Box.U16(span, 16);
            sampleRate = (int)(Box.U32(span, 24) >> 16);
            childrenAt = version == 1 ? 44 : 28;
        }

        if (data.Length < childrenAt)
            throw new MediaFormatException("An audio sample entry is too short.");

        MediaCodec codec = MediaCodec.Unknown;
        string? codecString = null;
        ReadOnlyMemory<byte> extradata = default;
        foreach (var (box, body) in Box.Children(data[childrenAt..], dataPosition + childrenAt))
        {
            if (type == BoxType.Mp4a && box.Type == BoxType.Esds)
            {
                (codec, codecString, extradata) = ParseEsds(body, ref sampleRate, ref channels);
            }
            else if (type == BoxType.Opus && box.Type == BoxType.DOps)
            {
                codec = MediaCodec.Opus;
                codecString = "opus";
                extradata = OpusHeadFromDOps(body.Span, ref channels);
                sampleRate = 48000;
            }
            else if (type == BoxType.FLaC && box.Type == BoxType.DfLa)
            {
                codec = MediaCodec.Flac;
                codecString = "flac";
                extradata = FlacStreamInfo(body.Span);
            }
        }

        if (type == BoxType.Mp3)
        {
            codec = MediaCodec.Mp3;
            codecString = "mp3";
        }

        if (codec == MediaCodec.Unknown)
        {
            _context.Log.Emit(_context.Player, MediaEventKind.TrackAdded, MediaLogLevel.Info,
                $"MP4: skipping audio track {track.TrackId} with sample entry '{BoxType.Name(type)}'.",
                ("track", track.TrackId.ToString(CultureInfo.InvariantCulture)), ("entry", BoxType.Name(type)));
            return false;
        }

        if (channels == 0)
            channels = 1;
        _context.Limits.CheckAudioFormat(sampleRate, channels);
        track.Config = new CodecConfig(MediaTrackKind.Audio, codec, codecString, SampleRate: sampleRate, Channels: channels, Extradata: extradata);
        return true;
    }

    /// <summary>
    /// ISO/IEC 14496-1 ES_Descriptor → DecoderConfigDescriptor (object type) → DecoderSpecificInfo
    /// (the AudioSpecificConfig, ISO/IEC 14496-3 §1.6, which names the object type, rate and channels).
    /// </summary>
    private static (MediaCodec Codec, string? CodecString, ReadOnlyMemory<byte> Extradata) ParseEsds(ReadOnlyMemory<byte> esds, ref int sampleRate, ref int channels)
    {
        var span = esds.Span;
        int at = 4; // FullBox
        if (!ReadDescriptor(span, ref at, out int tag, out int length) || tag != 0x03)
            throw new MediaFormatException("The esds box has no ES_Descriptor.");
        int esEnd = at + length;
        at += 2; // ES_ID
        int flags = Box.U8(span, at++);
        if ((flags & 0x80) != 0) at += 2;
        if ((flags & 0x40) != 0) at += 1 + Box.U8(span, at);
        if ((flags & 0x20) != 0) at += 2;

        if (!ReadDescriptor(span, ref at, out tag, out length) || tag != 0x04)
            throw new MediaFormatException("The esds box has no DecoderConfigDescriptor.");
        int dcdEnd = at + length;
        int objectType = Box.U8(span, at);
        at += 13;

        ReadOnlyMemory<byte> specific = default;
        if (at < dcdEnd && ReadDescriptor(span, ref at, out tag, out length) && tag == 0x05)
        {
            if (at + length > span.Length)
                throw new MediaFormatException("The DecoderSpecificInfo runs past the esds box.");
            specific = esds.Slice(at, length);
        }

        _ = esEnd;
        switch (objectType)
        {
            case 0x40: // MPEG-4 Audio
            case 0x66:
            case 0x67:
            case 0x68: // MPEG-2 AAC
            {
                int audioObjectType = 2;
                if (specific.Length >= 2)
                {
                    var asc = specific.Span;
                    audioObjectType = asc[0] >> 3;
                    int frequencyIndex = ((asc[0] & 0x07) << 1) | (asc[1] >> 7);
                    int bitPosition = 9;
                    if (audioObjectType == 31)
                    {
                        audioObjectType = 32 + ReadBits(asc, bitPosition, 6);
                        bitPosition += 6;
                        frequencyIndex = ReadBits(asc, bitPosition, 4);
                        bitPosition += 4;
                    }

                    if (frequencyIndex == 15)
                    {
                        sampleRate = ReadBits(asc, bitPosition, 24);
                        bitPosition += 24;
                    }
                    else if (frequencyIndex < s_aacSampleRates.Length)
                    {
                        sampleRate = s_aacSampleRates[frequencyIndex];
                    }

                    int channelConfiguration = ReadBits(asc, bitPosition, 4);
                    if (channelConfiguration is >= 1 and <= 7)
                        channels = channelConfiguration == 7 ? 8 : channelConfiguration;
                }

                return (MediaCodec.Aac, $"mp4a.40.{audioObjectType.ToString(CultureInfo.InvariantCulture)}", specific);
            }
            case 0x69:
            case 0x6B:
                return (MediaCodec.Mp3, "mp3", default);
            default:
                return (MediaCodec.Unknown, null, default);
        }
    }

    private static readonly int[] s_aacSampleRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    private static int ReadBits(ReadOnlySpan<byte> data, int bitPosition, int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++)
        {
            int position = bitPosition + i;
            int byteIndex = position >> 3;
            int bit = byteIndex < data.Length ? (data[byteIndex] >> (7 - (position & 7))) & 1 : 0;
            value = (value << 1) | bit;
        }

        return value;
    }

    /// <summary>ISO/IEC 14496-1 §8.3.3: a tag byte and a size in up to four 7-bit groups.</summary>
    private static bool ReadDescriptor(ReadOnlySpan<byte> data, ref int at, out int tag, out int length)
    {
        tag = 0;
        length = 0;
        if (at >= data.Length)
            return false;
        tag = data[at++];
        for (int i = 0; i < 4; i++)
        {
            if (at >= data.Length)
                return false;
            int b = data[at++];
            length = (length << 7) | (b & 0x7F);
            if ((b & 0x80) == 0)
                break;
        }

        return at + length <= data.Length;
    }

    /// <summary>The Opus-in-ISOBMFF dOps box (big-endian) rewritten as the RFC 7845 OpusHead libavcodec expects.</summary>
    private static byte[] OpusHeadFromDOps(ReadOnlySpan<byte> dOps, ref int channels)
    {
        if (dOps.Length < 11)
            throw new MediaFormatException("The dOps box is too short.");
        channels = dOps[1];
        int mappingFamily = dOps[10];
        int extra = mappingFamily == 0 ? 0 : 2 + channels;
        if (dOps.Length < 11 + extra)
            throw new MediaFormatException("The dOps box lacks its channel mapping.");
        var head = new byte[19 + extra];
        "OpusHead"u8.CopyTo(head);
        head[8] = 1;
        head[9] = (byte)channels;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), BinaryPrimitives.ReadUInt16BigEndian(dOps[2..]));
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), BinaryPrimitives.ReadUInt32BigEndian(dOps[4..]));
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(16), BinaryPrimitives.ReadUInt16BigEndian(dOps[8..]));
        head[18] = (byte)mappingFamily;
        dOps.Slice(11, extra).CopyTo(head.AsSpan(19));
        return head;
    }

    /// <summary>The FLAC-in-ISOBMFF dfLa box: version and flags, then metadata blocks; the STREAMINFO block is the extradata.</summary>
    private static ReadOnlyMemory<byte> FlacStreamInfo(ReadOnlySpan<byte> dfLa)
    {
        int at = 4;
        while (at + 4 <= dfLa.Length)
        {
            int header = dfLa[at];
            int length = (dfLa[at + 1] << 16) | (dfLa[at + 2] << 8) | dfLa[at + 3];
            if (at + 4 + length > dfLa.Length)
                break;
            if ((header & 0x7F) == 0)
                return dfLa.Slice(at + 4, length).ToArray();
            if ((header & 0x80) != 0)
                break;
            at += 4 + length;
        }

        throw new MediaFormatException("The dfLa box has no STREAMINFO block.");
    }

    // ---- moof ----

    /// <summary>§8.8: one movie fragment; every traf's runs are appended to its track at absolute offsets.</summary>
    private void ParseMoof(ReadOnlyMemory<byte> data, BoxHeader moof, long length)
    {
        long runningOffset = moof.Start;
        foreach (var (box, body) in Box.Children(data, moof.DataStart))
        {
            if (box.Type != BoxType.Traf)
                continue;
            runningOffset = ParseTraf(body, box.DataStart, moof.Start, runningOffset, length);
        }
    }

    private long ParseTraf(ReadOnlyMemory<byte> data, long dataPosition, long moofStart, long runningOffset, long length)
    {
        Track? track = null;
        uint tfFlags = 0;
        long baseDataOffset = moofStart;
        bool explicitBase = false;
        uint defaultDuration = 0, defaultSize = 0, defaultFlags = 0;
        long? decodeTime = null;
        var runs = new List<(BoxHeader Header, ReadOnlyMemory<byte> Body)>();

        foreach (var (box, body) in Box.Children(data, dataPosition))
        {
            var span = body.Span;
            if (box.Type == BoxType.Tfhd)
            {
                tfFlags = Box.FullBox(span).Flags;
                uint trackId = Box.U32(span, 4);
                _tracksById.TryGetValue(trackId, out track);
                int at = 8;
                if ((tfFlags & 0x1) != 0) { baseDataOffset = Track.Clamp((Int128)Box.U64(span, at)); explicitBase = true; at += 8; }
                if ((tfFlags & 0x2) != 0) at += 4;
                defaultDuration = track?.DefaultSampleDuration ?? 0;
                defaultSize = track?.DefaultSampleSize ?? 0;
                defaultFlags = track?.DefaultSampleFlags ?? 0;
                if ((tfFlags & 0x8) != 0) { defaultDuration = Box.U32(span, at); at += 4; }
                if ((tfFlags & 0x10) != 0) { defaultSize = Box.U32(span, at); at += 4; }
                if ((tfFlags & 0x20) != 0) { defaultFlags = Box.U32(span, at); at += 4; }
                if (!explicitBase && (tfFlags & 0x20000) == 0)
                    baseDataOffset = runningOffset;
            }
            else if (box.Type == BoxType.Tfdt)
            {
                var (version, _) = Box.FullBox(span);
                decodeTime = version == 1 ? Track.Clamp((Int128)Box.U64(span, 4)) : Box.U32(span, 4);
            }
            else if (box.Type == BoxType.Trun)
            {
                runs.Add((box, body));
            }
        }

        if (track is null)
            return runningOffset; // a track this engine does not play
        if ((tfFlags & 0x10000) != 0)
            return runningOffset; // duration-is-empty
        if (decodeTime is { } start)
            track.FragmentDts = Track.Clamp(start);

        long end = runningOffset;
        foreach (var (box, body) in runs)
        {
            var span = body.Span;
            var (version, flags) = Box.FullBox(span);
            int perSample = ((flags & 0x100) != 0 ? 4 : 0) + ((flags & 0x200) != 0 ? 4 : 0) + ((flags & 0x400) != 0 ? 4 : 0) + ((flags & 0x800) != 0 ? 4 : 0);
            int count = Box.Count(span, 4, Math.Max(perSample, 1), _context.Limits.MaxSampleTableEntries, "trun");
            int at = 8;
            long offset = baseDataOffset;
            if ((flags & 0x1) != 0) { offset = Track.Clamp((Int128)offset + Box.S32(span, at)); at += 4; }
            uint firstFlags = defaultFlags;
            if ((flags & 0x4) != 0) { firstFlags = Box.U32(span, at); at += 4; }
            if (perSample == 0 && count > 0 && (long)count * 0 > span.Length - at)
                throw new MediaFormatException("The trun box is truncated.");
            if ((long)count * perSample > span.Length - at)
                throw new MediaFormatException("The trun box declares more samples than it holds.");
            if (track.Samples.Count + count > _context.Limits.MaxSampleTableEntries)
                throw new MediaLimitExceededException(nameof(MediaLimits.MaxSampleTableEntries), track.Samples.Count + count, _context.Limits.MaxSampleTableEntries);

            for (int i = 0; i < count; i++)
            {
                uint duration = defaultDuration;
                uint size = defaultSize;
                uint sampleFlags = i == 0 ? firstFlags : defaultFlags;
                int cts = 0;
                if ((flags & 0x100) != 0) { duration = Box.U32(span, at); at += 4; }
                if ((flags & 0x200) != 0) { size = Box.U32(span, at); at += 4; }
                if ((flags & 0x400) != 0) { sampleFlags = Box.U32(span, at); at += 4; }
                if ((flags & 0x800) != 0) { cts = version == 0 ? (int)Math.Min(int.MaxValue, Box.U32(span, at)) : Box.S32(span, at); at += 4; }

                int sampleSize = ToSampleSize(size);
                _context.Limits.CheckPacketSize(track.Kind, sampleSize);
                if (offset < 0 || offset > length - sampleSize)
                    throw new MediaFormatException("A fragment sample lies outside the resource.");
                bool sync = ((sampleFlags >> 16) & 1) == 0;
                track.Samples.Add(new Sample(offset, sampleSize, track.FragmentDts, cts, (int)Math.Min(int.MaxValue, duration), sync));
                track.FragmentDts = Track.Clamp((Int128)track.FragmentDts + duration);
                offset += sampleSize;
            }

            end = offset;
        }

        return end;
    }

    // ---- packets ----

    public async ValueTask<EncodedPacket?> ReadPacketAsync(CancellationToken cancellationToken)
    {
        if (!_open)
            throw new InvalidOperationException("InitializeAsync has not run.");

        // The track whose next sample decodes earliest goes first, so audio and video interleave.
        Track? chosen = null;
        MediaTime earliest = MediaTime.PositiveInfinity;
        foreach (var track in _tracks)
        {
            if (track.Next >= track.Samples.Count)
                continue;
            var dts = track.Time(track.Samples[track.Next].Dts);
            if (chosen is null || dts < earliest)
            {
                chosen = track;
                earliest = dts;
            }
        }

        if (chosen is null)
            return null;

        var sample = chosen.Samples[chosen.Next++];
        var pts = chosen.Time(Track.Clamp((Int128)sample.Dts + sample.CtsOffset));
        var duration = MediaTime.FromTimescale(sample.Duration, chosen.Timescale);
        var packet = EncodedPacket.Rent(_context.Limits, chosen.Kind, chosen.Id, sample.Size, pts, chosen.Time(sample.Dts), duration, chosen.Kind == MediaTrackKind.Audio || sample.IsSync);
        int read = await _source.ReadAtLeastAsync(sample.Offset, packet.Memory, cancellationToken).ConfigureAwait(false);
        if (read < sample.Size)
        {
            packet.Dispose();
            throw new MediaFormatException("A sample is truncated.");
        }

        return packet;
    }

    /// <summary>
    /// Every track resumes at its last sync sample whose presentation time is at or before
    /// the target (all audio samples are sync); the caller discards what it decodes before it.
    /// </summary>
    public ValueTask SeekAsync(MediaTime target, CancellationToken cancellationToken)
    {
        if (!_open)
            throw new InvalidOperationException("InitializeAsync has not run.");
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var track in _tracks)
        {
            int index = 0;
            for (int i = 0; i < track.Samples.Count; i++)
            {
                var sample = track.Samples[i];
                if (track.Time(Track.Clamp((Int128)sample.Dts + sample.CtsOffset)) > target)
                    break;
                if (sample.IsSync || track.Kind == MediaTrackKind.Audio)
                    index = i;
            }

            // B-frames: a later sync sample may show after an earlier one that decodes
            // first; resume at the sync sample so every reference is decoded.
            track.Next = index;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
