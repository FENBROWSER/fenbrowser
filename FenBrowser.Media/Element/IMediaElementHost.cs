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

    /// <summary>
    /// EME §7.1 "Initialization Data Encountered": fires a <c>MediaEncryptedEvent</c> at
    /// the element so the page can ask its key system for a licence.
    /// </summary>
    void FireEncrypted(string initDataType, byte[] initData)
    {
    }

    /// <summary>Fires a simple event at a child node (a failed <c>source</c> element).</summary>
    void FireEventAt(object node, string type);

    /// <summary>Sets the "delaying-the-load-event" flag.</summary>
    void SetDelayingLoadEvent(bool delaying);

    /// <summary>The show-poster flag or the video's natural size changed; repaint or relayout.</summary>
    void InvalidateRendering(bool sizeChanged);

    /// <summary>
    /// HTML §4.8.12.8 "time marches on": the current playback position moved, by normal
    /// playback when <paramref name="monotonic"/>, otherwise by a seek or a new resource.
    /// The host runs the text track cue activation steps.
    /// </summary>
    void PlaybackPositionChanged(bool monotonic)
    {
    }

    /// <summary>
    /// The load algorithm reset the element (§4.8.11.5 step 7): every text track cue's
    /// active flag is cleared and no cue events fire for the abandoned resource.
    /// </summary>
    void TextTracksReset()
    {
    }

    /// <summary>
    /// HTML §4.8.12.11.3 "blocked on pending text tracks": true while a track element's
    /// enabled text track is still loading. The ready state does not advance past
    /// HAVE_CURRENT_DATA meanwhile; the host calls
    /// <see cref="HtmlMediaElementController.PendingTextTracksChanged"/> when this changes.
    /// </summary>
    bool HasPendingTextTracks => false;

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

    /// <summary>
    /// The user agent's background policy (design §5), not a spec concept: false when
    /// nothing is showing this element's pictures - a hidden tab, or a box that is not
    /// rendered. A resource that also has audio stops decoding video and releases the
    /// pictures it holds until it is shown again.
    /// </summary>
    void UpdateVideoVisibility(bool visible)
    {
    }

    /// <summary>
    /// Hands the pipeline the keys the element's <c>MediaKeys</c> holds, or null when it
    /// has none. Decryption happens next to the decoder, so this is all the element does
    /// with them.
    /// </summary>
    void SetMediaKeys(Eme.IMediaKeySource? keys)
    {
    }

    /// <summary>
    /// Audio Output Devices API: send this resource's audio to one named output endpoint,
    /// or to the system default for the empty identifier. A resource with no audio, and one
    /// that cannot choose an endpoint, ignores it.
    /// </summary>
    void SetAudioSink(string deviceId)
    {
    }

    /// <summary>
    /// Where a copy of this resource's audio goes before the element's volume and muting,
    /// or null to stop copying: what a captureStream() audio track carries.
    /// </summary>
    void SetAudioCapture(Audio.IAudioCapture? capture)
    {
    }

    /// <summary>The latest picture for the compositor, or null for a resource without video.</summary>
    Video.VideoPresenter? Presenter => null;

    /// <summary>
    /// True for a media provider object (a MediaSource): MSE §2.4.2 "attaching to a
    /// media element" stops delaying the document's load event as soon as the source is
    /// attached, since script, not a fetch, supplies the data.
    /// </summary>
    bool IsProviderObject => false;

    /// <summary>The counts behind <c>getVideoPlaybackQuality()</c>, or null for a resource without video.</summary>
    VideoPlaybackQuality? GetVideoPlaybackQuality() => null;
}

/// <summary>
/// Media Playback Quality: <c>totalVideoFrames</c> (every picture the decoder produced
/// for the current resource) and <c>droppedVideoFrames</c> (those the compositor never
/// showed because the clock had passed them).
/// </summary>
public readonly record struct VideoPlaybackQuality(long TotalVideoFrames, long DroppedVideoFrames);

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

    /// <summary>
    /// EME §7.1: the container announced initialization data for a protection system.
    /// </summary>
    void EncryptedInitData(string initDataType, byte[] initData)
    {
    }

    /// <summary>
    /// EME §7.2: a packet cannot be decrypted because its key has not arrived. Playback
    /// stalls here until it does.
    /// </summary>
    void WaitingForKey()
    {
    }
}

/// <summary>
/// What became of a <c>setSinkId()</c> call: the page's promise resolves for the first two
/// and rejects with a <c>NotFoundError</c> for the third.
/// </summary>
public enum SinkIdOutcome
{
    /// <summary>The element now plays through the endpoint that was named.</summary>
    Applied,

    /// <summary>It was already playing through that endpoint.</summary>
    Unchanged,

    /// <summary>No output endpoint has that identifier.</summary>
    NotFound,
}
