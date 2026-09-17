namespace FenBrowser.Media.Element;

/// <summary>HTML §4.8.11.4 network states.</summary>
public enum MediaNetworkState : ushort
{
    Empty = 0,
    Idle = 1,
    Loading = 2,
    NoSource = 3,
}

/// <summary>HTML §4.8.11.7 ready states.</summary>
public enum MediaReadyState : ushort
{
    HaveNothing = 0,
    HaveMetadata = 1,
    HaveCurrentData = 2,
    HaveFutureData = 3,
    HaveEnoughData = 4,
}

/// <summary>HTML §4.8.11.1 <c>MediaError</c> codes.</summary>
public enum MediaErrorCode : ushort
{
    Aborted = 1,
    Network = 2,
    Decode = 3,
    SrcNotSupported = 4,
}

/// <summary>A <c>MediaError</c>: a code and a diagnostic message.</summary>
public sealed record MediaElementError(MediaErrorCode Code, string Message);

/// <summary>The DOMException names a play promise can be rejected with.</summary>
public enum PlayRejection
{
    AbortError,
    NotAllowedError,
    NotSupportedError,
}

/// <summary>How a media resource failed, as reported by the pipeline.</summary>
public enum MediaResourceFailure
{
    /// <summary>The container or every codec is unsupported, or the data is not media.</summary>
    Unsupported,

    /// <summary>A fatal network error (DNS, HTTP 4xx/5xx, connection loss).</summary>
    Network,

    /// <summary>The media data is corrupt.</summary>
    Decode,

    /// <summary>The user stopped the fetch.</summary>
    AbortedByUser,
}

/// <summary>What a fetch needs to know about the element and its attributes.</summary>
/// <param name="CrossOrigin">The <c>crossorigin</c> attribute value, or null when absent.</param>
/// <param name="Preload">The <c>preload</c> attribute value, or null when absent.</param>
public sealed record MediaFetchRequest(string Url, bool IsVideo, string? CrossOrigin, string? Preload);

/// <summary>The facts learned once a resource is usable (HTML "media data processing steps").</summary>
/// <param name="Duration">End of the timeline, or <see cref="MediaTime.PositiveInfinity"/> when unbounded.</param>
public sealed record MediaResourceMetadata(
    MediaTime Duration,
    int VideoWidth,
    int VideoHeight,
    IReadOnlyList<MediaTrackInfo> Tracks,
    MediaTime EarliestPossiblePosition = default);
