namespace FenBrowser.Media.WebAudio;

/// <summary>
/// WA 1.27 OscillatorNode: a periodic wave at computedOscFrequency = frequency ·
/// 2^(detune / 1200), read from band-limited wavetables. A computed frequency at or past
/// Nyquist produces silence.
/// </summary>
public sealed class OscillatorKernel : ScheduledSourceKernel
{
    private readonly int _tableSize;
    private PeriodicWaveData _wave;
    private float[][] _tables;
    private double _phase;
    private bool _phaseStarted;

    public OscillatorKernel(AudioGraph graph)
        : base(graph, 2)
    {
        float nyquist = graph.SampleRate / 2;
        Frequency = AddParam("frequency", 440f, -nyquist, nyquist, AutomationRate.ARate);
        Detune = AddParam("detune", 0f, -153600f, 153600f, AutomationRate.ARate);
        _tableSize = PeriodicWaveData.TableSizeFor(graph.SampleRate);
        _wave = PeriodicWaveData.BuiltIn(OscillatorWaveform.Sine, _tableSize);
        _tables = _wave.Tables(_tableSize);
    }

    public AudioParamKernel Frequency { get; }

    public AudioParamKernel Detune { get; }

    /// <summary>Control message for the type attribute or setPeriodicWave().</summary>
    public void SetWave(PeriodicWaveData wave)
    {
        _wave = wave;
        _tables = wave.Tables(_tableSize);
    }

    protected override void Process(long frame)
    {
        var (from, to) = PlayingRange(frame);
        if (from == to)
        {
            OutputSilence();
            if (HasStarted && !HasEnded && frame >= StopFrame)
                Finish();
            return;
        }

        var output = Outputs[0].Bus;
        output.Reset(1);
        var dst = output.Channel(0);
        double sampleRate = Graph.SampleRate;
        double nyquist = sampleRate / 2;
        var frequency = Frequency.Values;
        var detune = Detune.Values;
        bool constantDetune = Detune.IsConstant && detune[0] == 0;
        int tableSize = _tableSize;
        var tables = _tables;
        int maxPartials = tableSize / 2;

        if (!_phaseStarted)
        {
            // Sub-sample accurate start: the phase the wave would have reached had it begun
            // exactly at the start time.
            double f0 = ComputedFrequency(frequency[from], detune[from], constantDetune);
            double lead = (frame + from) / sampleRate - StartTime;
            _phase = Wrap(lead * f0);
            _phaseStarted = true;
        }

        for (int i = from; i < to; i++)
        {
            double f = ComputedFrequency(frequency[i], detune[i], constantDetune);
            double magnitude = Math.Abs(f);
            if (magnitude >= nyquist || double.IsNaN(f))
            {
                dst[i] = 0;
            }
            else
            {
                var table = tables[TableIndex(magnitude, nyquist, maxPartials, tables.Length)];
                double position = _phase * tableSize;
                int k = (int)position;
                double fraction = position - k;
                float s0 = table[k];
                dst[i] = (float)(s0 + (table[k + 1] - s0) * fraction);
            }

            _phase = Wrap(_phase + f / sampleRate);
        }

        output.MarkNotSilent();
        if (ReachesStop(frame))
            Finish();
    }

    private static double ComputedFrequency(float frequency, float detune, bool noDetune) =>
        noDetune ? frequency : frequency * Math.Pow(2, detune / 1200.0);

    // The richest table whose highest partial stays below Nyquist at this frequency.
    private static int TableIndex(double frequency, double nyquist, int maxPartials, int count)
    {
        if (frequency <= 0)
            return 0;
        double allowed = nyquist / frequency;
        int r = 0;
        while (r < count - 1 && (maxPartials >> r) > allowed)
            r++;
        return r;
    }

    private static double Wrap(double phase)
    {
        phase -= Math.Floor(phase);
        return phase >= 1 ? 0 : phase;
    }
}
