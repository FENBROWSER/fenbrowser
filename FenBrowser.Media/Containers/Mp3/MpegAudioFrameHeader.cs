namespace FenBrowser.Media.Containers.Mp3;

/// <summary>
/// A 32-bit MPEG audio frame header (ISO/IEC 11172-3 §2.4.1.3 for MPEG-1, ISO/IEC 13818-3
/// for MPEG-2, and the unofficial MPEG-2.5 extension used by every decoder).
/// </summary>
public readonly record struct MpegAudioFrameHeader(
    int Version,        // 1, 2 or 25 (MPEG-2.5)
    int Layer,          // 1, 2 or 3
    int BitrateKbps,
    int SampleRate,
    int Channels,
    int SamplesPerFrame,
    int FrameLength,    // bytes including the header and padding
    bool HasCrc)
{
    private static readonly int[][] s_bitrates =
    [
        // MPEG-1 Layer I
        [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448, 0],
        // MPEG-1 Layer II
        [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 0],
        // MPEG-1 Layer III
        [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0],
        // MPEG-2/2.5 Layer I
        [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256, 0],
        // MPEG-2/2.5 Layer II and III
        [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0],
    ];

    private static readonly int[] s_sampleRatesMpeg1 = [44100, 48000, 32000];

    /// <summary>Parses the header at the start of <paramref name="bytes"/>; false for anything that is not a frame.</summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, out MpegAudioFrameHeader header)
    {
        header = default;
        if (bytes.Length < 4)
            return false;
        // 11-bit sync.
        if (bytes[0] != 0xFF || (bytes[1] & 0xE0) != 0xE0)
            return false;

        int versionBits = (bytes[1] >> 3) & 0x03;   // 00 = 2.5, 01 = reserved, 10 = 2, 11 = 1
        int layerBits = (bytes[1] >> 1) & 0x03;     // 00 = reserved, 01 = III, 10 = II, 11 = I
        bool hasCrc = (bytes[1] & 0x01) == 0;
        int bitrateIndex = bytes[2] >> 4;
        int sampleRateIndex = (bytes[2] >> 2) & 0x03;
        int padding = (bytes[2] >> 1) & 0x01;
        int channelMode = bytes[3] >> 6;            // 11 = mono

        if (versionBits == 1 || layerBits == 0 || bitrateIndex is 0 or 15 || sampleRateIndex == 3)
            return false;

        int version = versionBits switch { 3 => 1, 2 => 2, _ => 25 };
        int layer = 4 - layerBits;
        int sampleRate = s_sampleRatesMpeg1[sampleRateIndex];
        if (version == 2)
            sampleRate /= 2;
        else if (version == 25)
            sampleRate /= 4;

        int table = version == 1 ? layer - 1 : (layer == 1 ? 3 : 4);
        int bitrate = s_bitrates[table][bitrateIndex];
        if (bitrate == 0)
            return false;

        int samplesPerFrame = layer switch
        {
            1 => 384,
            2 => 1152,
            _ => version == 1 ? 1152 : 576,
        };

        // Frame length (ISO 11172-3 §2.4.3.1 and the MPEG-2 half-rate variant).
        int frameLength = layer == 1
            ? (12 * bitrate * 1000 / sampleRate + padding) * 4
            : samplesPerFrame / 8 * bitrate * 1000 / sampleRate + padding;
        if (frameLength < 4)
            return false;

        header = new MpegAudioFrameHeader(version, layer, bitrate, sampleRate, channelMode == 3 ? 1 : 2, samplesPerFrame, frameLength, hasCrc);
        return true;
    }

    /// <summary>Two headers belong to the same stream when the fields that cannot change between frames agree.</summary>
    public bool IsCompatibleWith(in MpegAudioFrameHeader other) =>
        Version == other.Version && Layer == other.Layer && SampleRate == other.SampleRate && Channels == other.Channels;

    /// <summary>Offset of the side information / Xing tag inside a Layer III frame, after the header and CRC.</summary>
    public int XingOffset
    {
        get
        {
            int sideInfo = Version == 1 ? (Channels == 1 ? 17 : 32) : (Channels == 1 ? 9 : 17);
            return 4 + sideInfo;
        }
    }
}
