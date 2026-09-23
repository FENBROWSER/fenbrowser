namespace FenBrowser.Media.WebAudio;

/// <summary>
/// An in-place radix-2 complex FFT over double-precision split real and imaginary arrays,
/// with precomputed twiddles. Used by the analyser and the convolver; one instance per
/// size, never shared across threads.
/// </summary>
public sealed class Fft
{
    private readonly int _size;
    private readonly double[] _cos;
    private readonly double[] _sin;
    private readonly int[] _reverse;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(size), "The FFT size must be a power of two.");

        _size = size;
        _cos = new double[size / 2];
        _sin = new double[size / 2];
        for (int i = 0; i < size / 2; i++)
        {
            _cos[i] = Math.Cos(2 * Math.PI * i / size);
            _sin[i] = -Math.Sin(2 * Math.PI * i / size);
        }

        _reverse = new int[size];
        int bits = System.Numerics.BitOperations.Log2((uint)size);
        for (int i = 0; i < size; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++)
                r |= ((i >> b) & 1) << (bits - 1 - b);
            _reverse[i] = r;
        }
    }

    public int Size => _size;

    /// <summary>Forward transform: X[k] = sum over n of x[n] e^(-2 pi i k n / N).</summary>
    public void Forward(double[] real, double[] imag) => Transform(real, imag, inverse: false);

    /// <summary>Inverse transform, scaled by 1/N so Inverse(Forward(x)) is x.</summary>
    public void Inverse(double[] real, double[] imag)
    {
        Transform(real, imag, inverse: true);
        double scale = 1.0 / _size;
        for (int i = 0; i < _size; i++)
        {
            real[i] *= scale;
            imag[i] *= scale;
        }
    }

    private void Transform(double[] real, double[] imag, bool inverse)
    {
        int n = _size;
        for (int i = 0; i < n; i++)
        {
            int j = _reverse[i];
            if (j > i)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        for (int length = 2; length <= n; length <<= 1)
        {
            int half = length >> 1;
            int step = n / length;
            for (int start = 0; start < n; start += length)
            {
                for (int k = 0; k < half; k++)
                {
                    double wr = _cos[k * step];
                    double wi = inverse ? -_sin[k * step] : _sin[k * step];
                    int a = start + k, b = a + half;
                    double tr = real[b] * wr - imag[b] * wi;
                    double ti = real[b] * wi + imag[b] * wr;
                    real[b] = real[a] - tr;
                    imag[b] = imag[a] - ti;
                    real[a] += tr;
                    imag[a] += ti;
                }
            }
        }
    }
}
