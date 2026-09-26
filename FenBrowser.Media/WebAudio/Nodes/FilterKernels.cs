namespace FenBrowser.Media.WebAudio;

/// <summary>WA 1.10 BiquadFilterType.</summary>
public enum BiquadFilterType
{
    Lowpass,
    Highpass,
    Bandpass,
    Lowshelf,
    Highshelf,
    Peaking,
    Notch,
    Allpass,
}

/// <summary>Normalized biquad coefficients: H(z) = (b0 + b1/z + b2/z^2) / (1 + a1/z + a2/z^2).</summary>
public readonly record struct BiquadCoefficients(double B0, double B1, double B2, double A1, double A2)
{
    public static readonly BiquadCoefficients Identity = new(1, 0, 0, 0, 0);

    /// <summary>WA 1.10 getFrequencyResponse at normalized frequency <paramref name="omega"/> (radians per sample).</summary>
    public (double Magnitude, double Phase) Response(double omega)
    {
        // z^-1 = e^(-j omega).
        double c1 = Math.Cos(omega), s1 = -Math.Sin(omega);
        double c2 = Math.Cos(2 * omega), s2 = -Math.Sin(2 * omega);
        double nr = B0 + B1 * c1 + B2 * c2, ni = B1 * s1 + B2 * s2;
        double dr = 1 + A1 * c1 + A2 * c2, di = A1 * s1 + A2 * s2;
        double d = dr * dr + di * di;
        double hr = (nr * dr + ni * di) / d, hi = (ni * dr - nr * di) / d;
        return (Math.Sqrt(hr * hr + hi * hi), Math.Atan2(hi, hr));
    }
}

/// <summary>
/// WA 1.10 BiquadFilterNode. Coefficients follow the Audio EQ Cookbook with the spec's Q
/// conventions (dB for lowpass and highpass) and its limits at 0 Hz, Nyquist and Q = 0.
/// </summary>
public sealed class BiquadKernel : AudioNodeKernel
{
    private readonly double[] _x1 = new double[WebAudioLimits.MaxChannels];
    private readonly double[] _x2 = new double[WebAudioLimits.MaxChannels];
    private readonly double[] _y1 = new double[WebAudioLimits.MaxChannels];
    private readonly double[] _y2 = new double[WebAudioLimits.MaxChannels];

    public BiquadKernel(AudioGraph graph)
        : base(graph, 1, 1, 2, ChannelCountMode.Max, ChannelInterpretation.Speakers)
    {
        float nyquist = graph.SampleRate / 2;
        Frequency = AddParam("frequency", 350f, 0f, nyquist, AutomationRate.ARate);
        Detune = AddParam("detune", 0f, -153600f, 153600f, AutomationRate.ARate);
        Q = AddParam("Q", 1f, float.MinValue, float.MaxValue, AutomationRate.ARate);
        Gain = AddParam("gain", 0f, float.MinValue, 1541.273f, AutomationRate.ARate);
    }

    public BiquadFilterType Type { get; set; }

    public AudioParamKernel Frequency { get; }

    public AudioParamKernel Detune { get; }

    public AudioParamKernel Q { get; }

    public AudioParamKernel Gain { get; }

    /// <summary>The coefficients for one set of parameter values at <paramref name="sampleRate"/>.</summary>
    public static BiquadCoefficients Coefficients(BiquadFilterType type, double frequency, double detune, double q, double gain, double sampleRate)
    {
        double nyquist = sampleRate / 2;
        double f0 = frequency * Math.Pow(2, detune / 1200);
        double freq = Math.Clamp(f0 / nyquist, 0, 1);
        if (double.IsNaN(freq))
            freq = 0;
        double a = Math.Pow(10, gain / 40);

        switch (type)
        {
            case BiquadFilterType.Lowpass:
            {
                if (freq == 1)
                    return BiquadCoefficients.Identity;
                double theta = Math.PI * freq;
                double alpha = Math.Sin(theta) / (2 * Math.Pow(10, q / 20));
                double cosw = Math.Cos(theta);
                double beta = (1 - cosw) / 2;
                return Normalize(beta, 2 * beta, beta, 1 + alpha, -2 * cosw, 1 - alpha);
            }

            case BiquadFilterType.Highpass:
            {
                if (freq == 1)
                    return new BiquadCoefficients(0, 0, 0, 0, 0);
                if (freq == 0)
                    return BiquadCoefficients.Identity;
                double theta = Math.PI * freq;
                double alpha = Math.Sin(theta) / (2 * Math.Pow(10, q / 20));
                double cosw = Math.Cos(theta);
                double beta = (1 + cosw) / 2;
                return Normalize(beta, -2 * beta, beta, 1 + alpha, -2 * cosw, 1 - alpha);
            }

            case BiquadFilterType.Bandpass:
            {
                if (freq <= 0 || freq >= 1)
                    return new BiquadCoefficients(0, 0, 0, 0, 0);
                if (q <= 0)
                    return BiquadCoefficients.Identity;
                double w0 = Math.PI * freq;
                double alpha = Math.Sin(w0) / (2 * q);
                double k = Math.Cos(w0);
                return Normalize(alpha, 0, -alpha, 1 + alpha, -2 * k, 1 - alpha);
            }

            case BiquadFilterType.Lowshelf:
            {
                if (freq == 1)
                    return new BiquadCoefficients(a * a, 0, 0, 0, 0);
                if (freq == 0)
                    return BiquadCoefficients.Identity;
                double w0 = Math.PI * freq;
                double alpha = 0.5 * Math.Sin(w0) * Math.Sqrt((a + 1 / a) * (1 / 1.0 - 1) + 2);
                double k = Math.Cos(w0);
                double k2 = 2 * Math.Sqrt(a) * alpha;
                double ap1 = a + 1, am1 = a - 1;
                return Normalize(
                    a * (ap1 - am1 * k + k2),
                    2 * a * (am1 - ap1 * k),
                    a * (ap1 - am1 * k - k2),
                    ap1 + am1 * k + k2,
                    -2 * (am1 + ap1 * k),
                    ap1 + am1 * k - k2);
            }

            case BiquadFilterType.Highshelf:
            {
                if (freq == 1)
                    return BiquadCoefficients.Identity;
                if (freq <= 0)
                    return new BiquadCoefficients(a * a, 0, 0, 0, 0);
                double w0 = Math.PI * freq;
                double alpha = 0.5 * Math.Sin(w0) * Math.Sqrt((a + 1 / a) * (1 / 1.0 - 1) + 2);
                double k = Math.Cos(w0);
                double k2 = 2 * Math.Sqrt(a) * alpha;
                double ap1 = a + 1, am1 = a - 1;
                return Normalize(
                    a * (ap1 + am1 * k + k2),
                    -2 * a * (am1 + ap1 * k),
                    a * (ap1 + am1 * k - k2),
                    ap1 - am1 * k + k2,
                    2 * (am1 - ap1 * k),
                    ap1 - am1 * k - k2);
            }

            case BiquadFilterType.Peaking:
            {
                if (freq <= 0 || freq >= 1)
                    return BiquadCoefficients.Identity;
                if (q <= 0)
                    return new BiquadCoefficients(a * a, 0, 0, 0, 0);
                double w0 = Math.PI * freq;
                double alpha = Math.Sin(w0) / (2 * q);
                double k = Math.Cos(w0);
                return Normalize(1 + alpha * a, -2 * k, 1 - alpha * a, 1 + alpha / a, -2 * k, 1 - alpha / a);
            }

            case BiquadFilterType.Notch:
            {
                if (freq <= 0 || freq >= 1)
                    return BiquadCoefficients.Identity;
                if (q <= 0)
                    return new BiquadCoefficients(0, 0, 0, 0, 0);
                double w0 = Math.PI * freq;
                double alpha = Math.Sin(w0) / (2 * q);
                double k = Math.Cos(w0);
                return Normalize(1, -2 * k, 1, 1 + alpha, -2 * k, 1 - alpha);
            }

            default:
            {
                if (freq <= 0 || freq >= 1)
                    return BiquadCoefficients.Identity;
                if (q <= 0)
                    return new BiquadCoefficients(-1, 0, 0, 0, 0);
                double w0 = Math.PI * freq;
                double alpha = Math.Sin(w0) / (2 * q);
                double k = Math.Cos(w0);
                return Normalize(1 - alpha, -2 * k, 1 + alpha, 1 + alpha, -2 * k, 1 - alpha);
            }
        }
    }

    private static BiquadCoefficients Normalize(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        double scale = 1 / a0;
        return new BiquadCoefficients(b0 * scale, b1 * scale, b2 * scale, a1 * scale, a2 * scale);
    }

    protected override void Process(long frame)
    {
        var input = Inputs[0].Bus;
        var output = Outputs[0].Bus;
        output.Reset(input.ChannelCount);
        int n = output.Frames;
        double sampleRate = Graph.SampleRate;

        bool constant = Frequency.IsConstant && Detune.IsConstant && Q.IsConstant && Gain.IsConstant;
        var coefficients = Coefficients(Type, Frequency.FirstValue, Detune.FirstValue, Q.FirstValue, Gain.FirstValue, sampleRate);

        bool anySignal = false;
        for (int c = 0; c < input.ChannelCount; c++)
        {
            var x = input.Channel(c);
            var y = output.Channel(c);
            double x1 = _x1[c], x2 = _x2[c], y1 = _y1[c], y2 = _y2[c];
            for (int i = 0; i < n; i++)
            {
                if (!constant)
                    coefficients = Coefficients(Type, Frequency.Values[i], Detune.Values[i], Q.Values[i], Gain.Values[i], sampleRate);
                double xi = input.IsSilent ? 0 : x[i];
                double yi = coefficients.B0 * xi + coefficients.B1 * x1 + coefficients.B2 * x2 - coefficients.A1 * y1 - coefficients.A2 * y2;
                x2 = x1;
                x1 = xi;
                y2 = y1;
                y1 = FlushDenormal(yi);
                y[i] = (float)y1;
            }

            _x1[c] = x1;
            _x2[c] = x2;
            _y1[c] = y1;
            _y2[c] = y2;
            anySignal |= y1 != 0 || y2 != 0 || !input.IsSilent;
        }

        if (anySignal)
            output.MarkNotSilent();
    }

    // Flush what would be a denormal once stored as a float sample (below FLT_MIN): the
    // feedback then never grinds through denormals, and two filters whose coefficients differ
    // by a power of two stay exact scalings of each other down to that boundary.
    internal static double FlushDenormal(double v) => Math.Abs(v) < FltMin ? 0 : v;

    private const double FltMin = 1.1754943508222875e-38;
}

/// <summary>
/// WA 1.22 IIRFilterNode: a general IIR filter from its feedforward (b) and feedback (a)
/// coefficients, normalized by a[0], in direct form with double-precision history.
/// </summary>
public sealed class IirFilterKernel : AudioNodeKernel
{
    private double[] _b = [1];
    private double[] _a = [1];
    private readonly double[][] _xHistory = new double[WebAudioLimits.MaxChannels][];
    private readonly double[][] _yHistory = new double[WebAudioLimits.MaxChannels][];
    private readonly int[] _cursor = new int[WebAudioLimits.MaxChannels];
    private int _order = 1;

    /// <summary>Starts as a wire; the realm hands over the coefficients right after creating it.</summary>
    public IirFilterKernel(AudioGraph graph)
        : base(graph, 1, 1, 2, ChannelCountMode.Max, ChannelInterpretation.Speakers)
    {
    }

    /// <summary>Control message: the coefficients, normalized here by feedback[0].</summary>
    public void SetCoefficients(double[] feedforward, double[] feedback)
    {
        ArgumentNullException.ThrowIfNull(feedforward);
        ArgumentNullException.ThrowIfNull(feedback);
        if (feedforward.Length is < 1 or > 20 || feedback.Length is < 1 or > 20 || feedback[0] == 0)
            throw new ArgumentException("Invalid IIR coefficients.");

        double a0 = feedback[0];
        _b = feedforward.Select(v => v / a0).ToArray();
        _a = feedback.Select(v => v / a0).ToArray();
        _order = Math.Max(_b.Length, _a.Length);
        Array.Clear(_xHistory);
        Array.Clear(_yHistory);
        Array.Clear(_cursor);
    }

    /// <summary>WA 1.22 getFrequencyResponse at <paramref name="omega"/> radians per sample.</summary>
    public static (double Magnitude, double Phase) Response(double[] feedforward, double[] feedback, double omega)
    {
        double nr = 0, ni = 0, dr = 0, di = 0;
        for (int k = 0; k < feedforward.Length; k++)
        {
            nr += feedforward[k] * Math.Cos(k * omega);
            ni -= feedforward[k] * Math.Sin(k * omega);
        }

        for (int k = 0; k < feedback.Length; k++)
        {
            dr += feedback[k] * Math.Cos(k * omega);
            di -= feedback[k] * Math.Sin(k * omega);
        }

        double d = dr * dr + di * di;
        double hr = (nr * dr + ni * di) / d, hi = (ni * dr - nr * di) / d;
        return (Math.Sqrt(hr * hr + hi * hi), Math.Atan2(hi, hr));
    }

    protected override void Process(long frame)
    {
        var input = Inputs[0].Bus;
        var output = Outputs[0].Bus;
        output.Reset(input.ChannelCount);
        int n = output.Frames;
        int size = _order;
        bool anySignal = false;

        for (int c = 0; c < input.ChannelCount; c++)
        {
            var xs = _xHistory[c] ??= new double[size];
            var ys = _yHistory[c] ??= new double[size];
            int cursor = _cursor[c];
            var x = input.Channel(c);
            var y = output.Channel(c);
            bool channelActive = false;
            for (int i = 0; i < n; i++)
            {
                double xi = input.IsSilent ? 0 : x[i];
                xs[cursor] = xi;
                double yi = _b[0] * xi;
                for (int k = 1; k < _b.Length; k++)
                    yi += _b[k] * xs[(cursor - k + size) % size];
                for (int k = 1; k < _a.Length; k++)
                    yi -= _a[k] * ys[(cursor - k + size) % size];
                yi = BiquadKernel.FlushDenormal(yi);
                ys[cursor] = yi;
                y[i] = (float)yi;
                channelActive |= yi != 0;
                cursor = (cursor + 1) % size;
            }

            _cursor[c] = cursor;
            anySignal |= channelActive || !input.IsSilent;
        }

        if (anySignal)
            output.MarkNotSilent();
    }
}

/// <summary>WA 1.31 OverSampleType.</summary>
public enum OverSampleType
{
    None,
    TwoX,
    FourX,
}

/// <summary>
/// WA 1.31 WaveShaperNode: each sample mapped through the curve by linear interpolation,
/// optionally at twice or four times the sample rate with windowed-sinc resampling
/// around the shaping so the non-linearity aliases less.
/// </summary>
public sealed class WaveShaperKernel : AudioNodeKernel
{
    private const int FilterTaps = 32;
    private readonly float[]?[] _upHistory = new float[WebAudioLimits.MaxChannels][];
    private readonly float[]?[] _downHistory = new float[WebAudioLimits.MaxChannels][];
    private float[] _scratch = [];
    private double[] _filter = [];
    private int _filterFactor;

    public WaveShaperKernel(AudioGraph graph)
        : base(graph, 1, 1, 2, ChannelCountMode.Max, ChannelInterpretation.Speakers)
    {
    }

    public float[]? Curve { get; set; }

    public OverSampleType Oversample { get; set; }

    /// <summary>WA 1.31: the curve lookup for one input sample.</summary>
    public static float Shape(float[] curve, float x)
    {
        int n = curve.Length;
        double v = (n - 1) / 2.0 * (x + 1);
        if (double.IsNaN(v))
            return curve[(n - 1) / 2];
        if (v <= 0)
            return curve[0];
        if (v >= n - 1)
            return curve[n - 1];
        int k = (int)Math.Floor(v);
        double f = v - k;
        return (float)((1 - f) * curve[k] + f * curve[k + 1]);
    }

    protected override void Process(long frame)
    {
        var input = Inputs[0].Bus;
        var output = Outputs[0].Bus;
        var curve = Curve;
        output.Reset(input.ChannelCount);

        if (curve is null)
        {
            // WA 1.31: with no curve the input passes through unchanged.
            if (!input.IsSilent)
                output.CopyFrom(input);
            return;
        }

        int factor = Oversample switch { OverSampleType.TwoX => 2, OverSampleType.FourX => 4, _ => 1 };
        int n = output.Frames;
        for (int c = 0; c < input.ChannelCount; c++)
        {
            var x = input.Channel(c);
            var y = output.Channel(c);
            if (factor == 1)
            {
                for (int i = 0; i < n; i++)
                    y[i] = Shape(curve, input.IsSilent ? 0 : x[i]);
            }
            else
            {
                ProcessOversampled(c, factor, curve, input.IsSilent ? null : x, y);
            }
        }

        output.MarkNotSilent();
    }

    // Upsample by zero-stuffing and low-pass filtering, shape at the higher rate, low-pass
    // again and keep every factor-th sample.
    private void ProcessOversampled(int channel, int factor, float[] curve, Span<float> x, Span<float> y)
    {
        EnsureFilter(factor);
        int n = y.Length;
        int up = n * factor;
        if (_scratch.Length < up)
            _scratch = new float[up];

        var upHistory = _upHistory[channel] ??= new float[FilterTaps];
        var downHistory = _downHistory[channel] ??= new float[FilterTaps];

        for (int i = 0; i < up; i++)
        {
            float stuffed = i % factor == 0 && !x.IsEmpty ? x[i / factor] * factor : 0f;
            _scratch[i] = (float)Fir(upHistory, stuffed);
        }

        for (int i = 0; i < up; i++)
        {
            double filtered = Fir(downHistory, Shape(curve, _scratch[i]));
            if (i % factor == 0)
                y[i / factor] = (float)filtered;
        }
    }

    private double Fir(float[] history, float sample)
    {
        Array.Copy(history, 1, history, 0, history.Length - 1);
        history[^1] = sample;
        double sum = 0;
        for (int k = 0; k < FilterTaps; k++)
            sum += _filter[k] * history[FilterTaps - 1 - k];
        return sum;
    }

    // A Blackman-windowed sinc low-pass at the original Nyquist.
    private void EnsureFilter(int factor)
    {
        if (_filterFactor == factor)
            return;

        _filter = new double[FilterTaps];
        double cutoff = 0.5 / factor;
        double center = (FilterTaps - 1) / 2.0;
        double sum = 0;
        for (int k = 0; k < FilterTaps; k++)
        {
            double m = k - center;
            double sinc = m == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * m) / (Math.PI * m);
            double window = 0.42 - 0.5 * Math.Cos(2 * Math.PI * k / (FilterTaps - 1)) + 0.08 * Math.Cos(4 * Math.PI * k / (FilterTaps - 1));
            _filter[k] = sinc * window;
            sum += _filter[k];
        }

        for (int k = 0; k < FilterTaps; k++)
            _filter[k] /= sum;
        _filterFactor = factor;
    }
}

/// <summary>
/// WA 1.29 StereoPannerNode: equal-power panning of a mono or stereo input to stereo by the
/// a-rate <c>pan</c> in [-1, 1].
/// </summary>
public sealed class StereoPannerKernel : AudioNodeKernel
{
    public StereoPannerKernel(AudioGraph graph)
        : base(graph, 1, 1, 2, ChannelCountMode.ClampedMax, ChannelInterpretation.Speakers)
    {
        Pan = AddParam("pan", 0f, -1f, 1f, AutomationRate.ARate);
    }

    public AudioParamKernel Pan { get; }

    protected override void Process(long frame)
    {
        var input = Inputs[0].Bus;
        var output = Outputs[0].Bus;
        output.Reset(2);
        if (input.IsSilent)
            return;

        var pan = Pan.Values;
        var left = output.Channel(0);
        var right = output.Channel(1);
        int n = output.Frames;
        if (input.ChannelCount == 1)
        {
            var m = input.Channel(0);
            for (int i = 0; i < n; i++)
            {
                double x = (pan[i] + 1) / 2 * Math.PI / 2;
                left[i] = (float)(m[i] * Math.Cos(x));
                right[i] = (float)(m[i] * Math.Sin(x));
            }
        }
        else
        {
            var inL = input.Channel(0);
            var inR = input.Channel(1);
            for (int i = 0; i < n; i++)
            {
                double p = pan[i];
                double x = (p <= 0 ? p + 1 : p) * Math.PI / 2;
                double gainL = Math.Cos(x), gainR = Math.Sin(x);
                if (p <= 0)
                {
                    left[i] = (float)(inL[i] + inR[i] * gainL);
                    right[i] = (float)(inR[i] * gainR);
                }
                else
                {
                    left[i] = (float)(inL[i] * gainL);
                    right[i] = (float)(inR[i] + inL[i] * gainR);
                }
            }
        }

        output.MarkNotSilent();
    }
}
