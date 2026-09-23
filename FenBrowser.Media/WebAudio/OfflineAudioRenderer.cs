namespace FenBrowser.Media.WebAudio;

/// <summary>
/// WA 1.3 OfflineAudioContext rendering (design WA-D5): quanta rendered as fast as possible
/// into a planar result of the context's length. The caller may pause between quanta, which
/// is how <c>suspend(suspendTime)</c> is honoured.
/// </summary>
public sealed class OfflineAudioRenderer
{
    private readonly AudioGraph _graph;
    private readonly float[][] _result;
    private long _written;

    public OfflineAudioRenderer(AudioGraph graph, int channels, int length)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (channels < 1 || channels > WebAudioLimits.MaxChannels)
            throw new ArgumentOutOfRangeException(nameof(channels));
        if (length < 1 || (long)length * channels > WebAudioLimits.MaxBufferSamples)
            throw new ArgumentOutOfRangeException(nameof(length));

        _graph = graph;
        _result = new float[channels][];
        for (int c = 0; c < channels; c++)
            _result[c] = new float[length];
        Length = length;
    }

    public int Length { get; }

    /// <summary>Frames rendered so far.</summary>
    public long Written => _written;

    public bool IsComplete => _written >= Length;

    /// <summary>The rendered channels; complete once <see cref="IsComplete"/>.</summary>
    public float[][] Result => _result;

    /// <summary>Renders one quantum and copies what fits into the result.</summary>
    public void RenderQuantum()
    {
        if (IsComplete)
            return;

        _graph.RenderQuantum();
        var output = _graph.Destination.Output;
        int count = (int)Math.Min(WebAudioLimits.RenderQuantumFrames, Length - _written);
        for (int c = 0; c < _result.Length; c++)
        {
            var destination = _result[c].AsSpan((int)_written, count);
            if (c < output.ChannelCount && !output.IsSilent)
            {
                var source = output.Channel(c)[..count];
                for (int i = 0; i < count; i++)
                {
                    // A non-finite sample never leaves the graph (design section 3).
                    float v = source[i];
                    destination[i] = float.IsFinite(v) ? v : 0f;
                }
            }
        }

        _written += count;
    }

    /// <summary>Renders to the end.</summary>
    public float[][] RenderAll(CancellationToken cancellationToken = default)
    {
        while (!IsComplete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RenderQuantum();
        }

        return _result;
    }
}
