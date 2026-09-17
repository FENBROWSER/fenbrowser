using System.Collections.Concurrent;
using System.Globalization;
using FenBrowser.Media.Audio;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Clock;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Element;

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
}

/// <summary>
/// One player: the media session of design §2.2 running the §2.3 pipeline for an audio
/// resource. It owns the demuxer, the decoder, the audio renderer and the output stream,
/// drives them from a single media task, and reports to the element through
/// <see cref="IMediaResourceClient"/> on the element's thread.
/// </summary>
/// <remarks>
/// Every public member is safe to call from the element thread: they post commands to the
/// media task. The media task is the only thing that touches the demuxer and decoder. The
/// audio device pulls from the renderer on its own thread and the master clock reads the
/// device position, so <c>currentTime</c> is what is audible, not what was decoded.
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
    private IDemuxer? _demuxer;
    private IMediaDecoder<AudioBlock>? _decoder;
    private IAudioOutput? _output;
    private AudioRenderer? _renderer;
    private AudioMasterClock? _clock;
    private MediaTrackInfo? _track;
    private MediaTime _duration = MediaTime.PositiveInfinity;
    private bool _endOfStream;
    private bool _endReported;
    private bool _potentiallyPlaying;
    private bool _outputRunning;
    private bool _preservesPitch = true;
    private MediaReadyState _readyState = MediaReadyState.HaveNothing;
    private MediaTime? _pendingSeek;
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
        MediaPipelineContext context)
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
    }

    public PlayerId Player => _context.Player;

    /// <summary>The device position clock, once the pipeline is up (tests read it).</summary>
    public IMediaClock? Clock => _clock;

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
            if (_renderer is not null)
            {
                _renderer.Rate = playbackRate > 0 ? playbackRate : 1.0;
                _renderer.Volume = effectiveVolume;
                _renderer.Muted = effectiveVolume <= 0;
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
                    await SeekCoreAsync(seekTarget, cancellation).ConfigureAwait(false);
                }

                await FillAheadAsync(cancellation).ConfigureAwait(false);
                UpdateOutputState();
                ReportReadiness();
                ReportPosition();

                if (_failed)
                    return;

                var wait = _outputRunning ? _services.PositionInterval : TimeSpan.FromMilliseconds(250);
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
        // Sniff (MIME Sniffing §6.2 resource header) and choose the container.
        var header = new byte[Sniffing.MediaSniffer.ResourceHeaderLength];
        int headerLength = await _source.ReadAtLeastAsync(0, header, cancellation).ConfigureAwait(false);
        var factory = _services.Demuxers.Select(header.AsSpan(0, headerLength), _declaredMime, _context);
        if (factory is null)
        {
            Report(() => _client.Failed(MediaResourceFailure.Unsupported, "No demuxer recognises the resource."));
            return false;
        }

        DemuxerInfo info;
        try
        {
            _demuxer = factory.Create(_source, _context);
            info = await _demuxer.InitializeAsync(cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MediaFormatException or MediaLimitExceededException)
        {
            LogLimit(ex);
            Report(() => _client.Failed(MediaResourceFailure.Unsupported, $"The {factory.Name} container could not be read: {ex.Message}"));
            return false;
        }

        _track = info.Tracks.FirstOrDefault(t => t.Kind == MediaTrackKind.Audio);
        if (_track is null)
        {
            Report(() => _client.Failed(MediaResourceFailure.Unsupported, "The resource has no audio track; video playback is not available yet."));
            return false;
        }

        var candidates = _services.Decoders.GetAudioCandidates(_track.Config);
        _decoder = await DecoderSelector.SelectAsync(candidates, _track.Config, _context, cancellation).ConfigureAwait(false);
        if (_decoder is null)
        {
            Report(() => _client.Failed(MediaResourceFailure.Unsupported, $"No decoder for {_track.Config.Codec}."));
            return false;
        }

        // The device decides the format the renderer produces; the clock follows the device.
        _output = _services.AudioOutputs.Create();
        var format = await _output.OpenAsync(new AudioStreamFormat(_track.Config.SampleRate, _track.Config.Channels), _relay, cancellation).ConfigureAwait(false);
        _clock = new AudioMasterClock(_output.Position);
        _renderer = new AudioRenderer(format, _clock);
        _relay.Target = _renderer;

        _duration = info.Duration;
        var tracks = info.Tracks;
        var duration = _duration;
        Report(() =>
        {
            _client.MetadataAvailable(new MediaResourceMetadata(duration, 0, 0, tracks));
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
        var renderer = _renderer!;
        var demuxer = _demuxer!;
        var decoder = _decoder!;
        var output = new RendererOutput(renderer);

        try
        {
            while (!_endOfStream && renderer.QueuedDuration < _services.DecodeAhead)
            {
                var packet = await demuxer.ReadPacketAsync(cancellation).ConfigureAwait(false);
                if (packet is null)
                {
                    await decoder.DrainAsync(output, cancellation).ConfigureAwait(false);
                    renderer.MarkEndOfStream();
                    _endOfStream = true;
                    break;
                }

                using (packet)
                {
                    if (packet.TrackId != _track!.Id)
                        continue;
                    await decoder.DecodeAsync(packet, output, cancellation).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is MediaFormatException or MediaDecoderException or MediaLimitExceededException)
        {
            LogLimit(ex);
            Fail(ex);
        }
    }

    private void UpdateOutputState()
    {
        var output = _output!;
        bool wantRunning = _potentiallyPlaying && !_failed && !(_endOfStream && _renderer!.IsDrained);
        if (wantRunning && !_outputRunning)
        {
            output.Start();
            _outputRunning = true;
        }
        else if (!wantRunning && _outputRunning)
        {
            output.Stop();
            _outputRunning = false;
        }
    }

    private void ReportReadiness()
    {
        var renderer = _renderer!;
        var queued = renderer.QueuedDuration;
        MediaReadyState state;
        if (_endOfStream)
            state = MediaReadyState.HaveEnoughData;
        else if (queued >= _services.DecodeAhead)
            state = MediaReadyState.HaveEnoughData;
        else if (queued >= _services.FutureDataThreshold)
            state = MediaReadyState.HaveFutureData;
        else if (queued > MediaTime.Zero || renderer.BlocksConsumed > 0)
            state = MediaReadyState.HaveCurrentData;
        else
            state = MediaReadyState.HaveMetadata;

        // Once data has been seen the state never falls back below current data except
        // through a seek, which resets it explicitly; a momentary dip is not a stall.
        if (state < _readyState && state >= MediaReadyState.HaveCurrentData)
            return;
        if (state == _readyState)
            return;

        _readyState = state;
        Report(() => _client.ReadyStateChanged(state));
    }

    private void ReportPosition()
    {
        var clock = _clock!;
        var renderer = _renderer!;
        if (_endOfStream && renderer.IsDrained && !_endReported && (_outputRunning || _potentiallyPlaying))
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
        Report(() => _client.PositionChanged(position, monotonic: true));
    }

    private async ValueTask SeekCoreAsync(MediaTime target, CancellationToken cancellation)
    {
        var renderer = _renderer!;
        if (!_duration.IsInfinite && target > _duration)
            target = _duration;
        if (target < MediaTime.Zero)
            target = MediaTime.Zero;

        _context.Log.Emit(_context.Player, MediaEventKind.SeekStart, MediaLogLevel.Debug, $"Seek to {target}.", ("target", target.ToString()));
        renderer.Flush(target);
        _endOfStream = false;
        _endReported = false;
        await _decoder!.ResetAsync(cancellation).ConfigureAwait(false);
        await _demuxer!.SeekAsync(target, cancellation).ConfigureAwait(false);
        _readyState = MediaReadyState.HaveMetadata;
        Report(() => _client.ReadyStateChanged(MediaReadyState.HaveMetadata));

        await FillAheadAsync(cancellation).ConfigureAwait(false);
        if (_failed)
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
        var failure = ex is MediaFormatException or MediaLimitExceededException or MediaDecoderException
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
            if (_decoder is not null)
                await _decoder.DisposeAsync().ConfigureAwait(false);
            if (_demuxer is not null)
                await _demuxer.DisposeAsync().ConfigureAwait(false);
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

    private sealed class RendererOutput(AudioRenderer renderer) : IDecodeOutput<AudioBlock>
    {
        public void Emit(AudioBlock item) => renderer.Enqueue(item);
    }
}
