using System.Collections.Concurrent;

namespace FenBrowser.Media.WebAudio;

/// <summary>What the rendering thread tells the control thread (WA 2.2): each call is queued there as a task.</summary>
public interface IWebAudioEventSink
{
    /// <summary>A scheduled source node has finished playing (WA 1.9 "ended").</summary>
    void SourceEnded(int nodeId);
}

/// <summary>
/// The rendering-thread graph of one <c>BaseAudioContext</c> (WA 2.4): its nodes, the
/// control message queue, and one render quantum at a time. Only the rendering thread
/// touches nodes and connections; the control thread reaches them through
/// <see cref="Post"/>, drained at the start of every quantum.
/// </summary>
public sealed class AudioGraph
{
    private readonly ConcurrentQueue<Action<AudioGraph>> _controlMessages = new();
    private readonly List<AudioNodeKernel> _nodes = [];
    private readonly List<AudioNodeKernel> _order = [];
    private bool _orderDirty = true;
    private long _currentFrame;
    private int _nextNodeId;

    public AudioGraph(float sampleRate, int destinationChannels, IWebAudioEventSink? events = null)
    {
        if (!WebAudioLimits.IsValidSampleRate(sampleRate))
            throw new ArgumentOutOfRangeException(nameof(sampleRate));

        SampleRate = sampleRate;
        Events = events;
        Destination = new DestinationKernel(this, destinationChannels);
        _nodes.Add(Destination);
    }

    public float SampleRate { get; }

    public IWebAudioEventSink? Events { get; }

    public DestinationKernel Destination { get; }

    /// <summary>The first frame of the next quantum to render; safe to read from any thread.</summary>
    public long CurrentFrame => Interlocked.Read(ref _currentFrame);

    /// <summary>WA 1.1 currentTime: the start of the next quantum, in seconds.</summary>
    public double CurrentTime => CurrentFrame / (double)SampleRate;

    internal int NextNodeId() => Interlocked.Increment(ref _nextNodeId);

    /// <summary>Queues a control message; it runs on the rendering thread before the next quantum.</summary>
    public void Post(Action<AudioGraph> message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _controlMessages.Enqueue(message);
    }

    /// <summary>Adds a node created on the control thread; call from a control message.</summary>
    public void AddNode(AudioNodeKernel node)
    {
        _nodes.Add(node);
        _orderDirty = true;
    }

    /// <summary>WA 1.5.3 connect(AudioNode): output <paramref name="output"/> of <paramref name="source"/> to an input.</summary>
    public void Connect(AudioNodeKernel source, int output, AudioNodeKernel destination, int input)
    {
        var connection = new AudioConnection(source, output);
        var list = destination.Inputs[input].Connections;
        if (!list.Contains(connection))
            list.Add(connection);
        _orderDirty = true;
    }

    /// <summary>WA 1.5.3 connect(AudioParam).</summary>
    public void Connect(AudioNodeKernel source, int output, AudioParamKernel destination)
    {
        var connection = new AudioConnection(source, output);
        if (!destination.Connections.Contains(connection))
            destination.Connections.Add(connection);
        _orderDirty = true;
    }

    /// <summary>
    /// WA 1.5.3 disconnect(): removes every connection from <paramref name="source"/> that
    /// matches the given output, destination node and input; a null narrows nothing.
    /// </summary>
    public void Disconnect(AudioNodeKernel source, int? output, AudioNodeKernel? destination, int? input)
    {
        foreach (var node in _nodes)
        {
            if (destination is not null && !ReferenceEquals(node, destination))
                continue;

            for (int i = 0; i < node.Inputs.Length; i++)
            {
                if (input is not null && input.Value != i)
                    continue;
                node.Inputs[i].Connections.RemoveAll(c => ReferenceEquals(c.Node, source) && (output is null || c.Output == output.Value));
            }

            if (input is null)
            {
                foreach (var param in node.Params)
                    param.Connections.RemoveAll(c => ReferenceEquals(c.Node, source) && (output is null || c.Output == output.Value));
            }
        }

        _orderDirty = true;
    }

    /// <summary>WA 1.5.3 disconnect(AudioParam ...).</summary>
    public void Disconnect(AudioNodeKernel source, int? output, AudioParamKernel destination)
    {
        destination.Connections.RemoveAll(c => ReferenceEquals(c.Node, source) && (output is null || c.Output == output.Value));
        _orderDirty = true;
    }

    /// <summary>Renders one quantum; the destination's input holds the result afterwards.</summary>
    public void RenderQuantum()
    {
        while (_controlMessages.TryDequeue(out var message))
            message(this);

        if (_orderDirty)
        {
            ComputeOrder();
            _orderDirty = false;
        }

        long frame = CurrentFrame;
        foreach (var node in _order)
            node.RenderQuantum(frame);

        Interlocked.Add(ref _currentFrame, WebAudioLimits.RenderQuantumFrames);
    }

    /// <summary>Runs pending control messages without rendering (a suspended or closed context).</summary>
    public void DrainControlMessages()
    {
        while (_controlMessages.TryDequeue(out var message))
            message(this);
    }

    internal void RaiseSourceEnded(AudioNodeKernel node) => Events?.SourceEnded(node.Id);

    // WA 2.4 steps 3-4: order the nodes so every node comes after what feeds it, and mute
    // the nodes of any cycle that has no DelayNode in it. Iterative Tarjan, so a long chain
    // of nodes cannot overflow the stack.
    private void ComputeOrder()
    {
        _order.Clear();
        int count = _nodes.Count;
        var index = new Dictionary<AudioNodeKernel, int>(count, ReferenceEqualityComparer.Instance);
        for (int i = 0; i < count; i++)
            index[_nodes[i]] = i;

        var dependencies = new List<int>[count];
        for (int i = 0; i < count; i++)
        {
            var list = new List<int>();
            var node = _nodes[i];
            foreach (var input in node.Inputs)
            {
                foreach (var c in input.Connections)
                {
                    if (index.TryGetValue(c.Node, out int d))
                        list.Add(d);
                }
            }

            foreach (var param in node.Params)
            {
                foreach (var c in param.Connections)
                {
                    if (index.TryGetValue(c.Node, out int d))
                        list.Add(d);
                }
            }

            dependencies[i] = list;
        }

        var order = new int[count];
        var low = new int[count];
        var onStack = new bool[count];
        Array.Fill(order, -1);
        var stack = new Stack<int>();
        var work = new Stack<(int Node, int Next)>();
        int counter = 0;

        for (int root = 0; root < count; root++)
        {
            if (order[root] >= 0)
                continue;

            work.Push((root, 0));
            order[root] = low[root] = counter++;
            stack.Push(root);
            onStack[root] = true;

            while (work.Count > 0)
            {
                var (v, next) = work.Pop();
                var deps = dependencies[v];
                if (next < deps.Count)
                {
                    work.Push((v, next + 1));
                    int w = deps[next];
                    if (order[w] < 0)
                    {
                        order[w] = low[w] = counter++;
                        stack.Push(w);
                        onStack[w] = true;
                        work.Push((w, 0));
                    }
                    else if (onStack[w])
                    {
                        low[v] = Math.Min(low[v], order[w]);
                    }

                    continue;
                }

                if (work.Count > 0)
                {
                    int parent = work.Peek().Node;
                    low[parent] = Math.Min(low[parent], low[v]);
                }

                if (low[v] == order[v])
                {
                    // A strongly connected component, emitted after everything it depends on.
                    var component = new List<int>();
                    int w;
                    do
                    {
                        w = stack.Pop();
                        onStack[w] = false;
                        component.Add(w);
                    }
                    while (w != v);

                    bool cyclic = component.Count > 1 || dependencies[v].Contains(v);
                    foreach (int member in component)
                    {
                        _nodes[member].MutedByCycle = cyclic;
                        _order.Add(_nodes[member]);
                    }
                }
            }
        }
    }
}
