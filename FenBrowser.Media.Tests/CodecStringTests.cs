using FenBrowser.Media.Types;

namespace FenBrowser.Media.Tests;

// RFC 6381 codecs entries and the per-codec parameter string formats.
public class CodecStringTests
{
    [Theory]
    [InlineData("vp8", MediaCodec.Vp8, false)]
    [InlineData("VP8", MediaCodec.Vp8, false)]
    [InlineData("vp8.0", MediaCodec.Vp8, false)]
    [InlineData("vp9", MediaCodec.Vp9, false)]
    [InlineData("vp9.0", MediaCodec.Vp9, false)]
    [InlineData("vp09.00.10.08", MediaCodec.Vp9, false)]
    [InlineData("vp09.02.10.10.01.09.16.09.01", MediaCodec.Vp9, false)]
    [InlineData("av01.0.04M.08", MediaCodec.Av1, false)]
    [InlineData("av01.0.04M.10.0.112.09.16.09.0", MediaCodec.Av1, false)]
    [InlineData("av01.2.19H.12", MediaCodec.Av1, false)]
    [InlineData("avc1", MediaCodec.H264, true)]
    [InlineData("avc1.42E01E", MediaCodec.H264, false)]
    [InlineData("avc3.64001f", MediaCodec.H264, false)]
    [InlineData("hvc1.1.6.L93.B0", MediaCodec.Hevc, false)]
    [InlineData("hev1.A1.80.L93.B0", MediaCodec.Hevc, false)]
    [InlineData("hev1.2.4.H120.90", MediaCodec.Hevc, false)]
    [InlineData("mp4a.40", MediaCodec.Aac, true)]
    [InlineData("mp4a.40.2", MediaCodec.Aac, false)]
    [InlineData("mp4a.40.5", MediaCodec.Aac, false)]
    [InlineData("mp4a.40.29", MediaCodec.Aac, false)]
    [InlineData("mp4a.67", MediaCodec.Aac, false)]
    [InlineData("mp4a.40.34", MediaCodec.Mp3, false)]
    [InlineData("mp4a.69", MediaCodec.Mp3, false)]
    [InlineData("mp4a.6B", MediaCodec.Mp3, false)]
    [InlineData("mp4a.6b", MediaCodec.Mp3, false)]
    [InlineData("opus", MediaCodec.Opus, false)]
    [InlineData("Opus", MediaCodec.Opus, false)]
    [InlineData("vorbis", MediaCodec.Vorbis, false)]
    [InlineData("flac", MediaCodec.Flac, false)]
    [InlineData("fLaC", MediaCodec.Flac, false)]
    [InlineData("mp3", MediaCodec.Mp3, false)]
    [InlineData("1", MediaCodec.Pcm, false)]
    [InlineData("3", MediaCodec.Pcm, false)]
    public void Parse_Identifies(string raw, MediaCodec codec, bool ambiguous)
    {
        var parsed = CodecString.Parse(raw);
        Assert.NotNull(parsed);
        Assert.Equal(codec, parsed.Codec);
        Assert.Equal(ambiguous, parsed.IsAmbiguous);
        Assert.Equal(raw, parsed.Raw);
    }

    [Theory]
    [InlineData("")]
    [InlineData("theora")]
    [InlineData("vp8.1")]
    [InlineData("vp09")]
    [InlineData("vp09.04.10.08")]        // profile 4 does not exist
    [InlineData("vp09.00.12.08")]        // level 1.2 does not exist
    [InlineData("vp09.00.10.09")]        // bit depth 9
    [InlineData("vp09.00.10.08.01")]     // optional fields are all-or-nothing
    [InlineData("vp09.0.10.08")]         // two digits required
    [InlineData("av01.3.04M.08")]        // profile 3
    [InlineData("av01.0.04X.08")]        // tier must be M or H
    [InlineData("av01.0.40M.08")]        // level out of range
    [InlineData("av01.0.04M.09")]
    [InlineData("av01.0.04M.10.0.124.09.16.09.0")]   // chroma sample position 4
    [InlineData("av01.0.04M.10.0.112.9.16.09.0")]    // colour fields are two digits
    [InlineData("av01.0.04M.10.2.112.09.16.09.0")]   // monochrome flag is 0 or 1
    [InlineData("avc1.42E01")]
    [InlineData("avc1.42E01G")]
    [InlineData("avc1.42E01E.00")]
    [InlineData("hvc1.1.6")]
    [InlineData("hvc1.1.6.X93")]
    [InlineData("hvc1.1.6.L93.XYZ")]
    [InlineData("mp4a")]
    [InlineData("mp4a.40.3")]
    [InlineData("mp4a.4")]
    [InlineData("mp4a.ZZ")]
    [InlineData("mp4a.40.2.1")]
    [InlineData("opus.1")]
    [InlineData("2")]
    public void Parse_RejectsUnknownOrMalformed(string raw)
    {
        Assert.Null(CodecString.Parse(raw));
    }

    [Theory]
    [InlineData("vp8, vorbis", new[] { "vp8", "vorbis" })]
    [InlineData(" avc1.42E01E ,mp4a.40.2 ", new[] { "avc1.42E01E", "mp4a.40.2" })]
    [InlineData("opus", new[] { "opus" })]
    [InlineData("", new string[0])]
    [InlineData("vp8,", new[] { "vp8", "" })]
    public void SplitList_TrimsEntries(string value, string[] expected)
    {
        Assert.Equal(expected, CodecString.SplitList(value));
    }

    [Fact]
    public void Kinds_AreAssigned()
    {
        Assert.Equal(MediaTrackKind.Video, CodecString.Parse("vp8")!.Kind);
        Assert.Equal(MediaTrackKind.Audio, CodecString.Parse("opus")!.Kind);
    }
}
