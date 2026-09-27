namespace FenBrowser.Media.WebAudio;

/// <summary>
/// WA 1.8 AnalyserNode: passes its input through unchanged and keeps the most recent
/// frames of it, down-mixed to mono, for the control thread to analyse. The frequency data
/// is a Blackman-windowed FFT scaled by 1/N, smoothed over time and converted to dB; the
/// smoothing advances at most once per render quantum however often script asks.
/// </summary>
public sealed class AnalyserKernel : AudioNodeKernel
{
    /// <summary>WA 1.8: the largest fftSize.</summary>
    public const int MaxFftSize = 32768;

    private readonly object _gate = new();
    private readonly float[] _history = new float[MaxFftSize];
    private readonly AudioBus _mono;
    private long _historyWritten;
    private double[] _smoothed = [];
    private long _smoothedAtFrame = -1;
    private int _smoothedFftSize;

    public AnalyserKernel(AudioGraph graph)
        : base(graph, 1, 1, 2, ChannelCountMode.Max, ChannelInterpretation.Speakers)
    {
        _mono = new AudioBus(1, graph.QuantumFrames);
    }

    public int FftSize { get; set; } = 2048;

    public double MinDecibels { get; set; } = -100;

    public double MaxDecibels { get; set; } = -30;

    public double SmoothingTimeConstant { get; set; } = 0.8;

    protected override void Process(long frame)
    {
        var input = Inputs[0].Bus;
        var output = Outputs[0].Bus;
        if (input.IsSilent)
            output.Reset(input.ChannelCount);
        else
            output.CopyFrom(input);

        _mono.Reset(1);
        ChannelMixing.MixInto(input, _mono, ChannelInterpretation.Speakers);
        var samples = _mono.Channel(0);
        lock (_gate)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                _history[(int)(_historyWritten % MaxFftSize)] = samples[i];
                _historyWritten++;
            }
        }
    }

    /// <summary>getFloatTimeDomainData: the most recent fftSize samples, oldest first.</summary>
    public float[] TimeDomain(int fftSize)
    {
        var result = new float[fftSize];
        lock (_gate)
        {
            long start = _historyWritten - fftSize;
            for (int i = 0; i < fftSize; i++)
            {
                long at = start + i;
                result[i] = at < 0 ? 0f : _history[(int)(at % MaxFftSize)];
            }
        }

        return result;
    }

    /// <summary>
    /// getFloatFrequencyData: the smoothed magnitude spectrum in dB, frequencyBinCount
    /// values. <paramref name="currentFrame"/> is the context's current frame, so a second
    /// call within the same quantum reuses the first's smoothing step.
    /// </summary>
    public double[] FrequencyDecibels(long currentFrame)
    {
        int n = FftSize;
        if (_smoothedAtFrame != currentFrame || _smoothedFftSize != n)
        {
            var samples = TimeDomain(n);
            var real = new double[n];
            var imag = new double[n];
            for (int i = 0; i < n; i++)
            {
                // WA 1.8 "Blackman window" with alpha = 0.16.
                double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / n) + 0.08 * Math.Cos(4 * Math.PI * i / n);
                real[i] = samples[i] * w;
            }

            new Fft(n).Forward(real, imag);
            if (_smoothed.Length != n / 2 || _smoothedFftSize != n)
                _smoothed = new double[n / 2];

            double tau = SmoothingTimeConstant;
            for (int k = 0; k < n / 2; k++)
            {
                double magnitude = Math.Sqrt(real[k] * real[k] + imag[k] * imag[k]) / n;
                double smoothed = tau * _smoothed[k] + (1 - tau) * magnitude;
                _smoothed[k] = double.IsFinite(smoothed) ? smoothed : 0;
            }

            _smoothedAtFrame = currentFrame;
            _smoothedFftSize = n;
        }

        var decibels = new double[n / 2];
        for (int k = 0; k < decibels.Length; k++)
            decibels[k] = 20 * Math.Log10(_smoothed[k]);
        return decibels;
    }
}
