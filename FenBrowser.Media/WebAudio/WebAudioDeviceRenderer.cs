using FenBrowser.Media.Audio;

namespace FenBrowser.Media.WebAudio;

/// <summary>
/// An AudioContext's output (design WA-D6): the device pulls, this renders quanta of the
/// graph on the device's thread, maps the destination's channels to the device's under the
/// speaker rules, and converts the rate with a streaming windowed-sinc filter when the page
/// asked for a rate the device does not run at. Nothing here allocates once running.
/// </summary>
public sealed class WebAudioDeviceRenderer : IAudioRenderCallback
{
    private const int Taps = 16;

    private readonly AudioGraph _graph;
    private readonly int _deviceChannels;
    private readonly double _step;
    private readonly AudioBus _mapped;
    private readonly float[][] _history;
    private readonly double[] _kernelScratch;
    private int _historyFill;
    private int _quantumRead;
    private double _position;
    private volatile bool _running;

    public WebAudioDeviceRenderer(AudioGraph graph, AudioStreamFormat deviceFormat)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
        _deviceChannels = Math.Clamp(deviceFormat.Channels, 1, WebAudioLimits.MaxChannels);
        _step = graph.SampleRate / (double)deviceFormat.SampleRate;
        _mapped = new AudioBus(WebAudioLimits.MaxChannels, graph.QuantumFrames);
        _mapped.Reset(_deviceChannels);
        _quantumRead = graph.QuantumFrames;

        // Input history for the resampler: the filter's reach on both sides plus a quantum.
        _history = new float[_deviceChannels][];
        for (int c = 0; c < _deviceChannels; c++)
            _history[c] = new float[2 * Taps + graph.QuantumFrames + 2];
        _kernelScratch = new double[2 * Taps + 1];
    }

    /// <summary>While false the device gets silence and the graph does not advance (a suspended context).</summary>
    public bool Running
    {
        get => _running;
        set => _running = value;
    }

    /// <summary>Frames the graph has rendered but the device has not yet taken.</summary>
    public int Buffered => _graph.QuantumFrames - _quantumRead;

    public int Render(Span<float> destination, int channels)
    {
        int frames = destination.Length / channels;
        if (!_running)
        {
            destination.Clear();
            return frames;
        }

        if (_step == 1.0)
        {
            for (int f = 0; f < frames; f++)
            {
                if (_quantumRead >= _graph.QuantumFrames)
                    NextQuantum();
                for (int c = 0; c < channels; c++)
                {
                    float v = c < _deviceChannels ? _mapped.Channel(c)[_quantumRead] : 0f;
                    destination[f * channels + c] = float.IsFinite(v) ? v : 0f;
                }

                _quantumRead++;
            }

            return frames;
        }

        for (int f = 0; f < frames; f++)
        {
            // Keep enough input for the filter to reach Taps samples past the read point.
            while (_position + Taps + 1 >= _historyFill)
                AppendQuantum();

            int center = (int)Math.Floor(_position);
            double fraction = _position - center;
            double cutoff = Math.Min(1.0, 1.0 / _step);
            double norm = 0;
            for (int k = -Taps; k <= Taps; k++)
            {
                double x = k - fraction;
                double t = x * cutoff;
                double sinc = t == 0 ? 1 : Math.Sin(Math.PI * t) / (Math.PI * t);
                double w = 0.42 + 0.5 * Math.Cos(Math.PI * x / (Taps + 1)) + 0.08 * Math.Cos(2 * Math.PI * x / (Taps + 1));
                _kernelScratch[k + Taps] = sinc * w;
                norm += sinc * w;
            }

            for (int c = 0; c < channels; c++)
            {
                if (c >= _deviceChannels)
                {
                    destination[f * channels + c] = 0;
                    continue;
                }

                var history = _history[c];
                double sum = 0;
                for (int k = -Taps; k <= Taps; k++)
                {
                    int at = center + k;
                    if (at >= 0 && at < _historyFill)
                        sum += history[at] * _kernelScratch[k + Taps];
                }

                float v = (float)(norm == 0 ? 0 : sum / norm);
                destination[f * channels + c] = float.IsFinite(v) ? v : 0f;
            }

            _position += _step;
        }

        return frames;
    }

    // One quantum of the graph, mapped to the device's channel layout.
    private void NextQuantum()
    {
        _graph.RenderQuantum();
        _mapped.Reset(_deviceChannels);
        ChannelMixing.MixInto(_graph.Destination.Output, _mapped, ChannelInterpretation.Speakers);
        _quantumRead = 0;
    }

    // Slides the resampler's history left past what it no longer needs, then appends a quantum.
    private void AppendQuantum()
    {
        int keepFrom = Math.Max(0, (int)Math.Floor(_position) - Taps - 1);
        if (keepFrom > 0)
        {
            for (int c = 0; c < _deviceChannels; c++)
                Array.Copy(_history[c], keepFrom, _history[c], 0, _historyFill - keepFrom);
            _historyFill -= keepFrom;
            _position -= keepFrom;
        }

        NextQuantum();
        int quantum = _graph.QuantumFrames;
        for (int c = 0; c < _deviceChannels; c++)
        {
            if (_history[c].Length < _historyFill + quantum)
                Array.Resize(ref _history[c], _historyFill + quantum);
            _mapped.Channel(c).CopyTo(_history[c].AsSpan(_historyFill, quantum));
        }

        _historyFill += quantum;
        _quantumRead = quantum;
    }
}
