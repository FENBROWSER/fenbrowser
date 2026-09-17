using FenBrowser.Media.Buffers;
using FenBrowser.Media.Pipeline;
using FenBrowser.Media.Types;

namespace FenBrowser.Media.Tests;

// HTML §4.8.11.3 canPlayType, answered from the registries.
public class CanPlayTypeTests
{
    private sealed class Container(string name, params string[] mimeTypes) : IDemuxerFactory
    {
        public string Name => name;
        public IReadOnlyList<string> MimeTypes => mimeTypes;
        public int Probe(ReadOnlySpan<byte> header) => 0;
        public IDemuxer Create(IByteSource source, MediaPipelineContext context) => throw new NotSupportedException();
    }

    private sealed class Decoder<T>(string name, DecoderSupport support, params MediaCodec[] codecs) : IDecoderFactory<T>
        where T : IDisposable
    {
        public string Name => name;
        public bool IsHardwareAccelerated => false;
        public int Priority => 0;

        public DecoderSupport Supports(CodecConfig config)
        {
            if (!codecs.Contains(config.Codec))
                return DecoderSupport.Unsupported;
            // Pretend 12-bit VP9 cannot be decoded, to prove the codec string reaches the factory.
            if (config.CodecString is { } s && s.StartsWith("vp09.02.10.12", StringComparison.Ordinal))
                return DecoderSupport.Unsupported;
            return support;
        }

        public IMediaDecoder<T> Create(MediaPipelineContext context) => throw new NotSupportedException();
    }

    private static MediaTypeSupport Build(bool withDecoders = true, bool withContainers = true)
    {
        var demuxers = new DemuxerRegistry();
        if (withContainers)
        {
            demuxers.Register(new Container("webm", "video/webm", "audio/webm"));
            demuxers.Register(new Container("mp4", "video/mp4", "audio/mp4"));
            demuxers.Register(new Container("ogg", "audio/ogg", "application/ogg"));
            demuxers.Register(new Container("wav", "audio/wav", "audio/wave"));
            demuxers.Register(new Container("mp3", "audio/mpeg"));
            demuxers.Register(new Container("flac", "audio/flac"));
        }

        var decoders = new DecoderRegistry();
        if (withDecoders)
        {
            decoders.Register(new Decoder<VideoFrame>("video-sw", DecoderSupport.Supported, MediaCodec.Vp8, MediaCodec.Vp9, MediaCodec.Av1));
            decoders.Register(new Decoder<VideoFrame>("h264-os", DecoderSupport.Maybe, MediaCodec.H264));
            decoders.Register(new Decoder<AudioBlock>("audio-sw", DecoderSupport.Supported, MediaCodec.Opus, MediaCodec.Vorbis, MediaCodec.Flac, MediaCodec.Mp3, MediaCodec.Pcm));
        }

        return new MediaTypeSupport(demuxers, decoders);
    }

    [Theory]
    [InlineData("video/webm", "maybe")]                                   // codecs allowed but absent
    [InlineData("video/webm; codecs=\"vp8, vorbis\"", "probably")]
    [InlineData("video/webm; codecs=\"vp09.00.10.08, opus\"", "probably")]
    [InlineData("video/webm; codecs=\"av01.0.04M.08\"", "probably")]
    [InlineData("VIDEO/WEBM; CODECS=VP8", "probably")]
    [InlineData("video/webm; codecs=vp9", "maybe")]                       // legacy spelling has no profile
    [InlineData("video/webm; codecs=\"vp09.02.10.12\"", "")]              // decoder refuses this profile
    [InlineData("video/webm; codecs=\"avc1.42E01E\"", "")]                // WebM cannot carry H.264
    [InlineData("video/webm; codecs=\"vp8, theora\"", "")]                // one unknown codec rules it out
    [InlineData("video/webm; codecs=\"\"", "")]
    [InlineData("video/webm; codecs=\"vp8,\"", "")]
    [InlineData("audio/webm; codecs=opus", "probably")]
    [InlineData("audio/webm; codecs=vp8", "")]                            // video codec in an audio type
    [InlineData("video/mp4; codecs=\"avc1.42E01E\"", "maybe")]            // OS decoder is only "maybe"
    [InlineData("video/mp4; codecs=\"avc1.42E01E, mp4a.40.2\"", "")]      // no AAC decoder registered
    [InlineData("video/mp4; codecs=\"av01.0.04M.08, opus\"", "probably")]
    [InlineData("audio/ogg; codecs=opus", "probably")]
    [InlineData("application/ogg", "maybe")]
    [InlineData("audio/wav; codecs=1", "probably")]
    [InlineData("audio/wave", "maybe")]
    [InlineData("audio/mpeg", "probably")]                                // codec implied by the type
    [InlineData("audio/mpeg; codecs=mp3", "probably")]
    [InlineData("audio/mpeg; codecs=opus", "")]
    [InlineData("audio/flac", "probably")]
    [InlineData("video/ogg", "")]                                         // no demuxer declares video/ogg
    [InlineData("audio/aac", "")]                                         // known type, no demuxer
    [InlineData("video/x-new-fictional-format;codecs=\"kittens,bunnies\"", "")]
    [InlineData("application/octet-stream", "")]
    [InlineData("application/octet-stream; codecs=vp8", "")]
    [InlineData("", "")]
    [InlineData("video", "")]
    [InlineData("video/webm; codecs", "maybe")]                           // parameter without a value is dropped
    public void CanPlayType_Answers(string type, string expected)
    {
        Assert.Equal(expected, MediaTypeSupport.ToDomString(Build().CanPlayType(type)));
    }

    [Theory]
    [InlineData("video/webm")]
    [InlineData("audio/mpeg")]
    [InlineData("video/webm; codecs=vp8")]
    public void NothingRegistered_AnswersEmptyForEverything(string type)
    {
        Assert.Equal(CanPlayTypeResult.No, Build(withDecoders: false, withContainers: false).CanPlayType(type));
    }

    [Fact]
    public void ContainerWithoutDecoders_IsOnlyMaybeWhenCodecsAreAbsent()
    {
        var support = Build(withDecoders: false);
        Assert.Equal(CanPlayTypeResult.Maybe, support.CanPlayType("video/webm"));
        Assert.Equal(CanPlayTypeResult.No, support.CanPlayType("video/webm; codecs=vp8"));
        Assert.Equal(CanPlayTypeResult.No, support.CanPlayType("audio/mpeg"));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("application/octet-stream", false)]
    [InlineData("video/webm", false)]
    [InlineData("video/webm; codecs=vp8", false)]
    [InlineData("application/octet-stream; x=y", true)]
    [InlineData("video/quicktime", true)]
    [InlineData("not a type", true)]
    [InlineData("video/webm; codecs=theora", true)]
    public void KnowsItCannotRender_ForSourceTypes(string type, bool expected)
    {
        Assert.Equal(expected, Build().KnowsItCannotRender(type));
    }

    [Fact]
    public void KillSwitch_TurnsAnswersOff()
    {
        var demuxers = new DemuxerRegistry();
        demuxers.Register(new Container("webm", "video/webm"));
        var decoders = new DecoderRegistry();
        decoders.Register(new Decoder<VideoFrame>("video-sw", DecoderSupport.Supported, MediaCodec.Vp8));
        var support = new MediaTypeSupport(demuxers, decoders);
        Assert.Equal(CanPlayTypeResult.Probably, support.CanPlayType("video/webm; codecs=vp8"));

        decoders.DisableCodec(MediaCodec.Vp8);
        Assert.Equal(CanPlayTypeResult.No, support.CanPlayType("video/webm; codecs=vp8"));
    }

    [Theory]
    [InlineData(CanPlayTypeResult.No, "")]
    [InlineData(CanPlayTypeResult.Maybe, "maybe")]
    [InlineData(CanPlayTypeResult.Probably, "probably")]
    public void DomStrings(CanPlayTypeResult result, string expected)
    {
        Assert.Equal(expected, MediaTypeSupport.ToDomString(result));
    }
}
