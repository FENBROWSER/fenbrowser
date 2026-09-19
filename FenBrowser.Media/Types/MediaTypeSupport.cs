using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Types;

/// <summary>What <c>canPlayType</c> answers.</summary>
public enum CanPlayTypeResult
{
    /// <summary>The empty string: a type the user agent knows it cannot render.</summary>
    No,
    Maybe,
    Probably,
}

/// <summary>
/// Which codecs each container can carry. A container is only offered when a
/// registered demuxer declares its MIME type; this table only rules codecs out.
/// </summary>
public sealed record MediaContainerType(
    string Essence,
    bool IsAudioOnly,
    IReadOnlySet<MediaCodec> Codecs,
    MediaCodec? ImpliedCodec = null)
{
    private static readonly HashSet<MediaCodec> s_webm = [MediaCodec.Vp8, MediaCodec.Vp9, MediaCodec.Av1, MediaCodec.Opus, MediaCodec.Vorbis];
    private static readonly HashSet<MediaCodec> s_mp4 = [MediaCodec.H264, MediaCodec.Hevc, MediaCodec.Av1, MediaCodec.Vp9, MediaCodec.Aac, MediaCodec.Mp3, MediaCodec.Opus, MediaCodec.Flac];
    private static readonly HashSet<MediaCodec> s_ogg = [MediaCodec.Opus, MediaCodec.Vorbis, MediaCodec.Flac];

    /// <summary>The containers FenBrowser knows about, by MIME essence.</summary>
    public static readonly IReadOnlyDictionary<string, MediaContainerType> Known = new Dictionary<string, MediaContainerType>(StringComparer.Ordinal)
    {
        ["video/webm"] = new("video/webm", false, s_webm),
        ["audio/webm"] = new("audio/webm", true, s_webm),
        ["video/mp4"] = new("video/mp4", false, s_mp4),
        ["audio/mp4"] = new("audio/mp4", true, s_mp4),
        ["audio/x-m4a"] = new("audio/x-m4a", true, s_mp4),
        ["video/ogg"] = new("video/ogg", false, s_ogg),
        ["audio/ogg"] = new("audio/ogg", true, s_ogg),
        ["application/ogg"] = new("application/ogg", false, s_ogg),
        ["audio/wav"] = new("audio/wav", true, new HashSet<MediaCodec> { MediaCodec.Pcm }),
        ["audio/wave"] = new("audio/wave", true, new HashSet<MediaCodec> { MediaCodec.Pcm }),
        ["audio/x-wav"] = new("audio/x-wav", true, new HashSet<MediaCodec> { MediaCodec.Pcm }),
        ["audio/mpeg"] = new("audio/mpeg", true, new HashSet<MediaCodec> { MediaCodec.Mp3 }, MediaCodec.Mp3),
        ["audio/mp3"] = new("audio/mp3", true, new HashSet<MediaCodec> { MediaCodec.Mp3 }, MediaCodec.Mp3),
        ["audio/flac"] = new("audio/flac", true, new HashSet<MediaCodec> { MediaCodec.Flac }, MediaCodec.Flac),
        ["audio/x-flac"] = new("audio/x-flac", true, new HashSet<MediaCodec> { MediaCodec.Flac }, MediaCodec.Flac),
        ["audio/aac"] = new("audio/aac", true, new HashSet<MediaCodec> { MediaCodec.Aac }, MediaCodec.Aac),
    };
}

/// <summary>
/// HTML §4.8.11.3 "MIME types": decides <c>canPlayType</c> and whether a
/// <c>source</c> element's <c>type</c> is "a type that the user agent knows it cannot render".
/// </summary>
/// <remarks>
/// Answers come only from the registries, so they are truthful: a container needs a
/// registered demuxer and every listed codec needs a usable decoder. "probably" is only
/// given when the codecs are fully specified, or implied by the container (HTML says a
/// type that allows a <c>codecs</c> parameter should not be "probably" without one).
/// </remarks>
public sealed class MediaTypeSupport
{
    private readonly DemuxerRegistry _demuxers;
    private readonly DecoderRegistry _decoders;

    public MediaTypeSupport(DemuxerRegistry demuxers, DecoderRegistry decoders)
    {
        ArgumentNullException.ThrowIfNull(demuxers);
        ArgumentNullException.ThrowIfNull(decoders);
        _demuxers = demuxers;
        _decoders = decoders;
    }

    /// <summary>The <c>canPlayType(type)</c> answer.</summary>
    public CanPlayTypeResult CanPlayType(string type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var mime = MimeType.Parse(type);
        if (mime is null)
            return CanPlayTypeResult.No;

        // "application/octet-stream" with no parameters is always the empty string.
        if (mime.Essence == "application/octet-stream" && mime.ParameterCount == 0)
            return CanPlayTypeResult.No;

        if (!MediaContainerType.Known.TryGetValue(mime.Essence, out var container) || !_demuxers.SupportsMimeType(mime.Essence))
            return CanPlayTypeResult.No;

        string? codecsParameter = mime.GetParameter("codecs");
        if (codecsParameter is null)
        {
            if (container.ImpliedCodec is { } implied)
                return FromSupport(_decoders.GetSupport(ConfigFor(implied, MediaTrackKind.Audio, null)), ambiguous: false);
            return CanPlayTypeResult.Maybe;
        }

        var entries = CodecString.SplitList(codecsParameter);
        if (entries.Count == 0)
            return CanPlayTypeResult.No;

        var result = CanPlayTypeResult.Probably;
        foreach (string entry in entries)
        {
            var codec = CodecString.Parse(entry);
            if (codec is null || !container.Codecs.Contains(codec.Codec))
                return CanPlayTypeResult.No;
            if (container.IsAudioOnly && codec.Kind != MediaTrackKind.Audio)
                return CanPlayTypeResult.No;

            var answer = FromSupport(_decoders.GetSupport(ConfigFor(codec.Codec, codec.Kind, codec.Raw)), codec.IsAmbiguous);
            if (answer == CanPlayTypeResult.No)
                return CanPlayTypeResult.No;
            if (answer < result)
                result = answer;
        }

        return result;
    }

    /// <summary>
    /// <c>MediaSource.isTypeSupported(type)</c> (MSE §2.2): the byte stream formats this
    /// engine implements are ISO BMFF and WebM, a codecs parameter is required, and every
    /// codec named must be one the container carries and a decoder handles outright.
    /// </summary>
    public bool IsMediaSourceTypeSupported(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var mime = MimeType.Parse(type);
        if (mime is null)
            return false;
        if (mime.Essence is not ("audio/webm" or "video/webm" or "audio/mp4" or "video/mp4"))
            return false;
        string? codecsParameter = mime.GetParameter("codecs");
        if (codecsParameter is null || CodecString.SplitList(codecsParameter).Count == 0)
            return false;
        return CanPlayType(type) == CanPlayTypeResult.Probably;
    }

    /// <summary>
    /// True when a <c>source</c> element's <c>type</c> attribute rules the candidate out
    /// (resource selection, "process candidate"). An empty attribute says nothing about
    /// the resource and, as in Chromium and Gecko, does not rule it out; neither does a
    /// bare <c>application/octet-stream</c>.
    /// </summary>
    public bool KnowsItCannotRender(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (MimeType.TrimHttpWhitespace(type).Length == 0)
            return false;
        if (MimeType.Parse(type) is { Essence: "application/octet-stream", ParameterCount: 0 })
            return false;
        return CanPlayType(type) == CanPlayTypeResult.No;
    }

    /// <summary>The DOM string for a result.</summary>
    public static string ToDomString(CanPlayTypeResult result) => result switch
    {
        CanPlayTypeResult.Probably => "probably",
        CanPlayTypeResult.Maybe => "maybe",
        _ => "",
    };

    private static CodecConfig ConfigFor(MediaCodec codec, MediaTrackKind kind, string? codecString) =>
        new(kind, codec, codecString);

    private static CanPlayTypeResult FromSupport(DecoderSupport support, bool ambiguous) => support switch
    {
        DecoderSupport.Supported when !ambiguous => CanPlayTypeResult.Probably,
        DecoderSupport.Supported or DecoderSupport.Maybe => CanPlayTypeResult.Maybe,
        _ => CanPlayTypeResult.No,
    };
}
