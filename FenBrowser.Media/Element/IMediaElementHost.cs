namespace FenBrowser.Media.Element;

/// <summary>
/// Everything <see cref="HtmlMediaElementController"/> needs from the DOM and the event loop.
/// </summary>
/// <remarks>
/// All members are called on the element's event loop thread. Child nodes are opaque
/// tokens: the controller only compares them by reference and hands them back.
/// </remarks>
public interface IMediaElementHost
{
    /// <summary>True for <c>video</c>, false for <c>audio</c>.</summary>
    bool IsVideo { get; }

    /// <summary>The <c>src</c> content attribute, or null when absent.</summary>
    string? SrcAttribute { get; }

    bool HasAutoplayAttribute { get; }

    bool HasLoopAttribute { get; }

    /// <summary>The <c>muted</c> content attribute (the default muted state).</summary>
    bool HasMutedAttribute { get; }

    string? CrossOriginAttribute { get; }

    string? PreloadAttribute { get; }

    object? FirstChild { get; }

    object? NextSibling(object child);

    bool IsSourceElement(object node);

    /// <summary>A content attribute of a <c>source</c> child, or null when absent.</summary>
    string? GetSourceAttribute(object source, string name);

    /// <summary>
    /// Encoding-parses <paramref name="value"/> against the node document and returns the
    /// serialized URL, or null on failure.
    /// </summary>
    string? ResolveUrl(string value);

    /// <summary>Whether a <c>source</c> element's <c>media</c> query matches the environment.</summary>
    bool MediaQueryMatches(string query);

    /// <summary>HTML "allowed to play": the user agent's autoplay policy for this context.</summary>
    bool IsAllowedToPlay { get; }

    /// <summary>
    /// The document-level autoplay conditions: no "sandboxed automatic features" flag, and the
    /// "autoplay" permissions-policy feature is allowed.
    /// </summary>
    bool DocumentAllowsAutoplay { get; }

    /// <summary>Queues a task on the media element event task source.</summary>
    void QueueTask(Action task);

    /// <summary>Runs <paramref name="continuation"/> at the next stable state.</summary>
    void AwaitStableState(Action continuation);

    /// <summary>Fires a simple event at the media element.</summary>
    void FireEvent(string type);

    /// <summary>Fires a simple event at a child node (a failed <c>source</c> element).</summary>
    void FireEventAt(object node, string type);

    /// <summary>Sets the "delaying-the-load-event" flag.</summary>
    void SetDelayingLoadEvent(bool delaying);

    /// <summary>The show-poster flag or the video's natural size changed; repaint or relayout.</summary>
    void InvalidateRendering(bool sizeChanged);

    object CreatePromise();

    void ResolvePromise(object promise);

    void RejectPromise(object promise, PlayRejection reason);

    /// <summary>
    /// Starts fetching and demuxing a resource. Returns null when the pipeline cannot even
    /// try (then the controller treats the resource as unsupported). The returned resource
    /// reports back through <paramref name="client"/>, on this thread.
    /// </summary>
    IMediaResource? StartResource(MediaFetchRequest request, IMediaResourceClient client);
}

/// <summary>The pipeline side of one media resource, driven by the element.</summary>
public interface IMediaResource : IDisposable
{
    /// <summary>Whether the element is potentially playing, at what rate and effective volume.</summary>
    void UpdatePlayback(bool potentiallyPlaying, double playbackRate, bool preservesPitch, double effectiveVolume);

    /// <summary>
    /// Starts a seek; the resource answers with <see cref="IMediaResourceClient.SeekCompleted"/>.
    /// A newer call supersedes an older one, and only the latest seek may be reported complete.
    /// </summary>
    void Seek(MediaTime target, bool approximateForSpeed);

    /// <summary>The resource may start loading beyond metadata (preload=none/metadata and play()).</summary>
    void RequestFullLoad();
}

/// <summary>
/// How a resource reports progress. Every call must arrive on the element's thread; calls
/// from a resource the element has abandoned (after <c>load()</c>) are ignored.
/// </summary>
public interface IMediaResourceClient
{
    void Failed(MediaResourceFailure failure, string message);

    void MetadataAvailable(MediaResourceMetadata metadata);

    void ReadyStateChanged(MediaReadyState state);

    void DurationChanged(MediaTime duration);

    void VideoSizeChanged(int width, int height);

    /// <summary>The current playback position moved: by normal playback when <paramref name="monotonic"/>.</summary>
    void PositionChanged(MediaTime position, bool monotonic);

    void ReachedEnd();

    void SeekCompleted(MediaTime position);

    void BufferedChanged(MediaTimeRanges buffered);

    /// <summary>Where seeking can land. Seeks outside these ranges snap to the nearest point; with none, seeks do nothing.</summary>
    void SeekableChanged(MediaTimeRanges seekable);

    /// <summary>Data arrived (throttled by the resource to about every 350 ms).</summary>
    void Progress();

    void Suspended();

    void Resumed();

    void Stalled();

    /// <summary>The entire resource is fetched and kept available.</summary>
    void FetchedEntirely();
}
