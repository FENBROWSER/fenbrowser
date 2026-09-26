namespace FenBrowser.Media.WebAudio;

/// <summary>
/// The PannerNode's "HRTF" panning model (design WA-D10): a synthetic head instead of a
/// measured impulse-response set. Each ear gets the three cues of Brown and Duda's structural
/// model ("A Structural Model for Binaural Sound Synthesis", IEEE Trans. Speech and Audio
/// Processing 6(5), 1998):
/// <list type="bullet">
/// <item>head shadow - a one-pole, one-zero filter whose high-frequency gain follows the angle
/// between the source and the ear (up to +6 dB facing it, down to -20 dB behind the head);</item>
/// <item>interaural time difference - a fractional delay from the path round a sphere;</item>
/// <item>pinna echoes - five short reflections whose delays depend on elevation, which is what
/// lets a listener (and a page) tell a source above from one below.</item>
/// </list>
/// Directions are taken once per render quantum; delays glide across the quantum so that a
/// moving source does not click.
/// </summary>
internal sealed class SphericalHeadHrtf
{
    // Head radius (m) and the speed of sound (m/s) of the model.
    private const double HeadRadius = 0.0875;
    private const double SpeedOfSound = 343;
    private const double AlphaMin = 0.1;
    private const double ThetaMin = 150 * Math.PI / 180;

    // Brown and Duda's pinna reflections: coefficient, and the A, B, D of
    // tau = A cos(azimuth / 2) sin(D (90 - elevation)) + B, in samples at 44.1 kHz.
    private static readonly double[] Rho = [0.5, -1, 0.5, -0.25, 0.25];
    private static readonly double[] A = [1, 5, 5, 5, 5];
    private static readonly double[] B = [2, 4, 7, 11, 13];
    private static readonly double[] D = [1, 0.5, 0.5, 0.5, 0.5];

    // The echoes add energy; this keeps a broadband source at roughly the level it has
    // through the equal-power model.
    private static readonly double EchoNormalization = 1 / Math.Sqrt(1 + Rho.Sum(r => r * r));

    private readonly double _sampleRate;
    private readonly Ear _left;
    private readonly Ear _right;

    public SphericalHeadHrtf(double sampleRate)
    {
        _sampleRate = sampleRate;
        // The longest read: the whole interaural delay plus the latest echo, with room to
        // interpolate.
        double longest = (HeadRadius / SpeedOfSound) * (Math.PI / 2 + 1) * sampleRate + 18 * sampleRate / 44100 + 4;
        int size = 1;
        while (size < longest)
            size <<= 1;
        _left = new Ear(size);
        _right = new Ear(size);
    }

    /// <summary>
    /// Points the head at a source at <paramref name="azimuth"/> and <paramref name="elevation"/>
    /// degrees (WA 6.2: azimuth 0 ahead and positive to the right, elevation positive up).
    /// </summary>
    public void SetDirection(double azimuth, double elevation)
    {
        double az = azimuth * Math.PI / 180, el = elevation * Math.PI / 180;
        // The source direction in the listener's frame: x right, y up, z ahead.
        double x = Math.Cos(el) * Math.Sin(az);
        double lateral = Math.Asin(Math.Clamp(x, -1, 1)) * 180 / Math.PI;
        _right.Aim(Math.Acos(Math.Clamp(x, -1, 1)), lateral, elevation, _sampleRate);
        _left.Aim(Math.Acos(Math.Clamp(-x, -1, 1)), -lateral, elevation, _sampleRate);
    }

    public void Process(ReadOnlySpan<float> inLeft, ReadOnlySpan<float> inRight, Span<float> left, Span<float> right, int frames)
    {
        _left.Process(inLeft, left, frames);
        _right.Process(inRight, right, frames);
    }

    private sealed class Ear
    {
        private readonly float[] _ring;
        private readonly int _mask;
        private int _write;
        private double _b0 = 1, _b1, _a1;
        private double _x1, _y1;
        private double _delay, _targetDelay;
        private readonly double[] _echo = new double[Rho.Length];
        private readonly double[] _targetEcho = new double[Rho.Length];
        private bool _aimed;

        public Ear(int size)
        {
            _ring = new float[size];
            _mask = size - 1;
        }

        // incidence: the angle between the source and this ear's axis, in radians.
        public void Aim(double incidence, double lateral, double elevation, double sampleRate)
        {
            // Head shadow: H(s) = (alpha s + beta) / (s + beta), beta = 2c/a, by the bilinear
            // transform.
            double alpha = (1 + AlphaMin / 2) + (1 - AlphaMin / 2) * Math.Cos(incidence / ThetaMin * Math.PI);
            double beta = 2 * SpeedOfSound / HeadRadius;
            double k = 2 * sampleRate;
            _b0 = (beta + alpha * k) / (beta + k);
            _b1 = (beta - alpha * k) / (beta + k);
            _a1 = (beta - k) / (beta + k);

            // Interaural time: the path round the sphere, offset so the nearer ear is at zero.
            double seconds = incidence < Math.PI / 2
                ? -Math.Cos(incidence)
                : incidence - Math.PI / 2;
            _targetDelay = (seconds + 1) * HeadRadius / SpeedOfSound * sampleRate;

            double scale = sampleRate / 44100;
            double halfLateral = lateral / 2 * Math.PI / 180;
            for (int e = 0; e < Rho.Length; e++)
            {
                double tau = A[e] * Math.Cos(halfLateral) * Math.Sin(D[e] * (90 - elevation) * Math.PI / 180) + B[e];
                _targetEcho[e] = tau * scale;
            }

            if (!_aimed)
            {
                _delay = _targetDelay;
                Array.Copy(_targetEcho, _echo, _echo.Length);
                _aimed = true;
            }
        }

        public void Process(ReadOnlySpan<float> input, Span<float> output, int frames)
        {
            double delayStep = (_targetDelay - _delay) / frames;
            for (int i = 0; i < frames; i++)
            {
                double x = input[i];
                double shadowed = _b0 * x + _b1 * _x1 - _a1 * _y1;
                _x1 = x;
                _y1 = Math.Abs(shadowed) < 1e-30 ? 0 : shadowed;
                _ring[_write] = (float)shadowed;

                double t = (i + 1) / (double)frames;
                double delay = _delay + delayStep * (i + 1);
                double sum = Read(delay);
                for (int e = 0; e < Rho.Length; e++)
                    sum += Rho[e] * Read(delay + _echo[e] + (_targetEcho[e] - _echo[e]) * t);

                output[i] = (float)(sum * EchoNormalization);
                _write = (_write + 1) & _mask;
            }

            _delay = _targetDelay;
            Array.Copy(_targetEcho, _echo, _echo.Length);
        }

        // The shadowed signal `delay` samples before the one just written, interpolated.
        private double Read(double delay)
        {
            double position = _write - delay;
            double floor = Math.Floor(position);
            double fraction = position - floor;
            int a = (int)((long)floor & _mask);
            int b = (a + 1) & _mask;
            return _ring[a] + (_ring[b] - _ring[a]) * fraction;
        }
    }
}
