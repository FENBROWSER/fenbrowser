namespace FenBrowser.Media.Clock;

/// <summary>
/// The playback position that video frame selection and <c>currentTime</c> follow.
/// Safe to read from any thread.
/// </summary>
public interface IMediaClock
{
    MediaTime CurrentTime { get; }

    double PlaybackRate { get; }
}

/// <summary>
/// A wall-clock-driven media clock for content with no audio track, or while no audio
/// device is available (ADR-0003 fallback).
/// </summary>
public sealed class MonotonicMediaClock : IMediaClock
{
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private MediaTime _anchorMedia;
    private long _anchorTimestamp;
    private double _rate = 1.0;
    private bool _running;

    public MonotonicMediaClock(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _running;
        }
    }

    public double PlaybackRate
    {
        get
        {
            lock (_gate)
                return _rate;
        }
    }

    public MediaTime CurrentTime
    {
        get
        {
            lock (_gate)
                return CurrentTimeLocked(_timeProvider.GetTimestamp());
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running)
                return;
            _anchorTimestamp = _timeProvider.GetTimestamp();
            _running = true;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (!_running)
                return;
            _anchorMedia = CurrentTimeLocked(_timeProvider.GetTimestamp());
            _running = false;
        }
    }

    /// <summary>Jumps to <paramref name="time"/> (seek), keeping the running state.</summary>
    public void SetTime(MediaTime time)
    {
        lock (_gate)
        {
            _anchorMedia = time;
            _anchorTimestamp = _timeProvider.GetTimestamp();
        }
    }

    /// <summary>Changes speed from now on. Negative playback is not supported by the pipeline.</summary>
    public void SetPlaybackRate(double rate)
    {
        if (!double.IsFinite(rate) || rate < 0)
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "The playback rate must be finite and not negative.");

        lock (_gate)
        {
            long now = _timeProvider.GetTimestamp();
            _anchorMedia = CurrentTimeLocked(now);
            _anchorTimestamp = now;
            _rate = rate;
        }
    }

    private MediaTime CurrentTimeLocked(long now)
    {
        if (!_running || _anchorMedia.IsInfinite)
            return _anchorMedia;

        Int128 elapsedMicros = (Int128)(now - _anchorTimestamp) * MediaTime.MicrosecondsPerSecond / _timeProvider.TimestampFrequency;
        double advanced = Math.Round((double)elapsedMicros * _rate);
        return _anchorMedia + MediaTime.FromMicroseconds((long)Math.Min(advanced, long.MaxValue / 2));
    }
}

/// <summary>What an audio output reports about playback progress.</summary>
public interface IAudioPlaybackPosition
{
    int SampleRate { get; }

    /// <summary>
    /// Sample frames that have become audible since the stream opened. Backends subtract
    /// their output latency, so this is what the listener hears now. Never decreases.
    /// </summary>
    long FramesPlayed { get; }
}

/// <summary>
/// Media time derived from the audio device (the master clock of docs/MEDIA_ENGINE_DESIGN.md §2.4).
/// </summary>
/// <remarks>
/// The audio renderer describes every run of frames it writes as a segment: where it starts
/// in the output stream, how many frames, the media time of its first frame, and the rate
/// it was time-stretched at. Mapping the device's played position through those segments
/// keeps the clock exact across rate changes and gaps. When playback runs past the written
/// data (an underrun), time holds at the end of the last segment instead of running ahead
/// of the audio.
/// </remarks>
public sealed class AudioMasterClock : IMediaClock
{
    private readonly IAudioPlaybackPosition _position;
    private readonly Lock _gate = new();
    private readonly List<Segment> _segments = [];
    private MediaTime _idleTime;

    public AudioMasterClock(IAudioPlaybackPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        if (position.SampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(position), position.SampleRate, "The sample rate must be positive.");
        _position = position;
    }

    public MediaTime CurrentTime
    {
        get
        {
            long played = _position.FramesPlayed;
            lock (_gate)
            {
                if (_segments.Count == 0)
                    return _idleTime;
                PruneLocked(played);
                return TimeAtLocked(played);
            }
        }
    }

    public double PlaybackRate
    {
        get
        {
            long played = _position.FramesPlayed;
            lock (_gate)
            {
                if (_segments.Count == 0)
                    return 1.0;
                foreach (var s in _segments)
                {
                    if (played < s.OutputEnd)
                        return s.Rate;
                }

                return _segments[^1].Rate;
            }
        }
    }

    /// <summary>True when the device has played everything written, so time is holding.</summary>
    public bool IsStarved
    {
        get
        {
            long played = _position.FramesPlayed;
            lock (_gate)
                return _segments.Count > 0 && played >= _segments[^1].OutputEnd;
        }
    }

    /// <summary>
    /// Records that <paramref name="frameCount"/> output frames starting at output frame
    /// <paramref name="outputStartFrame"/> carry media from <paramref name="mediaStart"/>
    /// at <paramref name="rate"/> (0 for inserted silence that does not advance time).
    /// </summary>
    public void AppendSegment(long outputStartFrame, long frameCount, MediaTime mediaStart, double rate)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(outputStartFrame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCount);
        if (!double.IsFinite(rate) || rate < 0)
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "The rate must be finite and not negative.");

        lock (_gate)
        {
            if (_segments.Count > 0 && outputStartFrame < _segments[^1].OutputEnd)
                throw new InvalidOperationException("Audio segments must not overlap or go backwards in the output stream.");
            _segments.Add(new Segment(outputStartFrame, frameCount, mediaStart, rate));
        }
    }

    /// <summary>Forgets every segment (flush or seek); time reads <paramref name="time"/> until new audio is written.</summary>
    public void Reset(MediaTime time)
    {
        lock (_gate)
        {
            _segments.Clear();
            _idleTime = time;
        }
    }

    private MediaTime TimeAtLocked(long played)
    {
        var first = _segments[0];
        if (played < first.OutputStart)
            return first.MediaStart;

        for (int i = 0; i < _segments.Count; i++)
        {
            var s = _segments[i];
            if (played < s.OutputStart)
                return _segments[i - 1].MediaEnd(_position.SampleRate); // in a gap: hold
            if (played < s.OutputEnd)
                return s.MediaAt(played - s.OutputStart, _position.SampleRate);
        }

        return _segments[^1].MediaEnd(_position.SampleRate);
    }

    private void PruneLocked(long played)
    {
        // Keep the segment being played (or the last one); drop everything fully behind it.
        int drop = 0;
        while (drop < _segments.Count - 1 && _segments[drop + 1].OutputStart <= played)
            drop++;
        if (drop > 0)
            _segments.RemoveRange(0, drop);
    }

    private readonly record struct Segment(long OutputStart, long FrameCount, MediaTime MediaStart, double Rate)
    {
        public long OutputEnd => OutputStart + FrameCount;

        public MediaTime MediaAt(long framesIn, int sampleRate)
        {
            double micros = Math.Round((double)framesIn * Rate * MediaTime.MicrosecondsPerSecond / sampleRate);
            return MediaStart + MediaTime.FromMicroseconds((long)micros);
        }

        public MediaTime MediaEnd(int sampleRate) => MediaAt(FrameCount, sampleRate);
    }
}
