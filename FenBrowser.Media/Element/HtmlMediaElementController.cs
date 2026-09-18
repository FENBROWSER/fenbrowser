using System.Globalization;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Types;

namespace FenBrowser.Media.Element;

/// <summary>
/// The state machine behind one <c>audio</c> or <c>video</c> element: WHATWG HTML §4.8.11.
/// </summary>
/// <remarks>
/// <para>
/// The controller implements the spec algorithms and owns every piece of element state
/// (network and ready states, paused, seeking, positions, pending play promises, volume).
/// It never touches the DOM, the event loop or a codec directly: the DOM and event loop
/// come through <see cref="IMediaElementHost"/>, and the media pipeline through
/// <see cref="IMediaResource"/>. That keeps the algorithms testable without a browser.
/// </para>
/// <para>
/// It is single-threaded: every public member and every <see cref="IMediaResourceClient"/>
/// call must happen on the element's event loop thread.
/// </para>
/// <para>
/// Not yet covered, by design of the phase plan: <c>srcObject</c> (MSE, M6), text tracks
/// (M5), lazy loading, and the track lists (the tracks are recorded but not exposed).
/// </para>
/// </remarks>
public sealed class HtmlMediaElementController
{
    // Chromium's supported range; outside it, setting playbackRate throws NotSupportedError.
    public const double MinimumPlaybackRate = 0.0625;
    public const double MaximumPlaybackRate = 16.0;

    private static readonly TimeSpan s_timeUpdateInterval = TimeSpan.FromMilliseconds(250);

    private readonly IMediaElementHost _host;
    private readonly MediaTypeSupport _typeSupport;
    private readonly IMediaLogSink _log;
    private readonly TimeProvider _time;
    private readonly List<ElementTask> _queuedTasks = [];
    private List<object> _pendingPlayPromises = [];

    // Resource selection state.
    private int _selectionGeneration;
    private SelectionMode _mode;
    private object? _candidate;
    private object? _pointerBefore;
    private bool _waitingForSource;
    private IMediaResource? _resource;
    private IReadOnlyList<MediaTrackInfo> _tracks = [];

    // Playback state.
    private MediaTime _currentPosition;
    private MediaTime _officialPosition;
    // Kept as the double script set, so currentTime reads it back exactly (Number.MAX_VALUE
    // stays Number.MAX_VALUE) until media data arrives and it becomes a seek target.
    private double _defaultPlaybackStartPosition;
    private MediaTime _earliestPossiblePosition;
    private MediaTime? _duration;
    private bool _loadedDataFiredSinceLoad;
    private bool _canAutoplay = true;
    private bool? _mutedState;
    private double _volume = 1.0;
    private double _playbackRate = 1.0;
    private double _defaultPlaybackRate = 1.0;
    private bool _preservesPitch = true;
    private long _lastTimeUpdateTimestamp = long.MinValue;
    private MediaTime? _playedStart;
    private MediaTimeRanges _played = MediaTimeRanges.Empty;
    private bool _pendingSeek;
    private bool _seekableReported;

    public HtmlMediaElementController(
        IMediaElementHost host,
        MediaTypeSupport typeSupport,
        IMediaLogSink? log = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(typeSupport);
        _host = host;
        _typeSupport = typeSupport;
        _log = log ?? NullMediaLogSink.Instance;
        _time = timeProvider ?? TimeProvider.System;
        Player = PlayerId.Next();
        _log.Emit(Player, MediaEventKind.PlayerCreated, MediaLogLevel.Debug,
            host.IsVideo ? "video element created" : "audio element created");
    }

    private enum SelectionMode
    {
        None,
        Attribute,
        Children,
    }

    public PlayerId Player { get; }

    // ---- IDL attributes (HTML §4.8.11) -------------------------------------------------

    public MediaElementError? Error { get; private set; }

    public string CurrentSrc { get; private set; } = string.Empty;

    public MediaNetworkState NetworkState { get; private set; } = MediaNetworkState.Empty;

    public MediaReadyState ReadyState { get; private set; } = MediaReadyState.HaveNothing;

    public bool Seeking { get; private set; }

    public bool Paused { get; private set; } = true;

    /// <summary>The show poster flag (§4.8.11.6).</summary>
    public bool ShowPoster { get; private set; } = true;

    public int VideoWidth { get; private set; }

    public int VideoHeight { get; private set; }

    public MediaTimeRanges Buffered { get; private set; } = MediaTimeRanges.Empty;

    public MediaTimeRanges Seekable { get; private set; } = MediaTimeRanges.Empty;

    public bool IsCurrentlyStalled { get; private set; }

    public bool DelayingLoadEvent { get; private set; }

    /// <summary>The tracks of the current resource, recorded for the track lists (M5).</summary>
    public IReadOnlyList<MediaTrackInfo> Tracks => _tracks;

    /// <summary>
    /// <c>currentTime</c> getter: the default playback start position unless it is zero,
    /// otherwise the official playback position.
    /// </summary>
    public double CurrentTime =>
        _defaultPlaybackStartPosition != 0.0
            ? _defaultPlaybackStartPosition
            : _officialPosition.TotalSeconds;

    /// <summary><c>duration</c>: NaN without media data, Infinity when unbounded.</summary>
    public double Duration => _duration?.TotalSeconds ?? double.NaN;

    /// <summary><c>ended</c>: playback has ended and the direction is forwards.</summary>
    public bool Ended => HasEndedPlayback;

    public double DefaultPlaybackRate
    {
        get => _defaultPlaybackRate;
        set
        {
            if (value.Equals(_defaultPlaybackRate))
                return;
            _defaultPlaybackRate = value;
            QueueEvent("ratechange");
        }
    }

    public double PlaybackRate => _playbackRate;

    public bool PreservesPitch
    {
        get => _preservesPitch;
        set
        {
            _preservesPitch = value;
            PushPlaybackState();
        }
    }

    public double Volume => _volume;

    /// <summary><c>muted</c> getter: the muted state, or the <c>muted</c> attribute while the state is "default".</summary>
    public bool Muted => _mutedState ?? _host.HasMutedAttribute;

    /// <summary><c>played</c>: the ranges reached through normal playback.</summary>
    public MediaTimeRanges Played =>
        _playedStart is { } start && _currentPosition > start
            ? _played.Union(MediaTimeRanges.Single(start, _currentPosition))
            : _played;

    /// <summary>The effective media volume sent to the audio output.</summary>
    public double EffectiveVolume => Muted ? 0.0 : _volume;

    /// <summary>§4.8.11.8 "potentially playing".</summary>
    public bool IsPotentiallyPlaying => !Paused && !HasEndedPlayback && !IsBlocked;

    private bool IsBlocked => ReadyState <= MediaReadyState.HaveCurrentData;

    // Negative rates are rejected by the setter, so the direction is always forwards.
    private bool HasEndedPlayback => IsAtTheEnd && !_host.HasLoopAttribute;

    /// <summary>The current playback position is the end of a media resource with a known duration.</summary>
    private bool IsAtTheEnd =>
        ReadyState >= MediaReadyState.HaveMetadata
        && _duration is { } duration
        && !duration.IsInfinite
        && _currentPosition >= duration;

    private bool IsEligibleForAutoplay =>
        _canAutoplay && Paused && _host.HasAutoplayAttribute && _host.DocumentAllowsAutoplay;

    // ---- Attribute and tree notifications ------------------------------------------------

    /// <summary>§4.8.11.2: setting or changing <c>src</c> runs the media element load algorithm.</summary>
    public void OnSrcAttributeSet() => RunLoadAlgorithm();

    /// <summary>
    /// A child was inserted. Wakes a waiting resource selection, and (§4.8.11.2) starts
    /// selection when a <c>source</c> is added to an idle element with no <c>src</c>.
    /// </summary>
    public void OnChildInserted(object node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (_waitingForSource && _mode == SelectionMode.Children && NodeAfterPointer() is not null)
        {
            _waitingForSource = false;
            int generation = _selectionGeneration;
            _host.AwaitStableState(() =>
            {
                if (generation != _selectionGeneration)
                    return;
                // ⌛ Delay the load event again, go back to loading, find the next candidate.
                SetDelayingLoadEvent(true);
                SetNetworkState(MediaNetworkState.Loading);
                FindNextCandidate(generation);
            });
            return;
        }

        if (_host.IsSourceElement(node) && _host.SrcAttribute is null && NetworkState == MediaNetworkState.Empty)
            InvokeResourceSelection();
    }

    /// <summary>
    /// A child was removed. <paramref name="previousSibling"/> is the removed node's previous
    /// sibling before removal; the selection pointer keeps its place among the remaining nodes.
    /// </summary>
    public void OnChildRemoved(object node, object? previousSibling)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (ReferenceEquals(node, _pointerBefore))
            _pointerBefore = previousSibling;
    }

    /// <summary>§4.8.11.8: removed from a document; pause at the next stable state unless re-inserted.</summary>
    public void OnRemovedFromDocument(Func<bool> isInDocument)
    {
        ArgumentNullException.ThrowIfNull(isInDocument);
        _host.AwaitStableState(() =>
        {
            if (isInDocument())
                return;
            RunInternalPauseSteps();
        });
    }

    // ---- Methods (HTML §4.8.11.5, §4.8.11.8) ---------------------------------------------

    /// <summary><c>load()</c>.</summary>
    public void Load() => RunLoadAlgorithm();

    /// <summary><c>canPlayType(type)</c>.</summary>
    public string CanPlayType(string type) => MediaTypeSupport.ToDomString(_typeSupport.CanPlayType(type));

    /// <summary><c>play()</c>. Returns the promise object created by the host.</summary>
    public object Play()
    {
        // 1. Not allowed to play: a promise rejected with NotAllowedError.
        if (!_host.IsAllowedToPlay)
        {
            var rejected = _host.CreatePromise();
            _host.RejectPromise(rejected, PlayRejection.NotAllowedError);
            Log(MediaEventKind.Error, MediaLogLevel.Info, "play() was blocked by the autoplay policy.", ("reason", "NotAllowedError"));
            return rejected;
        }

        // 2. The dedicated media source failure steps have run: NotSupportedError.
        if (Error is { Code: MediaErrorCode.SrcNotSupported })
        {
            var rejected = _host.CreatePromise();
            _host.RejectPromise(rejected, PlayRejection.NotSupportedError);
            return rejected;
        }

        // 3. Lazy load resumption steps: lazy loading is not implemented.

        // 4. New promise in the pending list.
        var promise = _host.CreatePromise();
        _pendingPlayPromises.Add(promise);

        // 5. Internal play steps.
        RunInternalPlaySteps();

        // 6.
        return promise;
    }

    /// <summary><c>pause()</c>.</summary>
    public void Pause()
    {
        // 1. An empty element starts selecting a resource.
        if (NetworkState == MediaNetworkState.Empty)
            InvokeResourceSelection();

        // 2.
        RunInternalPauseSteps();
    }

    /// <summary><c>currentTime</c> setter.</summary>
    public void SetCurrentTime(double seconds)
    {
        if (ReadyState == MediaReadyState.HaveNothing)
        {
            _defaultPlaybackStartPosition = seconds;
            return;
        }

        var time = MediaTime.FromSeconds(seconds);
        _officialPosition = time;
        Seek(time, approximateForSpeed: false);
    }

    /// <summary><c>fastSeek(time)</c>.</summary>
    public void FastSeek(double seconds) => Seek(MediaTime.FromSeconds(seconds), approximateForSpeed: true);

    /// <summary>
    /// <c>playbackRate</c> setter. Returns false when the value is unsupported, in which case
    /// the binding throws NotSupportedError and nothing changes.
    /// </summary>
    public bool TrySetPlaybackRate(double rate)
    {
        // 1. Unsupported values throw.
        if (!IsSupportedPlaybackRate(rate))
            return false;

        // 2. Set, and change the speed if potentially playing.
        if (rate.Equals(_playbackRate))
            return true;
        _playbackRate = rate;
        PushPlaybackState();
        QueueEvent("ratechange");
        return true;
    }

    public static bool IsSupportedPlaybackRate(double rate) =>
        rate == 0 || (rate >= MinimumPlaybackRate && rate <= MaximumPlaybackRate);

    /// <summary>
    /// <c>volume</c> setter. Returns false when the value is outside 0..1, in which case the
    /// binding throws IndexSizeError and nothing changes.
    /// </summary>
    public bool TrySetVolume(double volume)
    {
        if (!(volume >= 0.0 && volume <= 1.0))
            return false;

        // "set the playback volume"
        if (volume.Equals(_volume))
            return true;
        _volume = volume;
        if (!_host.IsAllowedToPlay)
            RunInternalPauseSteps();
        PushPlaybackState();
        QueueEvent("volumechange");
        return true;
    }

    /// <summary><c>muted</c> setter ("set the muted state").</summary>
    public void SetMuted(bool muted)
    {
        if (_mutedState == muted)
            return;
        _mutedState = muted;
        if (!_host.IsAllowedToPlay)
            RunInternalPauseSteps();
        PushPlaybackState();
        QueueEvent("volumechange");
    }

    // ---- Load algorithm (§4.8.11.5) ------------------------------------------------------

    private void RunLoadAlgorithm()
    {
        // 1. Not currently stalled.
        IsCurrentlyStalled = false;

        // 2. Abort any running resource selection.
        AbortResourceSelection();

        // 3–5. Settle the promises of pending element tasks now, in order, then drop the tasks.
        var pending = _queuedTasks.ToList();
        _queuedTasks.Clear();
        foreach (var task in pending)
        {
            task.Cancelled = true;
            task.SettlePromisesNow?.Invoke();
        }

        // 6. abort
        if (NetworkState is MediaNetworkState.Loading or MediaNetworkState.Idle)
            QueueEvent("abort");

        // 7.
        if (NetworkState != MediaNetworkState.Empty)
        {
            // 7.1
            QueueEvent("emptied");

            // 7.2 Stop fetching.
            StopResource();

            // 7.3 MediaSource detach: srcObject is not supported yet.

            // 7.4
            ForgetMediaResourceSpecificTracks();

            // 7.5
            if (ReadyState != MediaReadyState.HaveNothing)
                SetReadyStateSilently(MediaReadyState.HaveNothing);
            _resourceReadyState = MediaReadyState.HaveNothing;

            // 7.6
            if (!Paused)
            {
                Paused = true;
                RejectPendingPlayPromises(TakePendingPlayPromises(), PlayRejection.AbortError);
            }

            // 7.7
            Seeking = false;
            _pendingSeek = false;

            // 7.8–7.9
            bool officialChanged = _officialPosition != MediaTime.Zero;
            _currentPosition = MediaTime.Zero;
            _officialPosition = MediaTime.Zero;
            _earliestPossiblePosition = MediaTime.Zero;
            _host.TextTracksReset();

            // 7.10
            if (officialChanged)
                QueueEvent("timeupdate");

            // 7.11 timeline offset is NaN (getStartDate is not exposed yet).
            // 7.12 duration is NaN, without durationchange.
            _duration = null;

            Buffered = MediaTimeRanges.Empty;
            Seekable = MediaTimeRanges.Empty;
            _seekableReported = false;
            _played = MediaTimeRanges.Empty;
            _playedStart = null;
            if (VideoWidth != 0 || VideoHeight != 0)
            {
                VideoWidth = 0;
                VideoHeight = 0;
                _host.InvalidateRendering(sizeChanged: true);
            }
        }

        // 8.
        if (!_playbackRate.Equals(_defaultPlaybackRate) && IsSupportedPlaybackRate(_defaultPlaybackRate))
            _playbackRate = _defaultPlaybackRate;

        // 9.
        Error = null;
        _canAutoplay = true;
        _loadedDataFiredSinceLoad = false;

        // 10.
        InvokeResourceSelection();

        // 11. Playback of the previous resource has stopped with StopResource above.
    }

    private void AbortResourceSelection()
    {
        _selectionGeneration++;
        _mode = SelectionMode.None;
        _candidate = null;
        _pointerBefore = null;
        _waitingForSource = false;
    }

    // ---- Resource selection algorithm (§4.8.11.5) ----------------------------------------

    private void InvokeResourceSelection()
    {
        int generation = ++_selectionGeneration;
        _waitingForSource = false;

        // 1–2.
        SetNetworkState(MediaNetworkState.NoSource);
        SetShowPoster(true);

        // 3. Lazy loading is not implemented, so the element is always "eager".
        SetDelayingLoadEvent(true);

        // 4. Await a stable state; the rest is the synchronous section.
        _host.AwaitStableState(() =>
        {
            if (generation == _selectionGeneration)
                SelectResource(generation);
        });
    }

    private void SelectResource(int generation)
    {
        // ⌛ 5. Pending text tracks: M5.
        // ⌛ 6–9. Choose the mode (no srcObject yet).
        object? firstSource = null;
        if (_host.SrcAttribute is null)
        {
            for (var node = _host.FirstChild; node is not null; node = _host.NextSibling(node))
            {
                if (_host.IsSourceElement(node))
                {
                    firstSource = node;
                    break;
                }
            }
        }

        if (_host.SrcAttribute is not null)
        {
            _mode = SelectionMode.Attribute;
        }
        else if (firstSource is not null)
        {
            _mode = SelectionMode.Children;
            _candidate = firstSource;
        }
        else
        {
            // ⌛ 9. Nothing to load.
            SetNetworkState(MediaNetworkState.Empty);
            SetDelayingLoadEvent(false);
            _mode = SelectionMode.None;
            return;
        }

        // ⌛ 10–11.
        SetNetworkState(MediaNetworkState.Loading);
        QueueEvent("loadstart");

        // 12.
        if (_mode == SelectionMode.Attribute)
        {
            string src = _host.SrcAttribute!;

            // ⌛ An empty src fails without resolving.
            if (src.Length == 0)
            {
                FailWithAttribute(generation, "The src attribute is empty.");
                return;
            }

            // ⌛ Resolve; set currentSrc when it parsed.
            string? url = _host.ResolveUrl(src);
            if (url is null)
            {
                FailWithAttribute(generation, "The src attribute is not a valid URL.");
                return;
            }

            CurrentSrc = url;
            StartResource(url, generation);
            return;
        }

        // Children: the pointer sits just after the first candidate.
        _pointerBefore = _candidate;
        ProcessCandidate(generation);
    }

    private void ProcessCandidate(int generation)
    {
        var candidate = _candidate!;

        // ⌛ Process candidate: a src attribute is required.
        string? src = _host.GetSourceAttribute(candidate, "src");
        if (string.IsNullOrEmpty(src))
        {
            FailedWithElements(generation, candidate, "The source element has no src.");
            return;
        }

        // ⌛ A media query that does not match rules the candidate out.
        string? media = _host.GetSourceAttribute(candidate, "media");
        if (media is not null && !_host.MediaQueryMatches(media))
        {
            FailedWithElements(generation, candidate, "The source element's media query does not match.");
            return;
        }

        // ⌛ Resolve the URL.
        string? url = _host.ResolveUrl(src);
        if (url is null)
        {
            FailedWithElements(generation, candidate, "The source element's src is not a valid URL.");
            return;
        }

        // ⌛ A type the user agent knows it cannot render rules the candidate out.
        string? type = _host.GetSourceAttribute(candidate, "type");
        if (type is not null && _typeSupport.KnowsItCannotRender(type))
        {
            FailedWithElements(generation, candidate, $"The type '{type}' cannot be played.");
            return;
        }

        // ⌛ currentSrc, then fetch.
        CurrentSrc = url;
        StartResource(url, generation);
    }

    private void FailWithAttribute(int generation, string reason)
    {
        // Failed with attribute: take pending play promises and queue the dedicated failure steps.
        var promises = TakePendingPlayPromises();
        Log(MediaEventKind.SourceSelected, MediaLogLevel.Warn, reason, ("mode", "attribute"));
        QueueTask(
            () => RunDedicatedMediaSourceFailureSteps(promises, reason),
            settleNow: () => RejectPendingPlayPromises(promises, PlayRejection.NotSupportedError));
        _mode = SelectionMode.None;
        _ = generation;
    }

    private void FailedWithElements(int generation, object candidate, string reason)
    {
        Log(MediaEventKind.SourceSelected, MediaLogLevel.Info, reason, ("mode", "children"));

        // Failed with elements: error at the candidate.
        QueueTask(() => _host.FireEventAt(candidate, "error"));

        // Await a stable state, then look for the next candidate.
        _host.AwaitStableState(() =>
        {
            if (generation != _selectionGeneration)
                return;
            ForgetMediaResourceSpecificTracks();
            FindNextCandidate(generation);
        });
    }

    private void FindNextCandidate(int generation)
    {
        // ⌛ Find next candidate / search loop.
        _candidate = null;
        while (true)
        {
            var next = NodeAfterPointer();
            if (next is null)
            {
                EnterWaiting(generation);
                return;
            }

            _pointerBefore = next;
            if (_host.IsSourceElement(next))
            {
                _candidate = next;
                ProcessCandidate(generation);
                return;
            }
        }
    }

    private void EnterWaiting(int generation)
    {
        // ⌛ Waiting: no source; show the poster; stop delaying the load event (in a task).
        SetNetworkState(MediaNetworkState.NoSource);
        SetShowPoster(true);
        QueueTask(() =>
        {
            if (generation == _selectionGeneration)
                SetDelayingLoadEvent(false);
        });

        // Wait until a node is inserted after the pointer (OnChildInserted).
        _waitingForSource = true;
    }

    private object? NodeAfterPointer() =>
        _pointerBefore is null ? _host.FirstChild : _host.NextSibling(_pointerBefore);

    private void RunDedicatedMediaSourceFailureSteps(List<object> promises, string reason)
    {
        // 1.
        Error = new MediaElementError(MediaErrorCode.SrcNotSupported, reason);

        // 2.
        ForgetMediaResourceSpecificTracks();

        // 3–4.
        SetNetworkState(MediaNetworkState.NoSource);
        SetShowPoster(true);

        // 5.
        Log(MediaEventKind.Error, MediaLogLevel.Warn, reason, ("code", "MEDIA_ERR_SRC_NOT_SUPPORTED"));
        _host.FireEvent("error");

        // 6.
        RejectPendingPlayPromises(promises, PlayRejection.NotSupportedError);

        // 7.
        SetDelayingLoadEvent(false);
    }

    // ---- Resource fetch algorithm (§4.8.11.5) --------------------------------------------

    private void StartResource(string url, int generation)
    {
        StopResource();
        _tracks = [];
        Log(MediaEventKind.SourceSelected, MediaLogLevel.Info, "Fetching the media resource.",
            ("mode", _mode == SelectionMode.Attribute ? "attribute" : "children"));

        var client = new ResourceClient(this, generation);
        var request = new MediaFetchRequest(url, _host.IsVideo, _host.CrossOriginAttribute, _host.PreloadAttribute);
        var resource = _host.StartResource(request, client);
        if (resource is null)
        {
            // The pipeline cannot try at all: behave as for an unsupported format, in a task.
            QueueTask(() => client.Failed(MediaResourceFailure.Unsupported, "No media pipeline can load this resource."));
            return;
        }

        _resource = resource;
        PushPlaybackState();
        if (!Paused || _host.HasAutoplayAttribute)
            _resource.RequestFullLoad();
    }

    private void StopResource()
    {
        var resource = _resource;
        _resource = null;
        resource?.Dispose();
    }

    private void ForgetMediaResourceSpecificTracks() => _tracks = [];

    // "Abort this subalgorithm, returning to the resource selection algorithm."
    private void OnResourceUnusable(int generation, string reason)
    {
        StopResource();
        switch (_mode)
        {
            case SelectionMode.Attribute:
                FailWithAttribute(generation, reason);
                break;
            case SelectionMode.Children when _candidate is { } candidate:
                FailedWithElements(generation, candidate, reason);
                break;
        }
    }

    private void OnFatalErrorAfterUsable(MediaErrorCode code, string reason)
    {
        // Network or decode error after metadata: cancel, set the error, idle, stop delaying, fire error.
        StopResource();
        Error = new MediaElementError(code, reason);
        SetNetworkState(MediaNetworkState.Idle);
        SetDelayingLoadEvent(false);
        Log(MediaEventKind.Error, MediaLogLevel.Error, reason,
            ("code", code == MediaErrorCode.Network ? "MEDIA_ERR_NETWORK" : "MEDIA_ERR_DECODE"));
        _host.FireEvent("error");
        AbortResourceSelection();
    }

    private void OnAbortedByUser(string reason)
    {
        StopResource();
        Error = new MediaElementError(MediaErrorCode.Aborted, reason);
        _host.FireEvent("abort");
        if (ReadyState == MediaReadyState.HaveNothing)
        {
            SetNetworkState(MediaNetworkState.Empty);
            SetShowPoster(true);
            _host.FireEvent("emptied");
        }
        else
        {
            SetNetworkState(MediaNetworkState.Idle);
        }

        SetDelayingLoadEvent(false);
        AbortResourceSelection();
    }

    private void OnMetadata(MediaResourceMetadata metadata)
    {
        _tracks = metadata.Tracks;

        // Establish the timeline; current and official positions start at the earliest position.
        _earliestPossiblePosition = metadata.EarliestPossiblePosition;
        _currentPosition = _earliestPossiblePosition;
        _officialPosition = _earliestPossiblePosition;
        if (_defaultPlaybackStartPosition <= 0.0)
            _host.PlaybackPositionChanged(monotonic: false); // otherwise the seek below reports the real position

        // Duration, then a queued durationchange.
        _duration = metadata.Duration;
        QueueEvent("durationchange");

        // Video size, then a queued resize.
        if (_host.IsVideo)
        {
            VideoWidth = Math.Max(0, metadata.VideoWidth);
            VideoHeight = Math.Max(0, metadata.VideoHeight);
            _host.InvalidateRendering(sizeChanged: true);
            QueueEvent("resize");
        }

        // HAVE_METADATA fires loadedmetadata.
        SetReadyState(MediaReadyState.HaveMetadata);

        // Default playback start position: seek there, then reset it.
        var start = _defaultPlaybackStartPosition;
        _defaultPlaybackStartPosition = 0.0;
        if (start > 0.0)
            Seek(MediaTime.FromSeconds(start), approximateForSpeed: false);

        PushPlaybackState();
    }

    // ---- Ready states (§4.8.11.7) --------------------------------------------------------

    // The readiness the resource last reported; re-applied once pending text tracks load.
    private MediaReadyState _resourceReadyState;

    /// <summary>
    /// The resource's readiness, clamped to HAVE_CURRENT_DATA while the element is blocked on
    /// pending text tracks (§4.8.12.11.3), so canplay waits for a default track to load.
    /// </summary>
    private void SetResourceReadyState(MediaReadyState state)
    {
        _resourceReadyState = state;
        if (state > MediaReadyState.HaveCurrentData && _host.HasPendingTextTracks)
            state = MediaReadyState.HaveCurrentData;
        SetReadyState(state);
    }

    /// <summary>The host's pending text tracks list changed; the held-back readiness may apply now.</summary>
    public void PendingTextTracksChanged()
    {
        if (ReadyState == MediaReadyState.HaveNothing || _resource is null)
            return;
        if (_resourceReadyState > ReadyState && !_host.HasPendingTextTracks)
            SetReadyState(_resourceReadyState);
    }

    private void SetReadyStateSilently(MediaReadyState state)
    {
        ReadyState = state;
        Log(MediaEventKind.ReadyStateChanged, MediaLogLevel.Debug, $"readyState {state}", ("readyState", ((int)state).ToString(CultureInfo.InvariantCulture)));
    }

    private void SetReadyState(MediaReadyState newState)
    {
        var previous = ReadyState;
        if (previous == newState)
            return;

        bool wasPotentiallyPlaying = IsPotentiallyPlaying;
        SetReadyStateSilently(newState);
        if (NetworkState == MediaNetworkState.Empty)
            return;

        if (previous == MediaReadyState.HaveNothing && newState == MediaReadyState.HaveMetadata)
        {
            QueueEvent("loadedmetadata");
        }
        else
        {
            if (previous == MediaReadyState.HaveMetadata && newState >= MediaReadyState.HaveCurrentData && !_loadedDataFiredSinceLoad)
            {
                _loadedDataFiredSinceLoad = true;
                QueueTask(() =>
                {
                    _host.FireEvent("loadeddata");
                    // After loadeddata, stop delaying the load event.
                    SetDelayingLoadEvent(false);
                });
            }

            if (previous >= MediaReadyState.HaveFutureData && newState <= MediaReadyState.HaveCurrentData)
            {
                if (wasPotentiallyPlaying && !HasEndedPlayback)
                {
                    QueueEvent("timeupdate");
                    QueueEvent("waiting");
                }
            }
            else if (previous <= MediaReadyState.HaveCurrentData && newState == MediaReadyState.HaveFutureData)
            {
                QueueEvent("canplay");
                if (!Paused)
                    NotifyAboutPlaying();
            }
            else if (newState == MediaReadyState.HaveEnoughData)
            {
                if (previous <= MediaReadyState.HaveCurrentData)
                {
                    QueueEvent("canplay");
                    if (!Paused)
                        NotifyAboutPlaying();
                }

                QueueEvent("canplaythrough");

                if (IsEligibleForAutoplay && _host.IsAllowedToPlay)
                {
                    Paused = false;
                    SetShowPoster(false);
                    QueueEvent("play");
                    NotifyAboutPlaying();
                    StartPlayedRange();
                }
            }
        }

        PushPlaybackState();
    }

    // ---- Playing (§4.8.11.8) -------------------------------------------------------------

    private void RunInternalPlaySteps()
    {
        // 1.
        if (NetworkState == MediaNetworkState.Empty)
            InvokeResourceSelection();

        // 2. Ended and forwards: restart from the earliest possible position. A loop
        // attribute set after the end makes "ended" false, but play() still restarts,
        // as the shipping engines do (whatwg/html#4487, WPT loop-from-ended.tentative).
        if (HasEndedPlayback || IsAtTheEnd)
            Seek(_earliestPossiblePosition, approximateForSpeed: false);

        // 3.
        if (Paused)
        {
            Paused = false;
            SetShowPoster(false);
            QueueEvent("play");

            if (ReadyState <= MediaReadyState.HaveCurrentData)
                QueueEvent("waiting");
            else
                NotifyAboutPlaying();
        }
        else if (ReadyState >= MediaReadyState.HaveFutureData)
        {
            // 4. Already playing: resolve in a task.
            var promises = TakePendingPlayPromises();
            QueueTask(
                () => ResolvePendingPlayPromises(promises),
                settleNow: () => ResolvePendingPlayPromises(promises));
        }

        // 5.
        _canAutoplay = false;

        _resource?.RequestFullLoad();
        StartPlayedRange();
        PushPlaybackState();
    }

    private void RunInternalPauseSteps()
    {
        // 1.
        _canAutoplay = false;

        // 2.
        if (!Paused)
        {
            Paused = true;
            var promises = TakePendingPlayPromises();
            QueueTask(
                () =>
                {
                    _host.FireEvent("timeupdate");
                    _host.FireEvent("pause");
                    RejectPendingPlayPromises(promises, PlayRejection.AbortError);
                },
                settleNow: () => RejectPendingPlayPromises(promises, PlayRejection.AbortError));
            _officialPosition = _currentPosition;
            EndPlayedRange();
        }

        PushPlaybackState();
    }

    private void NotifyAboutPlaying()
    {
        var promises = TakePendingPlayPromises();
        QueueTask(
            () =>
            {
                _host.FireEvent("playing");
                ResolvePendingPlayPromises(promises);
            },
            settleNow: () => ResolvePendingPlayPromises(promises));
    }

    private List<object> TakePendingPlayPromises()
    {
        var promises = _pendingPlayPromises;
        _pendingPlayPromises = [];
        return promises;
    }

    private void ResolvePendingPlayPromises(List<object> promises)
    {
        foreach (var promise in promises)
            _host.ResolvePromise(promise);
        promises.Clear();
    }

    private void RejectPendingPlayPromises(List<object> promises, PlayRejection reason)
    {
        foreach (var promise in promises)
            _host.RejectPromise(promise, reason);
        promises.Clear();
    }

    private void OnPositionChanged(MediaTime position, bool monotonic)
    {
        // While a seek is in flight the resource may still report the clock it is
        // leaving; the position is the seek target until SeekCompleted says otherwise.
        if (_pendingSeek && monotonic)
            return;

        if (!monotonic)
            EndPlayedRange();
        _currentPosition = position;
        _officialPosition = position;
        if (!monotonic)
            StartPlayedRange();
        _host.PlaybackPositionChanged(monotonic);

        // Time marches on: timeupdate during normal playback at most every 250 ms.
        if (monotonic)
        {
            long now = _time.GetTimestamp();
            if (_lastTimeUpdateTimestamp == long.MinValue || _time.GetElapsedTime(_lastTimeUpdateTimestamp, now) >= s_timeUpdateInterval)
            {
                _lastTimeUpdateTimestamp = now;
                QueueEvent("timeupdate");
            }
        }
    }

    private void OnReachedEnd()
    {
        if (_duration is { } duration && !duration.IsInfinite)
            _currentPosition = MediaTime.Max(_currentPosition, duration);
        _host.PlaybackPositionChanged(monotonic: true);

        // 1. Loop: seek to the earliest possible position.
        if (_host.HasLoopAttribute)
        {
            Seek(_earliestPossiblePosition, approximateForSpeed: false);
            return;
        }

        EndPlayedRange();

        // 2–3.
        QueueTask(() =>
        {
            _host.FireEvent("timeupdate");
            if (HasEndedPlayback && !Paused)
            {
                Paused = true;
                _host.FireEvent("pause");
                RejectPendingPlayPromises(TakePendingPlayPromises(), PlayRejection.AbortError);
            }

            _host.FireEvent("ended");
            PushPlaybackState();
        });
        PushPlaybackState();
    }

    private void StartPlayedRange()
    {
        if (!Paused && _playedStart is null)
            _playedStart = _currentPosition;
    }

    private void EndPlayedRange()
    {
        if (_playedStart is { } start)
        {
            if (_currentPosition > start)
                _played = _played.Union(MediaTimeRanges.Single(start, _currentPosition));
            _playedStart = null;
        }
    }

    // ---- Seeking (§4.8.11.9) -------------------------------------------------------------

    private void Seek(MediaTime target, bool approximateForSpeed)
    {
        // 1–2.
        SetShowPoster(false);
        if (ReadyState == MediaReadyState.HaveNothing)
            return;

        // 3–4. A newer seek replaces a running one.
        Seeking = true;

        // 6–7. Clamp to the resource.
        if (_duration is { } duration && !duration.IsInfinite && target > duration)
            target = duration;
        if (target < _earliestPossiblePosition)
            target = _earliestPossiblePosition;

        // 8. Snap into the seekable ranges; with none, the seek ends here. Right after
        // metadata the resource may not have reported its ranges yet although the whole
        // timeline is known; a finite duration then stands in for [earliest, duration], so
        // a default playback start position set before metadata is honoured.
        bool seekableUnknown = !_seekableReported && _duration is { } known && !known.IsInfinite;
        if (!seekableUnknown && !Seekable.Contains(target))
        {
            if (Seekable.Nearest(target) is not { } nearest)
            {
                Seeking = false;
                return;
            }

            target = nearest;
        }

        // 10. seeking
        QueueEvent("seeking");

        // 11. Move the current playback position. The official position follows at once
        // (the ⌛ steps run before the script continues), so currentTime reads the clamped
        // target right after the setter, as WPT seeking/seek-to-max-value expects.
        EndPlayedRange();
        _currentPosition = target;
        _officialPosition = target;
        StartPlayedRange();
        _host.PlaybackPositionChanged(monotonic: false);
        Log(MediaEventKind.SeekStart, MediaLogLevel.Debug, $"seek to {target}", ("target", target.ToString()));

        // 12. Wait for the data: the resource answers with SeekCompleted.
        if (_resource is not null)
        {
            _pendingSeek = true;
            _resource.Seek(target, approximateForSpeed);
        }
        else
        {
            CompleteSeek(target);
        }
    }

    private void CompleteSeek(MediaTime position)
    {
        // 13–16, at a stable state.
        _pendingSeek = false;
        _host.AwaitStableState(() =>
        {
            if (_pendingSeek)
                return; // a newer seek is running
            Seeking = false;
            _currentPosition = position;
            _officialPosition = position;
            _host.PlaybackPositionChanged(monotonic: false);
            QueueEvent("timeupdate");
            QueueEvent("seeked");
            Log(MediaEventKind.SeekEnd, MediaLogLevel.Debug, $"seeked to {position}");
            PushPlaybackState();
        });
    }

    // ---- Helpers ------------------------------------------------------------------------

    private void PushPlaybackState() =>
        _resource?.UpdatePlayback(IsPotentiallyPlaying, _playbackRate, _preservesPitch, EffectiveVolume);

    private void SetNetworkState(MediaNetworkState state)
    {
        if (NetworkState == state)
            return;
        NetworkState = state;
        Log(MediaEventKind.NetworkStateChanged, MediaLogLevel.Debug, $"networkState {state}",
            ("networkState", ((int)state).ToString(CultureInfo.InvariantCulture)));
    }

    private void SetShowPoster(bool value)
    {
        if (ShowPoster == value)
            return;
        ShowPoster = value;
        _host.InvalidateRendering(sizeChanged: false);
    }

    private void SetDelayingLoadEvent(bool delaying)
    {
        if (DelayingLoadEvent == delaying)
            return;
        DelayingLoadEvent = delaying;
        _host.SetDelayingLoadEvent(delaying);
    }

    private void QueueEvent(string type) => QueueTask(() => _host.FireEvent(type));

    private void QueueTask(Action run, Action? settleNow = null)
    {
        var task = new ElementTask(run, settleNow);
        _queuedTasks.Add(task);
        _host.QueueTask(() =>
        {
            if (task.Cancelled)
                return;
            _queuedTasks.Remove(task);
            task.Run();
        });
    }

    private void Log(MediaEventKind kind, MediaLogLevel level, string message, params ReadOnlySpan<(string Key, string Value)> fields) =>
        _log.Emit(Player, kind, level, message, fields);

    private sealed class ElementTask(Action run, Action? settleNow)
    {
        public Action Run { get; } = run;

        public Action? SettlePromisesNow { get; } = settleNow;

        public bool Cancelled { get; set; }
    }

    /// <summary>Routes pipeline reports into the element, dropping reports from abandoned loads.</summary>
    private sealed class ResourceClient(HtmlMediaElementController owner, int generation) : IMediaResourceClient
    {
        private bool IsCurrent => generation == owner._selectionGeneration;

        public void Failed(MediaResourceFailure failure, string message)
        {
            if (!IsCurrent)
                return;
            owner.QueueTask(() =>
            {
                if (!IsCurrent)
                    return;
                if (failure == MediaResourceFailure.AbortedByUser)
                    owner.OnAbortedByUser(message);
                else if (owner.ReadyState == MediaReadyState.HaveNothing)
                    owner.OnResourceUnusable(generation, message);
                else if (failure == MediaResourceFailure.Network)
                    owner.OnFatalErrorAfterUsable(MediaErrorCode.Network, message);
                else
                    owner.OnFatalErrorAfterUsable(MediaErrorCode.Decode, message);
            });
        }

        public void MetadataAvailable(MediaResourceMetadata metadata)
        {
            ArgumentNullException.ThrowIfNull(metadata);
            Enqueue(() =>
            {
                if (owner.ReadyState == MediaReadyState.HaveNothing)
                    owner.OnMetadata(metadata);
            });
        }

        public void ReadyStateChanged(MediaReadyState state) => Enqueue(() =>
        {
            // Nothing above HAVE_METADATA before the metadata step has run.
            if (owner.ReadyState != MediaReadyState.HaveNothing && state >= MediaReadyState.HaveMetadata)
                owner.SetResourceReadyState(state);
        });

        public void DurationChanged(MediaTime duration) => Enqueue(() =>
        {
            if (owner.ReadyState == MediaReadyState.HaveNothing || owner._duration == duration)
                return;
            owner._duration = duration;
            owner._host.FireEvent("durationchange");
            if (!duration.IsInfinite && owner._currentPosition > duration)
                owner.Seek(duration, approximateForSpeed: false);
        });

        public void VideoSizeChanged(int width, int height) => Enqueue(() =>
        {
            if (!owner._host.IsVideo || (owner.VideoWidth == width && owner.VideoHeight == height))
                return;
            owner.VideoWidth = Math.Max(0, width);
            owner.VideoHeight = Math.Max(0, height);
            owner._host.InvalidateRendering(sizeChanged: true);
            owner._host.FireEvent("resize");
        });

        public void PositionChanged(MediaTime position, bool monotonic) => Enqueue(() => owner.OnPositionChanged(position, monotonic));

        public void ReachedEnd() => Enqueue(owner.OnReachedEnd);

        public void SeekCompleted(MediaTime position) => Enqueue(() =>
        {
            if (owner._pendingSeek)
                owner.CompleteSeek(position);
        });

        public void BufferedChanged(MediaTimeRanges buffered)
        {
            ArgumentNullException.ThrowIfNull(buffered);
            Enqueue(() => owner.Buffered = buffered);
        }

        public void SeekableChanged(MediaTimeRanges seekable)
        {
            ArgumentNullException.ThrowIfNull(seekable);
            Enqueue(() =>
            {
                owner.Seekable = seekable;
                owner._seekableReported = true;
            });
        }

        public void Progress() => Enqueue(() =>
        {
            owner.IsCurrentlyStalled = false;
            owner._host.FireEvent("progress");
        });

        public void Suspended() => Enqueue(() =>
        {
            owner.SetNetworkState(MediaNetworkState.Idle);
            owner._host.FireEvent("suspend");
        });

        public void Resumed() => Enqueue(() => owner.SetNetworkState(MediaNetworkState.Loading));

        public void Stalled() => Enqueue(() =>
        {
            owner.IsCurrentlyStalled = true;
            owner.Log(MediaEventKind.Stall, MediaLogLevel.Info, "No media data for the stall timeout.");
            owner._host.FireEvent("stalled");
        });

        public void FetchedEntirely() => Enqueue(() =>
        {
            owner._host.FireEvent("progress");
            owner.SetNetworkState(MediaNetworkState.Idle);
            owner._host.FireEvent("suspend");
        });

        private void Enqueue(Action action)
        {
            if (!IsCurrent)
                return;
            owner.QueueTask(() =>
            {
                if (IsCurrent)
                    action();
            });
        }
    }
}
