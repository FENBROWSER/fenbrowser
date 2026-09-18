using System.Buffers.Binary;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Containers.Mp4;

/// <summary>Box types (ISO/IEC 14496-12, ISO/IEC 14496-15, ISO/IEC 14496-14) the demuxer acts on, as big-endian four-character codes.</summary>
public static class BoxType
{
    public static uint Of(string fourcc) => (uint)(fourcc[0] << 24 | fourcc[1] << 16 | fourcc[2] << 8 | fourcc[3]);

    public static readonly uint Ftyp = Of("ftyp");
    public static readonly uint Moov = Of("moov");
    public static readonly uint Mvhd = Of("mvhd");
    public static readonly uint Trak = Of("trak");
    public static readonly uint Tkhd = Of("tkhd");
    public static readonly uint Edts = Of("edts");
    public static readonly uint Elst = Of("elst");
    public static readonly uint Mdia = Of("mdia");
    public static readonly uint Mdhd = Of("mdhd");
    public static readonly uint Hdlr = Of("hdlr");
    public static readonly uint Minf = Of("minf");
    public static readonly uint Stbl = Of("stbl");
    public static readonly uint Stsd = Of("stsd");
    public static readonly uint Stts = Of("stts");
    public static readonly uint Ctts = Of("ctts");
    public static readonly uint Stss = Of("stss");
    public static readonly uint Stsc = Of("stsc");
    public static readonly uint Stsz = Of("stsz");
    public static readonly uint Stz2 = Of("stz2");
    public static readonly uint Stco = Of("stco");
    public static readonly uint Co64 = Of("co64");
    public static readonly uint Mvex = Of("mvex");
    public static readonly uint Trex = Of("trex");
    public static readonly uint Mehd = Of("mehd");
    public static readonly uint Moof = Of("moof");
    public static readonly uint Mfhd = Of("mfhd");
    public static readonly uint Traf = Of("traf");
    public static readonly uint Tfhd = Of("tfhd");
    public static readonly uint Tfdt = Of("tfdt");
    public static readonly uint Trun = Of("trun");
    public static readonly uint Mdat = Of("mdat");
    public static readonly uint Free = Of("free");
    public static readonly uint Skip = Of("skip");
    public static readonly uint Uuid = Of("uuid");

    // Sample entries and their configuration boxes.
    public static readonly uint Avc1 = Of("avc1");
    public static readonly uint Avc3 = Of("avc3");
    public static readonly uint AvcC = Of("avcC");
    public static readonly uint Hvc1 = Of("hvc1");
    public static readonly uint Hev1 = Of("hev1");
    public static readonly uint HvcC = Of("hvcC");
    public static readonly uint Vp09 = Of("vp09");
    public static readonly uint VpcC = Of("vpcC");
    public static readonly uint Av01 = Of("av01");
    public static readonly uint Av1C = Of("av1C");
    public static readonly uint Mp4a = Of("mp4a");
    public static readonly uint Esds = Of("esds");
    public static readonly uint Opus = Of("Opus");
    public static readonly uint DOps = Of("dOps");
    public static readonly uint FLaC = Of("fLaC");
    public static readonly uint DfLa = Of("dfLa");
    public static readonly uint Mp3 = Of(".mp3");

    public static readonly uint HandlerVideo = Of("vide");
    public static readonly uint HandlerAudio = Of("soun");

    public static string Name(uint type) => new(
    [
        (char)((type >> 24) & 0xFF),
        (char)((type >> 16) & 0xFF),
        (char)((type >> 8) & 0xFF),
        (char)(type & 0xFF),
    ]);
}

/// <summary>A box header: type, where its payload starts and how long the whole box is.</summary>
/// <param name="Size">The whole box including the header; 0 means "to the end of the enclosing box or file".</param>
public readonly record struct BoxHeader(uint Type, long Start, long Size, int HeaderLength)
{
    public long DataStart => Start + HeaderLength;

    public long End => Start + Size;

    public long DataLength => Size - HeaderLength;
}

/// <summary>
/// ISO/IEC 14496-12 §4.2 box headers and the fixed-width readers the demuxer needs. Every
/// reader is bounds-checked against the span it is given and reports a bad layout as a
/// <see cref="MediaFormatException"/>, never an index error.
/// </summary>
public static class Box
{
    /// <summary>Parses the header at the start of <paramref name="bytes"/>, whose first byte sits at <paramref name="position"/> in the resource.</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> bytes, long position, long limit, out BoxHeader header)
    {
        header = default;
        if (bytes.Length < 8)
            return false;
        long size = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        uint type = BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]);
        int headerLength = 8;
        if (size == 1)
        {
            if (bytes.Length < 16)
                return false;
            ulong large = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
            if (large > long.MaxValue)
                return false;
            size = (long)large;
            headerLength = 16;
        }
        else if (size == 0)
        {
            size = limit - position;
        }

        if (type == BoxType.Uuid)
            headerLength += 16;
        if (size < headerLength || position + size > limit)
            return false;
        header = new BoxHeader(type, position, size, headerLength);
        return true;
    }

    /// <summary>The child boxes of a container held in memory; a malformed child ends the sequence with an error.</summary>
    public static IEnumerable<(BoxHeader Header, ReadOnlyMemory<byte> Body)> Children(ReadOnlyMemory<byte> data, long dataPosition)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            if (data.Length - offset < 8)
                throw new MediaFormatException("A box ends inside a child header.");
            if (!TryReadHeader(data.Span[offset..], dataPosition + offset, dataPosition + data.Length, out var child))
                throw new MediaFormatException("A box contains a malformed child header.");
            yield return (child, data.Slice(offset + child.HeaderLength, checked((int)child.DataLength)));
            offset += checked((int)child.Size);
        }
    }

    public static byte U8(ReadOnlySpan<byte> data, int at) =>
        at < data.Length ? data[at] : throw new MediaFormatException("A box is shorter than its layout.");

    public static ushort U16(ReadOnlySpan<byte> data, int at) =>
        at + 2 <= data.Length ? BinaryPrimitives.ReadUInt16BigEndian(data[at..]) : throw new MediaFormatException("A box is shorter than its layout.");

    public static uint U32(ReadOnlySpan<byte> data, int at) =>
        at + 4 <= data.Length ? BinaryPrimitives.ReadUInt32BigEndian(data[at..]) : throw new MediaFormatException("A box is shorter than its layout.");

    public static int S32(ReadOnlySpan<byte> data, int at) =>
        at + 4 <= data.Length ? BinaryPrimitives.ReadInt32BigEndian(data[at..]) : throw new MediaFormatException("A box is shorter than its layout.");

    public static ulong U64(ReadOnlySpan<byte> data, int at) =>
        at + 8 <= data.Length ? BinaryPrimitives.ReadUInt64BigEndian(data[at..]) : throw new MediaFormatException("A box is shorter than its layout.");

    public static long S64(ReadOnlySpan<byte> data, int at) =>
        at + 8 <= data.Length ? BinaryPrimitives.ReadInt64BigEndian(data[at..]) : throw new MediaFormatException("A box is shorter than its layout.");

    /// <summary>A FullBox's version byte and 24-bit flags (§4.2.2).</summary>
    public static (int Version, uint Flags) FullBox(ReadOnlySpan<byte> data) =>
        (U8(data, 0), U32(data, 0) & 0x00FFFFFF);

    /// <summary>An entry count that must fit the box: <paramref name="entrySize"/> bytes each from <paramref name="at"/>.</summary>
    public static int Count(ReadOnlySpan<byte> data, int at, int entrySize, int max, string box)
    {
        uint count = U32(data, at);
        if (count > max)
            throw new MediaLimitExceededException(nameof(MediaLimits.MaxSampleTableEntries), count, max);
        if ((long)count * entrySize > data.Length - at - 4)
            throw new MediaFormatException($"The {box} box declares more entries than it holds.");
        return (int)count;
    }
}
