using System.Globalization;

namespace FenBrowser.Media.Types;

/// <summary>
/// One entry of an RFC 6381 <c>codecs</c> parameter, identified.
/// </summary>
/// <param name="IsAmbiguous">
/// True when the string names a codec without the detail needed to be sure it plays
/// (for example a bare <c>avc1</c> with no profile); such entries cap
/// <c>canPlayType</c> at "maybe".
/// </param>
public sealed record CodecString(string Raw, MediaCodec Codec, MediaTrackKind Kind, bool IsAmbiguous)
{
    /// <summary>
    /// Splits a <c>codecs</c> parameter value into its comma-separated entries (RFC 6381 §3.2),
    /// trimming whitespace. Returns an empty list for an empty value.
    /// </summary>
    /// <summary>
    /// The colour primaries the codec string names (ITU-T H.273 values), or the binding's
    /// default of 1 (BT.709) when it leaves them out. Null for codec strings whose binding
    /// carries no colour description at all.
    /// </summary>
    public int? ColourPrimaries { get; init; }

    /// <summary>The transfer characteristics the codec string names, 1 (BT.709) by default.</summary>
    public int? TransferCharacteristics { get; init; }

    public static IReadOnlyList<string> SplitList(string codecsParameter)
    {
        ArgumentNullException.ThrowIfNull(codecsParameter);
        var result = new List<string>();
        foreach (string part in codecsParameter.Split(','))
            result.Add(MimeType.TrimHttpWhitespace(part));
        if (result.Count == 1 && result[0].Length == 0)
            result.Clear();
        return result;
    }

    /// <summary>
    /// Identifies one codec entry, validating the per-codec syntax. Returns null for
    /// anything unknown or malformed, which <c>canPlayType</c> treats as "cannot render".
    /// </summary>
    public static CodecString? Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length == 0)
            return null;

        string[] parts = raw.Split('.');
        string fourcc = parts[0];

        switch (fourcc.ToLowerInvariant())
        {
            // WebM Project: "vp8", "vp8.0".
            case "vp8" when parts.Length == 1 || (parts.Length == 2 && parts[1] == "0"):
                return new(raw, MediaCodec.Vp8, MediaTrackKind.Video, IsAmbiguous: false);

            // Legacy VP9 spellings carry no profile; the decoder handles every profile, so
            // the answer is "probably", as WPT canPlayType.html and the other engines expect.
            case "vp9" when parts.Length == 1 || (parts.Length == 2 && parts[1] == "0"):
                return new(raw, MediaCodec.Vp9, MediaTrackKind.Video, IsAmbiguous: false);

            // VP9 codec ISO media file format binding, "Codecs Parameter String".
            case "vp09" when IsValidVp09(parts):
                // VP9 codecs-parameter binding: the colour fields default to 1 (BT.709).
                return new(raw, MediaCodec.Vp9, MediaTrackKind.Video, IsAmbiguous: false)
                {
                    ColourPrimaries = parts.Length == 9 ? int.Parse(parts[5], CultureInfo.InvariantCulture) : 1,
                    TransferCharacteristics = parts.Length == 9 ? int.Parse(parts[6], CultureInfo.InvariantCulture) : 1,
                };

            // AV1 Codec ISO Media File Format Binding §5 "Codecs Parameter String".
            case "av01" when IsValidAv01(parts):
                return new(raw, MediaCodec.Av1, MediaTrackKind.Video, IsAmbiguous: false)
                {
                    ColourPrimaries = parts.Length == 10 ? int.Parse(parts[6], CultureInfo.InvariantCulture) : 1,
                    TransferCharacteristics = parts.Length == 10 ? int.Parse(parts[7], CultureInfo.InvariantCulture) : 1,
                };

            // RFC 6381 §3.3: avc1/avc3 followed by profile_idc, constraint flags, level_idc in hex.
            case "avc1" or "avc3" when parts.Length == 1:
                return new(raw, MediaCodec.H264, MediaTrackKind.Video, IsAmbiguous: true);
            case "avc1" or "avc3" when parts.Length == 2 && parts[1].Length == 6 && IsHex(parts[1]):
                return new(raw, MediaCodec.H264, MediaTrackKind.Video, IsAmbiguous: false);

            // ISO/IEC 14496-15 Annex E: hev1/hvc1.<profile>.<compat>.<tier+level>[.<constraints>].
            case "hev1" or "hvc1" when IsValidHevc(parts):
                return new(raw, MediaCodec.Hevc, MediaTrackKind.Video, IsAmbiguous: false);

            // RFC 6381 §3.3: mp4a.<OTI>[.<audio object type>].
            case "mp4a":
                return ParseMp4a(raw, parts);

            case "opus" when parts.Length == 1:
                return new(raw, MediaCodec.Opus, MediaTrackKind.Audio, IsAmbiguous: false);
            case "vorbis" when parts.Length == 1:
                return new(raw, MediaCodec.Vorbis, MediaTrackKind.Audio, IsAmbiguous: false);
            case "flac" when parts.Length == 1:
                return new(raw, MediaCodec.Flac, MediaTrackKind.Audio, IsAmbiguous: false);
            case "mp3" when parts.Length == 1:
                return new(raw, MediaCodec.Mp3, MediaTrackKind.Audio, IsAmbiguous: false);

            // RFC 2361: WAVE format tag 1 is integer PCM (and 3 is IEEE float).
            case "1" or "3" when parts.Length == 1:
                return new(raw, MediaCodec.Pcm, MediaTrackKind.Audio, IsAmbiguous: false);

            default:
                return null;
        }
    }

    private static CodecString? ParseMp4a(string raw, string[] parts)
    {
        if (parts.Length < 2 || !IsHex(parts[1]) || parts[1].Length != 2)
            return null;

        int oti = int.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        switch (oti)
        {
            case 0x40 when parts.Length == 2:
                return new(raw, MediaCodec.Aac, MediaTrackKind.Audio, IsAmbiguous: true);
            case 0x40 when parts.Length == 3 && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int aot):
                return aot switch
                {
                    2 or 5 or 29 => new(raw, MediaCodec.Aac, MediaTrackKind.Audio, IsAmbiguous: false), // AAC-LC, HE-AAC, HE-AACv2
                    34 => new(raw, MediaCodec.Mp3, MediaTrackKind.Audio, IsAmbiguous: false),          // MPEG-1/2 Layer III
                    _ => null,
                };
            case 0x66 or 0x67 or 0x68 when parts.Length == 2: // MPEG-2 AAC profiles
                return new(raw, MediaCodec.Aac, MediaTrackKind.Audio, IsAmbiguous: false);
            case 0x69 or 0x6B when parts.Length == 2:         // MPEG-2 / MPEG-1 audio
                return new(raw, MediaCodec.Mp3, MediaTrackKind.Audio, IsAmbiguous: false);
            default:
                return null;
        }
    }

    // vp09.<profile>.<level>.<bitDepth>[.<chroma>.<primaries>.<transfer>.<matrix>.<fullRange>]
    private static bool IsValidVp09(string[] parts)
    {
        if (parts.Length != 4 && parts.Length != 9)
            return false;
        if (!TwoDigits(parts[1], out int profile) || profile > 3)
            return false;
        if (!TwoDigits(parts[2], out int level) || !s_vp9Levels.Contains(level))
            return false;
        if (!TwoDigits(parts[3], out int bitDepth) || bitDepth is not (8 or 10 or 12))
            return false;
        if (parts.Length == 9)
        {
            if (!TwoDigits(parts[4], out int chroma) || chroma > 3)
                return false;
            for (int i = 5; i <= 7; i++)
            {
                if (!TwoDigits(parts[i], out _))
                    return false;
            }

            if (!TwoDigits(parts[8], out int fullRange) || fullRange > 1)
                return false;
        }

        return true;
    }

    private static readonly HashSet<int> s_vp9Levels = [10, 11, 20, 21, 30, 31, 40, 41, 50, 51, 52, 60, 61, 62];

    // av01.<profile>.<level><tier>.<bitDepth>[.<mono>.<chroma>.<primaries>.<transfer>.<matrix>.<fullRange>]
    private static bool IsValidAv01(string[] parts)
    {
        if (parts.Length != 4 && parts.Length != 10)
            return false;
        if (parts[1].Length != 1 || parts[1][0] is < '0' or > '2')
            return false;
        string levelTier = parts[2];
        if (levelTier.Length != 3 || !TwoDigits(levelTier[..2], out int level) || level > 31 || levelTier[2] is not ('M' or 'H'))
            return false;
        if (!TwoDigits(parts[3], out int bitDepth) || bitDepth is not (8 or 10 or 12))
            return false;
        if (parts.Length == 10)
        {
            if (parts[4] is not ("0" or "1"))
                return false;
            // chromaSubsampling: subsampling_x, subsampling_y (0 or 1), chroma_sample_position (0–3).
            string chroma = parts[5];
            if (chroma.Length != 3 || chroma[0] is not ('0' or '1') || chroma[1] is not ('0' or '1') || chroma[2] is < '0' or > '3')
                return false;

            // colorPrimaries, transferCharacteristics, matrixCoefficients: two digits each.
            for (int i = 6; i <= 8; i++)
            {
                if (!TwoDigits(parts[i], out _))
                    return false;
            }

            if (parts[9] is not ("0" or "1"))
                return false;
        }

        return true;
    }

    // hev1.<general_profile_space?profile_idc>.<compat flags hex>.<tier L|H><level_idc>[.<constraint bytes hex>]{0,6}
    private static bool IsValidHevc(string[] parts)
    {
        if (parts.Length < 4 || parts.Length > 10)
            return false;

        string profile = parts[1];
        if (profile.Length > 0 && profile[0] is 'A' or 'B' or 'C' or 'a' or 'b' or 'c')
            profile = profile[1..];
        if (profile.Length is 0 or > 2 || !profile.All(char.IsAsciiDigit))
            return false;

        if (parts[2].Length is 0 or > 8 || !IsHex(parts[2]))
            return false;

        string tierLevel = parts[3];
        if (tierLevel.Length < 2 || tierLevel[0] is not ('L' or 'H' or 'l' or 'h') || !tierLevel[1..].All(char.IsAsciiDigit) || tierLevel.Length > 4)
            return false;

        for (int i = 4; i < parts.Length; i++)
        {
            if (parts[i].Length is 0 or > 2 || !IsHex(parts[i]))
                return false;
        }

        return true;
    }

    private static bool TwoDigits(string s, out int value)
    {
        value = 0;
        if (s.Length != 2 || !char.IsAsciiDigit(s[0]) || !char.IsAsciiDigit(s[1]))
            return false;
        value = ((s[0] - '0') * 10) + (s[1] - '0');
        return true;
    }

    private static bool IsHex(string s) => s.Length > 0 && s.All(char.IsAsciiHexDigit);
}
