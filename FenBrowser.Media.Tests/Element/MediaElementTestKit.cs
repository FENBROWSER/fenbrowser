using FenBrowser.Media.Buffers;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Element;
using FenBrowser.Media.Pipeline;
using FenBrowser.Media.Types;

namespace FenBrowser.Media.Tests.Element;

internal sealed class ManualTime : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public void Advance(TimeSpan by) => _ticks += by.Ticks;
}

internal static class MediaElementTestKit
{
    private sealed class WebM : IDemuxerFactory
    {
        public string Name => "webm";
        public IReadOnlyList<string> MimeTypes => ["video/webm", "audio/webm"];
        public int Probe(ReadOnlySpan<byte> header) => 0;
        public IDemuxer Create(IByteSource source, MediaPipelineContext context) => throw new NotSupportedException();
    }

    private sealed class Vp8 : IDecoderFactory<VideoFrame>
    {
        public string Name => "vp8";
        public bool IsHardwareAccelerated => false;
        public int Priority => 0;
        public DecoderSupport Supports(CodecConfig config) => config.Codec == MediaCodec.Vp8 ? DecoderSupport.Supported : DecoderSupport.Unsupported;
        public IMediaDecoder<VideoFrame> Create(MediaPipelineContext context) => throw new NotSupportedException();
    }

    public static MediaTypeSupport TypeSupport()
    {
        var demuxers = new DemuxerRegistry();
        demuxers.Register(new WebM());
        var decoders = new DecoderRegistry();
        decoders.Register(new Vp8());
        return new MediaTypeSupport(demuxers, decoders);
    }

    public static (FakeMediaElementHost Host, HtmlMediaElementController Element, RecordingMediaLogSink Log, ManualTime Time) Create(
        Action<FakeMediaElementHost>? configure = null)
    {
        var host = new FakeMediaElementHost();
        configure?.Invoke(host);
        var log = new RecordingMediaLogSink();
        var time = new ManualTime();
        var element = new HtmlMediaElementController(host, TypeSupport(), log, time);
        return (host, element, log, time);
    }

    /// <summary>An element with src set, metadata loaded and the given ready state reached.</summary>
    public static (FakeMediaElementHost Host, HtmlMediaElementController Element, ManualTime Time) Loaded(
        MediaReadyState state,
        Action<FakeMediaElementHost>? configure = null,
        double duration = 10)
    {
        var (host, element, _, time) = Create(h =>
        {
            h.SrcAttribute = "clip.webm";
            configure?.Invoke(h);
        });
        element.OnSrcAttributeSet();
        host.Run();
        host.Resource!.LoadMetadata(duration);
        if (state > MediaReadyState.HaveMetadata)
            host.Resource.Client.ReadyStateChanged(state);
        host.Run();
        host.TakeLog();
        return (host, element, time);
    }

    public static FakePromise AsPromise(object promise) => (FakePromise)promise;
}
