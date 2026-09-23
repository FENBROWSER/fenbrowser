using FenBrowser.Media.WebAudio;

namespace FenBrowser.Media.Tests;

/// <summary>
/// The WA3 kernels: FFT, convolver (WA 1.14), analyser (WA 1.8), panner and listener
/// (WA 1.23, 1.24, 6) and compressor (WA 1.15).
/// </summary>
public class WebAudioSpatialTests
{
    private const float Rate = 44100f;

    [Fact]
    public void TheFftRoundTrips()
    {
        var fft = new Fft(64);
        var real = Enumerable.Range(0, 64).Select(i => Math.Sin(i * 0.7) + i * 0.01).ToArray();
        var original = (double[])real.Clone();
        var imag = new double[64];
        fft.Forward(real, imag);
        fft.Inverse(real, imag);
        for (int i = 0; i < 64; i++)
            Assert.Equal(original[i], real[i], 10);
    }

    [Fact]
    public void ConvolvingWithADelayedImpulseShiftsTheSignal()
    {
        var graph = new AudioGraph(Rate, 1);
        var data = Enumerable.Range(1, 300).Select(i => (float)i).ToArray();
        var source = new BufferSourceKernel(graph) { Buffer = new AudioBufferData([data], Rate) };
        var convolver = new ConvolverKernel(graph);
        var impulse = new float[200];
        impulse[150] = 1;
        convolver.SetResponse([impulse]);
        graph.Post(g =>
        {
            g.AddNode(source);
            g.AddNode(convolver);
            g.Connect(source, 0, convolver, 0);
            g.Connect(convolver, 0, g.Destination, 0);
            source.Start(0, 0, double.PositiveInfinity);
        });

        var output = new OfflineAudioRenderer(graph, 1, 512).RenderAll()[0];
        Assert.Equal(0f, output[149], 3);
        Assert.Equal(1f, output[150], 3);
        Assert.Equal(300f, output[449], 2);
        Assert.Equal(0f, output[450], 3);
    }

    [Fact]
    public void ALongImpulseResponseMatchesDirectConvolution()
    {
        var graph = new AudioGraph(Rate, 1);
        var random = new Random(7);
        var data = Enumerable.Range(0, 640).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();
        var ir = Enumerable.Range(0, 700).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray();
        var source = new BufferSourceKernel(graph) { Buffer = new AudioBufferData([data], Rate) };
        var convolver = new ConvolverKernel(graph);
        convolver.SetResponse([ir]);
        graph.Post(g =>
        {
            g.AddNode(source);
            g.AddNode(convolver);
            g.Connect(source, 0, convolver, 0);
            g.Connect(convolver, 0, g.Destination, 0);
            source.Start(0, 0, double.PositiveInfinity);
        });

        var output = new OfflineAudioRenderer(graph, 1, 1280).RenderAll()[0];
        for (int n = 0; n < 1280; n++)
        {
            double expected = 0;
            for (int k = 0; k < ir.Length; k++)
            {
                int i = n - k;
                if (i >= 0 && i < data.Length)
                    expected += data[i] * (double)ir[k];
            }

            Assert.Equal(expected, output[n], 3);
        }
    }

    [Fact]
    public void ConvolverNormalizationFollowsTheSpecFormula()
    {
        var ir = new float[] { 1f, 0f, 0f, 0f };
        // power = sqrt(1 / 4) = 0.5; scale = 1 / 0.5 * 0.00125 * 44100 / 44100.
        Assert.Equal(0.0025, ConvolverKernel.NormalizationScale([ir], 44100f), 12);
    }

    [Fact]
    public void ASourceToTheListenersRightIsAtNinetyDegreesAzimuth()
    {
        double azimuth = PannerKernel.Azimuth(new Vec(1, 0, 0), new Vec(0, 0, 0), new Vec(0, 0, -1), new Vec(0, 1, 0));
        Assert.Equal(90, azimuth, 6);
        double ahead = PannerKernel.Azimuth(new Vec(0, 0, -1), new Vec(0, 0, 0), new Vec(0, 0, -1), new Vec(0, 1, 0));
        Assert.Equal(0, ahead, 6);
    }

    [Fact]
    public void ThePannerDistanceModelsFollowTheSpec()
    {
        var graph = new AudioGraph(Rate, 2);
        var panner = new PannerKernel(graph) { RefDistance = 1, RolloffFactor = 1, MaxDistance = 10 };
        panner.DistanceModel = DistanceModel.Inverse;
        Assert.Equal(0.5, panner.DistanceGain(2), 9);
        panner.DistanceModel = DistanceModel.Exponential;
        Assert.Equal(0.25, panner.DistanceGain(4), 9);
        panner.DistanceModel = DistanceModel.Linear;
        Assert.Equal(0.5, panner.DistanceGain(5.5), 9);
    }

    [Fact]
    public void AHardRightMonoSourcePansFully()
    {
        var graph = new AudioGraph(Rate, 2);
        var source = new ConstantSourceKernel(graph);
        var panner = new PannerKernel(graph);
        panner.PositionX.Timeline.SetValueAtTime(1, 0);
        panner.RefDistance = 1;
        graph.Post(g =>
        {
            g.AddNode(source);
            g.AddNode(panner);
            g.Connect(source, 0, panner, 0);
            g.Connect(panner, 0, g.Destination, 0);
            source.Start(0);
        });

        var result = new OfflineAudioRenderer(graph, 2, 128).RenderAll();
        Assert.Equal(0f, result[0][0], 5);
        Assert.Equal(1f, result[1][0], 5);
    }

    [Fact]
    public void ALoudSignalMakesTheCompressorReduceGain()
    {
        var graph = new AudioGraph(Rate, 1);
        var source = new ConstantSourceKernel(graph);
        var compressor = new CompressorKernel(graph);
        graph.Post(g =>
        {
            g.AddNode(source);
            g.AddNode(compressor);
            g.Connect(source, 0, compressor, 0);
            g.Connect(compressor, 0, g.Destination, 0);
            source.Start(0);
        });

        new OfflineAudioRenderer(graph, 1, 4096).RenderAll();
        Assert.True(compressor.Reduction < -10, $"reduction {compressor.Reduction}");
    }

    [Fact]
    public void TheAnalyserSeesASineAtItsBin()
    {
        var graph = new AudioGraph(Rate, 1);
        var osc = new OscillatorKernel(graph);
        double binWidth = Rate / 2048.0;
        osc.Frequency.Timeline.SetValueAtTime(100 * binWidth, 0);
        var analyser = new AnalyserKernel(graph) { SmoothingTimeConstant = 0 };
        graph.Post(g =>
        {
            g.AddNode(osc);
            g.AddNode(analyser);
            g.Connect(osc, 0, analyser, 0);
            osc.Start(0);
        });

        new OfflineAudioRenderer(graph, 1, 4096).RenderAll();
        var decibels = analyser.FrequencyDecibels(graph.CurrentFrame);
        int peak = Array.IndexOf(decibels, decibels.Max());
        Assert.Equal(100, peak);
    }
    // An impulse through an HRTF panner at (x, y, z), left and right ear.
    private static (float[] Left, float[] Right) HrtfImpulse(float x, float y, float z)
    {
        var graph = new AudioGraph(Rate, 2);
        var impulse = new float[512];
        impulse[0] = 1;
        var source = new BufferSourceKernel(graph) { Buffer = new AudioBufferData([impulse], Rate) };
        var panner = new PannerKernel(graph) { PanningModel = PanningModel.Hrtf, RolloffFactor = 0 };
        panner.PositionX.Timeline.SetValueAtTime(x, 0);
        panner.PositionY.Timeline.SetValueAtTime(y, 0);
        panner.PositionZ.Timeline.SetValueAtTime(z, 0);
        graph.Post(g =>
        {
            g.AddNode(source);
            g.AddNode(panner);
            g.Connect(source, 0, panner, 0);
            g.Connect(panner, 0, g.Destination, 0);
            source.Start(0, 0, double.PositiveInfinity);
        });

        var result = new OfflineAudioRenderer(graph, 2, 512).RenderAll();
        return (result[0], result[1]);
    }

    private static int FirstNonZero(float[] data) => Array.FindIndex(data, v => Math.Abs(v) > 1e-6f);

    private static double Energy(float[] data) => data.Sum(v => (double)v * v);

    [Fact]
    public void AnHrtfSourceStraightAheadReachesBothEarsAlike()
    {
        var (left, right) = HrtfImpulse(0, 0, -1);
        for (int i = 0; i < left.Length; i++)
            Assert.Equal(left[i], right[i], 6);
        Assert.True(Energy(left) > 0.1);
    }

    [Fact]
    public void AnHrtfSourceToTheRightIsLouderAndEarlierInTheRightEar()
    {
        var (left, right) = HrtfImpulse(1, 0, 0);
        Assert.True(Energy(right) > 2 * Energy(left), $"right {Energy(right)} left {Energy(left)}");
        // About 0.65 ms of interaural delay at 44.1 kHz.
        Assert.InRange(FirstNonZero(left) - FirstNonZero(right), 20, 35);
    }

    [Fact]
    public void AnHrtfSourceAboveSoundsDifferentFromOneBelow()
    {
        var above = HrtfImpulse(0, 1, -1);
        var below = HrtfImpulse(0, -1, -1);
        double difference = 0;
        for (int i = 0; i < above.Left.Length; i++)
            difference = Math.Max(difference, Math.Abs(above.Left[i] - below.Left[i]));
        Assert.True(difference > 0.05, $"max difference {difference}");
        // Same distance and azimuth: the level barely changes, the spectrum does.
        Assert.InRange(Energy(above.Left) / Energy(below.Left), 0.5, 2);
    }
}
