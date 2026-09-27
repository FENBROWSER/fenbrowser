namespace FenBrowser.Media.WebAudio;

/// <summary>
/// WA 1.8 AudioDestinationNode: its input, mixed to its channel count, is the context's
/// output. It has one output that nothing reads.
/// </summary>
public sealed class DestinationKernel : AudioNodeKernel
{
    internal DestinationKernel(AudioGraph graph, int channels)
        : base(graph, 1, 1, channels, ChannelCountMode.Explicit, ChannelInterpretation.Speakers)
    {
    }

    /// <summary>The rendered quantum.</summary>
    public AudioBus Output => Inputs[0].Bus;

    protected override void Process(long frame) => Outputs[0].Bus.Reset(1);
}

/// <summary>WA 1.19 GainNode: the input times the a-rate <c>gain</c>.</summary>
public sealed class GainKernel : AudioNodeKernel
{
    public GainKernel(AudioGraph graph)
        : base(graph, 1, 1, 2, ChannelCountMode.Max, ChannelInterpretation.Speakers)
    {
        Gain = AddParam("gain", 1f, float.MinValue, float.MaxValue, AutomationRate.ARate);
    }

    public AudioParamKernel Gain { get; }

    protected override void Process(long frame)
    {
        var input = Inputs[0].Bus;
        var output = Outputs[0].Bus;
        output.Reset(input.ChannelCount);
        if (input.IsSilent)
            return;

        var gain = Gain.Values;
        bool constant = Gain.IsConstant;
        float g = gain[0];
        for (int c = 0; c < input.ChannelCount; c++)
        {
            var src = input.Channel(c);
            var dst = output.Channel(c);
            if (constant)
            {
                for (int i = 0; i < dst.Length; i++)
                    dst[i] = src[i] * g;
            }
            else
            {
                for (int i = 0; i < dst.Length; i++)
                    dst[i] = src[i] * gain[i];
            }
        }

        output.MarkNotSilent();
    }
}

/// <summary>
/// WA 1.26 ChannelSplitterNode: each channel of the input to its own mono output. The input
/// is explicit and discrete with one channel per output.
/// </summary>
public sealed class ChannelSplitterKernel : AudioNodeKernel
{
    public ChannelSplitterKernel(AudioGraph graph, int outputs)
        : base(graph, 1, outputs, outputs, ChannelCountMode.Explicit, ChannelInterpretation.Discrete)
    {
    }

    protected override void Process(long frame)
    {
        var input = Inputs[0].Bus;
        for (int o = 0; o < Outputs.Length; o++)
        {
            var output = Outputs[o].Bus;
            output.Reset(1);
            if (o < input.ChannelCount && !input.IsSilent)
            {
                input.Channel(o).CopyTo(output.Channel(0));
                output.MarkNotSilent();
            }
        }
    }
}

/// <summary>
/// WA 1.25 ChannelMergerNode: each mono input becomes one channel of the output. Each input
/// is explicit, one channel, speakers - a stereo connection is mixed down to mono.
/// </summary>
public sealed class ChannelMergerKernel : AudioNodeKernel
{
    public ChannelMergerKernel(AudioGraph graph, int inputs)
        : base(graph, inputs, 1, 1, ChannelCountMode.Explicit, ChannelInterpretation.Speakers)
    {
    }

    protected override void Process(long frame)
    {
        var output = Outputs[0].Bus;
        output.Reset(Inputs.Length);
        for (int i = 0; i < Inputs.Length; i++)
        {
            var input = Inputs[i].Bus;
            if (input.IsSilent)
                continue;
            input.Channel(0).CopyTo(output.Channel(i));
            output.MarkNotSilent();
        }
    }
}

/// <summary>
/// WA 1.3 AudioScheduledSourceNode: start and stop times, the frames of a quantum the source
/// plays in, and the one-time ended notification.
/// </summary>
public abstract class ScheduledSourceKernel : AudioNodeKernel
{
    protected ScheduledSourceKernel(AudioGraph graph, int channelCount)
        : base(graph, 0, 1, channelCount, ChannelCountMode.Max, ChannelInterpretation.Speakers)
    {
        IsSilentOutput = true;
    }

    /// <summary>The time start() asked for, or NaN until it is called.</summary>
    public double StartTime { get; private set; } = double.NaN;

    public double StopTime { get; private set; } = double.PositiveInfinity;

    public bool HasStarted => !double.IsNaN(StartTime);

    public bool HasEnded { get; private set; }

    /// <summary>Control message for start(); a time in the past means now.</summary>
    public virtual void Start(double when) => StartTime = Math.Max(0, when);

    /// <summary>Control message for stop(); a later stop() replaces an earlier one.</summary>
    public void Stop(double when) => StopTime = Math.Max(0, when);

    protected long StartFrame => TimeToFrame(StartTime, Graph.SampleRate);

    protected long StopFrame => double.IsPositiveInfinity(StopTime) ? long.MaxValue : TimeToFrame(StopTime, Graph.SampleRate);

    /// <summary>
    /// The first frame at or after <paramref name="time"/>. A time that is a whole frame up
    /// to round-off (frame / sampleRate computed in script) lands on that frame, not the next.
    /// </summary>
    public static long TimeToFrame(double time, double sampleRate)
    {
        double exact = time * sampleRate;
        double nearest = Math.Round(exact);
        return Math.Abs(exact - nearest) < 1e-6 ? (long)nearest : (long)Math.Ceiling(exact);
    }

    /// <summary>
    /// The range [from, to) of this quantum's frames in which the source plays, relative to
    /// the quantum. Empty when it has not started yet or has already ended.
    /// </summary>
    protected (int From, int To) PlayingRange(long frame)
    {
        if (!HasStarted || HasEnded)
            return (0, 0);

        long end = frame + Graph.QuantumFrames;
        long from = Math.Max(frame, StartFrame);
        long to = Math.Min(end, StopFrame);
        if (from >= to)
            return (0, 0);
        return ((int)(from - frame), (int)(to - frame));
    }

    /// <summary>True once the quantum starting at <paramref name="frame"/> reaches the stop time.</summary>
    protected bool ReachesStop(long frame) =>
        HasStarted && frame + Graph.QuantumFrames >= StopFrame;

    /// <summary>Marks the source finished and tells the control thread, once.</summary>
    protected void Finish()
    {
        if (HasEnded)
            return;
        HasEnded = true;
        Graph.RaiseSourceEnded(this);
    }

    /// <summary>Writes one channel of silence and marks the output as not actively processing.</summary>
    protected void OutputSilence()
    {
        Outputs[0].Bus.Reset(1);
        IsSilentOutput = true;
    }
}

/// <summary>WA 1.13 ConstantSourceNode: the a-rate <c>offset</c> while playing.</summary>
public sealed class ConstantSourceKernel : ScheduledSourceKernel
{
    public ConstantSourceKernel(AudioGraph graph)
        : base(graph, 2)
    {
        Offset = AddParam("offset", 1f, float.MinValue, float.MaxValue, AutomationRate.ARate);
    }

    public AudioParamKernel Offset { get; }

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
        var offset = Offset.Values;
        for (int i = from; i < to; i++)
            dst[i] = offset[i];
        output.MarkNotSilent();

        if (ReachesStop(frame))
            Finish();
    }
}
