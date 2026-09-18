using System.Collections.Concurrent;
using System.Globalization;
using FenBrowser.Media.Audio;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Clock;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Element;
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
    public TimeSpan FrameInterval { get; init; } = TimeSpan.FromMilliseconds(8);

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
    private readonly IByteSource _source;
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
    private MediaTime _duration = MediaTime.PositiveInfinity;
    private bool _endOfStream;
    private bool _endReported;
    private bool _potentiallyPlaying;
    private bool _outputRunning;
    private bool _preservesPitch = true;
    private double _rate = 1.0;
    private TimeStretcher? _stretcher;
    private int _stretcherRate;
    private int _stretcherChannels;
    private MediaReadyState _readyState = MediaReadyState.HaveNothing;
    private MediaTime? _pendingSeek;
    private MediaTime? _pendingResync;
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _lifetime.Cancel();
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
        _commands.Enqueue(command);
        _wake.Release();
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
                UpdateOutputState();
                PresentVideo();
                ReportReadiness();
                ReportPosition();

                if (_failed)
                    return;

                var wait = !_outputRunning ? TimeSpan.FromMilliseconds(250)
                    : _video is not null ? _services.FrameInterval
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
        var factory = _services.DecodeSources ?? new LocalMediaDecodeSourceFactory(_services.Demuxers, _services.Decoders);
        _decodeSource = factory.Create(_source, _declaredMime, _context);

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
        }

        _duration = info.Duration;
        var tracks = info.Tracks;
        var duration = _duration;
        Report(() =>
        {
            _client.MetadataAvailable(new MediaResourceMetadata(duration, videoWidth, videoHeight, tracks));
            var whole = duration.IsInfinite ? MediaTimeRanges.Empty : MediaTimeRanges.Single(MediaTime.Zero, duration);
            if (info.IsSeekable)
                _client.SeekableChanged(whole);
            _client.BufferedChanged(whole);
            _client.FetchedEntirely();
        });
        _readyState = MediaReadyState.HaveMetadata;
        return true;
    }

    private async ValueTask FillAheadAsync(CancellationToken cancellation)
    {
        var source = _decodeSource!;
        var output = _renderer is null ? null : new RendererOutput(_renderer, this);

        try
        {
            if (_heldVideo is not null && _video is { IsFull: false })
            {
                _video.Enqueue(_heldVideo);
                _heldVideo = null;
            }

            while (!_endOfStream && NeedsMoreDecoded())
            {
                var item = await source.ReadAsync(cancellation).ConfigureAwait(false);
                if (item is not { } decoded)
                {
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
    private bool NeedsMoreDecoded()
    {
        if (_heldVideo is not null)
            return false;
        bool audioWants = _renderer is not null && _renderer.QueuedDuration < _services.DecodeAhead;
        bool videoWants = _video is { IsFull: false };
        bool audioTooFar = _renderer is not null && _renderer.QueuedDuration >= _services.DecodeAhead + _services.DecodeAhead;
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
        bool changed;
        if (_outputRunning)
        {
            changed = video.Select(_clock!.CurrentTime, out frame);
        }
        else if (_presentFirstFrame)
        {
            changed = video.SelectFirst(out frame);
        }
        else
        {
            return;
        }

        if (!changed || frame is null)
            return;

        _presentFirstFrame = false;
        Presenter.Publish(frame, _context.Limits);
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
        if (_video is { } video)
        {
            var videoState = VideoReadiness(video);
            if (videoState < state)
                state = videoState;
        }

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
        if (video.QueuedCount > 0 || video.Current is not null)
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

        await FillAheadAsync(cancellation).ConfigureAwait(false);
        if (_failed)
            return;
        PresentVideo();
        if (!report)
            return;

        var landed = target;
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

            _renderer?.Dispose();
            _heldVideo?.Dispose();
            _video?.Dispose();
            Presenter.Clear();
            if (_decodeSource is not null)
                await _decodeSource.DisposeAsync().ConfigureAwait(false);
            else
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
