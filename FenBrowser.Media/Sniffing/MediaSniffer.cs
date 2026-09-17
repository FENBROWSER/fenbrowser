namespace FenBrowser.Media.Sniffing;

/// <summary>
/// WHATWG MIME Sniffing §6.2 "Matching an audio or video type pattern".
/// </summary>
/// <remarks>
/// Callers pass the resource header (the first 1445 bytes at most, §5.2 "Reading the
/// resource header"). Every read is bounds-checked against the span, so truncated or
/// hostile headers only ever produce <c>null</c>.
///
/// Where the prose of the MP3 steps is known to be broken, this follows Gecko's
/// <c>toolkit/components/mediasniffer/mp3sniff.c</c>, which the steps were transcribed
/// from. Each deviation is marked "Spec text:" below.
/// </remarks>
public static class MediaSniffer
{
    public const int ResourceHeaderLength = 1445;

    public const string Aiff = "audio/aiff";
    public const string Mpeg = "audio/mpeg";
    public const string Ogg = "application/ogg";
    public const string Midi = "audio/midi";
    public const string Avi = "video/avi";
    public const string Wave = "audio/wave";
    public const string Mp4 = "video/mp4";
    public const string WebM = "video/webm";

    private static readonly Row[] s_table =
    [
        // "FORM" ???? "AIFF"
        new([0x46, 0x4F, 0x52, 0x4D, 0x00, 0x00, 0x00, 0x00, 0x41, 0x49, 0x46, 0x46],
            [0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF], Aiff),
        // "ID3"
        new([0x49, 0x44, 0x33], [0xFF, 0xFF, 0xFF], Mpeg),
        // "OggS" NUL
        new([0x4F, 0x67, 0x67, 0x53, 0x00], [0xFF, 0xFF, 0xFF, 0xFF, 0xFF], Ogg),
        // "MThd" 00 00 00 06
        new([0x4D, 0x54, 0x68, 0x64, 0x00, 0x00, 0x00, 0x06],
            [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], Midi),
        // "RIFF" ???? "AVI "
        new([0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x41, 0x56, 0x49, 0x20],
            [0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF], Avi),
        // "RIFF" ???? "WAVE"
        new([0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45],
            [0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF], Wave),
    ];

    // MPEG-1 Layer III bitrates, then MPEG-2/2.5 Layer III bitrates (bits per second).
    private static readonly int[] s_mpeg1Bitrates =
        [0, 32000, 40000, 48000, 56000, 64000, 80000, 96000, 112000, 128000, 160000, 192000, 224000, 256000, 320000];
    private static readonly int[] s_mpeg2Bitrates =
        [0, 8000, 16000, 24000, 32000, 40000, 48000, 56000, 64000, 80000, 96000, 112000, 128000, 144000, 160000];
    private static readonly int[] s_sampleRates = [44100, 48000, 32000];

    /// <summary>
    /// The audio or video type pattern matching algorithm. Returns the computed MIME type,
    /// or <c>null</c> for "undefined".
    /// </summary>
    public static string? Sniff(ReadOnlySpan<byte> input)
    {
        foreach (var row in s_table)
        {
            if (MatchesPattern(input, row.Pattern, row.Mask))
                return row.MimeType;
        }

        if (MatchesMp4Signature(input))
            return Mp4;
        if (MatchesWebMSignature(input))
            return WebM;
        if (MatchesMp3WithoutId3Signature(input))
            return Mpeg;
        return null;
    }

    /// <summary>§6 "pattern matching algorithm" with no ignored leading bytes.</summary>
    internal static bool MatchesPattern(ReadOnlySpan<byte> input, ReadOnlySpan<byte> pattern, ReadOnlySpan<byte> mask)
    {
        if (input.Length < pattern.Length)
            return false;
        for (int p = 0; p < pattern.Length; p++)
        {
            if ((input[p] & mask[p]) != pattern[p])
                return false;
        }

        return true;
    }

    /// <summary>§6.2 "matches the signature for MP4".</summary>
    public static bool MatchesMp4Signature(ReadOnlySpan<byte> sequence)
    {
        int length = sequence.Length;
        if (length < 12)
            return false;

        uint boxSize = (uint)(sequence[0] << 24 | sequence[1] << 16 | sequence[2] << 8 | sequence[3]);
        if (length < boxSize || boxSize % 4 != 0)
            return false;

        if (sequence[4] != 0x66 || sequence[5] != 0x74 || sequence[6] != 0x79 || sequence[7] != 0x70) // "ftyp"
            return false;

        if (IsMp4Brand(sequence, 8))
            return true;

        // Skip the major brand's four-byte version, then walk the compatible brands.
        for (long bytesRead = 16; bytesRead < boxSize; bytesRead += 4)
        {
            if (IsMp4Brand(sequence, (int)bytesRead))
                return true;
        }

        return false;
    }

    /// <summary>§6.2 "matches the signature for WebM".</summary>
    public static bool MatchesWebMSignature(ReadOnlySpan<byte> sequence)
    {
        int length = sequence.Length;
        if (length < 4)
            return false;
        if (sequence[0] != 0x1A || sequence[1] != 0x45 || sequence[2] != 0xDF || sequence[3] != 0xA3)
            return false;

        int iter = 4;
        while (iter < length && iter < 38)
        {
            // The EBML DocType element ID. Reading iter + 1 needs its own bounds check;
            // the spec loop condition only guards iter.
            if (iter + 1 < length && sequence[iter] == 0x42 && sequence[iter + 1] == 0x82)
            {
                iter += 2;
                if (iter >= length)
                    return false;

                iter += ParseVintSize(sequence[iter]);
                if (iter >= length - 4)
                    return false;

                if (MatchesPaddedWebM(sequence, iter))
                    return true;
            }

            iter++;
        }

        return false;
    }

    /// <summary>
    /// §6.2 "matches the signature for MP3 without ID3": a Layer III frame header, and a
    /// second one exactly one frame later.
    /// </summary>
    public static bool MatchesMp3WithoutId3Signature(ReadOnlySpan<byte> sequence)
    {
        int length = sequence.Length;
        int s = 0;
        if (!MatchesMp3Header(sequence, s))
            return false;

        int skippedBytes = ComputeMp3FrameSize(sequence, s);

        // Spec text: "greater than s - length", which is always negative. Gecko rejects a
        // frame whose end leaves no room for the next 4-byte header.
        if (skippedBytes < 4 || skippedBytes + 4 >= length - s)
            return false;

        s += skippedBytes;
        return MatchesMp3Header(sequence, s);
    }

    /// <summary>§6.2 "match an mp3 header".</summary>
    internal static bool MatchesMp3Header(ReadOnlySpan<byte> sequence, int s)
    {
        // Spec text: "If length is less than 4". The header is read at offset s.
        if (s < 0 || sequence.Length - s < 4)
            return false;

        // Spec text joins these with "and"; a frame sync needs both, as in Gecko.
        if (sequence[s] != 0xFF || (sequence[s + 1] & 0xE0) != 0xE0)
            return false;

        // Spec text writes "x & 0x06 >> 1"; the intended grouping is (x & 0x06) >> 1.
        int layer = (sequence[s + 1] & 0x06) >> 1;
        if (layer == 0)
            return false;

        int bitRate = (sequence[s + 2] & 0xF0) >> 4;
        if (bitRate == 15)
            return false;

        int sampleRate = (sequence[s + 2] & 0x0C) >> 2;
        if (sampleRate == 3)
            return false;

        int finalLayer = 4 - layer;
        return finalLayer == 3;
    }

    /// <summary>§6.2 "parse an mp3 frame" followed by "compute an mp3 frame size".</summary>
    internal static int ComputeMp3FrameSize(ReadOnlySpan<byte> sequence, int s)
    {
        int version = (sequence[s + 1] & 0x18) >> 3;
        int bitrateIndex = (sequence[s + 2] & 0xF0) >> 4;

        // Spec text picks "mp2.5-rates" when version & 1 is set. Version 3 is MPEG-1, so
        // Gecko (and the MPEG header layout) use the MPEG-1 table there.
        int bitrate = (version & 1) != 0 ? s_mpeg1Bitrates[bitrateIndex] : s_mpeg2Bitrates[bitrateIndex];

        int samplerate = s_sampleRates[(sequence[s + 2] & 0x0C) >> 2];
        if (version == 2)
            samplerate /= 2;
        else if (version == 0)
            samplerate /= 4;

        int pad = (sequence[s + 2] & 0x02) >> 1;

        // Spec text: scale 72 when "version is 1". Gecko uses 72 for MPEG-2/2.5
        // (version & 1 == 0) and 144 for MPEG-1, which matches the Layer III frame length.
        int scale = (version & 1) == 0 ? 72 : 144;
        int size = bitrate * scale / samplerate;
        if (pad != 0)
            size++;
        return size;
    }

    /// <summary>
    /// "parse a vint", reduced to the number size the WebM signature uses: the count of
    /// leading zero bits in the first byte plus one, capped at 8.
    /// </summary>
    internal static int ParseVintSize(byte first)
    {
        int mask = 128;
        int numberSize = 1;
        while (numberSize < 8)
        {
            if ((first & mask) != 0)
                break;
            mask >>= 1;
            numberSize++;
        }

        return numberSize;
    }

    /// <summary>
    /// "matching a padded sequence" for "webm" from <paramref name="offset"/>: zero or more
    /// 0x00 bytes, then the pattern, all inside the header. The WebM steps give no end
    /// offset, so the end of the header is used.
    /// </summary>
    private static bool MatchesPaddedWebM(ReadOnlySpan<byte> sequence, int offset)
    {
        int i = offset;
        while (i < sequence.Length && sequence[i] == 0x00)
            i++;
        return sequence[i..].StartsWith("webm"u8);
    }

    private static bool IsMp4Brand(ReadOnlySpan<byte> sequence, int offset) =>
        offset + 2 < sequence.Length &&
        sequence[offset] == 0x6D && sequence[offset + 1] == 0x70 && sequence[offset + 2] == 0x34; // "mp4"

    private sealed record Row(byte[] Pattern, byte[] Mask, string MimeType);
}
