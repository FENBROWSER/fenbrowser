using FenBrowser.Media.Buffers;

namespace FenBrowser.Media.Audio;

/// <summary>
/// Pitch-preserving time stretching for <c>preservesPitch</c> (HTML §4.8.11.8): WSOLA in
/// the shape of SoundTouch's TDStretch. Input is cut into overlapping sequences, each new
/// sequence is placed where it correlates best with the tail of the previous one, and the
/// two are cross-faded, so the output is shorter or longer than the input by the rate but
/// keeps its pitch.
/// </summary>
/// <remarks>
/// Runs on the media task, between the decoder and the renderer; the renderer then plays
/// the stretched blocks at rate 1 while its clock still maps output frames to media time
/// with the real rate. Channels share the offset found on a mono mix so they stay aligned.
/// </remarks>
public sealed class TimeStretcher
{
    // SoundTouch's defaults at 44.1 kHz, scaled to the stream rate: sequence 82 ms,
    // seek window 28 ms, overlap 12 ms.
    private const double SequenceMs = 82;
    private const double SeekMs = 28;
    private const double OverlapMs = 12;

    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _sequence;
    private readonly int _seek;
    private readonly int _overlap;
    private readonly float[] _mid;        // tail of the previous sequence, interleaved
    private readonly float[] _midMono;
    private float[] _input = new float[0];  // interleaved FIFO
    private int _inputFrames;
    private MediaTime _inputTime = MediaTime.Zero;
    private bool _hasInputTime;
    private bool _hasMid;
    private double _skipFraction;
    private double _rate = 1.0;

    public TimeStretcher(int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        _sampleRate = sampleRate;
        _channels = channels;
        _sequence = (int)(SequenceMs * sampleRate / 1000);
        _seek = (int)(SeekMs * sampleRate / 1000);
        _overlap = (int)(OverlapMs * sampleRate / 1000);
        _mid = new float[_overlap * channels];
        _midMono = new float[_overlap];
    }

    /// <summary>Playback rate: 2 halves the output length, 0.5 doubles it. Pitch is unchanged.</summary>
    public double Rate
    {
        get => _rate;
        set
        {
            if (!double.IsFinite(value) || value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), value, "The rate must be finite and positive.");
            _rate = value;
        }
    }

    /// <summary>Input frames buffered and not yet turned into output.</summary>
    public int BufferedFrames => _inputFrames;

    /// <summary>Takes a decoded block; ownership moves here.</summary>
    public void Push(AudioBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (block.SampleRate != _sampleRate || block.Channels != _channels)
            throw new ArgumentException("The block does not match the stretcher's format.", nameof(block));

        if (!_hasInputTime)
        {
            _inputTime = block.Timestamp;
            _hasInputTime = true;
        }

        int needed = (_inputFrames + block.FrameCount) * _channels;
        if (_input.Length < needed)
            Array.Resize(ref _input, Math.Max(needed, _input.Length * 2));
        block.Samples.CopyTo(_input.AsSpan(_inputFrames * _channels));
        _inputFrames += block.FrameCount;
        block.Dispose();
    }

    /// <summary>
    /// Produces the next stretched block, or null when more input is needed. With
    /// <paramref name="flush"/> the remaining input is returned as-is (the end of the stream).
    /// </summary>
    public AudioBlock? Pull(MediaLimits limits, bool flush = false)
    {
        ArgumentNullException.ThrowIfNull(limits);
        // A step reads one sequence anywhere in the seek window and then consumes the
        // nominal skip, so it needs whichever of the two reaches further.
        int required = Math.Max(_sequence + _seek, (int)Math.Ceiling((_sequence - _overlap) * _rate));
        if (_inputFrames < required)
        {
            if (!flush || _inputFrames == 0)
                return null;
            if (_inputFrames < _sequence)
            {
                // Less than one sequence left: it plays as it is.
                var tail = AudioBlock.Allocate(limits, _sampleRate, _channels, _inputFrames, _inputTime);
                _input.AsSpan(0, _inputFrames * _channels).CopyTo(tail.Samples);
                Consume(_inputFrames);
                _hasMid = false;
                return tail;
            }
        }

        // At the end of the stream the seek window shrinks to what is left.
        int seekLimit = Math.Min(_seek, _inputFrames - _sequence);
        int offset = _hasMid ? SeekBestOverlap(seekLimit) : 0;
        int outFrames = _sequence - _overlap;
        var block = AudioBlock.Allocate(limits, _sampleRate, _channels, outFrames, _inputTime);
        var output = block.Samples;
        var input = _input.AsSpan();

        // Cross-fade the previous tail into the start of this sequence.
        for (int i = 0; i < _overlap; i++)
        {
            float t = (float)i / _overlap;
            int inBase = (offset + i) * _channels;
            for (int c = 0; c < _channels; c++)
                output[i * _channels + c] = _hasMid ? _mid[i * _channels + c] * (1 - t) + input[inBase + c] * t : input[inBase + c];
        }

        // Then the middle of the sequence unchanged.
        int middle = _sequence - 2 * _overlap;
        input.Slice((offset + _overlap) * _channels, middle * _channels).CopyTo(output[(_overlap * _channels)..]);

        // Remember the tail for the next cross-fade.
        input.Slice((offset + _sequence - _overlap) * _channels, _overlap * _channels).CopyTo(_mid);
        for (int i = 0; i < _overlap; i++)
        {
            float sum = 0;
            for (int c = 0; c < _channels; c++)
                sum += _mid[i * _channels + c];
            _midMono[i] = sum / _channels;
        }

        _hasMid = true;

        // Advance the input by the nominal skip for this rate, keeping the fraction.
        double skip = outFrames * _rate + _skipFraction;
        int whole = (int)skip;
        _skipFraction = skip - whole;
        Consume(Math.Min(whole, _inputFrames));
        return block;
    }

    /// <summary>Drops buffered input and the cross-fade tail (a seek).</summary>
    public void Flush()
    {
        _inputFrames = 0;
        _hasInputTime = false;
        _hasMid = false;
        _skipFraction = 0;
    }

    private void Consume(int frames)
    {
        if (frames <= 0)
            return;
        int remaining = _inputFrames - frames;
        if (remaining > 0)
            Array.Copy(_input, frames * _channels, _input, 0, remaining * _channels);
        _inputFrames = Math.Max(0, remaining);
        _inputTime += MediaTime.FromTimescale(frames, _sampleRate);
    }

    /// <summary>The offset in the seek window where the input best continues the previous tail (normalised cross-correlation on a mono mix).</summary>
    private int SeekBestOverlap(int seekLimit)
    {
        int best = 0;
        double bestScore = double.NegativeInfinity;
        var input = _input.AsSpan();
        for (int offset = 0; offset <= seekLimit; offset++)
        {
            double corr = 0;
            double energy = 1e-9;
            for (int i = 0; i < _overlap; i++)
            {
                float sample = 0;
                int inBase = (offset + i) * _channels;
                for (int c = 0; c < _channels; c++)
                    sample += input[inBase + c];
                sample /= _channels;
                corr += sample * _midMono[i];
                energy += sample * sample;
            }

            double score = corr / Math.Sqrt(energy);
            if (score > bestScore)
            {
                bestScore = score;
                best = offset;
            }
        }

        return best;
    }
}
