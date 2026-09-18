using System;
using FenBrowser.Media;
using FenBrowser.Media.Audio;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Pipeline;
using FenBrowser.Media.Types;

namespace FenBrowser.FenEngine.Media
{
    /// <summary>
    /// The process-wide media engine objects the browser hands to every media element: the
    /// format and decoder registries, the <c>canPlayType</c> answerer built on them, the
    /// engine-log sink and the autoplay policy.
    /// </summary>
    /// <remarks>
    /// Formats and decoders are registered here once, at first use, so <c>canPlayType</c>, the
    /// element and (later) the pipeline all agree on what this build supports. Until the
    /// demuxers and decoders land (design §8, M2 onward) the registries are empty, and every
    /// element truthfully reports nothing playable.
    /// </remarks>
    public static class MediaEngineServices
    {
        private static readonly Lazy<Registries> s_registries = new(CreateRegistries);

        public static DemuxerRegistry Demuxers => s_registries.Value.Demuxers;

        public static DecoderRegistry Decoders => s_registries.Value.Decoders;

        public static MediaTypeSupport TypeSupport => s_registries.Value.TypeSupport;

        public static IMediaLogSink Log => EngineLogMediaSink.Instance;

        public static MediaAutoplayPolicy Autoplay => MediaAutoplayPolicy.Default;

        /// <summary>
        /// The audio device backend players open their streams on: WASAPI on Windows, the
        /// null output elsewhere or when no device can be opened (ADR-0003); tests swap it.
        /// </summary>
        public static IAudioOutputFactory AudioOutputs { get; set; } = FenBrowser.Media.Audio.Windows.PlatformAudioOutputFactory.Instance;

        /// <summary>
        /// Where players demux and decode: null means in this process; the renderer child
        /// installs the media-process transport here (design §2.2, ADR-0004).
        /// </summary>
        public static IMediaDecodeSourceFactory DecodeSources { get; set; }

        /// <summary>Everything a <see cref="MediaPlayer"/> needs, built from the registries above.</summary>
        public static MediaPlayerServices PlayerServices =>
            new(Demuxers, Decoders, AudioOutputs, TimeProvider.System) { DecodeSources = DecodeSources };

        private static Registries CreateRegistries()
        {
            var demuxers = new DemuxerRegistry();
            var decoders = new DecoderRegistry();
            MediaFormats.RegisterBuiltIn(demuxers, decoders);
            // libavcodec (ADR-0001) registers what it can; without it the managed PCM path remains.
            FenBrowser.Media.Codecs.Ffmpeg.FfmpegDecoders.TryRegister(decoders, Log);
            ApplyCodecKillSwitch(decoders);
            return new Registries(demuxers, decoders, new MediaTypeSupport(demuxers, decoders));
        }

        /// <summary><c>FEN_MEDIA_DISABLE_CODECS=vp9,opus</c> turns codecs off for the process (design §4).</summary>
        private static void ApplyCodecKillSwitch(DecoderRegistry decoders)
        {
            var raw = Environment.GetEnvironmentVariable("FEN_MEDIA_DISABLE_CODECS");
            if (string.IsNullOrWhiteSpace(raw))
                return;

            foreach (var name in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Enum.TryParse<FenBrowser.Media.MediaCodec>(name, ignoreCase: true, out var codec) &&
                    codec != FenBrowser.Media.MediaCodec.Unknown)
                {
                    decoders.DisableCodec(codec);
                }
            }
        }

        private sealed record Registries(DemuxerRegistry Demuxers, DecoderRegistry Decoders, MediaTypeSupport TypeSupport);
    }
}
