using FenBrowser.Media.Codecs.Ffmpeg;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Pipeline;
using FenBrowser.Media.Types;

namespace FenBrowser.Media.Tests;

/// <summary>
/// Media Capabilities (https://w3c.github.io/media-capabilities/) §3.1 configuration
/// validity and §3.2 decodingInfo, checked against the cases in the
/// <c>media-capabilities/decodingInfo.any.js</c> WPT file.
/// </summary>
public class MediaCapabilitiesTests
{
    private static MediaTypeSupport Support()
    {
        var demuxers = new DemuxerRegistry();
        var decoders = new DecoderRegistry();
        MediaFormats.RegisterBuiltIn(demuxers, decoders);
        Assert.True(FfmpegDecoders.TryRegister(decoders, NullMediaLogSink.Instance), "libavcodec must be available on the development machine");
        return new MediaTypeSupport(demuxers, decoders);
    }

    private static MediaCapabilitiesVideo Video(string contentType, double framerate = 24, string? gamut = null, string? transfer = null, string? hdr = null) =>
        new(contentType, 800, 600, framerate, gamut, transfer, hdr);

    [Theory]
    // The minimal configuration the WPT file uses.
    [InlineData("video/webm; codecs=\"vp09.00.10.08\"", 24, true)]
    [InlineData("video/webm; codecs=\"vp8\"", 24, true)]
    // A frame rate must be finite and above zero.
    [InlineData("video/webm; codecs=\"vp09.00.10.08\"", -1, false)]
    [InlineData("video/webm; codecs=\"vp09.00.10.08\"", 0, false)]
    [InlineData("video/webm; codecs=\"vp09.00.10.08\"", double.PositiveInfinity, false)]
    // Content types that are not one valid media MIME type naming one video codec.
    [InlineData("fgeoa", 24, false)]
    [InlineData("video/webm;", 24, false)]
    [InlineData("audio/fgeoa", 24, false)]
    [InlineData("application/ogg; codecs=vorbis", 24, false)]
    [InlineData("video/webm; codecs=\"vp09.00.10.08\"; foo=\"bar\"", 24, false)]
    [InlineData("video/webm; foo=\"bar\"", 24, false)]
    [InlineData("video/webm", 24, false)]
    [InlineData("video/webm; codecs=\"vp09.00.10.08, vp8\"", 24, false)]
    [InlineData("video/webm; codecs=\"vp09.00.10.08, opus\"", 24, false)]
    public void VideoConfigurationValidity(string contentType, double framerate, bool valid) =>
        Assert.Equal(valid, MediaTypeSupport.IsValidVideoConfiguration(MediaCapabilitiesUse.File, Video(contentType, framerate)));

    [Theory]
    [InlineData("audio/webm; codecs=\"opus\"", true)]
    [InlineData("audio/mpeg", true)]
    [InlineData("fgeoa", false)]
    [InlineData("audio/mpeg;", false)]
    [InlineData("video/fgeoa", false)]
    [InlineData("application/ogg; codecs=theora", false)]
    [InlineData("audio/webm; codecs=\"opus\"; foo=\"bar\"", false)]
    [InlineData("audio/webm; foo=\"bar\"", false)]
    [InlineData("audio/webm", false)]
    [InlineData("audio/webm; codecs=\"vorbis, opus\"", false)]
    [InlineData("audio/webm; codecs=\"vp09.00.10.08, opus\"", false)]
    public void AudioConfigurationValidity(string contentType, bool valid) =>
        Assert.Equal(valid, MediaTypeSupport.IsValidAudioConfiguration(MediaCapabilitiesUse.File, new MediaCapabilitiesAudio(contentType)));

    [Fact]
    public void TheMinimalConfigurationIsDecodable()
    {
        var info = Support().DecodingInfo(
            MediaCapabilitiesUse.File,
            new MediaCapabilitiesAudio("audio/webm; codecs=\"opus\""),
            Video("video/webm; codecs=\"vp09.00.10.08\""));
        Assert.True(info.Supported);
        Assert.True(info.Smooth);
    }

    /// <summary>
    /// A VP9 codec string without colour fields means BT.709, so asking about rec2020 and
    /// PQ describes something else ("decodingInfo with mismatched codec color space is
    /// unsupported").
    /// </summary>
    [Fact]
    public void AMismatchedColourSpaceIsNotSupported()
    {
        var support = Support();
        Assert.False(support.DecodingInfo(MediaCapabilitiesUse.File, null,
            Video("video/webm; codecs=\"vp09.00.10.08\"", gamut: "rec2020", transfer: "pq")).Supported);
        Assert.True(support.DecodingInfo(MediaCapabilitiesUse.File, null,
            Video("video/webm; codecs=\"vp09.00.10.08.00.09.16.09.00\"", gamut: "rec2020", transfer: "pq")).Supported);
        Assert.True(support.DecodingInfo(MediaCapabilitiesUse.File, null,
            Video("video/webm; codecs=\"vp09.00.10.08\"", gamut: "srgb", transfer: "srgb")).Supported);
    }

    /// <summary>No HDR metadata is produced, so naming any kind is unsupported.</summary>
    [Fact]
    public void HdrMetadataIsNotSupported() =>
        Assert.False(Support().DecodingInfo(MediaCapabilitiesUse.File, null,
            Video("video/webm; codecs=\"vp09.00.10.08.00.09.16.09.00\"", gamut: "rec2020", transfer: "pq", hdr: "smpteSt2086")).Supported);

    [Fact]
    public void MediaSourceUsesTheMseTypeCheck()
    {
        var support = Support();
        Assert.True(support.DecodingInfo(MediaCapabilitiesUse.MediaSource, null, Video("video/webm; codecs=\"vp09.00.10.08\"")).Supported);
        // Ogg is not one of the MSE byte stream formats.
        Assert.True(support.DecodingInfo(MediaCapabilitiesUse.File, new MediaCapabilitiesAudio("audio/ogg; codecs=\"opus\""), null).Supported);
        Assert.False(support.DecodingInfo(MediaCapabilitiesUse.MediaSource, new MediaCapabilitiesAudio("audio/ogg; codecs=\"opus\""), null).Supported);
    }

    [Fact]
    public void WebRtcAndEncodingAreNotSupported()
    {
        var support = Support();
        var video = Video("video/webm; codecs=\"vp09.00.10.08\"");
        Assert.False(support.DecodingInfo(MediaCapabilitiesUse.WebRtc, null, video).Supported);
        Assert.False(support.EncodingInfo(MediaCapabilitiesUse.Record, null, video).Supported);
        Assert.False(support.EncodingInfo(MediaCapabilitiesUse.WebRtc, null, video).Supported);
    }

    /// <summary>H.264 decodes on the OS decoder, which runs on video hardware here.</summary>
    [Fact]
    public void PowerEfficientFollowsTheHardwareDecoders()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var demuxers = new DemuxerRegistry();
        var decoders = new DecoderRegistry();
        MediaFormats.RegisterBuiltIn(demuxers, decoders);
        _ = FfmpegDecoders.TryRegister(decoders, NullMediaLogSink.Instance);
        _ = FenBrowser.Media.Codecs.MediaFoundation.MediaFoundationDecoders.TryRegister(decoders, NullMediaLogSink.Instance);
        var support = new MediaTypeSupport(demuxers, decoders);

        var pcm = support.DecodingInfo(MediaCapabilitiesUse.File, new MediaCapabilitiesAudio("audio/wav; codecs=\"1\""), null);
        Assert.True(pcm.Supported);
        Assert.False(pcm.PowerEfficient);
    }
}
