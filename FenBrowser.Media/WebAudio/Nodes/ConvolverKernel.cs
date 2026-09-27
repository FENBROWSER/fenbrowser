namespace FenBrowser.Media.WebAudio;

/// <summary>
/// WA 1.14 ConvolverNode: linear convolution of the input with an impulse response,
/// computed by uniformly partitioned overlap-save FFT convolution in blocks of one render
/// quantum, so it adds no latency. A 1-, 2- or 4-channel response is applied as the spec's
/// channel diagrams say; with normalize set the response is scaled as in WA 1.14.3.
/// </summary>
public sealed class ConvolverKernel : AudioNodeKernel
{
    private Engine? _engine;

    public ConvolverKernel(AudioGraph graph)
        : base(graph, 1, 1, 2, ChannelCountMode.ClampedMax, ChannelInterpretation.Speakers)
    {
    }

    /// <summary>
    /// WA 1.14.3 "Normalization": the scale for an impulse response at its own sample rate.
    /// </summary>
    public static double NormalizationScale(float[][] response, float sampleRate)
    {
        const double gainCalibration = 0.00125;
        const double gainCalibrationSampleRate = 44100;
        const double minPower = 0.000125;

        int channels = response.Length;
        int length = response[0].Length;
        double power = 0;
        foreach (var channel in response)
        {
            foreach (float s in channel)
                power += s * s;
        }

        power = Math.Sqrt(power / (channels * length));
        if (!double.IsFinite(power) || double.IsNaN(power) || power < minPower)
            power = minPower;

        double scale = 1 / power;
        scale *= gainCalibration;
        if (sampleRate > 0)
            scale *= gainCalibrationSampleRate / sampleRate;

        // A true-stereo response sums two paths into each output.
        if (channels == 4)
            scale *= 0.5;
        return scale;
    }

    /// <summary>Control message: a new impulse response (or none), already normalized if asked.</summary>
    public void SetResponse(float[][]? response)
    {
        _engine = response is null ? null : new Engine(response, Graph.QuantumFrames);
        _stereoTailEnd = 0;
        _rightPathLive = false;
    }

    // A mono response turns stereo input into stereo output. Up-mixing must stay linear: the
    // right path keeps running, fed the mono input, until the last stereo input's tail has
    // left, and starts from the left path's history when stereo arrives after mono.
    private long _stereoTailEnd;
    private bool _rightPathLive;

    protected override void Process(long frame)
    {
        var input = Inputs[0].Bus;
        var output = Outputs[0].Bus;
        var engine = _engine;
        if (engine is null)
        {
            // WA 1.14: with no buffer the node outputs one channel of silence.
            output.Reset(1);
            return;
        }

        int irChannels = engine.Channels;
        bool stereoInput = input.ChannelCount >= 2;
        if (stereoInput)
            _stereoTailEnd = frame + Graph.QuantumFrames + engine.ResponseLength;
        bool stereoOutput = stereoInput || frame < _stereoTailEnd;
        int outChannels = irChannels == 1 && !stereoOutput ? 1 : 2;
        output.Reset(outChannels);
        // Mono input up-mixed the way this node's channelInterpretation says (WA 4): both
        // sides for "speakers", the left alone for "discrete".
        bool discrete = ChannelInterpretation == ChannelInterpretation.Discrete;
        if (irChannels == 1)
        {
            if (stereoOutput && !_rightPathLive)
            {
                if (discrete)
                    engine.ClearPath(1);
                else
                    engine.CopyPath(0, 1);
            }

            _rightPathLive = stereoOutput;
        }

        bool silent = input.IsSilent;
        var left = silent ? Span<float>.Empty : input.Channel(0);
        var right = silent ? Span<float>.Empty : stereoInput ? input.Channel(1) : discrete && irChannels == 1 ? Span<float>.Empty : left;

        switch (irChannels)
        {
            case 1:
                engine.Convolve(0, 0, left, output.Channel(0), accumulate: false);
                if (outChannels == 2)
                    engine.Convolve(1, 0, right, output.Channel(1), accumulate: false);
                break;
            case 2:
                engine.Convolve(0, 0, left, output.Channel(0), accumulate: false);
                engine.Convolve(1, 1, right, output.Channel(1), accumulate: false);
                break;
            default:
                // True stereo: L = in_L * ir0 + in_R * ir2, R = in_L * ir1 + in_R * ir3.
                engine.Convolve(0, 0, left, output.Channel(0), accumulate: false);
                engine.Convolve(1, 2, right, output.Channel(0), accumulate: true);
                engine.Convolve(2, 1, left, output.Channel(1), accumulate: false);
                engine.Convolve(3, 3, right, output.Channel(1), accumulate: true);
                break;
        }

        output.MarkNotSilent();
    }

    // One overlap-save convolver per (input path, response channel) pair: the response is
    // split into block-sized partitions kept in the frequency domain, and each block of
    // input is transformed once and multiplied against every partition's spectrum.
    private sealed class Engine
    {
        private readonly int _block;
        private readonly int _fftSize;
        private readonly Fft _fft;
        private readonly double[][][] _partitionsReal;
        private readonly double[][][] _partitionsImag;
        private readonly Path[] _paths = new Path[4];
        private readonly double[] _workReal;
        private readonly double[] _workImag;

        public Engine(float[][] response, int block)
        {
            _block = block;
            _fftSize = 2 * NextPowerOfTwo(block);
            _fft = new Fft(_fftSize);
            Channels = response.Length;
            int length = response[0].Length;
            ResponseLength = length;
            int partitions = Math.Max(1, (length + block - 1) / block);
            _partitionsReal = new double[Channels][][];
            _partitionsImag = new double[Channels][][];
            for (int c = 0; c < Channels; c++)
            {
                _partitionsReal[c] = new double[partitions][];
                _partitionsImag[c] = new double[partitions][];
                for (int p = 0; p < partitions; p++)
                {
                    var re = new double[_fftSize];
                    var im = new double[_fftSize];
                    int from = p * block;
                    int count = Math.Min(block, length - from);
                    for (int i = 0; i < count; i++)
                        re[i] = response[c][from + i];
                    _fft.Forward(re, im);
                    _partitionsReal[c][p] = re;
                    _partitionsImag[c][p] = im;
                }
            }

            _workReal = new double[_fftSize];
            _workImag = new double[_fftSize];
            for (int i = 0; i < _paths.Length; i++)
                _paths[i] = new Path(partitions, _fftSize);
        }

        public int Channels { get; }

        public int ResponseLength { get; }

        /// <summary>Forgets a path's input history.</summary>
        public void ClearPath(int index)
        {
            var path = _paths[index];
            Array.Clear(path.Window);
            foreach (var spectrum in path.SpectraReal)
                Array.Clear(spectrum);
            foreach (var spectrum in path.SpectraImag)
                Array.Clear(spectrum);
        }

        /// <summary>Gives path <paramref name="to"/> the input history of path <paramref name="from"/>.</summary>
        public void CopyPath(int from, int to)
        {
            var a = _paths[from];
            var b = _paths[to];
            Array.Copy(a.Window, b.Window, a.Window.Length);
            for (int i = 0; i < a.SpectraReal.Length; i++)
            {
                Array.Copy(a.SpectraReal[i], b.SpectraReal[i], a.SpectraReal[i].Length);
                Array.Copy(a.SpectraImag[i], b.SpectraImag[i], a.SpectraImag[i].Length);
            }

            b.Head = a.Head;
        }

        /// <summary>Convolves one block of <paramref name="input"/> (null = silence) with a response channel.</summary>
        public void Convolve(int pathIndex, int responseChannel, Span<float> input, Span<float> output, bool accumulate)
        {
            var path = _paths[pathIndex];
            int block = _block;

            // Overlap-save: the transform window is the previous block followed by this one.
            Array.Copy(path.Window, block, path.Window, 0, _fftSize - block);
            for (int i = 0; i < block; i++)
                path.Window[_fftSize - block + i] = input.IsEmpty ? 0 : input[i];

            Array.Copy(path.Window, _workReal, _fftSize);
            Array.Clear(_workImag);
            _fft.Forward(_workReal, _workImag);

            // Frequency-domain delay line of input spectra, newest first.
            path.Advance();
            Array.Copy(_workReal, path.SpectraReal[path.Head], _fftSize);
            Array.Copy(_workImag, path.SpectraImag[path.Head], _fftSize);

            Array.Clear(_workReal);
            Array.Clear(_workImag);
            var pr = _partitionsReal[responseChannel];
            var pi = _partitionsImag[responseChannel];
            for (int p = 0; p < pr.Length; p++)
            {
                int slot = (path.Head - p + path.SpectraReal.Length) % path.SpectraReal.Length;
                var xr = path.SpectraReal[slot];
                var xi = path.SpectraImag[slot];
                var hr = pr[p];
                var hi = pi[p];
                for (int k = 0; k < _fftSize; k++)
                {
                    _workReal[k] += xr[k] * hr[k] - xi[k] * hi[k];
                    _workImag[k] += xr[k] * hi[k] + xi[k] * hr[k];
                }
            }

            _fft.Inverse(_workReal, _workImag);

            // The last block of the circular result is the linear convolution's output.
            for (int i = 0; i < block; i++)
            {
                float v = (float)_workReal[_fftSize - block + i];
                output[i] = accumulate ? output[i] + v : v;
            }
        }

        private static int NextPowerOfTwo(int n)
        {
            int p = 1;
            while (p < n)
                p <<= 1;
            return p;
        }

        private sealed class Path
        {
            public Path(int partitions, int fftSize)
            {
                Window = new double[fftSize];
                SpectraReal = new double[partitions][];
                SpectraImag = new double[partitions][];
                for (int i = 0; i < partitions; i++)
                {
                    SpectraReal[i] = new double[fftSize];
                    SpectraImag[i] = new double[fftSize];
                }
            }

            public double[] Window { get; }

            public double[][] SpectraReal { get; }

            public double[][] SpectraImag { get; }

            public int Head { get; set; }

            public void Advance() => Head = (Head + 1) % SpectraReal.Length;
        }
    }
}
