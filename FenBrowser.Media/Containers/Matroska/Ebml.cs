using System.Buffers.Binary;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Containers.Matroska;

/// <summary>Element IDs (RFC 8794 EBML; Matroska specification, "Element IDs") the demuxer understands.</summary>
public static class EbmlId
{
    public const uint EbmlHeader = 0x1A45DFA3;
    public const uint DocType = 0x4282;
    public const uint DocTypeReadVersion = 0x4285;
    public const uint Void = 0xEC;
    public const uint Crc32 = 0xBF;

    public const uint Segment = 0x18538067;
    public const uint SeekHead = 0x114D9B74;
    public const uint Seek = 0x4DBB;
    public const uint SeekId = 0x53AB;
    public const uint SeekPosition = 0x53AC;
    public const uint Info = 0x1549A966;
    public const uint TimestampScale = 0x2AD7B1;
    public const uint Duration = 0x4489;
    public const uint Tracks = 0x1654AE6B;
    public const uint TrackEntry = 0xAE;
    public const uint TrackNumber = 0xD7;
    public const uint TrackType = 0x83;
    public const uint FlagDefault = 0x88;
    public const uint DefaultDuration = 0x23E383;
    public const uint Name = 0x536E;
    public const uint Language = 0x22B59C;
    public const uint LanguageBcp47 = 0x22B59D;
    public const uint CodecId = 0x86;
    public const uint CodecPrivate = 0x63A2;
    public const uint CodecDelay = 0x56AA;
    public const uint SeekPreRoll = 0x56BB;
    public const uint Video = 0xE0;
    public const uint PixelWidth = 0xB0;
    public const uint PixelHeight = 0xBA;
    public const uint DisplayWidth = 0x54B0;
    public const uint DisplayHeight = 0x54BA;
    public const uint Audio = 0xE1;
    public const uint SamplingFrequency = 0xB5;
    public const uint Channels = 0x9F;
    public const uint BitDepth = 0x6264;
    public const uint ContentEncodings = 0x6D80;
    public const uint ContentEncoding = 0x6240;
    public const uint ContentEncodingScope = 0x5032;
    public const uint ContentEncodingType = 0x5033;
    public const uint ContentCompression = 0x5034;
    public const uint ContentCompAlgo = 0x4254;
    public const uint ContentCompSettings = 0x4255;
    public const uint Cluster = 0x1F43B675;
    public const uint Timestamp = 0xE7;
    public const uint SimpleBlock = 0xA3;
    public const uint BlockGroup = 0xA0;
    public const uint Block = 0xA1;
    public const uint BlockDuration = 0x9B;
    public const uint ReferenceBlock = 0xFB;
    public const uint Cues = 0x1C53BB6B;
    public const uint CuePoint = 0xBB;
    public const uint CueTime = 0xB3;
    public const uint CueTrackPositions = 0xB7;
    public const uint CueTrack = 0xF7;
    public const uint CueClusterPosition = 0xF1;
    public const uint CueRelativePosition = 0xF0;
    public const uint Tags = 0x1254C367;
    public const uint Attachments = 0x1941A469;
    public const uint Chapters = 0x1043A770;
}

/// <summary>An element header: its ID, the size of its data, and where that data starts.</summary>
/// <param name="DataLength">Null for an element of unknown size (RFC 8794 §6.2), allowed only on Segment and Cluster.</param>
public readonly record struct EbmlElement(uint Id, long? DataLength, long DataStart, int HeaderLength)
{
    public long? End => DataLength is { } length ? DataStart + length : null;
}

/// <summary>
/// RFC 8794 variable-size integers and element headers, parsed from spans so the demuxer
/// can bound every read before it happens. Nothing here throws on short or hostile input;
/// callers get <c>false</c> and decide.
/// </summary>
public static class Ebml
{
    public const int MaxHeaderLength = 4 + 8;

    /// <summary>
    /// Reads an element ID: a VINT whose marker bits are kept (RFC 8794 §5). IDs are one to
    /// four bytes; the reserved all-ones ID and an ID with a longer-than-needed encoding are invalid.
    /// </summary>
    public static bool TryReadId(ReadOnlySpan<byte> bytes, out uint id, out int length)
    {
        id = 0;
        length = 0;
        if (bytes.Length == 0)
            return false;
        int first = bytes[0];
        if (first == 0)
            return false;
        int n = 1;
        while ((first & (0x80 >> (n - 1))) == 0)
            n++;
        if (n > 4 || bytes.Length < n)
            return false;
        uint value = 0;
        for (int i = 0; i < n; i++)
            value = (value << 8) | bytes[i];
        // An ID whose value bits are all ones is reserved (RFC 8794 §5).
        uint marker = 1u << (7 * n);
        if ((value & (marker - 1)) == marker - 1)
            return false;
        id = value;
        length = n;
        return true;
    }

    /// <summary>
    /// Reads an element data size (RFC 8794 §6.2). <paramref name="size"/> is null for the
    /// reserved all-ones "unknown size" encoding.
    /// </summary>
    public static bool TryReadSize(ReadOnlySpan<byte> bytes, out long? size, out int length)
    {
        size = null;
        length = 0;
        if (bytes.Length == 0)
            return false;
        int first = bytes[0];
        if (first == 0)
            return false;
        int n = 1;
        while ((first & (0x80 >> (n - 1))) == 0)
            n++;
        if (n > 8 || bytes.Length < n)
            return false;
        long value = first & (0xFF >> n);
        bool allOnes = value == (0xFF >> n);
        for (int i = 1; i < n; i++)
        {
            value = (value << 8) | bytes[i];
            allOnes &= bytes[i] == 0xFF;
        }

        length = n;
        size = allOnes ? null : value;
        return true;
    }

    /// <summary>Parses the header of the element that starts at the beginning of <paramref name="bytes"/>.</summary>
    public static bool TryReadElement(ReadOnlySpan<byte> bytes, long position, out EbmlElement element)
    {
        element = default;
        if (!TryReadId(bytes, out uint id, out int idLength))
            return false;
        if (!TryReadSize(bytes[idLength..], out long? size, out int sizeLength))
            return false;
        int headerLength = idLength + sizeLength;
        element = new EbmlElement(id, size, position + headerLength, headerLength);
        return true;
    }

    /// <summary>An unsigned integer element of zero to eight bytes (RFC 8794 §7.1).</summary>
    public static ulong ReadUnsigned(ReadOnlySpan<byte> data)
    {
        if (data.Length > 8)
            throw new MediaFormatException($"An EBML unsigned integer of {data.Length} bytes is not allowed.");
        ulong value = 0;
        foreach (byte b in data)
            value = (value << 8) | b;
        return value;
    }

    /// <summary>A signed integer element of zero to eight bytes (RFC 8794 §7.2).</summary>
    public static long ReadSigned(ReadOnlySpan<byte> data)
    {
        if (data.Length > 8)
            throw new MediaFormatException($"An EBML signed integer of {data.Length} bytes is not allowed.");
        if (data.Length == 0)
            return 0;
        long value = (sbyte)data[0];
        for (int i = 1; i < data.Length; i++)
            value = (value << 8) | data[i];
        return value;
    }

    /// <summary>A float element of zero, four or eight bytes (RFC 8794 §7.3).</summary>
    public static double ReadFloat(ReadOnlySpan<byte> data) => data.Length switch
    {
        0 => 0.0,
        4 => BinaryPrimitives.ReadSingleBigEndian(data),
        8 => BinaryPrimitives.ReadDoubleBigEndian(data),
        _ => throw new MediaFormatException($"An EBML float of {data.Length} bytes is not allowed."),
    };

    /// <summary>A string element, with the null padding RFC 8794 §7.4 allows removed.</summary>
    public static string ReadString(ReadOnlySpan<byte> data)
    {
        int end = data.IndexOf((byte)0);
        if (end >= 0)
            data = data[..end];
        return System.Text.Encoding.UTF8.GetString(data);
    }
}
