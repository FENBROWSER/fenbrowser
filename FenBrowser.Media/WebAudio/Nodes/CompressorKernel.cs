namespace FenBrowser.Media.WebAudio;

/// <summary>
/// WA 1.15 DynamicsCompressorNode: a soft-knee compressor with k-rate threshold, knee,
/// ratio, attack and release. The detector follows the loudest channel, the gain reduction
/// moves towards its target with the attack or release time constant, makeup gain is the
/// curve's reduction at 0 dBFS raised to 0.6, and the signal path carries the spec's 6 ms
/// lookahead so the gain can react before a transient arrives.
/// </summary>
public sealed class CompressorKernel : AudioNodeKernel
{
    private const double LookaheadSeconds = 0.006;

    private readonly float[]?[] _delay = new float[WebAudioLimits.MaxChannels][];
    private readonly int _delayFrames;
    private int _delayIndex;
    private int _delayChannels = 1;
    private double _reductionDb;
    private float _reportedReduction;

    public CompressorKernel(AudioGraph graph)
        : base(graph, 1, 1, 2, ChannelCountMode.ClampedMax, ChannelInterpretation.Speakers)
    {
        Threshold = AddParam("threshold", -24f, -100f, 0f, AutomationRate.KRate, rateFixed: true);
        Knee = AddParam("knee", 30f, 0f, 40f, AutomationRate.KRate, rateFixed: true);
        Ratio = AddParam("ratio", 12f, 1f, 20f, AutomationRate.KRate, rateFixed: true);
        Attack = AddParam("attack", 0.003f, 0f, 1f, AutomationRate.KRate, rateFixed: true);
        Release = AddParam("release", 0.25f, 0f, 1f, AutomationRate.KRate, rateFixed: true);
        _delayFrames = Math.Max(1, (int)Math.Round(LookaheadSeconds * graph.SampleRate));
        _delay[0] = new float[_delayFrames];
    }

    public AudioParamKernel Threshold { get; }
    public AudioParamKernel Knee { get; }
    public AudioParamKernel Ratio { get; }
    public AudioParamKernel Attack { get; }
    public AudioParamKernel Release { get; }

    /// <summary>WA 1.15 reduction: the current gain reduction in dB (zero or negative).</summary>
    public float Reduction => Volatile.Read(ref _reportedReduction);

    /// <summary>The static curve: output level in dB for an input level in dB.</summary>
    public static double Curve(double inputDb, double threshold, double knee, double ratio)
    {
        double over = inputDb - threshold;
        if (2 * over < -knee)
            return inputDb;
        if (knee > 0 && 2 * Math.Abs(over) <= knee)
        {
            double t = over + knee / 2;
            return inputDb + (1 / ratio - 1) * t * t / (2 * knee);
        }

        return threshold + over / ratio;
    }

    protected override void Process(long frame)
    {
        var input = Inputs[0].Bus;
        var output = Outputs[0].Bus;
        int channels = Math.Max(input.ChannelCount, _delayChannels);
        for (int c = _delayChannels; c < channels; c++)
            _delay[c] ??= new float[_delayFrames];
        _delayChannels = channels;
        output.Reset(channels);

        double threshold = Threshold.FirstValue, knee = Knee.FirstValue, ratio = Ratio.FirstValue;
        double sampleRate = Graph.SampleRate;
        double attackCoefficient = Coefficient(Attack.FirstValue, sampleRate);
        double releaseCoefficient = Coefficient(Release.FirstValue, sampleRate);
        double makeupDb = -0.6 * Curve(0, threshold, knee, ratio);
        int n = output.Frames;
        bool silent = input.IsSilent;

        for (int i = 0; i < n; i++)
        {
            double peak = 0;
            if (!silent)
            {
                for (int c = 0; c < input.ChannelCount; c++)
                    peak = Math.Max(peak, Math.Abs(input.Channel(c)[i]));
            }

            double levelDb = peak > 0 ? 20 * Math.Log10(peak) : -1000;
            double targetDb = peak > 0 ? Curve(levelDb, threshold, knee, ratio) - levelDb : 0;
            double coefficient = targetDb < _reductionDb ? attackCoefficient : releaseCoefficient;
            _reductionDb = targetDb + coefficient * (_reductionDb - targetDb);
            double gain = Math.Pow(10, (_reductionDb + makeupDb) / 20);

            for (int c = 0; c < channels; c++)
            {
                var line = _delay[c]!;
                float delayed = line[_delayIndex];
                line[_delayIndex] = !silent && c < input.ChannelCount ? input.Channel(c)[i] : 0f;
                output.Channel(c)[i] = (float)(delayed * gain);
            }

            _delayIndex = (_delayIndex + 1) % _delayFrames;
        }

        Volatile.Write(ref _reportedReduction, (float)_reductionDb);
        output.MarkNotSilent();
    }

    private static double Coefficient(double seconds, double sampleRate) =>
        seconds <= 0 ? 0 : Math.Exp(-1 / (seconds * sampleRate));
}
