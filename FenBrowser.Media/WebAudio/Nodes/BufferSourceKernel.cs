namespace FenBrowser.Media.WebAudio;

/// <summary>
/// A copy of an <c>AudioBuffer</c>'s channels, taken when WA 1.4 "acquire the content"
/// happens. Immutable; the rendering thread may hold it for as long as it likes.
/// </summary>
public sealed class AudioBufferData
{
    public AudioBufferData(float[][] channels, float sampleRate)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Length < 1 || channels.Length > WebAudioLimits.MaxChannels)
            throw new ArgumentOutOfRangeException(nameof(channels));

        int length = channels[0].Length;
        foreach (var channel in channels)
        {
            if (channel.Length != length)
                throw new ArgumentException("Every channel must have the same length.", nameof(channels));
        }

        if ((long)length * channels.Length > WebAudioLimits.MaxBufferSamples)
            throw new ArgumentOutOfRangeException(nameof(channels), "The buffer is larger than the engine allows.");

        Channels = channels;
        SampleRate = sampleRate;
        Length = length;
    }

    public float[][] Channels { get; }

    public float SampleRate { get; }

    public int Length { get; }

    public int ChannelCount => Channels.Length;

    public double Duration => Length / (double)SampleRate;
}

/// <summary>
/// WA 1.9 AudioBufferSourceNode, following the playback algorithm of WA 1.9.5: a playhead
/// in buffer seconds, started sub-sample accurately, advanced by the computed playback
/// rate, wrapped into the loop once it has entered it, and read by linear interpolation.
/// </summary>
public sealed class BufferSourceKernel : ScheduledSourceKernel
{
    private double _position;
    private double _elapsed;
    private bool _playing;
    private bool _enteredLoop;

    public BufferSourceKernel(AudioGraph graph)
        : base(graph, 2)
    {
        PlaybackRate = AddParam("playbackRate", 1f, float.MinValue, float.MaxValue, AutomationRate.KRate, rateFixed: true);
        Detune = AddParam("detune", 0f, float.MinValue, float.MaxValue, AutomationRate.KRate, rateFixed: true);
    }

    public AudioParamKernel PlaybackRate { get; }

    public AudioParamKernel Detune { get; }

    public AudioBufferData? Buffer { get; set; }

    public bool Loop { get; set; }

    public double LoopStart { get; set; }

    public double LoopEnd { get; set; }

    /// <summary>start()'s offset, in buffer seconds.</summary>
    public double Offset { get; private set; }

    /// <summary>start()'s duration, in buffer seconds of playback; infinite when not given.</summary>
    public double Duration { get; private set; } = double.PositiveInfinity;

    /// <summary>Control message for start(when, offset, duration).</summary>
    public void Start(double when, double offset, double duration)
    {
        Start(when);
        Offset = Math.Max(0, offset);
        Duration = duration;
    }

    protected override void Process(long frame)
    {
        var buffer = Buffer;
        if (!HasStarted || HasEnded || buffer is null)
        {
            // WA 1.9.5: started with no buffer, the node's stop time becomes now - it ends at
            // once, whatever start time it was given, and a buffer set later is not played.
            if (HasStarted && !HasEnded && buffer is null)
                Finish();
            OutputSilence();
            return;
        }

        // Nothing to do before the quantum that contains the first frame at or after the start time.
        if (frame + Graph.QuantumFrames <= StartFrame)
        {
            OutputSilence();
            return;
        }

        double sampleRate = Graph.SampleRate;
        double bufferRate = buffer.SampleRate;
        double computedPlaybackRate = PlaybackRate.FirstValue * Math.Pow(2, Detune.FirstValue / 1200.0);

        // The playhead is kept in buffer frames, not seconds: at a rate of 1 it then moves by
        // exactly 1.0 a frame, so a loop boundary on a whole frame is met exactly rather than
        // after round-off has crept into a running sum of 1/sampleRate.
        double step = computedPlaybackRate * bufferRate / sampleRate;
        double length = buffer.Length;
        double loopStart = 0, loopEnd = length;
        bool loop = Loop;
        if (loop)
        {
            // A negative loopStart is clamped into the buffer; a loop that is then empty or
            // inverted falls back to the whole buffer.
            double clampedStart = Math.Clamp(LoopStart, 0, buffer.Duration);
            if (LoopEnd > 0 && clampedStart < LoopEnd)
            {
                loopStart = clampedStart * bufferRate;
                loopEnd = Math.Min(LoopEnd * bufferRate, length);
            }
        }
        else
        {
            _enteredLoop = false;
        }

        // WA 1.9.4 start(): an offset past the end is clamped to it; playing a loop
        // backwards from before the loop starts from the loop's start.
        double offset = Math.Min(Offset * bufferRate, length);
        if (loop && step < 0 && offset < loopStart)
            offset = loopStart;
        double durationFrames = Duration * bufferRate;
        long startFrame = StartFrame;
        long stopFrame = StopFrame;

        var output = Outputs[0].Bus;
        output.Reset(buffer.ChannelCount);
        bool finished = false;

        int quantum = Graph.QuantumFrames;
        for (int index = 0; index < quantum; index++)
        {
            long f = frame + index;
            if (f >= stopFrame || _elapsed >= durationFrames)
            {
                finished = true;
                break;
            }

            if (f < startFrame)
                continue;

            if (!_playing)
            {
                // Sub-sample accurate start: the playhead is where it would have been had
                // playback begun exactly at the start time.
                double lead = ((f / sampleRate) - StartTime) * bufferRate;
                _position = offset + lead * computedPlaybackRate;
                // The grain's duration is measured from the exact start time, so the part of
                // a frame before the first output one has already elapsed.
                _elapsed = lead * Math.Abs(computedPlaybackRate);
                _playing = true;
            }

            if (loop)
            {
                if (!_enteredLoop)
                {
                    if (offset < loopEnd && _position >= loopStart)
                        _enteredLoop = true;
                    if (offset >= loopEnd && _position < loopEnd)
                        _enteredLoop = true;
                }

                if (_enteredLoop && loopEnd > loopStart)
                {
                    double span = loopEnd - loopStart;
                    while (_position >= loopEnd)
                        _position -= span;
                    while (_position < loopStart)
                        _position += span;
                }
            }
            else if ((step >= 0 && _position >= length) || (step < 0 && _position < 0))
            {
                // Played off the end of a buffer that does not loop.
                finished = true;
                break;
            }

            if (_position >= 0 && _position < length)
                WriteInterpolated(buffer, output, index, _position, loop && _enteredLoop, loopStart, loopEnd);

            _position += step;
            _elapsed += Math.Abs(step);
        }

        output.MarkNotSilent();
        if (finished)
            Finish();
    }

    // Linear interpolation between the two buffer frames around the playhead. At the end of
    // the buffer the second frame is the loop start when looping, otherwise the last frame
    // again, so a buffer that ends on a non-zero sample does not click towards zero.
    private static void WriteInterpolated(AudioBufferData buffer, AudioBus output, int index, double position, bool looping, double loopStart, double loopEnd)
    {
        int i0 = (int)Math.Floor(position);
        double fraction = position - i0;
        int length = buffer.Length;
        if (i0 >= length)
            i0 = length - 1;

        int i1 = i0 + 1;
        if (looping)
        {
            if (i1 >= Math.Min((int)Math.Ceiling(loopEnd), length))
                i1 = Math.Clamp((int)Math.Floor(loopStart), 0, length - 1);
        }
        else if (i1 >= length)
        {
            i1 = i0;
        }

        for (int c = 0; c < buffer.ChannelCount; c++)
        {
            var src = buffer.Channels[c];
            float s0 = src[i0];
            output.Channel(c)[index] = fraction == 0 ? s0 : (float)(s0 + (src[i1] - s0) * fraction);
        }
    }
}
