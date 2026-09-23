namespace FenBrowser.Media.WebAudio;

/// <summary>
/// One render quantum of planar audio: <see cref="ChannelCount"/> channels of
/// <see cref="Frames"/> frames each (the context's render quantum size). The channel arrays are
/// allocated once, at the most channels the bus will ever carry, so changing the channel
/// count while rendering never allocates (design WA-D3).
/// </summary>
public sealed class AudioBus
{
    private readonly float[][] _channels;

    public AudioBus(int capacity, int frames = WebAudioLimits.RenderQuantumFrames)
    {
        if (capacity < 1 || capacity > WebAudioLimits.MaxChannels)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (frames < 1 || frames > WebAudioLimits.MaxRenderQuantumFrames)
            throw new ArgumentOutOfRangeException(nameof(frames));

        _channels = new float[capacity][];
        for (int i = 0; i < capacity; i++)
            _channels[i] = new float[frames];
        Frames = frames;
        ChannelCount = 1;
    }

    /// <summary>The channels in use; the rest of the capacity is ignored.</summary>
    public int ChannelCount { get; private set; }

    public int Capacity => _channels.Length;

    public int Frames { get; }

    /// <summary>
    /// True when every channel is known to be zero. Kernels use it to skip work, and a node
    /// whose inputs are all silent can report silence without touching its samples.
    /// </summary>
    public bool IsSilent { get; private set; } = true;

    public Span<float> Channel(int index) => _channels[index];

    public float[] ChannelArray(int index) => _channels[index];

    /// <summary>Sets the channel count and clears the bus to silence.</summary>
    public void Reset(int channelCount)
    {
        SetChannelCount(channelCount);
        Zero();
    }

    /// <summary>Sets the channel count, leaving the samples as they are.</summary>
    public void SetChannelCount(int channelCount)
    {
        if (channelCount < 1 || channelCount > _channels.Length)
            throw new ArgumentOutOfRangeException(nameof(channelCount));
        ChannelCount = channelCount;
    }

    public void Zero()
    {
        for (int i = 0; i < ChannelCount; i++)
            Array.Clear(_channels[i]);
        IsSilent = true;
    }

    /// <summary>Marks the bus as carrying signal; call after writing samples into it.</summary>
    public void MarkNotSilent() => IsSilent = false;

    /// <summary>Copies <paramref name="source"/> channel for channel; the counts must match.</summary>
    public void CopyFrom(AudioBus source)
    {
        SetChannelCount(source.ChannelCount);
        for (int i = 0; i < ChannelCount; i++)
            source._channels[i].AsSpan().CopyTo(_channels[i]);
        IsSilent = source.IsSilent;
    }
}
