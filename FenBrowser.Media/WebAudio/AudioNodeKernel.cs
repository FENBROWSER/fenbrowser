namespace FenBrowser.Media.WebAudio;

/// <summary>One upstream connection: an output of <see cref="Node"/>.</summary>
public readonly record struct AudioConnection(AudioNodeKernel Node, int Output);

/// <summary>
/// An input of a node (WA 1.5): the connections into it and the bus they are mixed into
/// each quantum under the node's channel rules (WA 4).
/// </summary>
public sealed class AudioNodeInput
{
    private readonly AudioNodeKernel _owner;

    internal AudioNodeInput(AudioNodeKernel owner)
    {
        _owner = owner;
        Bus = new AudioBus(WebAudioLimits.MaxChannels);
    }

    public List<AudioConnection> Connections { get; } = [];

    /// <summary>The mixed input for the current quantum.</summary>
    public AudioBus Bus { get; }

    /// <summary>True when at least one connected output is carrying signal this quantum.</summary>
    public bool HasSignal { get; private set; }

    internal void Pull()
    {
        int maxChannels = 0;
        foreach (var c in Connections)
        {
            var bus = c.Node.Outputs[c.Output].Bus;
            if (!c.Node.IsSilentOutput)
                maxChannels = Math.Max(maxChannels, bus.ChannelCount);
        }

        int channels = ChannelMixing.ComputeChannelCount(_owner.ChannelCountMode, _owner.ChannelCount, maxChannels);
        Bus.Reset(channels);
        HasSignal = false;
        foreach (var c in Connections)
        {
            if (c.Node.IsSilentOutput)
                continue;
            var bus = c.Node.Outputs[c.Output].Bus;
            ChannelMixing.MixInto(bus, Bus, _owner.ChannelInterpretation);
            HasSignal |= !bus.IsSilent;
        }
    }
}

/// <summary>An output of a node: the bus it writes each quantum.</summary>
public sealed class AudioNodeOutput
{
    internal AudioNodeOutput()
    {
        Bus = new AudioBus(WebAudioLimits.MaxChannels);
    }

    public AudioBus Bus { get; }
}

/// <summary>
/// The rendering-thread half of an <c>AudioNode</c> (WA 1.5). Everything here is touched
/// only by the rendering thread; the control thread changes it through messages posted to
/// <see cref="AudioGraph.Post"/>.
/// </summary>
public abstract class AudioNodeKernel
{
    private readonly List<AudioParamKernel> _params = [];

    protected AudioNodeKernel(
        AudioGraph graph,
        int inputs,
        int outputs,
        int channelCount,
        ChannelCountMode mode,
        ChannelInterpretation interpretation)
    {
        Graph = graph;
        Id = graph.NextNodeId();
        Inputs = new AudioNodeInput[inputs];
        for (int i = 0; i < inputs; i++)
            Inputs[i] = new AudioNodeInput(this);
        Outputs = new AudioNodeOutput[outputs];
        for (int i = 0; i < outputs; i++)
            Outputs[i] = new AudioNodeOutput();
        ChannelCount = channelCount;
        ChannelCountMode = mode;
        ChannelInterpretation = interpretation;
    }

    public AudioGraph Graph { get; }

    public int Id { get; }

    public AudioNodeInput[] Inputs { get; }

    public AudioNodeOutput[] Outputs { get; }

    public IReadOnlyList<AudioParamKernel> Params => _params;

    public int ChannelCount { get; set; }

    public ChannelCountMode ChannelCountMode { get; set; }

    public ChannelInterpretation ChannelInterpretation { get; set; }

    /// <summary>
    /// True while the node is part of a cycle with no delay in it (WA 2.4): it renders
    /// silence and its outputs count as not connected.
    /// </summary>
    internal bool MutedByCycle { get; set; }

    /// <summary>
    /// True when this quantum's outputs are silence that downstream inputs should not count
    /// towards their channel count - a source that has not started or has finished, or a
    /// node muted by a cycle (WA 1.5 "actively processing").
    /// </summary>
    public bool IsSilentOutput { get; protected set; }

    protected AudioParamKernel AddParam(string name, float defaultValue, float minValue, float maxValue, AutomationRate rate, bool rateFixed = false)
    {
        var param = new AudioParamKernel(this, name, defaultValue, minValue, maxValue, rate, rateFixed);
        _params.Add(param);
        return param;
    }

    public AudioParamKernel? FindParam(string name)
    {
        foreach (var p in _params)
        {
            if (p.Name == name)
                return p;
        }

        return null;
    }

    internal void RenderQuantum(long frame)
    {
        foreach (var input in Inputs)
            input.Pull();

        foreach (var param in _params)
            param.Compute(frame, Graph.SampleRate);

        if (MutedByCycle)
        {
            foreach (var output in Outputs)
                output.Bus.Reset(1);
            IsSilentOutput = true;
            return;
        }

        IsSilentOutput = false;
        Process(frame);
    }

    /// <summary>Produces this quantum's outputs from <see cref="Inputs"/> and the computed params.</summary>
    protected abstract void Process(long frame);

    /// <summary>Called on the rendering thread after the node has been removed from the graph.</summary>
    internal virtual void OnRemoved()
    {
    }
}
