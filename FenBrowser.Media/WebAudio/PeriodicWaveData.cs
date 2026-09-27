using System.Collections.Concurrent;

namespace FenBrowser.Media.WebAudio;

/// <summary>WA 1.27 OscillatorType, minus "custom", which is a <see cref="PeriodicWaveData"/> the page built.</summary>
public enum OscillatorWaveform
{
    Sine,
    Square,
    Sawtooth,
    Triangle,
}

/// <summary>
/// WA 1.28 PeriodicWave: a waveform given by Fourier coefficients (real = cosine terms,
/// imag = sine terms), rendered band-limited through wavetables.
/// </summary>
/// <remarks>
/// A wavetable is one period sampled <see cref="TableSize"/> times. There is one table per
/// octave of partial count - all partials, half of them, a quarter, and so on - and a
/// frequency reads the richest table whose highest partial stays below Nyquist, so nothing
/// aliases. The tables are built once per wave and table size, on first use.
/// </remarks>
public sealed class PeriodicWaveData
{
    private static readonly ConcurrentDictionary<(OscillatorWaveform, int), PeriodicWaveData> s_builtIn = new();

    private readonly double[] _real;
    private readonly double[] _imag;
    private readonly bool _normalize;
    private readonly ConcurrentDictionary<int, float[][]> _tablesBySize = new();

    public PeriodicWaveData(float[] real, float[] imag, bool disableNormalization)
    {
        ArgumentNullException.ThrowIfNull(real);
        ArgumentNullException.ThrowIfNull(imag);
        if (real.Length != imag.Length || real.Length < 2)
            throw new ArgumentException("real and imag must have the same length, at least 2.");

        // WA 1.28.3: the DC term of both arrays is ignored.
        _real = new double[real.Length];
        _imag = new double[imag.Length];
        for (int i = 1; i < real.Length; i++)
        {
            _real[i] = real[i];
            _imag[i] = imag[i];
        }

        _normalize = !disableNormalization;
    }

    private PeriodicWaveData(double[] real, double[] imag)
    {
        _real = real;
        _imag = imag;
        _normalize = true;
    }

    /// <summary>Number of coefficients, the DC term included.</summary>
    public int Length => _real.Length;

    /// <summary>
    /// WA 1.28.4: the built-in waveforms as Fourier series, normalized like any other wave.
    /// <paramref name="tableSize"/> fixes how many partials the series needs.
    /// </summary>
    public static PeriodicWaveData BuiltIn(OscillatorWaveform waveform, int tableSize)
    {
        return s_builtIn.GetOrAdd((waveform, tableSize), key =>
        {
            int partials = key.Item2 / 2;
            var real = new double[partials + 1];
            var imag = new double[partials + 1];
            for (int n = 1; n <= partials; n++)
            {
                double piN = Math.PI * n;
                imag[n] = key.Item1 switch
                {
                    OscillatorWaveform.Sine => n == 1 ? 1 : 0,
                    OscillatorWaveform.Square => 2.0 / piN * (1 - ((n & 1) == 0 ? 1 : -1)),
                    OscillatorWaveform.Sawtooth => ((n & 1) == 1 ? 1 : -1) * 2.0 / piN,
                    _ => 8.0 * Math.Sin(piN / 2) / (piN * piN),
                };
            }

            return new PeriodicWaveData(real, imag);
        });
    }

    /// <summary>The table size used at <paramref name="sampleRate"/>: more samples for higher rates.</summary>
    public static int TableSizeFor(float sampleRate) => sampleRate <= 88200 ? 4096 : 16384;

    /// <summary>
    /// The tables for <paramref name="tableSize"/>: index r holds the wave with at most
    /// (tableSize / 2) >> r partials. Each has one extra guard sample equal to the first, so
    /// interpolation past the last sample needs no wrap.
    /// </summary>
    public float[][] Tables(int tableSize) => _tablesBySize.GetOrAdd(tableSize, BuildTables);

    private float[][] BuildTables(int tableSize)
    {
        int maxPartials = tableSize / 2;
        int available = Math.Min(maxPartials, _real.Length - 1);
        int ranges = 1;
        while ((maxPartials >> ranges) >= 1)
            ranges++;

        var tables = new float[ranges][];
        double scale = 1;
        float[]? previous = null;
        int previousPartials = -1;
        for (int r = 0; r < ranges; r++)
        {
            int partials = Math.Min(maxPartials >> r, available);
            if (partials == previousPartials && previous is not null)
            {
                tables[r] = previous;
                continue;
            }

            var table = Synthesize(tableSize, partials);
            if (r == 0 && _normalize)
            {
                // WA 1.28.3: normalized so the full wave peaks at 1; every band-limited
                // table shares that one factor, so the level does not jump with frequency.
                double peak = 0;
                foreach (double v in table)
                    peak = Math.Max(peak, Math.Abs(v));
                scale = peak > 0 ? 1.0 / peak : 1;
            }

            var samples = new float[tableSize + 1];
            for (int i = 0; i < tableSize; i++)
                samples[i] = (float)(table[i] * scale);
            samples[tableSize] = samples[0];
            tables[r] = samples;
            previous = samples;
            previousPartials = partials;
        }

        return tables;
    }

    // x(k) = sum over n of real[n] cos(2 pi n k / N) + imag[n] sin(2 pi n k / N).
    private double[] Synthesize(int tableSize, int partials)
    {
        var table = new double[tableSize];
        for (int n = 1; n <= partials; n++)
        {
            double a = _real[n], b = _imag[n];
            if (a == 0 && b == 0)
                continue;

            // Rotate a unit phasor instead of calling sin and cos per sample; in double the
            // drift over one table is around 1e-12, far below a float sample's resolution.
            double step = 2 * Math.PI * n / tableSize;
            double stepCos = Math.Cos(step), stepSin = Math.Sin(step);
            double c = 1, s = 0;
            for (int k = 0; k < tableSize; k++)
            {
                table[k] += a * c + b * s;
                double nc = c * stepCos - s * stepSin;
                s = s * stepCos + c * stepSin;
                c = nc;
            }
        }

        return table;
    }
}
