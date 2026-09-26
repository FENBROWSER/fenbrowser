using FenBrowser.Media.Audio;

namespace FenBrowser.Media.WebAudio;

/// <summary>
/// One output stream shared by every AudioContext that plays to the same sink (design
/// WA-D6). Browsers do not give each context a device of its own: a page that makes a
/// hundred contexts must not open a hundred device streams and a hundred render threads.
/// The device pulls once; each running context's renderer adds its quantum-rendered,
/// rate-converted output into the sum. Nothing allocates in the callback once running.
/// </summary>
public sealed class WebAudioOutputMixer : IAudioRenderCallback, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IAudioOutput _output;
    private WebAudioDeviceRenderer[] _renderers = [];
    private float[] _scratch = new float[4096];
    private bool _started;

    private WebAudioOutputMixer(IAudioOutput output, AudioStreamFormat format)
    {
        _output = output;
        Format = format;
    }

    public AudioStreamFormat Format { get; }

    public long Underruns => _output.Underruns;

    public int Count => Volatile.Read(ref _renderers).Length;

    /// <summary>Opens <paramref name="output"/> with this mixer as its callback.</summary>
    public static WebAudioOutputMixer Open(IAudioOutput output, int preferredRate)
    {
        ArgumentNullException.ThrowIfNull(output);
        var relay = new Relay();
        var format = output.OpenAsync(new AudioStreamFormat(preferredRate, 2), relay, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var mixer = new WebAudioOutputMixer(output, format);
        relay.Target = mixer;
        return mixer;
    }

    public void Add(WebAudioDeviceRenderer renderer)
    {
        lock (_gate)
        {
            var next = new WebAudioDeviceRenderer[_renderers.Length + 1];
            Array.Copy(_renderers, next, _renderers.Length);
            next[^1] = renderer;
            Volatile.Write(ref _renderers, next);
            if (!_started)
            {
                _output.Start();
                _started = true;
            }
        }
    }

    /// <summary>Removes a context; returns true when none are left.</summary>
    public bool Remove(WebAudioDeviceRenderer renderer)
    {
        lock (_gate)
        {
            var next = _renderers.Where(r => !ReferenceEquals(r, renderer)).ToArray();
            Volatile.Write(ref _renderers, next);
            if (next.Length == 0 && _started)
            {
                _output.Stop();
                _started = false;
            }

            return next.Length == 0;
        }
    }

    public int Render(Span<float> destination, int channels)
    {
        destination.Clear();
        var renderers = Volatile.Read(ref _renderers);
        if (renderers.Length == 0)
            return destination.Length / channels;

        if (_scratch.Length < destination.Length)
            _scratch = new float[destination.Length];
        var scratch = _scratch.AsSpan(0, destination.Length);
        foreach (var renderer in renderers)
        {
            if (!renderer.Running)
                continue;
            renderer.Render(scratch, channels);
            for (int i = 0; i < destination.Length; i++)
                destination[i] += scratch[i];
        }

        return destination.Length / channels;
    }

    public async ValueTask DisposeAsync()
    {
        _output.Stop();
        await _output.DisposeAsync().ConfigureAwait(false);
    }

    // The device is opened before the mixer exists (its format sizes nothing here, but the
    // mixer needs the format the open returned), so it first renders through this.
    private sealed class Relay : IAudioRenderCallback
    {
        public volatile WebAudioOutputMixer? Target;

        public int Render(Span<float> destination, int channels)
        {
            var target = Target;
            if (target != null)
                return target.Render(destination, channels);
            destination.Clear();
            return destination.Length / channels;
        }
    }
}
