namespace FenBrowser.Media.WebAudio;

/// <summary>WA 1.5.2 <c>ChannelCountMode</c>.</summary>
public enum ChannelCountMode
{
    Max,
    ClampedMax,
    Explicit,
}

/// <summary>WA 1.5.2 <c>ChannelInterpretation</c>.</summary>
public enum ChannelInterpretation
{
    Speakers,
    Discrete,
}

/// <summary>
/// WA 4 "Channel Up-Mixing and Down-Mixing": how an input's connections are brought to the
/// input's computed channel count and summed. The speaker layouts are mono, stereo, quad
/// (L R SL SR) and 5.1 (L R C LFE SL SR); any other pair of counts mixes discretely.
/// </summary>
public static class ChannelMixing
{
    private const float Sqrt1_2 = 0.70710678118654752f;

    /// <summary>WA 4.1: the computedNumberOfChannels of an input.</summary>
    public static int ComputeChannelCount(ChannelCountMode mode, int channelCount, int maxConnectionChannels)
    {
        int max = Math.Max(1, maxConnectionChannels);
        return mode switch
        {
            ChannelCountMode.Max => max,
            ChannelCountMode.ClampedMax => Math.Min(max, channelCount),
            _ => channelCount,
        };
    }

    /// <summary>
    /// Adds <paramref name="source"/>, mixed to <paramref name="destination"/>'s channel
    /// count under <paramref name="interpretation"/>, into <paramref name="destination"/>.
    /// </summary>
    public static void MixInto(AudioBus source, AudioBus destination, ChannelInterpretation interpretation)
    {
        if (source.IsSilent)
            return;

        int from = source.ChannelCount;
        int to = destination.ChannelCount;
        destination.MarkNotSilent();

        if (from == to)
        {
            for (int c = 0; c < to; c++)
                Add(source.Channel(c), destination.Channel(c), 1f);
            return;
        }

        if (interpretation == ChannelInterpretation.Speakers && TryMixSpeakers(source, destination, from, to))
            return;

        // Discrete: fill the lower channels, drop or leave silent the rest.
        int common = Math.Min(from, to);
        for (int c = 0; c < common; c++)
            Add(source.Channel(c), destination.Channel(c), 1f);
    }

    private static bool TryMixSpeakers(AudioBus s, AudioBus d, int from, int to)
    {
        switch (from, to)
        {
            // Up-mixing.
            case (1, 2):
                Add(s.Channel(0), d.Channel(0), 1f);
                Add(s.Channel(0), d.Channel(1), 1f);
                return true;
            case (1, 4):
                Add(s.Channel(0), d.Channel(0), 1f);
                Add(s.Channel(0), d.Channel(1), 1f);
                return true;
            case (1, 6):
                Add(s.Channel(0), d.Channel(2), 1f);
                return true;
            case (2, 4):
                Add(s.Channel(0), d.Channel(0), 1f);
                Add(s.Channel(1), d.Channel(1), 1f);
                return true;
            case (2, 6):
                Add(s.Channel(0), d.Channel(0), 1f);
                Add(s.Channel(1), d.Channel(1), 1f);
                return true;
            case (4, 6):
                Add(s.Channel(0), d.Channel(0), 1f);
                Add(s.Channel(1), d.Channel(1), 1f);
                Add(s.Channel(2), d.Channel(4), 1f);
                Add(s.Channel(3), d.Channel(5), 1f);
                return true;

            // Down-mixing.
            case (2, 1):
                Add(s.Channel(0), d.Channel(0), 0.5f);
                Add(s.Channel(1), d.Channel(0), 0.5f);
                return true;
            case (4, 1):
                for (int c = 0; c < 4; c++)
                    Add(s.Channel(c), d.Channel(0), 0.25f);
                return true;
            case (6, 1):
                Add(s.Channel(0), d.Channel(0), Sqrt1_2);
                Add(s.Channel(1), d.Channel(0), Sqrt1_2);
                Add(s.Channel(2), d.Channel(0), 1f);
                Add(s.Channel(4), d.Channel(0), 0.5f);
                Add(s.Channel(5), d.Channel(0), 0.5f);
                return true;
            case (4, 2):
                Add(s.Channel(0), d.Channel(0), 0.5f);
                Add(s.Channel(2), d.Channel(0), 0.5f);
                Add(s.Channel(1), d.Channel(1), 0.5f);
                Add(s.Channel(3), d.Channel(1), 0.5f);
                return true;
            case (6, 2):
                Add(s.Channel(0), d.Channel(0), 1f);
                Add(s.Channel(2), d.Channel(0), Sqrt1_2);
                Add(s.Channel(4), d.Channel(0), Sqrt1_2);
                Add(s.Channel(1), d.Channel(1), 1f);
                Add(s.Channel(2), d.Channel(1), Sqrt1_2);
                Add(s.Channel(5), d.Channel(1), Sqrt1_2);
                return true;
            case (6, 4):
                Add(s.Channel(0), d.Channel(0), 1f);
                Add(s.Channel(2), d.Channel(0), Sqrt1_2);
                Add(s.Channel(1), d.Channel(1), 1f);
                Add(s.Channel(2), d.Channel(1), Sqrt1_2);
                Add(s.Channel(4), d.Channel(2), 1f);
                Add(s.Channel(5), d.Channel(3), 1f);
                return true;
            default:
                return false;
        }
    }

    private static void Add(ReadOnlySpan<float> source, Span<float> destination, float gain)
    {
        if (gain == 1f)
        {
            for (int i = 0; i < destination.Length; i++)
                destination[i] += source[i];
        }
        else
        {
            for (int i = 0; i < destination.Length; i++)
                destination[i] += source[i] * gain;
        }
    }
}
