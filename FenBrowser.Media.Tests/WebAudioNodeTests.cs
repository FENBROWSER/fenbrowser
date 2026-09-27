using FenBrowser.Media.WebAudio;

namespace FenBrowser.Media.Tests;

/// <summary>
/// The WA2 kernels: oscillator and periodic wave (WA 1.27, 1.28), delay and delay cycles
/// (WA 1.17, 2.4), biquad (WA 1.10), IIR (WA 1.22), wave shaper (WA 1.31) and stereo
/// panner (WA 1.29).
/// </summary>
public class WebAudioNodeTests
{
    private const float Rate = 44100f;

    private static float[] Render(AudioGraph graph, int channels, int length) =>
        new OfflineAudioRenderer(graph, channels, length).RenderAll()[0];

    [Fact]
    public void ASineOscillatorMatchesTheMathematicalSine()
    {
        var graph = new AudioGraph(Rate, 1);
        var osc = new OscillatorKernel(graph);
        osc.Frequency.Timeline.SetValueAtTime(100, 0);
        graph.Post(g =>
        {
            g.AddNode(osc);
            g.Connect(osc, 0, g.Destination, 0);
            osc.Start(0);
        });

        var output = Render(graph, 1, 256);
        double worst = 0;
        for (int i = 0; i < output.Length; i++)
            worst = Math.Max(worst, Math.Abs(output[i] - Math.Sin(2 * Math.PI * 100 * i / Rate)));
        Assert.True(worst < 2e-5, $"worst error {worst}");
    }

    [Fact]
    public void ACustomWaveWithoutNormalizationKeepsItsAmplitude()
    {
        var graph = new AudioGraph(Rate, 1);
        var osc = new OscillatorKernel(graph);
        osc.Frequency.Timeline.SetValueAtTime(100, 0);
        osc.SetWave(new PeriodicWaveData([0f, 1f], [0f, 1f], disableNormalization: true));
        graph.Post(g =>
        {
            g.AddNode(osc);
            g.Connect(osc, 0, g.Destination, 0);
            osc.Start(0);
        });

        var output = Render(graph, 1, 256);
        for (int i = 0; i < output.Length; i++)
        {
            double t = 2 * Math.PI * 100 * i / Rate;
            Assert.Equal(Math.Cos(t) + Math.Sin(t), output[i], 4);
        }
    }

    [Fact]
    public void AnOscillatorPastNyquistIsSilent()
    {
        var graph = new AudioGraph(Rate, 1);
        var osc = new OscillatorKernel(graph);
        osc.Frequency.Timeline.SetValueAtTime(Rate / 2, 0);
        graph.Post(g =>
        {
            g.AddNode(osc);
            g.Connect(osc, 0, g.Destination, 0);
            osc.Start(0);
        });

        Assert.All(Render(graph, 1, 128), v => Assert.Equal(0f, v));
    }

    [Fact]
    public void ADelayShorterThanAQuantumDelaysByThatManyFrames()
    {
        var graph = new AudioGraph(Rate, 1);
        var data = Enumerable.Range(1, 64).Select(i => (float)i).ToArray();
        var source = new BufferSourceKernel(graph) { Buffer = new AudioBufferData([data], Rate) };
        var delay = new DelayKernel(graph, 1);
        delay.DelayTime.Timeline.SetValueAtTime(10 / (double)Rate, 0);
        graph.Post(g =>
        {
            g.AddNode(source);
            g.AddNode(delay);
            g.Connect(source, 0, delay, 0);
            g.Connect(delay, 0, g.Destination, 0);
            source.Start(0, 0, double.PositiveInfinity);
        });

        var output = Render(graph, 1, 128);
        Assert.Equal(0f, output[9]);
        Assert.Equal(1f, output[10], 4);
        Assert.Equal(64f, output[73], 4);
    }

    [Fact]
    public void ADelayOutputsMonoSilenceUntilItsDelayedInputArrives()
    {
        // WA 1.17 (WebAudio issue #25): the output's channel count is the delayed input's.
        var graph = new AudioGraph(Rate, 2);
        var ones = Enumerable.Repeat(1f, 1024).ToArray();
        var source = new BufferSourceKernel(graph) { Buffer = new AudioBufferData([ones, ones], Rate) };
        var delay = new DelayKernel(graph, 1);
        delay.DelayTime.Timeline.SetValueAtTime(256 / (double)Rate, 0);
        graph.Post(g =>
        {
            g.AddNode(source);
            g.AddNode(delay);
            g.Connect(source, 0, delay, 0);
            g.Connect(delay, 0, g.Destination, 0);
            source.Start(0, 0, double.PositiveInfinity);
        });

        var renderer = new OfflineAudioRenderer(graph, 2, 128 * 4);
        var counts = new List<int>();
        for (int q = 0; q < 4; q++)
        {
            renderer.RenderQuantum();
            counts.Add(delay.Outputs[0].Bus.ChannelCount);
        }

        // Quantum 1's last sample already interpolates towards frame 0 (the float delay time
        // lands a hair under 256 frames), so only the quanta either side are unambiguous.
        Assert.Equal(1, counts[0]);
        Assert.Equal(2, counts[2]);
        Assert.Equal(2, counts[3]);
    }

    [Fact]
    public void AFeedbackLoopThroughADelayIsNotMutedAndRepeatsEachQuantum()
    {
        var graph = new AudioGraph(Rate, 1);
        var impulse = new float[1];
        impulse[0] = 1;
        var source = new BufferSourceKernel(graph) { Buffer = new AudioBufferData([impulse], Rate) };
        var delay = new DelayKernel(graph, 1);
        var gain = new GainKernel(graph);
        gain.Gain.Timeline.SetValueAtTime(0.5, 0);
        graph.Post(g =>
        {
            g.AddNode(source);
            g.AddNode(delay);
            g.AddNode(gain);
            g.Connect(source, 0, delay, 0);
            g.Connect(delay, 0, gain, 0);
            g.Connect(gain, 0, delay, 0);
            g.Connect(delay, 0, g.Destination, 0);
            source.Start(0, 0, double.PositiveInfinity);
        });

        // In a cycle the delay is clamped to one quantum: the impulse comes out at 128,
        // then at half the level 128 frames later.
        var output = Render(graph, 1, 512);
        Assert.Equal(0f, output[0]);
        Assert.Equal(1f, output[128], 4);
        Assert.Equal(0.5f, output[256], 4);
        Assert.Equal(0.25f, output[384], 4);
    }

    [Fact]
    public void ABiquadFiltersLikeTheDirectForm()
    {
        var graph = new AudioGraph(Rate, 1);
        var data = Enumerable.Range(0, 256).Select(i => (float)Math.Sin(i * 0.3)).ToArray();
        var source = new BufferSourceKernel(graph) { Buffer = new AudioBufferData([data], Rate) };
        var biquad = new BiquadKernel(graph) { Type = BiquadFilterType.Lowpass };
        biquad.Frequency.Timeline.SetValueAtTime(1000, 0);
        graph.Post(g =>
        {
            g.AddNode(source);
            g.AddNode(biquad);
            g.Connect(source, 0, biquad, 0);
            g.Connect(biquad, 0, g.Destination, 0);
            source.Start(0, 0, double.PositiveInfinity);
        });

        var output = Render(graph, 1, 256);
        var c = BiquadKernel.Coefficients(BiquadFilterType.Lowpass, 1000, 0, 1, 0, Rate);
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < 256; i++)
        {
            double y = c.B0 * data[i] + c.B1 * x1 + c.B2 * x2 - c.A1 * y1 - c.A2 * y2;
            x2 = x1;
            x1 = data[i];
            y2 = y1;
            y1 = y;
            Assert.Equal(y, output[i], 5);
        }
    }

    [Fact]
    public void AnIirFilterWithOneFeedforwardCoefficientIsAGain()
    {
        var graph = new AudioGraph(Rate, 1);
        var data = Enumerable.Range(0, 128).Select(i => (float)i).ToArray();
        var source = new BufferSourceKernel(graph) { Buffer = new AudioBufferData([data], Rate) };
        var iir = new IirFilterKernel(graph);
        iir.SetCoefficients([3], [2]);
        graph.Post(g =>
        {
            g.AddNode(source);
            g.AddNode(iir);
            g.Connect(source, 0, iir, 0);
            g.Connect(iir, 0, g.Destination, 0);
            source.Start(0, 0, double.PositiveInfinity);
        });

        var output = Render(graph, 1, 128);
        Assert.Equal(1.5f * 10, output[10], 4);
    }

    [Fact]
    public void AWaveShaperMapsThroughItsCurve()
    {
        Assert.Equal(-1f, WaveShaperKernel.Shape([-1f, 0f, 1f], -1f));
        Assert.Equal(0.5f, WaveShaperKernel.Shape([-1f, 0f, 1f], 0.5f));
        Assert.Equal(1f, WaveShaperKernel.Shape([-1f, 0f, 1f], 5f));
    }

    [Fact]
    public void AStereoPannerHardLeftSendsMonoToTheLeft()
    {
        var graph = new AudioGraph(Rate, 2);
        var source = new ConstantSourceKernel(graph);
        var panner = new StereoPannerKernel(graph);
        panner.Pan.Timeline.SetValueAtTime(-1, 0);
        graph.Post(g =>
        {
            g.AddNode(source);
            g.AddNode(panner);
            g.Connect(source, 0, panner, 0);
            g.Connect(panner, 0, g.Destination, 0);
            source.Start(0);
        });

        var result = new OfflineAudioRenderer(graph, 2, 128).RenderAll();
        Assert.Equal(1f, result[0][0], 5);
        Assert.Equal(0f, result[1][0], 5);
    }
}
