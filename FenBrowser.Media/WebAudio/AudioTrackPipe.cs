namespace FenBrowser.Media.WebAudio;

/// <summary>
/// The audio a MediaStreamTrack carries between Web Audio graphs (WA 1.20, 1.21): one
/// producer - a MediaStreamAudioDestinationNode - writes planar frames at its context's
/// rate; any number of consumers - MediaStreamAudioSourceNodes, possibly in contexts at
/// other rates - read from their own positions. Bounded: a reader that falls more than the
/// ring behind skips ahead rather than making the ring grow.
/// </summary>
public sealed class AudioTrackPipe
{
    private readonly object _gate = new();
    private readonly float[][] _ring;
    private readonly int _capacity;
    private long _written;

    public AudioTrackPipe(float sampleRate, int channels, int capacityFrames = 16384)
    {
        if (channels < 1 || channels > WebAudioLimits.MaxChannels)
            throw new ArgumentOutOfRangeException(nameof(channels));
        SampleRate = sampleRate;
        Channels = channels;
        _capacity = capacityFrames;
        _ring = new float[channels][];
        for (int c = 0; c < channels; c++)
            _ring[c] = new float[capacityFrames];
    }

    public float SampleRate { get; }

    /// <summary>
    /// A producer the reader can drive: called on the reading thread with the number of
    /// frames it still lacks, and writes at least that many before returning. Null for a
    /// producer that writes on its own clock (a MediaStreamAudioDestinationNode).
    /// </summary>
    public Action<int>? Producer { get; set; }

    public int Channels { get; }

    /// <summary>Total frames written since the pipe was made.</summary>
    public long Written
    {
        get { lock (_gate) return _written; }
    }

    /// <summary>Appends one block from the producer, mixed to the pipe's channel count.</summary>
    public void Write(AudioBus block)
    {
        int frames = block.Frames;
        lock (_gate)
        {
            for (int i = 0; i < frames; i++)
            {
                int at = (int)((_written + i) % _capacity);
                for (int c = 0; c < Channels; c++)
                {
                    float v = 0;
                    if (!block.IsSilent)
                    {
                        if (c < block.ChannelCount)
                            v = block.Channel(c)[i];
                        else if (block.ChannelCount == 1)
                            v = block.Channel(0)[i];
                    }

                    _ring[c][at] = v;
                }
            }

            _written += frames;
        }
    }

    /// <summary>
    /// Copies frames [<paramref name="from"/>, from + count) into <paramref name="destination"/>;
    /// frames not yet written, or already overwritten, read as silence. Returns the frame
    /// after the last one read.
    /// </summary>
    public long Read(long from, int count, float[][] destination)
    {
        lock (_gate)
        {
            for (int i = 0; i < count; i++)
            {
                long frame = from + i;
                bool available = frame >= 0 && frame < _written && frame >= _written - _capacity;
                int at = available ? (int)(frame % _capacity) : 0;
                for (int c = 0; c < destination.Length && c < Channels; c++)
                    destination[c][i] = available ? _ring[c][at] : 0f;
            }

            return from + count;
        }
    }
}
