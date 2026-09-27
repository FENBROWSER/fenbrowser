using System.Buffers.Binary;

namespace FenBrowser.Media.Containers.Ogg;

/// <summary>An Ogg page header (RFC 3533 §6), parsed but not yet its body.</summary>
public readonly record struct OggPageHeader(
    byte Flags,
    long GranulePosition,
    uint Serial,
    uint Sequence,
    uint Crc,
    int SegmentCount,
    int HeaderLength,
    int BodyLength)
{
    public const int FixedHeaderLength = 27;
    public const int MaxSegments = 255;

    public bool IsContinued => (Flags & 0x01) != 0;
    public bool IsBeginningOfStream => (Flags & 0x02) != 0;
    public bool IsEndOfStream => (Flags & 0x04) != 0;

    public int TotalLength => HeaderLength + BodyLength;

    /// <summary>
    /// Parses the capture pattern, version, flags, granule, serial, sequence, CRC and the
    /// segment table. Needs at least 27 bytes plus the segment table.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, out OggPageHeader header, out ReadOnlySpan<byte> segmentTable)
    {
        header = default;
        segmentTable = default;
        if (bytes.Length < FixedHeaderLength || !bytes[..4].SequenceEqual("OggS"u8) || bytes[4] != 0)
            return false;

        int segments = bytes[26];
        if (bytes.Length < FixedHeaderLength + segments)
            return false;

        segmentTable = bytes.Slice(FixedHeaderLength, segments);
        int body = 0;
        foreach (byte lacing in segmentTable)
            body += lacing;

        header = new OggPageHeader(
            bytes[5],
            BinaryPrimitives.ReadInt64LittleEndian(bytes[6..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[14..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[18..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[22..]),
            segments,
            FixedHeaderLength + segments,
            body);
        return true;
    }
}

/// <summary>The Ogg CRC (RFC 3533 §6: polynomial 0x04c11db7, no reflection, zero initial value).</summary>
public static class OggCrc
{
    private static readonly uint[] s_table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint r = i << 24;
            for (int j = 0; j < 8; j++)
                r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04c11db7 : r << 1;
            table[i] = r;
        }

        return table;
    }

    /// <summary>The checksum of a whole page with its CRC field taken as zero.</summary>
    public static uint Compute(ReadOnlySpan<byte> page)
    {
        uint crc = 0;
        for (int i = 0; i < page.Length; i++)
        {
            byte b = i is >= 22 and < 26 ? (byte)0 : page[i];
            crc = (crc << 8) ^ s_table[((crc >> 24) & 0xFF) ^ b];
        }

        return crc;
    }
}
