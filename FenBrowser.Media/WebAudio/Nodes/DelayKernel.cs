namespace FenBrowser.Media.WebAudio;

/// <summary>
/// WA 1.17 DelayNode: the input delayed by the a-rate <c>delayTime</c>, read with linear
/// interpolation from a ring buffer. A delay inside a cycle breaks the cycle (WA 2.4): it
/// outputs from what it wrote in earlier quanta, takes its input after the rest of the
/// graph has rendered, and is clamped to at least one render quantum.
/// </summary>
public sealed class DelayKernel : AudioNodeKernel
{
    private readonly int _ringFrames;
    private readonly float[]?[] _ring = new float[WebAudioLimits.MaxChannels][];
    private int _ringChannels = 1;
    private long _writtenFrames;
    private bool _hadSignal;
    private long _silentSince = long.MaxValue;
    // The channel count of each quantum written, by quantum number (stored + 1 so that 0 is
    // "never written"): the output takes the count of the input it is delaying.
    private readonly long[] _blockNumber;
    private readonly int[] _blockChannels;

    public DelayKernel(AudioGraph graph, double maxDelayTime)
        : base(graph, 1, 1, 2, ChannelCountMode.Max, ChannelInterpretation.Speakers)
    {
        MaxDelayTime = maxDelayTime;
        DelayTime = AddParam("delayTime", 0f, 0f, (float)maxDelayTime, AutomationRate.ARate);
        // Room for the longest delay, one quantum being written, and interpolation.
        _ringFrames = (int)Math.Ceiling(maxDelayTime * graph.SampleRate) + 2 * graph.QuantumFrames + 2;
        _ring[0] = new float[_ringFrames];
        int blocks = _ringFrames / graph.QuantumFrames + 2;
        _blockNumber = new long[blocks];
        _blockChannels = new int[blocks];
    }

    public double MaxDelayTime { get; }

    public AudioParamKernel DelayTime { get; }

    /// <summary>Set by the graph's ordering pass: the delay closes a cycle this quantum.</summary>
    internal bool InCycle { get; set; }

    protected override void Process(long frame)
    {
        if (!InCycle)
            WriteInput(Inputs[0].Bus);
        ReadOutput();
        if (!InCycle)
            _writtenFrames += Graph.QuantumFrames;
    }

    /// <summary>For a delay in a cycle: takes this quantum's input after everything else rendered.</summary>
    internal void CompleteCycleQuantum()
    {
        Inputs[0].Pull();
        WriteInput(Inputs[0].Bus);
        _writtenFrames += Graph.QuantumFrames;
    }

    private void WriteInput(AudioBus input)
    {
        int quantum = Graph.QuantumFrames;
        if (input.ChannelCount > _ringChannels)
        {
            for (int c = _ringChannels; c < input.ChannelCount; c++)
                _ring[c] ??= new float[_ringFrames];
            _ringChannels = input.ChannelCount;
        }

        long block = _writtenFrames / quantum;
        _blockNumber[block % _blockNumber.Length] = block + 1;
        _blockChannels[block % _blockNumber.Length] = input.ChannelCount;

        int start = (int)(_writtenFrames % _ringFrames);
        for (int c = 0; c < _ringChannels; c++)
        {
            var ring = _ring[c]!;
            bool present = c < input.ChannelCount && !input.IsSilent;
            for (int i = 0; i < quantum; i++)
            {
                int at = start + i;
                if (at >= _ringFrames)
                    at -= _ringFrames;
                ring[at] = present ? input.Channel(c)[i] : 0f;
            }
        }

        if (!input.IsSilent)
        {
            _hadSignal = true;
            _silentSince = long.MaxValue;
        }
        else if (_silentSince == long.MaxValue)
        {
            _silentSince = _writtenFrames;
        }
    }

    private void ReadOutput()
    {
        int quantum = Graph.QuantumFrames;
        var output = Outputs[0].Bus;
        output.Reset(1);

        // Nothing has come in yet, or everything that did has already come out.
        long tail = (long)Math.Ceiling(MaxDelayTime * Graph.SampleRate) + quantum;
        if (!_hadSignal || (_silentSince != long.MaxValue && _writtenFrames - _silentSince > tail))
            return;

        double sampleRate = Graph.SampleRate;
        var delay = DelayTime.Values;
        double minimum = InCycle ? quantum : 0;
        double maximum = MaxDelayTime * sampleRate;

        // WA 1.17 (WebAudio issue #25): the output has the channel count of the input it is
        // delaying, so reading history that was never written is mono silence.
        long writtenEnd = _writtenFrames + (InCycle ? 0 : quantum);
        int channels = 1;
        for (int i = 0; i < quantum; i++)
        {
            double d = Math.Clamp(delay[i] * sampleRate, minimum, maximum);
            long k = (long)Math.Floor(_writtenFrames + i - d);
            channels = Math.Max(channels, Math.Max(ChannelsAt(k, writtenEnd), ChannelsAt(k + 1, writtenEnd)));
        }

        output.Reset(channels);

        // The frame index of this quantum's first sample in the write sequence (the counter
        // advances after the read). In a cycle this quantum has not been written yet, which
        // the one-quantum minimum covers.
        long baseFrame = _writtenFrames;
        for (int i = 0; i < quantum; i++)
        {
            double d = Math.Clamp(delay[i] * sampleRate, minimum, maximum);
            // Frames before the first one written read as silence, and are interpolated like
            // any other: a delay of a whole number of frames given as a float lands a hair
            // off the frame, and must still come out at (almost exactly) that frame.
            double position = baseFrame + i - d;
            if (position < -1)
                continue;

            long k = (long)Math.Floor(position);
            double fraction = position - k;
            int a = k < 0 ? -1 : (int)(k % _ringFrames);
            int b = k + 1 < 0 ? -1 : (int)((k + 1) % _ringFrames);
            for (int c = 0; c < channels; c++)
            {
                var ring = _ring[c]!;
                float s0 = a < 0 ? 0f : ring[a];
                float s1 = b < 0 ? 0f : ring[b];
                output.Channel(c)[i] = fraction == 0 ? s0 : (float)(s0 + (s1 - s0) * fraction);
            }
        }

        output.MarkNotSilent();
    }

    private int ChannelsAt(long frame, long writtenEnd)
    {
        if (frame < 0 || frame >= writtenEnd)
            return 0;
        long block = frame / Graph.QuantumFrames;
        int slot = (int)(block % _blockNumber.Length);
        return _blockNumber[slot] == block + 1 ? _blockChannels[slot] : 0;
    }
}
