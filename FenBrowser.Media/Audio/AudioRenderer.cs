using System.Collections.Concurrent;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Clock;

namespace FenBrowser.Media.Audio;

/// <summary>
/// The audio renderer node (design §2.3): decoded blocks go in on the media task queue, the
/// device pulls interleaved samples out on its own thread, and every run of output frames
/// is described to the <see cref="AudioMasterClock"/> so <c>currentTime</c> follows what
/// is audible.
/// </summary>
/// <remarks>
/// The pull side never allocates or blocks: blocks arrive through a lock-free queue and the
/// only state it touches is its own. Sample-rate conversion is linear interpolation between
/// neighbouring source frames, which is enough for a 44.1 ↔ 48 kHz device mismatch; the
/// playback rate is applied the same way (pitch follows rate) unless a time stretcher is
/// attached for <c>preservesPitch</c>. Channel layouts are mapped by the usual rules:
/// mono is duplicated, extra source channels are averaged down, missing ones are silent.
/// </remarks>
public sealed class AudioRenderer : IAudioRenderCallback
{
    private readonly ConcurrentQueue<AudioBlock> _queue = new();
    private readonly AudioStreamFormat _output;
    private readonly AudioMasterClock _clock;
    private long _queuedFrames;          // frames waiting in _queue (source rate)
    private int _sourceRate;             // sample rate of the blocks being queued
    private long _outputFrames;          // frames handed to the device so far
    private long _consumedBlocks;

    // Owned by the device thread.
    private AudioBlock? _current;
    private double _position;            // fractional frame position inside _current
    private bool _endOfStream;
    private int _flushGeneration;
    private int _renderedGeneration;

    // Written by the media thread, read by the device thread.
    private double _volume = 1.0;
    private double _rate = 1.0;
    private int _muted;
    private int _pitchPreserved;

    public AudioRenderer(AudioStreamFormat output, AudioMasterClock clock)
    {
        if (output.SampleRate <= 0 || output.Channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(output), output, "The output format must have a positive rate and channel count.");
        ArgumentNullException.ThrowIfNull(clock);
        _output = output;
        _clock = clock;
    }

    public AudioStreamFormat OutputFormat => _output;

    private IAudioCapture? _capture;

    /// <summary>
    /// Where a copy of every rendered frame goes before the volume and the muted state are
    /// applied, or null. A captured stream carries the element's audio whatever its volume
    /// (mediacapture-fromelement, captureStream()).
    /// </summary>
    public IAudioCapture? Capture
    {
        get => Volatile.Read(ref _capture);
        set => Volatile.Write(ref _capture, value);
    }

    /// <summary>Effective volume in [0, 1]; the muted state zeroes the output without changing it.</summary>
    public double Volume
    {
        get => Volatile.Read(ref _volume);
        set => Volatile.Write(ref _volume, Math.Clamp(value, 0.0, 1.0));
    }

    public bool Muted
    {
        get => Volatile.Read(ref _muted) != 0;
        set => Volatile.Write(ref _muted, value ? 1 : 0);
    }

    /// <summary>Playback rate; the master clock reports it back from the segments.</summary>
    public double Rate
    {
        get => Volatile.Read(ref _rate);
        set
        {
            if (!double.IsFinite(value) || value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), value, "The rate must be finite and positive.");
            Volatile.Write(ref _rate, value);
        }
    }

    /// <summary>
    /// True while a time stretcher feeds this renderer: blocks are already stretched, so
    /// they play at rate 1 while the clock still maps them to media time at <see cref="Rate"/>.
    /// </summary>
    public bool PitchPreserved
    {
        get => Volatile.Read(ref _pitchPreserved) != 0;
        set => Volatile.Write(ref _pitchPreserved, value ? 1 : 0);
    }

    /// <summary>Media time waiting to be played; the player decodes ahead until this is full.</summary>
    public MediaTime QueuedDuration
    {
        get
        {
            long frames = Interlocked.Read(ref _queuedFrames);
            int rate = Volatile.Read(ref _sourceRate);
            if (rate <= 0)
                return MediaTime.Zero;
            var queued = MediaTime.FromTimescale(frames, rate);
            return PitchPreserved ? MediaTime.FromSeconds(queued.TotalSeconds * Rate) : queued;
        }
    }

    public long OutputFramesWritten => Interlocked.Read(ref _outputFrames);

    public long BlocksConsumed => Interlocked.Read(ref _consumedBlocks);

    /// <summary>True once the stream end was marked and every queued frame has been rendered.</summary>
    public bool IsDrained => Volatile.Read(ref _endOfStream) && Interlocked.Read(ref _queuedFrames) == 0 && _current is null;

    /// <summary>Media thread: queues a decoded block. Ownership moves to the renderer.</summary>
    public void Enqueue(AudioBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        Volatile.Write(ref _sourceRate, block.SampleRate);
        Interlocked.Add(ref _queuedFrames, block.FrameCount);
        _queue.Enqueue(block);
    }

    public void MarkEndOfStream() => Volatile.Write(ref _endOfStream, true);

    /// <summary>More audio is coming after all (a MediaSource reopened by an append).</summary>
    public void ClearEndOfStream() => Volatile.Write(ref _endOfStream, false);

    /// <summary>
    /// Media thread: drops everything queued and tells the clock time now reads
    /// <paramref name="time"/> until new audio plays. The device thread notices the
    /// generation change and lets go of the block it was reading.
    /// </summary>
    public void Flush(MediaTime time)
    {
        Interlocked.Increment(ref _flushGeneration);
        Volatile.Write(ref _endOfStream, false);
        while (_queue.TryDequeue(out var block))
        {
            Interlocked.Add(ref _queuedFrames, -block.FrameCount);
            block.Dispose();
        }

        _clock.Reset(time);
    }

    /// <inheritdoc/>
    public int Render(Span<float> destination, int channels)
    {
        if (channels != _output.Channels)
            return 0;

        int frames = destination.Length / channels;
        int written = 0;
        double volume = Muted ? 0.0 : Volume;
        var capture = Capture;
        float mixVolume = capture is null ? (float)volume : 1f;
        double rate = Rate;
        double resample = PitchPreserved ? 1.0 : rate;

        int generation = Volatile.Read(ref _flushGeneration);
        if (generation != _renderedGeneration)
        {
            _renderedGeneration = generation;
            DropCurrent();
        }

        long clockEpoch = _clock.ResetCount;

        while (written < frames)
        {
            if (_current is null)
            {
                if (!_queue.TryDequeue(out var next))
                    break;
                _current = next;
                _position = 0;
            }

            var block = _current;
            double step = (double)block.SampleRate * resample / _output.SampleRate;
            int runStart = written;
            var mediaStart = block.Timestamp + MediaTime.FromMicroseconds((long)(_position / block.SampleRate * MediaTime.MicrosecondsPerSecond));

            // Copy until this block runs out or the destination is full.
            var samples = block.Samples;
            int blockChannels = block.Channels;
            int blockFrames = block.FrameCount;
            while (written < frames && _position < blockFrames)
            {
                int i0 = (int)_position;
                int i1 = Math.Min(i0 + 1, blockFrames - 1);
                float t = (float)(_position - i0);
                var dst = destination.Slice(written * channels, channels);
                Mix(samples, blockChannels, i0, i1, t, dst, mixVolume);
                written++;
                _position += step;
            }

            int run = written - runStart;
            if (run > 0)
                _clock.AppendSegment(_outputFrames + runStart, run, mediaStart, rate, clockEpoch);

            if (_position >= blockFrames)
            {
                Interlocked.Add(ref _queuedFrames, -blockFrames);
                Interlocked.Increment(ref _consumedBlocks);
                block.Dispose();
                _current = null;
                // Carry the overshoot (less than one step) into the next block so
                // resampling stays continuous across block boundaries.
                _position = Math.Max(0, _position - blockFrames);
            }
        }

        if (written < frames)
            destination[(written * channels)..].Clear();

        if (capture is not null)
        {
            var rendered = destination[..(frames * channels)];
            capture.Write(rendered, channels, _output.SampleRate);
            if (volume != 1.0)
            {
                float gain = (float)volume;
                for (int i = 0; i < rendered.Length; i++)
                    rendered[i] *= gain;
            }
        }

        Interlocked.Add(ref _outputFrames, frames);
        return written;
    }

    private void DropCurrent()
    {
        var current = _current;
        if (current is null)
            return;
        Interlocked.Add(ref _queuedFrames, -current.FrameCount);
        current.Dispose();
        _current = null;
        _position = 0;
    }

    /// <summary>One interpolated output frame from source frames i0/i1, mapped to the output channel count.</summary>
    private static void Mix(Span<float> samples, int sourceChannels, int i0, int i1, float t, Span<float> dst, float volume)
    {
        int outChannels = dst.Length;
        if (sourceChannels == outChannels)
        {
            for (int c = 0; c < outChannels; c++)
                dst[c] = Lerp(samples[i0 * sourceChannels + c], samples[i1 * sourceChannels + c], t) * volume;
        }
        else if (sourceChannels == 1)
        {
            float v = Lerp(samples[i0], samples[i1], t) * volume;
            dst.Fill(v);
        }
        else if (outChannels == 1)
        {
            float sum = 0;
            for (int c = 0; c < sourceChannels; c++)
                sum += Lerp(samples[i0 * sourceChannels + c], samples[i1 * sourceChannels + c], t);
            dst[0] = sum / sourceChannels * volume;
        }
        else
        {
            // Front left/right from the first two channels; the rest is folded in equally.
            for (int c = 0; c < outChannels; c++)
                dst[c] = c < sourceChannels ? Lerp(samples[i0 * sourceChannels + c], samples[i1 * sourceChannels + c], t) * volume : 0f;
            if (sourceChannels > outChannels)
            {
                float extra = 0;
                for (int c = outChannels; c < sourceChannels; c++)
                    extra += Lerp(samples[i0 * sourceChannels + c], samples[i1 * sourceChannels + c], t);
                float fold = extra / sourceChannels * volume;
                for (int c = 0; c < outChannels; c++)
                    dst[c] += fold;
            }
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>Releases every queued block. Not thread-safe with the device: stop the output first.</summary>
    public void Dispose()
    {
        DropCurrent();
        while (_queue.TryDequeue(out var block))
            block.Dispose();
        Interlocked.Exchange(ref _queuedFrames, 0);
    }
}
