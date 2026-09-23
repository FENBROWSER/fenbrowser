namespace FenBrowser.Media.WebAudio;

/// <summary>
/// WA 1.21 MediaStreamAudioDestinationNode: its input, mixed to its channel count, becomes
/// the audio of its stream's track.
/// </summary>
public sealed class StreamDestinationKernel : AudioNodeKernel
{
    private readonly AudioBus _block;

    public StreamDestinationKernel(AudioGraph graph, AudioTrackPipe pipe)
        : base(graph, 1, 0, 2, ChannelCountMode.Explicit, ChannelInterpretation.Speakers)
    {
        Pipe = pipe;
        _block = new AudioBus(WebAudioLimits.MaxChannels, graph.QuantumFrames);
    }

    public AudioTrackPipe Pipe { get; }

    protected override void Process(long frame)
    {
        _block.Reset(Pipe.Channels);
        ChannelMixing.MixInto(Inputs[0].Bus, _block, ChannelInterpretation.Speakers);
        Pipe.Write(_block);
    }
}

/// <summary>
/// WA 1.20 MediaStreamAudioSourceNode: a track's audio as it arrives. Reads never run past
/// what the producer has written, so a producer that is behind leaves silence instead of a
/// permanent gap; a track at another rate is interpolated to this context's.
/// </summary>
public sealed class StreamSourceKernel : AudioNodeKernel
{
    private AudioTrackPipe? _pipe;
    private double _step;
    private float[][] _chunk = [];
    private long _cursor = -1;
    private double _fraction;

    public StreamSourceKernel(AudioGraph graph)
        : base(graph, 0, 1, 2, ChannelCountMode.Max, ChannelInterpretation.Speakers)
    {
        IsSilentOutput = true;
    }

    /// <summary>
    /// Control message: true while the source must contribute silence - a cross-origin media
    /// element without CORS (WA 1.20, design section 3). The pipe is still read, so audio
    /// resumes in step when the flag clears.
    /// </summary>
    public bool Muted { get; set; }

    /// <summary>
    /// Control message: the track to read, or null for one that carries no audio this engine
    /// can see (an ended track, or a source outside Web Audio).
    /// </summary>
    public void SetPipe(AudioTrackPipe? pipe)
    {
        _pipe = pipe;
        _cursor = -1;
        _fraction = 0;
        if (pipe is null)
            return;
        _step = pipe.SampleRate / (double)Graph.SampleRate;
        _chunk = new float[pipe.Channels][];
        for (int c = 0; c < pipe.Channels; c++)
            _chunk[c] = new float[(int)Math.Ceiling(Graph.QuantumFrames * _step) + 2];
    }

    protected override void Process(long frame)
    {
        var output = Outputs[0].Bus;
        var pipe = _pipe;
        if (pipe is null)
        {
            output.Reset(1);
            IsSilentOutput = true;
            return;
        }

        output.Reset(pipe.Channels);
        long written = pipe.Written;
        if (_cursor < 0)
            _cursor = written;

        int quantum = output.Frames;
        int needed = _step == 1.0 ? quantum : (int)Math.Ceiling(quantum * _step + _fraction) + 1;
        long available = written - _cursor;
        if (available < needed && pipe.Producer is { } producer)
        {
            // A pulled source (a media element) renders exactly what this quantum reads, on
            // this thread, so the two never drift apart and no silence is spliced in.
            producer((int)(needed - available));
            written = pipe.Written;
            available = written - _cursor;
        }

        if (available <= 0)
            return;

        if (_step == 1.0)
        {
            // Same rate: frames are copied as written, with no lookahead to wait for.
            int count = (int)Math.Min(quantum, available);
            pipe.Read(_cursor, count, _chunk);
            for (int c = 0; c < pipe.Channels; c++)
                _chunk[c].AsSpan(0, count).CopyTo(output.Channel(c));
            _cursor += count;
            if (Muted)
                output.Zero();
            else
                output.MarkNotSilent();
            return;
        }

        int take = (int)Math.Min(needed, available);
        pipe.Read(_cursor, take, _chunk);

        double position = _fraction;
        int produced = 0;
        for (; produced < quantum; produced++)
        {
            int k = (int)Math.Floor(position);
            if (k + 1 >= take)
                break;
            double f = position - k;
            for (int c = 0; c < pipe.Channels; c++)
            {
                float a = _chunk[c][k], b = _chunk[c][k + 1];
                output.Channel(c)[produced] = (float)(a + (b - a) * f);
            }

            position += _step;
        }

        int consumed = (int)Math.Floor(position);
        _cursor += consumed;
        _fraction = position - consumed;
        if (Muted)
            output.Zero();
        else if (produced > 0)
            output.MarkNotSilent();
    }
}
