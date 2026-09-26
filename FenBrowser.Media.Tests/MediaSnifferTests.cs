using System.Text;
using FenBrowser.Media.Sniffing;

namespace FenBrowser.Media.Tests;

public class MediaSnifferTests
{
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Theory]
    [InlineData("464F524D01020304 41494646", MediaSniffer.Aiff)]     // FORM ???? AIFF
    [InlineData("494433 0400", MediaSniffer.Mpeg)]                   // ID3
    [InlineData("4F676753 00 02", MediaSniffer.Ogg)]                 // OggS NUL
    [InlineData("4D546864 00000006 0001", MediaSniffer.Midi)]        // MThd 6
    [InlineData("52494646 FFFFFFFF 41564920 4C495354", MediaSniffer.Avi)]  // RIFF ???? "AVI "
    [InlineData("52494646 24000000 57415645 666D7420", MediaSniffer.Wave)] // RIFF ???? WAVE
    public void Sniff_MatchesPatternTable(string hex, string expected)
    {
        Assert.Equal(expected, MediaSniffer.Sniff(Hex(hex)));
    }

    [Theory]
    [InlineData("4F676753")]                   // OggS without the NUL
    [InlineData("4D546864 00000007")]          // MThd with the wrong length
    [InlineData("52494646 00000000 57454250")] // RIFF WEBP is an image
    [InlineData("4944")]                       // truncated ID3
    [InlineData("")]
    public void Sniff_ReturnsNullForNearMisses(string hex)
    {
        Assert.Null(MediaSniffer.Sniff(Hex(hex)));
    }

    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));

    private static byte[] Ftyp(string majorBrand, params string[] compatible)
    {
        int size = 16 + (4 * compatible.Length);
        var box = new List<byte> { 0, 0, (byte)(size >> 8), (byte)size };
        box.AddRange(Ascii("ftyp"));
        box.AddRange(Ascii(majorBrand));
        box.AddRange([0, 0, 2, 0]);
        foreach (var brand in compatible)
            box.AddRange(Ascii(brand));
        return [.. box];
    }

    [Fact]
    public void Mp4_MajorBrandMp4()
    {
        Assert.Equal(MediaSniffer.Mp4, MediaSniffer.Sniff(Ftyp("mp42", "isom")));
    }

    [Fact]
    public void Mp4_CompatibleBrandMp4()
    {
        // What ffmpeg writes for an .m4a/.mp4 muxed as "isom": isom, iso2, avc1, mp41.
        Assert.Equal(MediaSniffer.Mp4, MediaSniffer.Sniff(Ftyp("isom", "isom", "iso2", "avc1", "mp41")));
    }

    [Fact]
    public void Mp4_NoMp4Brand_IsNotMp4()
    {
        // QuickTime: ftyp qt without an mp4 brand is undefined for the sniffer.
        Assert.Null(MediaSniffer.Sniff(Ftyp("qt  ", "qt  ")));
    }

    [Fact]
    public void Mp4_RejectsBoxSizeLargerThanHeaderOrMisaligned()
    {
        byte[] box = Ftyp("isom", "mp41");
        box[3] = 0xFC; // claims 252 bytes; header is 20
        Assert.False(MediaSniffer.MatchesMp4Signature(box));

        byte[] odd = Ftyp("isom", "mp41");
        odd[3] = 19;   // not a multiple of four
        Assert.False(MediaSniffer.MatchesMp4Signature(odd));
    }

    [Fact]
    public void Mp4_HugeBoxSizeDoesNotOverflow()
    {
        byte[] box = Ftyp("isom", "mp41");
        box[0] = 0xFF; box[1] = 0xFF; box[2] = 0xFF; box[3] = 0xFC;
        Assert.False(MediaSniffer.MatchesMp4Signature(box));
    }

    private static byte[] WebMHeader(string docType, byte sizeByte = 0x84, int padding = 0)
    {
        // EBML header: ID, a 1-byte size, EBMLVersion (0x4286 81 01), then DocType.
        return Concat(
            [0x1A, 0x45, 0xDF, 0xA3, 0x9F],
            [0x42, 0x86, 0x81, 0x01],
            [0x42, 0x82, sizeByte],
            new byte[padding],
            Ascii(docType),
            [0x42, 0x87, 0x81, 0x04, 0x42, 0x85, 0x81, 0x02]);
    }

    [Fact]
    public void WebM_DocTypeWebm()
    {
        Assert.Equal(MediaSniffer.WebM, MediaSniffer.Sniff(WebMHeader("webm")));
    }

    [Fact]
    public void WebM_DocTypeMatroska_IsNotWebm()
    {
        Assert.Null(MediaSniffer.Sniff(WebMHeader("matroska", 0x88)));
    }

    [Fact]
    public void WebM_ZeroPaddedDocType()
    {
        Assert.True(MediaSniffer.MatchesWebMSignature(WebMHeader("webm", 0x86, padding: 2)));
    }

    [Fact]
    public void WebM_DocTypeBeyondByte38_IsNotFound()
    {
        byte[] header = Concat(
            [0x1A, 0x45, 0xDF, 0xA3, 0xA3],
            new byte[40].Select(_ => (byte)0xEC).ToArray(), // 40 bytes of filler
            [0x42, 0x82, 0x84],
            Ascii("webm"),
            new byte[8]);
        Assert.False(MediaSniffer.MatchesWebMSignature(header));
    }

    [Fact]
    public void WebM_TruncatedAfterDocTypeId_IsRejected()
    {
        Assert.False(MediaSniffer.MatchesWebMSignature([0x1A, 0x45, 0xDF, 0xA3, 0x42, 0x82]));
        Assert.False(MediaSniffer.MatchesWebMSignature([0x1A, 0x45, 0xDF, 0xA3, 0x42, 0x82, 0x84, 0x77, 0x65]));
        Assert.False(MediaSniffer.MatchesWebMSignature([0x1A, 0x45, 0xDF, 0xA3, 0x42]));
    }

    [Theory]
    [InlineData(0x80, 1)]
    [InlineData(0x40, 2)]
    [InlineData(0x01, 8)]
    [InlineData(0x00, 8)]
    public void ParseVintSize_CountsLeadingZeros(byte first, int expected)
    {
        Assert.Equal(expected, MediaSniffer.ParseVintSize(first));
    }

    // MPEG-1 Layer III, 128 kbit/s, 44.1 kHz, no padding: FF FB 90 00, 417-byte frames.
    private static byte[] Mp3Frames(int frameCount, byte b1 = 0xFB, byte b2 = 0x90, int frameSize = 417)
    {
        var bytes = new byte[(frameCount * frameSize) + 4];
        for (int f = 0; f < frameCount; f++)
        {
            int o = f * frameSize;
            bytes[o] = 0xFF;
            bytes[o + 1] = b1;
            bytes[o + 2] = b2;
            bytes[o + 3] = 0x64;
        }

        return bytes;
    }

    [Fact]
    public void Mp3WithoutId3_TwoConsecutiveFrames()
    {
        byte[] frames = Mp3Frames(4);
        Assert.Equal(417, MediaSniffer.ComputeMp3FrameSize(frames, 0));
        Assert.Equal(MediaSniffer.Mpeg, MediaSniffer.Sniff(frames.AsSpan(0, MediaSniffer.ResourceHeaderLength)));
    }

    [Fact]
    public void Mp3WithoutId3_PaddingAddsAByte()
    {
        byte[] frames = Mp3Frames(2, b2: 0x92, frameSize: 418);
        Assert.Equal(418, MediaSniffer.ComputeMp3FrameSize(frames, 0));
        Assert.True(MediaSniffer.MatchesMp3WithoutId3Signature(frames));
    }

    [Fact]
    public void Mp3WithoutId3_Mpeg2UsesHalfRateTables()
    {
        // MPEG-2 Layer III (version bits 10), 64 kbit/s index 8, 22.05 kHz: 72 * 64000 / 22050 = 208.
        byte[] frames = Mp3Frames(3, b1: 0xF3, b2: 0x80, frameSize: 208);
        Assert.Equal(208, MediaSniffer.ComputeMp3FrameSize(frames, 0));
        Assert.True(MediaSniffer.MatchesMp3WithoutId3Signature(frames));
    }

    [Fact]
    public void Mp3WithoutId3_NoSecondHeader_IsRejected()
    {
        byte[] frames = Mp3Frames(1);
        Array.Resize(ref frames, 900);
        Assert.False(MediaSniffer.MatchesMp3WithoutId3Signature(frames));
    }

    [Fact]
    public void Mp3WithoutId3_FrameRunningPastHeader_IsRejected()
    {
        // A valid first header, but the header ends before the second frame would start.
        Assert.False(MediaSniffer.MatchesMp3WithoutId3Signature(Mp3Frames(1).AsSpan(0, 300)));
    }

    [Theory]
    [InlineData(0xFB, 0xF0)] // bitrate index 15
    [InlineData(0xFB, 0x9C)] // sample-rate index 3
    [InlineData(0xF9, 0x90)] // layer bits 00
    [InlineData(0xFD, 0x90)] // Layer II, not Layer III
    [InlineData(0x1B, 0x90)] // no frame sync in the second byte
    public void Mp3Header_RejectsInvalidFields(byte b1, byte b2)
    {
        Assert.False(MediaSniffer.MatchesMp3Header([0xFF, b1, b2, 0x00], 0));
    }

    [Fact]
    public void Mp3Header_FirstByteMustBeSync()
    {
        // The spec prose says "and"; either half failing must reject.
        Assert.False(MediaSniffer.MatchesMp3Header([0xFE, 0xFB, 0x90, 0x00], 0));
    }

    [Fact]
    public void Mp3Header_ReadsAtOffsetWithBoundsCheck()
    {
        Assert.False(MediaSniffer.MatchesMp3Header([0x00, 0xFF, 0xFB, 0x90], 1));
        Assert.True(MediaSniffer.MatchesMp3Header([0x00, 0xFF, 0xFB, 0x90, 0x00], 1));
    }

    [Fact]
    public void Sniff_NeverThrowsOnRandomInput()
    {
        var random = new Random(1445);
        var buffer = new byte[MediaSniffer.ResourceHeaderLength];
        for (int i = 0; i < 20_000; i++)
        {
            int length = random.Next(buffer.Length + 1);
            random.NextBytes(buffer.AsSpan(0, length));

            // Bias towards the interesting prefixes so the deeper branches run.
            switch (i % 4)
            {
                case 1 when length >= 4:
                    buffer[0] = 0x1A; buffer[1] = 0x45; buffer[2] = 0xDF; buffer[3] = 0xA3;
                    break;
                case 2 when length >= 2:
                    buffer[0] = 0xFF; buffer[1] |= 0xE0;
                    break;
                case 3 when length >= 8:
                    buffer[4] = (byte)'f'; buffer[5] = (byte)'t'; buffer[6] = (byte)'y'; buffer[7] = (byte)'p';
                    break;
            }

            _ = MediaSniffer.Sniff(buffer.AsSpan(0, length));
        }
    }
}
