using FenBrowser.Media.Codecs;
using FenBrowser.Media.Containers.Mp3;
using FenBrowser.Media.Containers.Ogg;
using FenBrowser.Media.Containers.Wav;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media;

/// <summary>
/// The one place the managed container formats and codecs are registered
/// (docs/MEDIA_ENGINE_DESIGN.md §7: "one file per container format, each registered in
/// one place"). The browser, <c>fenplay</c>, the tests and the fuzzers all call this, so
/// they agree on what plays. Native codec adapters register themselves separately.
/// </summary>
public static class MediaFormats
{
    public static void RegisterBuiltIn(DemuxerRegistry demuxers, DecoderRegistry decoders)
    {
        ArgumentNullException.ThrowIfNull(demuxers);
        ArgumentNullException.ThrowIfNull(decoders);

        demuxers.Register(WavDemuxerFactory.Instance);
        demuxers.Register(Mp3DemuxerFactory.Instance);
        demuxers.Register(OggDemuxerFactory.Instance);
        decoders.Register(PcmDecoderFactory.Instance);
    }
}
