using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Types;

/// <summary>Media Capabilities §2.1: what the configuration is going to be used for.</summary>
public enum MediaCapabilitiesUse
{
    /// <summary>Decoding a plain media resource (<c>MediaDecodingType "file"</c>).</summary>
    File,

    /// <summary>Decoding through Media Source Extensions.</summary>
    MediaSource,

    /// <summary>Decoding a WebRTC stream, or encoding one for transmission.</summary>
    WebRtc,

    /// <summary>Encoding for local recording (<c>MediaEncodingType "record"</c>).</summary>
    Record,
}

/// <summary>Media Capabilities §2.2 <c>VideoConfiguration</c>, with the members that change the answer.</summary>
public sealed record MediaCapabilitiesVideo(
    string ContentType,
    uint Width,
    uint Height,
    double Framerate,
    string? ColorGamut = null,
    string? TransferFunction = null,
    string? HdrMetadataType = null);

/// <summary>Media Capabilities §2.3 <c>AudioConfiguration</c>.</summary>
public sealed record MediaCapabilitiesAudio(string ContentType);

/// <summary>
/// Media Capabilities §2.5 <c>MediaCapabilitiesInfo</c>. <c>Supported</c> says the engine
/// can decode (or encode) the configuration at all; <c>Smooth</c> that it expects to keep
/// up with it; <c>PowerEfficient</c> that the work lands on hardware.
/// </summary>
public readonly record struct MediaCapabilitiesInfo(bool Supported, bool Smooth, bool PowerEfficient);

public sealed partial class MediaTypeSupport
{
    // ITU-T H.273 / ISO 23091-2 code points for the colour spaces the ColorGamut and
    // TransferFunction enums name.
    private const int PrimariesBt709 = 1;
    private const int PrimariesBt2020 = 9;
    private const int PrimariesSmpte432 = 12;
    private const int TransferBt709 = 1;
    private const int TransferSrgb = 13;
    private const int TransferPq = 16;
    private const int TransferHlg = 18;

    /// <summary>
    /// Media Capabilities §3.2 <c>decodingInfo()</c>, for a configuration the binding has
    /// already found valid. Nothing here throws: a configuration this engine cannot handle
    /// is an answer, not an error.
    /// </summary>
    public MediaCapabilitiesInfo DecodingInfo(MediaCapabilitiesUse use, MediaCapabilitiesAudio? audio, MediaCapabilitiesVideo? video)
    {
        // Nothing in this engine decodes a WebRTC stream (MediaStream is not implemented),
        // so those configurations are simply not supported.
        if (use == MediaCapabilitiesUse.WebRtc)
            return default;

        bool powerEfficient = true;
        if (audio is { } a && !IsDecodable(a.ContentType, use, ref powerEfficient))
            return default;
        if (video is { } v && (!IsDecodable(v.ContentType, use, ref powerEfficient) || !MatchesRequestedColour(v)))
            return default;

        // Every decoder this engine offers keeps up with the streams it accepts; there is
        // no playback history to say otherwise yet.
        return new MediaCapabilitiesInfo(Supported: true, Smooth: true, PowerEfficient: powerEfficient);
    }

    /// <summary>
    /// Media Capabilities §3.3 <c>encodingInfo()</c>. This engine has no encoders
    /// (ADR-0001 builds libavcodec with decoders only), so nothing is supported; the
    /// binding still validates the configuration, so a malformed one still rejects.
    /// </summary>
    public MediaCapabilitiesInfo EncodingInfo(MediaCapabilitiesUse use, MediaCapabilitiesAudio? audio, MediaCapabilitiesVideo? video)
    {
        _ = use;
        _ = audio;
        _ = video;
        return default;
    }

    /// <summary>
    /// Media Capabilities §3.1 "valid video configuration": the content type must be one
    /// valid media MIME type naming exactly one video codec, and the frame rate must be a
    /// finite number above zero. Width, height and bit rate are required members, which
    /// the binding checks before this is reached.
    /// </summary>
    public static bool IsValidVideoConfiguration(MediaCapabilitiesUse use, MediaCapabilitiesVideo video)
    {
        ArgumentNullException.ThrowIfNull(video);
        if (!double.IsFinite(video.Framerate) || video.Framerate <= 0)
            return false;
        return IsValidContentType(use, video.ContentType, MediaTrackKind.Video);
    }

    /// <summary>Media Capabilities §3.1 "valid audio configuration".</summary>
    public static bool IsValidAudioConfiguration(MediaCapabilitiesUse use, MediaCapabilitiesAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        return IsValidContentType(use, audio.ContentType, MediaTrackKind.Audio);
    }

    /// <summary>
    /// A WebRTC configuration names an RTP payload type, whose one parameter is the SDP
    /// format line rather than a codecs list; anything else - including a container type
    /// such as <c>video/webm</c>, which a page may legitimately ask about and get "no" for
    /// - is read by the file rules.
    /// </summary>
    private static bool IsValidContentType(MediaCapabilitiesUse use, string contentType, MediaTrackKind kind)
    {
        ArgumentNullException.ThrowIfNull(contentType);
        if (use == MediaCapabilitiesUse.WebRtc)
        {
            if (!MimeType.IsValidMimeTypeString(contentType))
                return false;
            var mime = MimeType.Parse(contentType);
            if (mime is null || mime.ParameterCount > 1)
                return false;
            if (mime.Type == (kind == MediaTrackKind.Video ? "video" : "audio") && s_rtpPayloads.Contains(mime.Subtype))
                return true;
        }

        return TryReadSingleCodec(contentType, out var codec) && IsOfKind(contentType, codec, kind);
    }

    /// <summary>
    /// The RTP payload names the WebRTC types carry, from the RTP payload format media
    /// type registry. Nothing here decodes a WebRTC stream, so they only decide whether a
    /// configuration is well formed.
    /// </summary>
    private static readonly HashSet<string> s_rtpPayloads = new(StringComparer.Ordinal)
    {
        // Video.
        "vp8", "vp9", "av1", "av1x", "h264", "h265", "h266", "h263-1998", "h263-2000",
        "rtx", "red", "ulpfec", "flexfec-03",
        // Audio.
        "opus", "multiopus", "pcmu", "pcma", "g722", "g729", "isac", "ilbc", "l16",
        "cn", "telephone-event",
    };

    /// <summary>
    /// A "valid media MIME type" naming exactly one codec: a valid MIME type string with
    /// at most one parameter, which must be <c>codecs</c> holding a single entry; without
    /// one, the container must imply exactly one codec.
    /// </summary>
    private static bool TryReadSingleCodec(string contentType, out CodecString codec)
    {
        codec = null!;
        ArgumentNullException.ThrowIfNull(contentType);
        if (!MimeType.IsValidMimeTypeString(contentType))
            return false;

        var mime = MimeType.Parse(contentType);
        if (mime is null || mime.ParameterCount > 1)
            return false;

        string? codecsParameter = mime.GetParameter("codecs");
        if (mime.ParameterCount == 1 && codecsParameter is null)
            return false;
        if (!MediaContainerType.Known.TryGetValue(mime.Essence, out var container))
            return false;

        if (codecsParameter is null)
        {
            if (container.ImpliedCodec is not { } implied)
                return false;
            codec = new CodecString(mime.Essence, implied, MediaTrackKind.Audio, IsAmbiguous: false);
            return true;
        }

        var entries = CodecString.SplitList(codecsParameter);
        if (entries.Count != 1)
            return false;
        var parsed = CodecString.Parse(entries[0]);
        if (parsed is null || !container.Codecs.Contains(parsed.Codec))
            return false;
        if (container.IsAudioOnly && parsed.Kind != MediaTrackKind.Audio)
            return false;
        codec = parsed;
        return true;
    }

    /// <summary>
    /// Whether the content type describes <paramref name="kind"/>. A type of <c>audio</c>
    /// or <c>video</c> says so itself; <c>application/ogg</c> and its like leave it to the
    /// codec.
    /// </summary>
    private static bool IsOfKind(string contentType, CodecString codec, MediaTrackKind kind)
    {
        string mediaType = MimeType.Parse(contentType)!.Type;
        if (mediaType is "audio" or "video")
            return mediaType == (kind == MediaTrackKind.Video ? "video" : "audio") && codec.Kind == kind;
        return codec.Kind == kind;
    }

    /// <summary>Whether a decoder handles the single codec this content type names.</summary>
    private bool IsDecodable(string contentType, MediaCapabilitiesUse use, ref bool powerEfficient)
    {
        if (!TryReadSingleCodec(contentType, out var codec))
            return false;

        var mime = MimeType.Parse(contentType)!;
        if (!_demuxers.SupportsMimeType(mime.Essence))
            return false;
        if (use == MediaCapabilitiesUse.MediaSource && !IsMediaSourceTypeSupported(contentType, relaxed: true))
            return false;

        var config = new CodecConfig(codec.Kind, codec.Codec, codec.Raw);
        if (_decoders.GetSupport(config) != DecoderSupport.Supported)
            return false;
        if (!_decoders.HasHardwareDecoder(config))
            powerEfficient = false;
        return true;
    }

    /// <summary>
    /// The colour space the codec string describes has to be the one the page asked about;
    /// a VP9 or AV1 string with no colour fields means BT.709, so asking about rec2020 or
    /// PQ on one of those describes something this engine will not be decoding. HDR
    /// metadata is not produced at all, so naming any kind is unsupported.
    /// </summary>
    private static bool MatchesRequestedColour(MediaCapabilitiesVideo video)
    {
        if (video.HdrMetadataType is not null)
            return false;
        if (video.ColorGamut is null && video.TransferFunction is null)
            return true;

        _ = TryReadSingleCodec(video.ContentType, out var codec);

        if (video.ColorGamut is { } gamut)
        {
            int? wanted = gamut switch
            {
                "srgb" => PrimariesBt709,
                "p3" => PrimariesSmpte432,
                "rec2020" => PrimariesBt2020,
                _ => null,
            };
            if (wanted is null || (codec?.ColourPrimaries is { } primaries && primaries != wanted))
                return false;
        }

        if (video.TransferFunction is { } transfer)
        {
            if (codec?.TransferCharacteristics is { } characteristics)
            {
                bool matches = transfer switch
                {
                    "srgb" => characteristics is TransferBt709 or TransferSrgb,
                    "pq" => characteristics == TransferPq,
                    "hlg" => characteristics == TransferHlg,
                    _ => false,
                };
                if (!matches)
                    return false;
            }
            else if (transfer is not ("srgb" or "pq" or "hlg"))
            {
                return false;
            }
        }

        return true;
    }
}
