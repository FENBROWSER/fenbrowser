using System.Collections.Concurrent;
using System.Diagnostics;
using FenBrowser.Media.Audio;
using FenBrowser.Media.Clock;

namespace FenBrowser.Media.WebAudio;

/// <summary>
/// WA 1.20 MediaElementAudioSourceNode (design WA-D8): an element's decoded audio routed
/// into a Web Audio graph instead of the device. The element's player opens an "output"
/// whose id names a tap; that output runs on its own clock at real-time pace and writes
/// what the player renders into the tap's pipe, which the source node reads.
/// </summary>
public static class AudioTapRegistry
{
    /// <summary>The prefix of an output id that names a tap rather than a device.</summary>
    public const string Prefix = "webaudio-tap:";

    private static readonly ConcurrentDictionary<string, AudioTrackPipe> s_taps = new(StringComparer.Ordinal);

    /// <summary>Registers a pipe and returns the output id that reaches it.</summary>
    public static string Register(AudioTrackPipe pipe)
    {
        string id = Prefix + Guid.NewGuid().ToString("N");
        s_taps[id] = pipe;
        return id;
    }

    public static void Unregister(string id) => s_taps.TryRemove(id, out _);

    public static bool TryGet(string id, out AudioTrackPipe pipe) => s_taps.TryGetValue(id, out pipe!);
}

/// <summary>An output factory that also understands tap ids, deferring every real device to the one it wraps.</summary>
public sealed class TapAwareAudioOutputFactory : IAudioOutputFactory
{
    private readonly IAudioOutputFactory _inner;

    public TapAwareAudioOutputFactory(IAudioOutputFactory inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public IAudioOutput Create() => _inner.Create();

    public IReadOnlyList<AudioOutputDevice> Devices => _inner.Devices;

    public IAudioOutput? Create(string deviceId)
    {
        if (deviceId is not null && deviceId.StartsWith(AudioTapRegistry.Prefix, StringComparison.Ordinal))
            return AudioTapRegistry.TryGet(deviceId, out var pipe) ? new PipeAudioOutput(pipe) : null;
        return _inner.Create(deviceId!);
    }
}

/// <summary>
/// An <see cref="IAudioOutput"/> that is a pipe. The graph reading the pipe pulls the
/// player's callback on its own rendering thread, so the element's audio arrives in step with
/// the context. While no graph pulls (the context is suspended, or not yet running) a thread
/// paced to real time takes the audio instead, so the element's clock keeps running as it
/// would on a device. Its position is the frames it has taken, whichever took them.
/// </summary>
public sealed class PipeAudioOutput : IAudioOutput, IAudioPlaybackPosition
{
    private const int Block = 128;
    private static readonly long PullGrace = Stopwatch.Frequency / 10;

    private readonly AudioTrackPipe _pipe;
    private readonly object _renderGate = new();
    private readonly float[] _interleaved;
    private readonly AudioBus _bus = new(WebAudioLimits.MaxChannels, Block);
    private IAudioRenderCallback? _callback;
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _disposed;
    private long _framesPlayed;
    private long _lastPull;

    public PipeAudioOutput(AudioTrackPipe pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        _pipe = pipe;
        _interleaved = new float[Block * pipe.Channels];
    }

    public string Name => "webaudio-tap";

    public IAudioPlaybackPosition Position => this;

    public long Underruns { get; private set; }

    public int SampleRate => (int)_pipe.SampleRate;

    public long FramesPlayed => Interlocked.Read(ref _framesPlayed);

    public ValueTask<AudioStreamFormat> OpenAsync(AudioStreamFormat requested, IAudioRenderCallback callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _callback = callback;
        _pipe.Producer = Pull;
        return ValueTask.FromResult(new AudioStreamFormat((int)_pipe.SampleRate, _pipe.Channels));
    }

    public void Start()
    {
        if (_running || _disposed)
            return;
        _running = true;
        _thread ??= StartThread();
    }

    public void Stop() => _running = false;

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _running = false;
        if (_pipe.Producer == Pull)
            _pipe.Producer = null;
        return ValueTask.CompletedTask;
    }

    // The graph's rendering thread: at least `frames` more, now.
    private void Pull(int frames)
    {
        Volatile.Write(ref _lastPull, Stopwatch.GetTimestamp());
        lock (_renderGate)
        {
            for (int done = 0; done < frames; done += Block)
                RenderBlock();
        }
    }

    private Thread StartThread()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "webaudio-tap" };
        thread.Start();
        return thread;
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        long taken = 0;
        double rate = _pipe.SampleRate;
        while (!_disposed)
        {
            bool pulled = Stopwatch.GetTimestamp() - Volatile.Read(ref _lastPull) < PullGrace;
            if (!_running || _callback is null || pulled)
            {
                Thread.Sleep(5);
                clock.Restart();
                taken = 0;
                continue;
            }

            if (taken + Block > clock.Elapsed.TotalSeconds * rate)
            {
                Thread.Sleep(1);
                continue;
            }

            lock (_renderGate)
                RenderBlock();
            taken += Block;
        }
    }

    // One block from the player into the pipe; silence while the element is not playing.
    private void RenderBlock()
    {
        int channels = _pipe.Channels;
        var interleaved = _interleaved;
        if (_running && _callback is { } callback)
        {
            int written = callback.Render(interleaved, channels);
            if (written < Block)
            {
                Underruns++;
                interleaved.AsSpan(written * channels).Clear();
            }

            Interlocked.Add(ref _framesPlayed, Block);
        }
        else
        {
            interleaved.AsSpan().Clear();
        }

        _bus.Reset(channels);
        for (int c = 0; c < channels; c++)
        {
            var channel = _bus.Channel(c);
            for (int i = 0; i < Block; i++)
                channel[i] = interleaved[i * channels + c];
        }

        _bus.MarkNotSilent();
        _pipe.Write(_bus);
    }
}
