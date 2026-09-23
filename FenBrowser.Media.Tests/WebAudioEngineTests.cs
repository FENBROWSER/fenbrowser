using FenBrowser.Media.WebAudio;

namespace FenBrowser.Media.Tests;

/// <summary>
/// The Web Audio engine core (WA1): channel mixing (WA 4), the AudioParam timeline
/// (WA 1.6), graph order and cycles (WA 2.4), and the WA1 nodes rendered offline.
/// </summary>
public class WebAudioEngineTests
{
    private const float Rate = 48000f;
    private const double RateD = 48000.0;

    private sealed class EndedRecorder : IWebAudioEventSink
    {
        public List<int> Ended { get; } = [];

        public void SourceEnded(int nodeId) => Ended.Add(nodeId);
    }

    private static AudioBus Bus(params float[][] channels)
    {
        var bus = new AudioBus(WebAudioLimits.MaxChannels);
        bus.Reset(channels.Length);
        for (int c = 0; c < channels.Length; c++)
            bus.Channel(c).Fill(channels[c][0]);
        bus.MarkNotSilent();
        return bus;
    }

    [Fact]
    public void StereoDownMixesToMonoByAveraging()
    {
        var source = Bus([1f], [0.5f]);
        var destination = new AudioBus(WebAudioLimits.MaxChannels);
        destination.Reset(1);
        ChannelMixing.MixInto(source, destination, ChannelInterpretation.Speakers);
        Assert.Equal(0.75f, destination.Channel(0)[0]);
    }

    [Fact]
    public void FivePointOneDownMixesToStereoWithTheCentreAndSurroundsAtMinusThreeDecibels()
    {
        var source = Bus([1f], [2f], [4f], [8f], [16f], [32f]);
        var destination = new AudioBus(WebAudioLimits.MaxChannels);
        destination.Reset(2);
        ChannelMixing.MixInto(source, destination, ChannelInterpretation.Speakers);
        float s = MathF.Sqrt(0.5f);
        Assert.Equal(1f + s * (4f + 16f), destination.Channel(0)[0], 5);
        Assert.Equal(2f + s * (4f + 32f), destination.Channel(1)[0], 5);
    }

    [Fact]
    public void DiscreteMixingDropsExtraChannels()
    {
        var source = Bus([1f], [2f], [3f]);
        var destination = new AudioBus(WebAudioLimits.MaxChannels);
        destination.Reset(2);
        ChannelMixing.MixInto(source, destination, ChannelInterpretation.Discrete);
        Assert.Equal(1f, destination.Channel(0)[0]);
        Assert.Equal(2f, destination.Channel(1)[0]);
    }

    [Fact]
    public void ALinearRampRunsFromThePreviousEvent()
    {
        var timeline = new AudioParamTimeline(0);
        timeline.SetValueAtTime(1, 1);
        timeline.LinearRampToValueAtTime(3, 2, 0, 0);
        Assert.Equal(0, timeline.ValueAt(0.5));
        Assert.Equal(1, timeline.ValueAt(1));
        Assert.Equal(2, timeline.ValueAt(1.5), 9);
        Assert.Equal(3, timeline.ValueAt(2));
        Assert.Equal(3, timeline.ValueAt(5));
    }

    [Fact]
    public void AnExponentialRampFollowsTheGeometricCurveAndHoldsWhenItCannotCrossZero()
    {
        var timeline = new AudioParamTimeline(0);
        timeline.SetValueAtTime(1, 0);
        timeline.ExponentialRampToValueAtTime(4, 2, 0, 0);
        Assert.Equal(2, timeline.ValueAt(1), 9);

        var signs = new AudioParamTimeline(0);
        signs.SetValueAtTime(-1, 0);
        signs.ExponentialRampToValueAtTime(1, 1, 0, 0);
        Assert.Equal(-1, signs.ValueAt(0.5));
        Assert.Equal(1, signs.ValueAt(1));
    }

    [Fact]
    public void SetTargetApproachesTheTargetExponentially()
    {
        var timeline = new AudioParamTimeline(1);
        timeline.SetTargetAtTime(0, 1, 0.5);
        Assert.Equal(1, timeline.ValueAt(1));
        Assert.Equal(Math.Exp(-2), timeline.ValueAt(2), 9);
    }

    [Fact]
    public void AValueCurveInterpolatesAndHoldsItsLastValue()
    {
        var timeline = new AudioParamTimeline(0);
        Assert.Equal(AutomationError.None, timeline.SetValueCurveAtTime([0f, 2f, 4f], 1, 2));
        Assert.Equal(1, timeline.ValueAt(1.5), 9);
        Assert.Equal(3, timeline.ValueAt(2.5), 9);
        Assert.Equal(4, timeline.ValueAt(3));
        Assert.Equal(4, timeline.ValueAt(10));
    }

    [Fact]
    public void AnEventInsideAValueCurveIsRefused()
    {
        var timeline = new AudioParamTimeline(0);
        timeline.SetValueCurveAtTime([0f, 1f], 1, 2);
        Assert.Equal(AutomationError.NotSupported, timeline.SetValueAtTime(5, 2));
        Assert.Equal(AutomationError.None, timeline.SetValueAtTime(5, 3));
        Assert.Equal(AutomationError.NotSupported, timeline.SetValueCurveAtTime([0f, 1f], 0.5, 1));
    }

    [Fact]
    public void CancelAndHoldStopsARampWhereItIs()
    {
        var timeline = new AudioParamTimeline(0);
        timeline.SetValueAtTime(0, 0);
        timeline.LinearRampToValueAtTime(10, 10, 0, 0);
        timeline.CancelAndHoldAtTime(4);
        Assert.Equal(2, timeline.ValueAt(2), 9);
        Assert.Equal(4, timeline.ValueAt(4), 9);
        Assert.Equal(4, timeline.ValueAt(8), 9);
    }

    [Fact]
    public void CancelScheduledValuesRemovesLaterEvents()
    {
        var timeline = new AudioParamTimeline(0);
        timeline.SetValueAtTime(1, 1);
        timeline.SetValueAtTime(2, 2);
        timeline.CancelScheduledValues(2);
        Assert.Equal(1, timeline.ValueAt(3));
        Assert.Equal(1, timeline.Count);
    }

    [Fact]
    public void AConstantSourceThroughAGainRendersFromItsStartFrame()
    {
        var graph = new AudioGraph(Rate, 1);
        var source = new ConstantSourceKernel(graph);
        var gain = new GainKernel(graph);
        gain.Gain.Timeline.SetValueAtTime(0.5, 0);
        graph.Post(g =>
        {
            g.AddNode(source);
            g.AddNode(gain);
            g.Connect(source, 0, gain, 0);
            g.Connect(gain, 0, g.Destination, 0);
            source.Start(10 / RateD);
        });

        var result = new OfflineAudioRenderer(graph, 1, 256).RenderAll();
        Assert.Equal(0f, result[0][9]);
        Assert.Equal(0.5f, result[0][10]);
        Assert.Equal(0.5f, result[0][255]);
    }

    [Fact]
    public void ANodeInACycleWithoutADelayIsMuted()
    {
        var graph = new AudioGraph(Rate, 1);
        var source = new ConstantSourceKernel(graph);
        var a = new GainKernel(graph);
        var b = new GainKernel(graph);
        graph.Post(g =>
        {
            g.AddNode(source);
            g.AddNode(a);
            g.AddNode(b);
            g.Connect(source, 0, a, 0);
            g.Connect(a, 0, b, 0);
            g.Connect(b, 0, a, 0);
            g.Connect(b, 0, g.Destination, 0);
            source.Start(0);
        });

        var result = new OfflineAudioRenderer(graph, 1, 128).RenderAll();
        Assert.All(result[0], v => Assert.Equal(0f, v));
    }

    [Fact]
    public void ASplitterAndMergerCanSwapChannels()
    {
        var graph = new AudioGraph(Rate, 2);
        var source = new BufferSourceKernel(graph)
        {
            Buffer = new AudioBufferData([Enumerable.Repeat(1f, 256).ToArray(), Enumerable.Repeat(2f, 256).ToArray()], Rate),
        };
        var splitter = new ChannelSplitterKernel(graph, 2);
        var merger = new ChannelMergerKernel(graph, 2);
        graph.Post(g =>
        {
            g.AddNode(source);
            g.AddNode(splitter);
            g.AddNode(merger);
            g.Connect(source, 0, splitter, 0);
            g.Connect(splitter, 0, merger, 1);
            g.Connect(splitter, 1, merger, 0);
            g.Connect(merger, 0, g.Destination, 0);
            source.Start(0, 0, double.PositiveInfinity);
        });

        var result = new OfflineAudioRenderer(graph, 2, 128).RenderAll();
        Assert.Equal(2f, result[0][0]);
        Assert.Equal(1f, result[1][0]);
    }

    [Fact]
    public void ABufferSourcePlaysItsSamplesThenEnds()
    {
        var events = new EndedRecorder();
        var graph = new AudioGraph(Rate, 1, events);
        var data = Enumerable.Range(0, 100).Select(i => (float)i).ToArray();
        var source = new BufferSourceKernel(graph) { Buffer = new AudioBufferData([data], Rate) };
        graph.Post(g =>
        {
            g.AddNode(source);
            g.Connect(source, 0, g.Destination, 0);
            source.Start(0, 0, double.PositiveInfinity);
        });

        var result = new OfflineAudioRenderer(graph, 1, 256).RenderAll();
        for (int i = 0; i < 100; i++)
            Assert.Equal(i, result[0][i]);
        Assert.Equal(0f, result[0][100]);
        Assert.Equal([source.Id], events.Ended);
    }

    [Fact]
    public void ABufferSourceStartedBetweenFramesInterpolates()
    {
        var graph = new AudioGraph(Rate, 1);
        var data = Enumerable.Range(0, 64).Select(i => (float)i).ToArray();
        var source = new BufferSourceKernel(graph) { Buffer = new AudioBufferData([data], Rate) };
        graph.Post(g =>
        {
            g.AddNode(source);
            g.Connect(source, 0, g.Destination, 0);
            source.Start(0.5 / RateD, 0, double.PositiveInfinity);
        });

        var result = new OfflineAudioRenderer(graph, 1, 128).RenderAll();
        Assert.Equal(0f, result[0][0]);
        Assert.Equal(0.5f, result[0][1], 5);
        Assert.Equal(1.5f, result[0][2], 5);
    }

    [Fact]
    public void ALoopingBufferSourceWrapsWithinItsLoop()
    {
        var graph = new AudioGraph(Rate, 1);
        var data = Enumerable.Range(0, 8).Select(i => (float)i).ToArray();
        var source = new BufferSourceKernel(graph)
        {
            Buffer = new AudioBufferData([data], Rate),
            Loop = true,
            LoopStart = 2 / RateD,
            LoopEnd = 6 / RateD,
        };
        graph.Post(g =>
        {
            g.AddNode(source);
            g.Connect(source, 0, g.Destination, 0);
            source.Start(0, 0, double.PositiveInfinity);
        });

        var result = new OfflineAudioRenderer(graph, 1, 128).RenderAll();
        Assert.Equal(new float[] { 0, 1, 2, 3, 4, 5, 2, 3, 4, 5, 2 }, result[0][..11]);
    }
}
