namespace FenBrowser.Media.WebAudio;

/// <summary>A block of input the ScriptProcessorNode hands to script, and the output script gives back.</summary>
public sealed class ScriptProcessorRequest
{
    internal ScriptProcessorRequest(long id, float[][] input, int outputChannels, int bufferSize, double playbackTime)
    {
        Id = id;
        Input = input;
        Output = new float[outputChannels][];
        for (int c = 0; c < outputChannels; c++)
            Output[c] = new float[bufferSize];
        PlaybackTime = playbackTime;
    }

    public long Id { get; }

    public float[][] Input { get; }

    public float[][] Output { get; }

    public double PlaybackTime { get; }

    internal ManualResetEventSlim Done { get; } = new(false);
}

/// <summary>
/// WA 1.30 ScriptProcessorNode: input gathered into bufferSize blocks for script's
/// audioprocess handler, and script's output played two blocks later. In an offline context
/// the rendering thread waits for script (so the result is the same every run); in a real-time
/// one a late block is silence.
/// </summary>
public sealed class ScriptProcessorKernel : AudioNodeKernel
{
    private readonly int _bufferSize;
    private readonly int _outputChannels;
    private readonly float[][] _gather;
    private int _gathered;
    private long _nextId = 1;
    private ScriptProcessorRequest? _playing;
    private ScriptProcessorRequest? _previous;
    private int _playRead;

    public ScriptProcessorKernel(AudioGraph graph, int bufferSize, int inputChannels, int outputChannels)
        : base(graph, 1, 1, Math.Max(1, inputChannels), ChannelCountMode.Explicit, ChannelInterpretation.Speakers)
    {
        _bufferSize = bufferSize;
        _outputChannels = Math.Max(1, outputChannels);
        _gather = new float[Math.Max(1, inputChannels)][];
        for (int c = 0; c < _gather.Length; c++)
            _gather[c] = new float[bufferSize];
        InputChannels = inputChannels;
    }

    public int InputChannels { get; }

    /// <summary>Set by the host for an offline context: wait for script instead of playing silence.</summary>
    public bool WaitForScript { get; set; }

    /// <summary>Raised on the rendering thread when a block is ready for script.</summary>
    public Action<ScriptProcessorKernel, ScriptProcessorRequest>? BlockReady { get; set; }

    /// <summary>The control thread's answer: script has filled the request's output.</summary>
    public static void Complete(ScriptProcessorRequest request) => request.Done.Set();

    protected override void Process(long frame)
    {
        var input = Inputs[0].Bus;
        var output = Outputs[0].Bus;
        output.Reset(_outputChannels);
        int quantum = output.Frames;
        double sampleRate = Graph.SampleRate;

        for (int i = 0; i < quantum; i++)
        {
            // This sample's output comes from the block script was handed one buffer ago,
            // before this sample's input is gathered: the node's latency is two buffers
            // (one to gather, one for script), as in every engine.
            if (_playing is { } playing && _playRead < _bufferSize)
            {
                for (int c = 0; c < _outputChannels; c++)
                    output.Channel(c)[i] = playing.Output[c][_playRead];
                _playRead++;
            }

            for (int c = 0; c < _gather.Length; c++)
                _gather[c][_gathered] = InputChannels > 0 && c < input.ChannelCount && !input.IsSilent ? input.Channel(c)[i] : 0f;
            _gathered++;
            if (_gathered < _bufferSize)
                continue;

            var copy = new float[_gather.Length][];
            for (int c = 0; c < _gather.Length; c++)
                copy[c] = (float[])_gather[c].Clone();
            // WA 1.30.1 playbackTime: when this block's output will be heard.
            double playbackTime = (frame + i + 1 + _bufferSize) / sampleRate;
            var request = new ScriptProcessorRequest(_nextId++, copy, _outputChannels, _bufferSize, playbackTime);
            BlockReady?.Invoke(this, request);
            _gathered = 0;
            _playing = null;
            _playRead = 0;

            // The block handed to script one buffer ago plays next. Offline, the rendering
            // thread waits for it so the result is the same every run; in real time a block
            // script has not finished is heard as silence.
            if (_previous is { } previous && (!WaitForScript || previous.Done.Wait(TimeSpan.FromSeconds(2))) && previous.Done.IsSet)
                _playing = previous;
            _previous = request;
        }

        output.MarkNotSilent();
    }
}
