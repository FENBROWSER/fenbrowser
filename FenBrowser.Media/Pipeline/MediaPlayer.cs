using System.Collections.Concurrent;
using System.Globalization;
using FenBrowser.Media.Audio;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Clock;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Element;
using FenBrowser.Media.Mse;
using FenBrowser.Media.Video;

namespace FenBrowser.Media.Pipeline;

/// <summary>What a <see cref="MediaPlayer"/> needs from its host beyond the bytes.</summary>
public sealed record MediaPlayerServices(
    DemuxerRegistry Demuxers,
    DecoderRegistry Decoders,
    IAudioOutputFactory AudioOutputs,
    TimeProvider TimeProvider)
{
    /// <summary>How much decoded audio is kept ahead of the device; also the HAVE_ENOUGH_DATA threshold.</summary>
    public MediaTime DecodeAhead { get; init; } = MediaTime.FromSeconds(1.0);

    /// <summary>Decoded audio ahead of the device that counts as HAVE_FUTURE_DATA.</summary>
    public MediaTime FutureDataThreshold { get; init; } = MediaTime.FromSeconds(0.2);

    /// <summary>How often the element hears the playback position while playing.</summary>
    public TimeSpan PositionInterval { get; init; } = TimeSpan.FromMilliseconds(40);

    /// <summary>How often the video renderer picks the picture for the clock while playing.</summary>
    public TimeSpan FrameInterval { get; init; } = TimeSpan.FromMilliseconds(4);

    /// <summary>
    /// Where demuxing and decoding run: null means in this process from the registries;
    /// the browser sets the media-process transport here (ADR-0004).
    /// </summary>
    public IMediaDecodeSourceFactory? DecodeSources { get; init; }
}

/// <summary>
/// One player: the media session of design §2.2 running the §2.3 pipeline for a resource
/// with audio, video or both. It owns the decode source (demuxer and decoders, here or in
/// the media process), the audio renderer and output stream, the video renderer and the
/// presenter, drives them from a single media task, and reports to the element through
/// <see cref="IMediaResourceClient"/> on the element's thread.
/// </summary>
/// <remarks>
/// Every public member is safe to call from the element thread: they post commands to the
/// media task. The media task is the only thing that touches the decode source. The
/// audio device pulls from the renderer on its own thread and the master clock reads the
/// device position, so <c>currentTime</c> is what is audible, not what was decoded; a
/// resource without audio runs on a monotonic clock (§2.4). Pictures are chosen for the
/// clock on the media task and handed to the compositor through <see cref="Presenter"/>.
/// </remarks>
public sealed class MediaPlayer : IMediaResource
{
    private readonly IByteSource? _source;
    private readonly MediaSourceModel? _mediaSource;
    private readonly string? _declaredMime;
    private readonly IMediaResourceClient _client;
    private readonly Action<Action> _post;
    private readonly MediaPlayerServices _services;
    private readonly MediaPipelineContext _context;
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly RenderRelay _relay = new();
    private Task? _task;

    // Media-task state.
    private IMediaDecodeSource? _decodeSource;
    private IAudioOutput? _output;
    private AudioRenderer? _renderer;
    private IMediaClock? _clock;
    private MonotonicMediaClock? _monotonic;
    private VideoRenderer? _video;
    private VideoFrame? _heldVideo;
    private MediaTime? _videoTrimBefore;
    private bool _presentFirstFrame;
    private bool _wholeResourceAvailable;
    private int _presentedWidth;
    private int _presentedHeight;
    private MediaTime _duration = MediaTime.PositiveInfinity;
    private bool _endOfStream;
    private bool _endReported;
    private bool _reopenRequested;
    private MediaTime? _reportedDuration;
    private bool _fetchedReported;
    private bool _potentiallyPlaying;
    private bool _outputRunning;
    private bool _videoVisible = true;
    private bool _videoDecodeOff;
    private bool _visibilityDirty;
    private bool _timerHeld;
    private bool _preservesPitch = true;
    private double _rate = 1.0;
    private TimeStretcher? _stretcher;
    private int _stretcherRate;
    private int _stretcherChannels;
    private MediaReadyState _readyState = MediaReadyState.HaveNothing;
    private MediaTime? _pendingSeek;
    private MediaTime? _pendingResync;

    /// <summary>A seek whose target the MediaSource has no data for yet: it completes when data arrives or the source ends.</summary>
    private MediaTime? _seekAwaitingData;
    private MediaTime? _trimBefore;
    private int _seekSequence;
    private long _lastPositionTick;
    private bool _failed;
    private int _disposed;

    public MediaPlayer(
        IByteSource source,
        string? declaredMime,
        IMediaResourceClient client,
        Action<Action> postToClient,
        MediaPlayerServices services,
        MediaPipelineContext context,
        VideoPresenter? presenter = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(postToClient);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);
        _source = source;
        _declaredMime = declaredMime;
        _client = client;
        _post = postToClient;
        _services = services;
        _context = context;
        Presenter = presenter ?? new VideoPresenter();
    }

    /// <summary>
    /// A player over a MediaSource (MSE §2.4.2 "attaching to a media element"): the
    /// frames come from the source buffers instead of a byte source, buffered and seekable
    /// follow the MediaSource, and a read that finds nothing appended yet stalls the
    /// pipeline until the next append wakes it.
    /// </summary>
    public MediaPlayer(
        MediaSourceModel mediaSource,
        IMediaResourceClient client,
        Action<Action> postToClient,
        MediaPlayerServices services,
        MediaPipelineContext context,
        VideoPresenter? presenter = null)
    {
        ArgumentNullException.ThrowIfNull(mediaSource);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(postToClient);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);
        _mediaSource = mediaSource;
        _client = client;
        _post = postToClient;
        _services = services;
        _context = context;
        Presenter = presenter ?? new VideoPresenter();
        mediaSource.Changed += OnMediaSourceChanged;
    }

    /// <summary>
    /// The MediaSource changed (under its gate, on the page thread): the element hears the
    /// new buffered, seekable and duration, and the media task wakes up to read on - or to
    /// take back an end of stream that an append reopened (§3.5.4 "prepare append" step 3).
    /// </summary>
    private void OnMediaSourceChanged()
    {
        var model = _mediaSource!;
        var buffered = model.Buffered;
        var seekable = model.Seekable;
        var duration = model.Duration;
        var state = model.ReadyState;
        // The media task learns the new duration and state whatever the readiness; the
        // element only hears the values after metadata, whose report carries the first.
        if (_readyState != MediaReadyState.HaveNothing)
        {
            Report(() =>
            {
                _client.BufferedChanged(buffered);
                _client.SeekableChanged(seekable);
                if (duration is { } value && value != _reportedDuration)
                {
                    _reportedDuration = value;
                    _client.DurationChanged(value);
                }

                // No progress events: script supplies the data, there is no fetch to report on
                // (HTML §4.8.11.5, the media provider object branch).
                bool ended = state == MediaSourceReadyState.Ended;
                if (ended && !_fetchedReported)
                    _client.FetchedEntirely();
                _fetchedReported = ended;
            });
        }

        Post(() =>
        {
            if (state == MediaSourceReadyState.Open && _endOfStream)
                _reopenRequested = true;
            _wholeResourceAvailable = state == MediaSourceReadyState.Ended;
            if (duration is { } value)
                _duration = value;
        });
    }

    public PlayerId Player => _context.Player;

    /// <summary>The playback clock, once the pipeline is up (tests read it).</summary>
    public IMediaClock? Clock => _clock;

    /// <summary>The latest picture for the compositor; empty for audio-only resources.</summary>
    public VideoPresenter Presenter { get; }

    VideoPresenter? IMediaResource.Presenter => Presenter;

    public VideoPlaybackQuality? GetVideoPlaybackQuality()
    {
        var video = _video;
        return video is null ? null : new VideoPlaybackQuality(video.DecodedFrames, video.DroppedFrames);
    }

    /// <summary>Starts the media task. Returns at once; the client hears the outcome.</summary>
    public void Start()
    {
        if (_task is not null)
            throw new InvalidOperationException("The player has already started.");
        _task = Task.Run(RunAsync);
    }

    // ---- IMediaResource (element thread) --------------------------------------------------

    public void UpdatePlayback(bool potentiallyPlaying, double playbackRate, bool preservesPitch, double effectiveVolume)
    {
        Post(() =>
        {
            _potentiallyPlaying = potentiallyPlaying;
            _preservesPitch = preservesPitch;
            _rate = playbackRate > 0 ? playbackRate : 1.0;
            _monotonic?.SetPlaybackRate(_rate);
            if (_renderer is not null)
            {
                bool stretch = _preservesPitch && Math.Abs(_rate - 1.0) > 1e-9;
                bool wasStretched = _renderer.PitchPreserved;
                bool rateChanged = Math.Abs(_renderer.Rate - _rate) > 1e-9;
                _renderer.PitchPreserved = stretch;
                _renderer.Rate = _rate;
                _renderer.Volume = effectiveVolume;
                _renderer.Muted = effectiveVolume <= 0;
                if (_stretcher is not null)
                    _stretcher.Rate = _rate;

                // Queued audio was prepared for the old mode or rate (plain blocks for a
                // stretched renderer, or stretched at another rate); rebuild it from the
                // current position rather than play it at the wrong speed.
                bool modeChanged = wasStretched != stretch || (stretch && rateChanged);
                if (modeChanged && _clock is not null && !_pendingSeek.HasValue && _renderer.QueuedDuration > MediaTime.Zero)
                    _pendingResync = _clock.CurrentTime;
            }
        });
    }

    public void Seek(MediaTime target, bool approximateForSpeed)
    {
        int sequence = Interlocked.Increment(ref _seekSequence);
        Post(() =>
        {
            if (sequence != Volatile.Read(ref _seekSequence))
                return; // superseded before it ran
            _pendingSeek = target;
        });
    }

    public void RequestFullLoad()
    {
        // The byte source already holds the whole resource.
    }

    public void UpdateVideoVisibility(bool visible)
    {
        Post(() =>
        {
            if (_videoVisible == visible)
                return;
            _videoVisible = visible;
            _visibilityDirty = true;
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _lifetime.Cancel();
        if (_mediaSource is { } mediaSource)
            mediaSource.Changed -= OnMediaSourceChanged;
        _wake.Release();
        // The primitives outlive the media task; release them once it has ended.
        var task = _task;
        if (task is null)
            ReleasePrimitives();
        else
            task.ContinueWith(_ => ReleasePrimitives(), TaskScheduler.Default);
    }

    private void ReleasePrimitives()
    {
        _wake.Dispose();
        _lifetime.Dispose();
    }

    private void Post(Action command)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        _commands.Enqueue(command);
        try
        {
            _wake.Release();
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the check and the release: the media task is gone anyway.
        }
    }

    // ---- The media task -----------------------------------------------------------------------

    private async Task RunAsync()
    {
        var cancellation = _lifetime.Token;
        try
        {
            if (!await LoadAsync(cancellation).ConfigureAwait(false))
                return;

            while (!cancellation.IsCancellationRequested)
            {
                while (_commands.TryDequeue(out var command))
                    command();

                if (_visibilityDirty)
                {
                    _visibilityDirty = false;
                    await ApplyVideoVisibilityAsync(cancellation).ConfigureAwait(false);
                }

                if (_pendingSeek is { } seekTarget)
                {
                    _pendingSeek = null;
                    _pendingResync = null;
                    await SeekCoreAsync(seekTarget, cancellation, report: true).ConfigureAwait(false);
                }
                else if (_pendingResync is { } resyncTarget)
                {
                    _pendingResync = null;
                    await SeekCoreAsync(resyncTarget, cancellation, report: false).ConfigureAwait(false);
                }

                await FillAheadAsync(cancellation).ConfigureAwait(false);
                LogFill("idle");
                if (_seekAwaitingData is { } awaited && (_endOfStream || HasDecodedData()))
                {
                    ReportReadiness();
                    CompleteSeek(awaited);
                }

                UpdateOutputState();
                PresentVideo();
                ReportReadiness();
                ReportPosition();

                if (_failed)
                    return;

                var wait = !_outputRunning ? TimeSpan.FromMilliseconds(250)
                    : _video is not null && !_videoDecodeOff ? _services.FrameInterval
                    : _services.PositionInterval;
                await _wake.WaitAsync(wait, cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            await TearDownAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<bool> LoadAsync(CancellationToken cancellation)
    {
        if (_mediaSource is { } mediaSource)
        {
            _decodeSource = new MseDecodeSource(mediaSource, _services.Decoders, _context);
        }
        else
        {
            var factory = _services.DecodeSources ?? new LocalMediaDecodeSourceFactory(_services.Demuxers, _services.Decoders);
            _decodeSource = factory.Create(_source!, _declaredMime, _context);
        }

        MediaSourceInfo info;
        try
        {
            info = await _decodeSource.OpenAsync(cancellation).ConfigureAwait(false);
        }
        catch (MediaUnsupportedException ex)
        {
            if (ex.InnerException is not null)
                LogLimit(ex.InnerException);
            string message = ex.Message;
            Report(() => _client.Failed(MediaResourceFailure.Unsupported, message));
            return false;
        }
        catch (Exception ex) when (ex is MediaFormatException or MediaDecoderException or MediaLimitExceededException or MediaProcessLostException)
        {
            LogLimit(ex);
            Fail(ex);
            return false;
        }

        if (info.AudioTrack is { } audioTrack)
        {
            // The device decides the format the renderer produces; the clock follows the device.
            _output = _services.AudioOutputs.Create();
            var format = await _output.OpenAsync(new AudioStreamFormat(audioTrack.Config.SampleRate, audioTrack.Config.Channels), _relay, cancellation).ConfigureAwait(false);
            var audioClock = new AudioMasterClock(_output.Position);
            _clock = audioClock;
            _renderer = new AudioRenderer(format, audioClock);
            _relay.Target = _renderer;
        }
        else
        {
            // No audio to follow: wall-clock time drives the pictures (design §2.4).
            _monotonic = new MonotonicMediaClock(_services.TimeProvider);
            _monotonic.SetPlaybackRate(_rate);
            _clock = _monotonic;
        }

        int videoWidth = 0;
        int videoHeight = 0;
        if (info.VideoTrack is { } videoTrack)
        {
            _video = new VideoRenderer(_context.Limits.MaxQueuedVideoFrames);
            _presentFirstFrame = true;
            videoWidth = videoTrack.Config.Width;
            videoHeight = videoTrack.Config.Height;
            _presentedWidth = videoWidth;
            _presentedHeight = videoHeight;
        }

        _duration = info.Duration;
        var tracks = info.Tracks;
        var duration = _duration;
        if (_mediaSource is { } model)
        {
            MediaTimeRanges buffered;
            MediaTimeRanges seekable;
            bool ended;
            lock (model.Gate)
            {
                buffered = model.Buffered;
                seekable = model.Seekable;
                ended = model.ReadyState == MediaSourceReadyState.Ended;
            }

            _reportedDuration = duration;
            _wholeResourceAvailable = ended;
            Report(() =>
            {
                _client.MetadataAvailable(new MediaResourceMetadata(duration, videoWidth, videoHeight, tracks));
                _client.SeekableChanged(seekable);
                _client.BufferedChanged(buffered);
                if (ended && !_fetchedReported)
                    _client.FetchedEntirely();
                _fetchedReported = ended;
            });
        }
        else
        {
            Report(() =>
            {
                _client.MetadataAvailable(new MediaResourceMetadata(duration, videoWidth, videoHeight, tracks));
                var whole = duration.IsInfinite ? MediaTimeRanges.Empty : MediaTimeRanges.Single(MediaTime.Zero, duration);
                if (info.IsSeekable)
                    _client.SeekableChanged(whole);
                _client.BufferedChanged(whole);
                _client.FetchedEntirely();
            });
        }

        _readyState = MediaReadyState.HaveMetadata;
        return true;
    }

    private async ValueTask FillAheadAsync(CancellationToken cancellation)
    {
        var source = _decodeSource!;
        var output = _renderer is null ? null : new RendererOutput(_renderer, this);

        try
        {
            if (_reopenRequested)
            {
                // An append after endOfStream: the stream goes on from where it ended.
                _reopenRequested = false;
                _endOfStream = false;
                _endReported = false;
                _renderer?.ClearEndOfStream();
                _video?.ClearEndOfStream();
            }

            if (_heldVideo is not null && _video is { IsFull: false })
            {
                _video.Enqueue(_heldVideo);
                _heldVideo = null;
            }

            while (!_endOfStream && NeedsMoreDecoded())
            {
                LogFill("read");
                var item = await source.ReadAsync(cancellation).ConfigureAwait(false);
                if (item is not { } decoded)
                {
                    if (source.WaitingForData)
                        break; // not the end: script has not appended this far yet

                    output?.FlushStretcher();
                    _renderer?.MarkEndOfStream();
                    _video?.MarkEndOfStream();
                    _endOfStream = true;
                    break;
                }

                if (decoded.Audio is { } block)
                {
                    if (output is null)
                        block.Dispose();
                    else
                        output.Emit(block);
                }
                else if (decoded.Video is { } frame)
                {
                    AcceptVideo(frame);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is MediaFormatException or MediaDecoderException or MediaLimitExceededException or MediaProcessLostException)
        {
            LogLimit(ex);
            Fail(ex);
        }
    }

    /// <summary>
    /// Keep decoding while the audio renderer wants more, or the video queue has room, but
    /// never while a picture is waiting for a slot, and never past twice the audio look-ahead
    /// (a video-heavy interleave must not pile up audio without bound).
    /// </summary>
    private string? _lastFill;

    private void LogFill(string why)
    {
        if (!_context.Log.IsEnabled(MediaLogLevel.Debug))
            return;
        var state = $"{why} audioQueued={_renderer?.QueuedDuration.ToString() ?? "-"} consumed={_renderer?.BlocksConsumed ?? 0} video={_video?.QueuedCount ?? -1} full={_video?.IsFull ?? false} held={_heldVideo is not null} eos={_endOfStream} waiting={_decodeSource is MseDecodeSource m && m.WaitingForData}";
        if (state == _lastFill)
            return;
        _lastFill = state;
        _context.Log.Emit(_context.Player, MediaEventKind.Buffering, MediaLogLevel.Debug, state);
    }

    private bool NeedsMoreDecoded()
    {
        if (_heldVideo is not null)
            return false;
        bool audioWants = _renderer is not null && _renderer.QueuedDuration < _services.DecodeAhead;
        bool videoWants = !_videoDecodeOff && _video is { IsFull: false };
        bool audioTooFar = _renderer is not null && _renderer.QueuedDuration >= _services.DecodeAhead + _services.DecodeAhead;
        // A video decoder with a deep pipeline (the OS H.264 decoder holds several
        // frames) may have shown nothing yet when the audio look-ahead is full: keep
        // feeding it until the first picture is out, or the element never gets past
        // HAVE_CURRENT_DATA while paused.
        if (audioTooFar && !_videoDecodeOff && _video is { QueuedCount: 0, HasCurrent: false, IsFull: false })
            audioTooFar = false;
        return (audioWants || videoWants) && !audioTooFar;
    }

    /// <summary>
    /// Queues a decoded picture, first dropping what an accurate seek (§4.8.11.9) must not
    /// show: pictures that end at or before the target. The picture that contains the
    /// target is kept, so the element shows the right frame the moment the seek lands.
    /// </summary>
    private void AcceptVideo(VideoFrame frame)
    {
        var video = _video;
        if (video is null)
        {
            frame.Dispose();
            return;
        }

        if (_videoTrimBefore is { } trim)
        {
            var end = frame.Timestamp + frame.Duration;
            if (end <= trim && frame.Duration > MediaTime.Zero)
            {
                frame.Dispose();
                return;
            }

            _videoTrimBefore = null;
        }

        if (video.IsFull)
            _heldVideo = frame;
        else
            video.Enqueue(frame);
    }

    /// <summary>
    /// Picks the picture for the clock (§2.4) and publishes it. While paused the first
    /// queued picture stands in for the current position: after a seek, or as the first
    /// frame when there is no poster (§4.8.9).
    /// </summary>
    private void PresentVideo()
    {
        var video = _video;
        if (video is null || _failed)
            return;

        VideoFrame? frame;
        if (_outputRunning)
        {
            // §2.4: the picture for the time the next tick will show, not for now, so a
            // picture due between two ticks is presented early rather than skipped late.
            var horizon = MediaTime.FromMicroseconds((long)(_services.FrameInterval.TotalMilliseconds * 1000 * _rate));
            frame = video.Select(_clock!.CurrentTime + horizon);
        }
        else if (_presentFirstFrame)
        {
            frame = video.SelectFirst();
        }
        else
        {
            return;
        }

        if (frame is null)
            return;

        _presentFirstFrame = false;
        Presenter.Publish(frame);

        // HTML §4.8.12.5: the intrinsic size follows the picture shown, and a change to it
        // (a MediaSource config change, say) fires resize on the element.
        if (frame.Width != _presentedWidth || frame.Height != _presentedHeight)
        {
            _presentedWidth = frame.Width;
            _presentedHeight = frame.Height;
            int width = frame.Width;
            int height = frame.Height;
            Report(() => _client.VideoSizeChanged(width, height));
        }
    }

    /// <summary>
    /// The background policy of design §5: while nothing is showing the pictures of a
    /// resource that also has audio, the video track is not decoded and its pictures are
    /// released - the audio clock keeps the element playing and time marching on. A
    /// resource without audio keeps decoding, because its pictures are what ends playback.
    /// When it comes back into view the pictures are caught up to the clock.
    /// </summary>
    private async ValueTask ApplyVideoVisibilityAsync(CancellationToken cancellation)
    {
        bool off = !_videoVisible && _video is not null && _renderer is not null;
        if (off == _videoDecodeOff || _decodeSource is null)
            return;

        _videoDecodeOff = off;
        await _decodeSource.SetVideoDecodeEnabledAsync(!off, cancellation).ConfigureAwait(false);
        if (off)
        {
            _heldVideo?.Dispose();
            _heldVideo = null;
            _video!.Flush();
            _videoTrimBefore = null;
        }
        else
        {
            // A paused element shows the picture at its position again; a playing one
            // catches up to the clock rather than resuming a group of pictures behind it.
            if (!_potentiallyPlaying)
                _presentFirstFrame = true;
            if (_clock is not null && !_pendingSeek.HasValue)
                _pendingResync = _clock.CurrentTime;
        }

        UpdateOutputState();
        _context.Log.Emit(_context.Player, MediaEventKind.Buffering, MediaLogLevel.Info,
            off ? "Hidden: video decoding stopped, audio continues." : "Shown: video decoding resumed.",
            ("video", off ? "background" : "foreground"));
    }

    private void UpdateOutputState()
    {
        bool wantRunning = _potentiallyPlaying && !_failed && !IsPlayedOut();
        if (wantRunning && !_outputRunning)
        {
            _output?.Start();
            _monotonic?.Start();
            _outputRunning = true;
        }
        else if (!wantRunning && _outputRunning)
        {
            _output?.Stop();
            _monotonic?.Pause();
            _outputRunning = false;
        }

        // The fine timer period costs power across the whole machine: hold it only while
        // pictures are actually being selected for the compositor.
        bool wantTimer = _outputRunning && _video is not null && !_videoDecodeOff;
        if (wantTimer && !_timerHeld)
        {
            PresentationTimer.Acquire();
            _timerHeld = true;
        }
        else if (!wantTimer && _timerHeld)
        {
            PresentationTimer.Release();
            _timerHeld = false;
        }
    }

    /// <summary>Everything decoded has been played: the audio drained, or without audio the last picture's time has passed.</summary>
    private bool IsPlayedOut()
    {
        if (!_endOfStream)
            return false;
        if (_renderer is not null)
            return _renderer.IsDrained;
        return _video is null || _video.IsDrained(_clock!.CurrentTime);
    }

    private void ReportReadiness()
    {
        MediaReadyState state = MediaReadyState.HaveEnoughData;
        if (_renderer is { } renderer)
            state = AudioReadiness(renderer);
        // While the background policy holds video decoding off there are no pictures to
        // judge, and the audio alone says how much data the element has.
        if (_video is { } video && !_videoDecodeOff)
        {
            var videoState = VideoReadiness(video);
            if (videoState < state)
                state = videoState;

            // With the picture queue full nothing more can be decoded until playback
            // consumes it, and the whole resource is already here: by §4.8.11.7 that is
            // HAVE_ENOUGH_DATA, whatever the audio look-ahead has reached. Otherwise a
            // paused element with autoplay would wait for audio that cannot arrive.
            if ((video.IsFull || _heldVideo is not null) && state >= MediaReadyState.HaveCurrentData)
                state = MediaReadyState.HaveEnoughData;
        }

        // HTML §4.8.11.7: with the whole resource in hand (an ended MediaSource) nothing
        // can stall playback, so future data is enough data.
        if (_wholeResourceAvailable && state == MediaReadyState.HaveFutureData)
            state = MediaReadyState.HaveEnoughData;

        // Once data has been seen the state never falls back below current data except
        // through a seek, which resets it explicitly; a momentary dip is not a stall.
        if (state < _readyState && state >= MediaReadyState.HaveCurrentData)
            return;
        if (state == _readyState)
            return;

        _readyState = state;
        Report(() => _client.ReadyStateChanged(state));
    }

    private MediaReadyState AudioReadiness(AudioRenderer renderer)
    {
        var queued = renderer.QueuedDuration;
        if (_endOfStream || queued >= _services.DecodeAhead)
            return MediaReadyState.HaveEnoughData;
        if (queued >= _services.FutureDataThreshold)
            return MediaReadyState.HaveFutureData;
        if (queued > MediaTime.Zero || renderer.BlocksConsumed > 0)
            return MediaReadyState.HaveCurrentData;
        return MediaReadyState.HaveMetadata;
    }

    /// <summary>§4.8.11.7 for pictures: the current one is current data; a full queue (or the end) is enough.</summary>
    private MediaReadyState VideoReadiness(VideoRenderer video)
    {
        if (_endOfStream || video.IsFull || _heldVideo is not null)
            return MediaReadyState.HaveEnoughData;
        if (video.QueuedCount >= 2)
            return MediaReadyState.HaveFutureData;
        if (video.QueuedCount > 0 || video.HasCurrent)
            return MediaReadyState.HaveCurrentData;
        return MediaReadyState.HaveMetadata;
    }

    private void ReportPosition()
    {
        var clock = _clock!;
        if (IsPlayedOut() && !_endReported && (_outputRunning || _potentiallyPlaying))
        {
            _endReported = true;
            var end = _duration.IsInfinite ? clock.CurrentTime : _duration;
            Report(() =>
            {
                _client.PositionChanged(end, monotonic: true);
                _client.ReachedEnd();
            });
            UpdateOutputState();
            return;
        }

        if (!_outputRunning)
            return;

        long now = _services.TimeProvider.GetTimestamp();
        if (_lastPositionTick != 0 && _services.TimeProvider.GetElapsedTime(_lastPositionTick, now) < _services.PositionInterval)
            return;
        _lastPositionTick = now;
        var position = clock.CurrentTime;
        if (!_duration.IsInfinite && position > _duration)
            position = _duration;
        Report(() => _client.PositionChanged(position, monotonic: true));
    }

    /// <summary>
    /// Repositions the pipeline at <paramref name="target"/>. With <paramref name="report"/>
    /// the element hears a seek; without it this is an internal rebuild of the queue at the
    /// same position (a rate or pitch-mode change) and the element sees nothing.
    /// </summary>
    private async ValueTask SeekCoreAsync(MediaTime target, CancellationToken cancellation, bool report)
    {
        if (!_duration.IsInfinite && target > _duration)
            target = _duration;
        if (target < MediaTime.Zero)
            target = MediaTime.Zero;

        _context.Log.Emit(_context.Player, MediaEventKind.SeekStart, MediaLogLevel.Debug, $"Seek to {target}.", ("target", target.ToString()));
        _blocksDeliveredSinceSeek = 0;
        _picturesPresentedAtSeek = _video?.PresentedFrames ?? 0;
        _renderer?.Flush(target);
        _stretcher?.Flush();
        _monotonic?.SetTime(target);
        _trimBefore = target;
        if (_video is not null)
        {
            _video.Flush();
            _heldVideo?.Dispose();
            _heldVideo = null;
            _videoTrimBefore = target;
            _presentFirstFrame = true;
        }

        _endOfStream = false;
        _endReported = false;
        try
        {
            await _decodeSource!.SeekAsync(target, cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MediaFormatException or MediaDecoderException or MediaLimitExceededException or MediaProcessLostException)
        {
            LogLimit(ex);
            Fail(ex);
            return;
        }

        if (report)
        {
            _readyState = MediaReadyState.HaveMetadata;
            Report(() => _client.ReadyStateChanged(MediaReadyState.HaveMetadata));
        }

        _seekAwaitingData = null;
        await FillAheadAsync(cancellation).ConfigureAwait(false);
        if (_failed)
            return;
        PresentVideo();
        if (!report)
            return;

        // HTML §4.8.11.9 step 12: the seek waits until the data for the new position is
        // there. A MediaSource may not have it yet (a seek past what script appended):
        // the seek stays open until an append brings it or endOfStream() settles it.
        if (!_endOfStream && _decodeSource is MseDecodeSource { WaitingForData: true } && !HasDecodedData())
        {
            _seekAwaitingData = target;
            _context.Log.Emit(_context.Player, MediaEventKind.Stall, MediaLogLevel.Debug, $"Seek to {target} waits for data.", ("target", target.ToString()));
            return;
        }

        // HTML §4.8.11.9 step 12 waits for the data, so the readiness the decoded data
        // justifies is reported ahead of the completion; the element applies it as the
        // seek completes, so a script asking readyState in its seeked handler sees at
        // least HAVE_CURRENT_DATA while playing still follows seeked
        // (mediasource-buffered-seek, mediasource-seek-during-pending-seek).
        ReportReadiness();
        CompleteSeek(target);
    }

    /// <summary>Anything decoded for the position since the last seek, queued or already shown (media thread counts, so the device thread's late drop of a flushed block cannot fake it).</summary>
    private bool HasDecodedData() =>
        _blocksDeliveredSinceSeek > 0
        || (_video is { } video && (video.QueuedCount > 0 || video.PresentedFrames > _picturesPresentedAtSeek));

    private long _blocksDeliveredSinceSeek;
    private long _picturesPresentedAtSeek;

    private void CompleteSeek(MediaTime target)
    {
        _seekAwaitingData = null;
        // A seek that ended up past everything on an ended MediaSource lands at the end:
        // the duration endOfStream() just set, which may not have reached this task yet.
        var limit = _duration;
        if (_mediaSource is { } model)
        {
            lock (model.Gate)
                limit = model.Duration ?? limit;
            _duration = limit;
        }

        var landed = !limit.IsInfinite && target > limit ? limit : target;
        _context.Log.Emit(_context.Player, MediaEventKind.SeekEnd, MediaLogLevel.Debug, $"Seek landed at {landed}.", ("position", landed.ToString()));
        Report(() =>
        {
            _client.SeekCompleted(landed);
            _client.PositionChanged(landed, monotonic: false);
        });
        _lastPositionTick = 0;
    }

    private void Fail(Exception ex)
    {
        if (_failed)
            return;
        _failed = true;
        var failure = ex is MediaFormatException or MediaLimitExceededException or MediaDecoderException or MediaProcessLostException
            ? MediaResourceFailure.Decode
            : MediaResourceFailure.Network;
        _context.Log.Emit(_context.Player, MediaEventKind.Error, MediaLogLevel.Error, ex.Message, ("reason", ex.GetType().Name));
        string message = ex.Message;
        Report(() => _client.Failed(failure, message));
        if (_outputRunning)
        {
            _output?.Stop();
            _outputRunning = false;
        }
    }

    private void LogLimit(Exception ex)
    {
        if (ex is MediaLimitExceededException limit)
        {
            _context.Log.Emit(_context.Player, MediaEventKind.LimitExceeded, MediaLogLevel.Warn, limit.Message,
                ("limit", limit.Limit), ("value", limit.Value), ("maximum", limit.Maximum));
        }
    }

    private void Report(Action report)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        try
        {
            _post(report);
        }
        catch (Exception ex)
        {
            _context.Log.Emit(_context.Player, MediaEventKind.Error, MediaLogLevel.Warn, $"Could not report to the element: {ex.Message}", ("reason", ex.GetType().Name));
        }
    }

    private async ValueTask TearDownAsync()
    {
        try
        {
            if (_output is not null)
            {
                _output.Stop();
                await _output.DisposeAsync().ConfigureAwait(false);
            }

            if (_timerHeld)
                PresentationTimer.Release();
            _timerHeld = false;
            _outputRunning = false;
            _renderer?.Dispose();
            _heldVideo?.Dispose();
            _video?.Dispose();
            Presenter.Clear();
            if (_decodeSource is not null)
                await _decodeSource.DisposeAsync().ConfigureAwait(false);
            else if (_source is not null)
                await _source.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _context.Log.Emit(_context.Player, MediaEventKind.Error, MediaLogLevel.Warn, $"Teardown failed: {ex.Message}", ("reason", ex.GetType().Name));
        }
        finally
        {
            _context.Log.Emit(_context.Player, MediaEventKind.PlayerDestroyed, MediaLogLevel.Debug, "player destroyed",
                ("underruns", (_output?.Underruns ?? 0).ToString(CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>Lets the output open before the renderer exists; the device sees silence until then.</summary>
    private sealed class RenderRelay : IAudioRenderCallback
    {
        public AudioRenderer? Target;

        public int Render(Span<float> destination, int channels)
        {
            var target = Volatile.Read(ref Target);
            if (target is null)
            {
                destination.Clear();
                return 0;
            }

            return target.Render(destination, channels);
        }
    }

    /// <summary>
    /// Hands decoded blocks to the renderer, first dropping what lies before the seek
    /// target: containers resume at a frame or page boundary before the target, and an
    /// accurate seek (§4.8.11.9) must not play that part.
    /// </summary>
    private sealed class RendererOutput(AudioRenderer renderer, MediaPlayer owner) : IDecodeOutput<AudioBlock>
    {
        public void Emit(AudioBlock item)
        {
            var trimBefore = owner._trimBefore;
            if (trimBefore is null || item.Timestamp >= trimBefore.Value)
            {
                owner._trimBefore = null;
                Deliver(item);
                return;
            }

            if (item.EndTime <= trimBefore.Value)
            {
                item.Dispose();
                return;
            }

            long skipFrames = (trimBefore.Value - item.Timestamp).ToTimescale(item.SampleRate);
            skipFrames = Math.Clamp(skipFrames, 0, item.FrameCount);
            int keep = item.FrameCount - (int)skipFrames;
            if (keep <= 0)
            {
                item.Dispose();
                return;
            }

            var trimmed = AudioBlock.Allocate(owner._context.Limits, item.SampleRate, item.Channels, keep, trimBefore.Value);
            item.Samples[(int)(skipFrames * item.Channels)..].CopyTo(trimmed.Samples);
            item.Dispose();
            owner._trimBefore = null;
            Deliver(trimmed);
        }

        /// <summary>Straight to the renderer, or through the time stretcher when pitch is preserved at a rate other than 1.</summary>
        private void Deliver(AudioBlock block)
        {
            owner._blocksDeliveredSinceSeek++;
            if (!renderer.PitchPreserved)
            {
                renderer.Enqueue(block);
                return;
            }

            var stretcher = owner._stretcher;
            bool formatChanged = block.SampleRate != owner._stretcherRate || block.Channels != owner._stretcherChannels;
            if (stretcher is null || (formatChanged && stretcher.BufferedFrames == 0))
            {
                stretcher = new TimeStretcher(block.SampleRate, block.Channels) { Rate = owner._rate };
                owner._stretcher = stretcher;
                owner._stretcherRate = block.SampleRate;
                owner._stretcherChannels = block.Channels;
                formatChanged = false;
            }

            if (formatChanged)
            {
                // A format change mid-stream: play what is stretched, then continue plain.
                FlushStretcher();
                renderer.Enqueue(block);
                return;
            }

            stretcher.Push(block);
            while (stretcher.Pull(owner._context.Limits) is { } stretched)
                renderer.Enqueue(stretched);
        }

        /// <summary>At the end of the stream the stretcher's remaining input plays as it is.</summary>
        public void FlushStretcher()
        {
            var stretcher = owner._stretcher;
            if (stretcher is null)
                return;
            while (stretcher.Pull(owner._context.Limits, flush: true) is { } stretched)
                renderer.Enqueue(stretched);
        }
    }
}
