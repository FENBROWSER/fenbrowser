using FenBrowser.Media.Types;

namespace FenBrowser.Media.Eme;

/// <summary>EME §5 <c>MediaKeysRequirement</c>.</summary>
public enum MediaKeysRequirement
{
    Required,
    Optional,
    NotAllowed,
}

/// <summary>
/// EME §5 <c>MediaKeySystemMediaCapability</c>. The content type is kept exactly as the
/// page wrote it, because <c>getConfiguration()</c> hands it back unchanged.
/// </summary>
public sealed record MediaKeySystemMediaCapability(string ContentType, string Robustness = "", string? EncryptionScheme = null);

/// <summary>EME §5 <c>MediaKeySystemConfiguration</c>, as the page offers it or as we accept it.</summary>
public sealed record MediaKeySystemConfiguration
{
    public string Label { get; init; } = string.Empty;

    public IReadOnlyList<string> InitDataTypes { get; init; } = [];

    public IReadOnlyList<MediaKeySystemMediaCapability> AudioCapabilities { get; init; } = [];

    public IReadOnlyList<MediaKeySystemMediaCapability> VideoCapabilities { get; init; } = [];

    public MediaKeysRequirement DistinctiveIdentifier { get; init; } = MediaKeysRequirement.Optional;

    public MediaKeysRequirement PersistentState { get; init; } = MediaKeysRequirement.Optional;

    /// <summary>Null when the page did not name any, which defaults to <c>["temporary"]</c>.</summary>
    public IReadOnlyList<string>? SessionTypes { get; init; }
}

/// <summary>
/// EME §3.1.1.1 "Get Supported Configuration": which of the configurations a page offers
/// this engine can actually honour, and in what reduced form.
/// </summary>
/// <remarks>
/// The answer is the page's contract with the CDM, so it is assembled rather than
/// approximated: unsupported capabilities are dropped one by one, an emptied list fails
/// the whole configuration, and what comes back is what <c>getConfiguration()</c> shows.
/// Support itself is asked of the demuxer and decoder registries through
/// <see cref="MediaTypeSupport"/>, so EME never claims a codec the engine cannot decode.
/// </remarks>
public sealed class MediaKeySystemSupport
{
    /// <summary>
    /// The encryption schemes this engine decrypts. "cenc" is AES-CTR full-sample; the
    /// "cbcs" family is AES-CBC over a 1:9 pattern.
    /// </summary>
    public static readonly IReadOnlySet<string> EncryptionSchemes =
        new HashSet<string>(StringComparer.Ordinal) { "cenc", "cbcs", "cbcs-1-9" };

    private readonly MediaTypeSupport _types;

    public MediaKeySystemSupport(MediaTypeSupport types)
    {
        ArgumentNullException.ThrowIfNull(types);
        _types = types;
    }

    /// <summary>
    /// True for the one key system this engine implements. The comparison is exact: EME
    /// key system strings are case sensitive and have no prefix or parent matching, so
    /// "org.w3.ClearKey", "org.w3.clearkey." and "org.w3.clearkey.foo" are all different
    /// systems, and none of them is ours.
    /// </summary>
    public static bool IsSupportedKeySystem(string? keySystem) => keySystem == ClearKeyCdm.KeySystem;

    /// <summary>
    /// Runs Get Supported Configuration over each candidate in order and answers the first
    /// one this engine supports, or null when none is supported.
    /// </summary>
    public MediaKeySystemConfiguration? GetSupportedConfiguration(IReadOnlyList<MediaKeySystemConfiguration> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        foreach (var candidate in candidates)
        {
            if (GetSupportedConfiguration(candidate) is { } supported)
                return supported;
        }

        return null;
    }

    /// <summary>EME §3.1.1.1, for one candidate.</summary>
    public MediaKeySystemConfiguration? GetSupportedConfiguration(MediaKeySystemConfiguration candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        // Step 3: an offered initDataTypes list must keep at least one entry.
        IReadOnlyList<string> initDataTypes = [];
        if (candidate.InitDataTypes.Count > 0)
        {
            var supported = new List<string>();
            foreach (string name in candidate.InitDataTypes)
            {
                if (EmeInitData.TryParseInitDataType(name, out _))
                    supported.Add(name);
            }

            if (supported.Count == 0)
                return null;
            initDataTypes = supported;
        }

        // Steps 4-6: Clear Key never uses a distinctive identifier, so a page that
        // requires one is asking for a different key system.
        if (candidate.DistinctiveIdentifier == MediaKeysRequirement.Required)
            return null;

        // Steps 7-8: nothing is kept across a page load, so persistent state cannot be
        // required either, and that rules out persistent-license sessions with it.
        if (candidate.PersistentState == MediaKeysRequirement.Required)
            return null;

        var sessionTypes = candidate.SessionTypes ?? ["temporary"];
        foreach (string sessionType in sessionTypes)
        {
            if (!ClearKeyLicense.TryParseSessionType(sessionType, out var parsed))
                return null;
            if (parsed == MediaKeySessionType.PersistentLicense)
                return null;
        }

        // Step 9: a configuration that names no media at all says nothing we can agree to.
        if (candidate.AudioCapabilities.Count == 0 && candidate.VideoCapabilities.Count == 0)
            return null;

        // Steps 10-11: reduce each capability list, and fail if an offered list empties.
        IReadOnlyList<MediaKeySystemMediaCapability> video = [];
        if (candidate.VideoCapabilities.Count > 0)
        {
            video = GetSupportedCapabilities(candidate.VideoCapabilities, MediaTrackKind.Video);
            if (video.Count == 0)
                return null;
        }

        IReadOnlyList<MediaKeySystemMediaCapability> audio = [];
        if (candidate.AudioCapabilities.Count > 0)
        {
            audio = GetSupportedCapabilities(candidate.AudioCapabilities, MediaTrackKind.Audio);
            if (audio.Count == 0)
                return null;
        }

        return new MediaKeySystemConfiguration
        {
            Label = candidate.Label,
            InitDataTypes = initDataTypes,
            AudioCapabilities = audio,
            VideoCapabilities = video,
            // "optional" is reported as "not-allowed" for what this engine will never use.
            DistinctiveIdentifier = MediaKeysRequirement.NotAllowed,
            PersistentState = MediaKeysRequirement.NotAllowed,
            SessionTypes = sessionTypes,
        };
    }

    /// <summary>
    /// EME §3.1.1.3 "Get Supported Capabilities for Audio/Video Type". An unsupported
    /// capability is skipped, not fatal: a page commonly offers several and expects the
    /// ones we can handle back.
    /// </summary>
    private List<MediaKeySystemMediaCapability> GetSupportedCapabilities(
        IReadOnlyList<MediaKeySystemMediaCapability> capabilities,
        MediaTrackKind kind)
    {
        var supported = new List<MediaKeySystemMediaCapability>();
        foreach (var capability in capabilities)
        {
            // Clear Key offers no robustness levels, so only the empty string fits.
            if (!string.IsNullOrEmpty(capability.Robustness))
                continue;

            if (capability.EncryptionScheme is { } scheme && !EncryptionSchemes.Contains(scheme))
                continue;

            if (IsSupportedContentType(capability.ContentType, kind))
                supported.Add(capability);
        }

        return supported;
    }

    /// <summary>
    /// Whether one capability's content type names media this engine can decrypt and
    /// decode. EME asks for a fully specified type: the container must be one we demux,
    /// every codec must be one we decode outright, the container and the codecs must all
    /// be of <paramref name="kind"/>, and no parameter other than <c>codecs</c> may
    /// appear, because a parameter we do not recognise may change what the bytes are.
    /// </summary>
    public bool IsSupportedContentType(string contentType, MediaTrackKind kind)
    {
        ArgumentNullException.ThrowIfNull(contentType);

        // Parsing strips the surrounding whitespace a page may leave in a content type, so
        // " video/mp4 ;codecs=..." is the same type as "video/mp4;codecs=...".
        var mime = MimeType.Parse(contentType);
        if (mime is null)
            return false;

        if (mime.ParameterCount != 1 || mime.GetParameter("codecs") is not { } codecsParameter)
            return false;

        if (!MediaContainerType.Known.TryGetValue(mime.Essence, out var container))
            return false;

        // An audio capability needs an audio container, and a video capability a video one.
        bool containerIsAudio = mime.Type == "audio";
        if (containerIsAudio != (kind == MediaTrackKind.Audio))
            return false;

        var codecs = CodecString.SplitList(codecsParameter);
        if (codecs.Count == 0)
            return false;

        foreach (string entry in codecs)
        {
            var codec = CodecString.Parse(entry);
            if (codec is null || codec.Kind != kind || !container.Codecs.Contains(codec.Codec))
                return false;
            if (!HasCanonicalCodecName(entry))
                return false;
        }

        // Everything above is about the shape of the type; whether a decoder exists for it
        // is the registries' answer, and "probably" is the only one strong enough here.
        return _types.CanPlayType(contentType) == CanPlayTypeResult.Probably;
    }

    /// <summary>
    /// RFC 6381 codec identifiers are case sensitive, and every one in the registries is
    /// spelled in lower case, so "AVC1.4D401E" names no codec at all. The shared codec
    /// parser is deliberately forgiving about this for <c>canPlayType</c>, where a wrong
    /// answer costs a page nothing; EME is a contract, so it is checked here.
    /// </summary>
    private static bool HasCanonicalCodecName(string codecString)
    {
        int dot = codecString.IndexOf('.', StringComparison.Ordinal);
        var fourcc = dot < 0 ? codecString.AsSpan() : codecString.AsSpan(0, dot);
        foreach (char c in fourcc)
        {
            if (char.IsAsciiLetterUpper(c))
                return false;
        }

        return true;
    }

    public static string ToName(MediaKeysRequirement requirement) => requirement switch
    {
        MediaKeysRequirement.Required => "required",
        MediaKeysRequirement.Optional => "optional",
        MediaKeysRequirement.NotAllowed => "not-allowed",
        _ => throw new ArgumentOutOfRangeException(nameof(requirement)),
    };

    public static bool TryParseRequirement(string? name, out MediaKeysRequirement requirement)
    {
        switch (name)
        {
            case "required":
                requirement = MediaKeysRequirement.Required;
                return true;
            case "optional":
                requirement = MediaKeysRequirement.Optional;
                return true;
            case "not-allowed":
                requirement = MediaKeysRequirement.NotAllowed;
                return true;
            default:
                requirement = default;
                return false;
        }
    }
}
