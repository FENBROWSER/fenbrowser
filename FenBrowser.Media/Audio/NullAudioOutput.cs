using FenBrowser.Media.Clock;

namespace FenBrowser.Media.Audio;

/// <summary>
/// An audio output with no device: it consumes samples at wall-clock speed on a timer
/// (the ADR-0003 fallback and <c>fenplay play --null-sink</c>) or only when a test pumps
/// it. Either way it reports a played position, so the master clock works as it would on
/// hardware.
/// </summary>
public sealed class NullAudioOutput : IAudioOutput, IAudioPlaybackPosition
{
    // A device-sized period: 10 ms at 48 kHz.
    private const int PeriodFrames = 480;

    private readonly TimeProvider _timeProvider;
    private readonly bool _realtime;
    private readonly Lock _gate = new();
    private float[] _buffer = [];
    private IAudioRenderCallback? _callback;
    private AudioStreamFormat _format;
    private ITimer? _timer;
    private long _framesPlayed;
    private long _lastTick;
    private long _underruns;
    private bool _running;
    private bool _disposed;

    public NullAudioOutput(TimeProvider? timeProvider = null, bool realtime = true)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _realtime = realtime;
    }

    public string Name => "null";

    public IAudioPlaybackPosition Position => this;

    public long Underruns => Interlocked.Read(ref _underruns);

    public int SampleRate => _format.SampleRate;

    public long FramesPlayed => Interlocked.Read(ref _framesPlayed);

    public AudioStreamFormat Format => _format;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _running;
        }
    }

    public ValueTask<AudioStreamFormat> OpenAsync(AudioStreamFormat requested, IAudioRenderCallback callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (requested.SampleRate <= 0 || requested.Channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(requested), requested, "The format must have a positive rate and channel count.");

        lock (_gate)
        {
            _format = requested;
            _callback = callback;
            _buffer = new float[PeriodFrames * 4 * requested.Channels];
        }

        return ValueTask.FromResult(requested);
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running || _callback is null)
                return;
            _running = true;
            _lastTick = _timeProvider.GetTimestamp();
            if (_realtime)
                _timer ??= _timeProvider.CreateTimer(_ => Tick(), null, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10));
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running)
                return;
            // Consume what elapsed so far, then hold.
            if (_realtime)
                PullElapsedLocked();
            _running = false;
        }
    }

    /// <summary>Test hook: consumes exactly <paramref name="frames"/> frames as if the device had played them.</summary>
    public void Pump(int frames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frames);
        lock (_gate)
        {
            if (!_running)
                return;
            PullLocked(frames);
        }
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_running)
                PullElapsedLocked();
        }
    }

    private void PullElapsedLocked()
    {
        long now = _timeProvider.GetTimestamp();
        double seconds = _timeProvider.GetElapsedTime(_lastTick, now).TotalSeconds;
        _lastTick = now;
        // Bound a long stall (debugger, sleep) to a quarter second so we do not
        // demand a burst of audio the pipeline could never have prepared.
        int frames = (int)Math.Min(_format.SampleRate / 4, Math.Round(seconds * _format.SampleRate));
        if (frames > 0)
            PullLocked(frames);
    }

    private void PullLocked(int frames)
    {
        var callback = _callback!;
        int channels = _format.Channels;
        while (frames > 0)
        {
            int chunk = Math.Min(frames, _buffer.Length / channels);
            var destination = _buffer.AsSpan(0, chunk * channels);
            int written = callback.Render(destination, channels);
            if (written < chunk)
                Interlocked.Increment(ref _underruns);
            Interlocked.Add(ref _framesPlayed, chunk);
            frames -= chunk;
        }
    }

    public ValueTask DisposeAsync()
    {
        ITimer? timer;
        lock (_gate)
        {
            _disposed = true;
            _running = false;
            timer = _timer;
            _timer = null;
        }

        return timer?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}

public sealed class NullAudioOutputFactory : IAudioOutputFactory
{
    public static readonly NullAudioOutputFactory Realtime = new(realtime: true);

    private readonly bool _realtime;
    private readonly TimeProvider? _timeProvider;

    public NullAudioOutputFactory(bool realtime, TimeProvider? timeProvider = null)
    {
        _realtime = realtime;
        _timeProvider = timeProvider;
    }

    public IAudioOutput Create() => new NullAudioOutput(_timeProvider, _realtime);
}
