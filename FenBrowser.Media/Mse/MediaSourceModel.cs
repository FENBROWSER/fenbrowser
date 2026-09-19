using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Mse;

public enum MediaSourceReadyState
{
    Closed,
    Open,
    Ended,
}

/// <summary>MSE <c>EndOfStreamError</c>, or none.</summary>
public enum EndOfStreamError
{
    None,
    Network,
    Decode,
}

/// <summary>Byte budgets for a SourceBuffer's coded frames (design §4 "MSE buffer quota").</summary>
public sealed record MseLimits(long VideoBufferQuota = 150L * 1024 * 1024, long AudioBufferQuota = 12L * 1024 * 1024)
{
    public static readonly MseLimits Default = new();
}

/// <summary>
/// The MSE <c>MediaSource</c> without its DOM surface (§2): ready state, duration, the
/// source buffers and the algorithms that touch all of them - attach/detach, "end of
/// stream", "duration change". The playback side reads coded frames through
/// <see cref="MseDecodeSource"/>. Every member runs under <see cref="Gate"/>, which the
/// decode source also takes, because appends come from the page thread and reads from the
/// player's media task.
/// </summary>
public sealed class MediaSourceModel
{
    private readonly List<SourceBufferModel> _sourceBuffers = [];
    private readonly MediaPipelineContext _context;
    private TaskCompletionSource<bool> _firstInitialization = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MediaSourceModel(MediaPipelineContext context, MseLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        Limits = limits ?? MseLimits.Default;
    }

    public object Gate { get; } = new();

    public MseLimits Limits { get; }

    internal MediaLimits MediaLimits => _context.Limits;

    public MediaSourceReadyState ReadyState { get; private set; } = MediaSourceReadyState.Closed;

    /// <summary>The duration attribute: null while NaN.</summary>
    public MediaTime? Duration { get; private set; }

    public EndOfStreamError EndOfStreamError { get; private set; }

    public IReadOnlyList<SourceBufferModel> SourceBuffers => _sourceBuffers;

    /// <summary>The live seekable range set by script, or null.</summary>
    public (MediaTime Start, MediaTime End)? LiveSeekableRange { get; private set; }

    /// <summary>Raised (under the gate) when buffered data, duration or the ready state changed; the player wakes up on it.</summary>
    public event Action? Changed;

    /// <summary>The first initialization segment has arrived; the decode source's OpenAsync waits on it.</summary>
    public Task<bool> FirstInitializationSegment => _firstInitialization.Task;

    // -- attach / detach (§2.4.2, §2.4.3) ---------------------------------------------------

    public void Attach()
    {
        lock (Gate)
        {
            if (ReadyState != MediaSourceReadyState.Closed)
                throw new MseInvalidOperationException("InvalidStateError", "The MediaSource is already attached.");
            ReadyState = MediaSourceReadyState.Open;
            Duration = null;
            EndOfStreamError = EndOfStreamError.None;
            Changed?.Invoke();
        }
    }

    public void Detach()
    {
        lock (Gate)
        {
            foreach (var buffer in _sourceBuffers)
                buffer.Clear();
            _sourceBuffers.Clear();
            ReadyState = MediaSourceReadyState.Closed;
            Duration = null;
            LiveSeekableRange = null;
            if (!_firstInitialization.Task.IsCompleted)
                _firstInitialization.TrySetResult(false);
            _firstInitialization = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Changed?.Invoke();
        }
    }

    // -- source buffers (§2.1 addSourceBuffer / removeSourceBuffer) --------------------------

    public SourceBufferModel AddSourceBuffer(string type, bool generateTimestamps)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        lock (Gate)
        {
            if (ReadyState != MediaSourceReadyState.Open)
                throw new MseInvalidOperationException("InvalidStateError", "The MediaSource is not open.");
            var parser = SegmentParser.Create(type, _context)
                ?? throw new MseInvalidOperationException("NotSupportedError", $"'{type}' is not a supported byte stream format.");
            var buffer = new SourceBufferModel(this, type, parser, generateTimestamps);
            _sourceBuffers.Add(buffer);
            return buffer;
        }
    }

    public void RemoveSourceBuffer(SourceBufferModel buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        lock (Gate)
        {
            if (!_sourceBuffers.Remove(buffer))
                throw new MseInvalidOperationException("NotFoundError", "The SourceBuffer does not belong to this MediaSource.");
            buffer.Clear();
            Changed?.Invoke();
        }
    }

    public SegmentParser? CreateParser(string type) => SegmentParser.Create(type, _context);

    internal bool TrackIdInUse(int trackId) => _sourceBuffers.Any(b => b.TrackBuffers.Any(t => t.TrackId == trackId));

    // -- duration (§2.4.6 "duration change") -------------------------------------------------

    public void SetDuration(MediaTime duration)
    {
        lock (Gate)
        {
            if (ReadyState != MediaSourceReadyState.Open)
                throw new MseInvalidOperationException("InvalidStateError", "The MediaSource is not open.");
            // Step 2-3: a new duration below the highest buffered presentation timestamp is an error.
            var highestStart = HighestBufferedStart();
            if (highestStart is { } start && duration < start)
                throw new MseInvalidOperationException("InvalidStateError", "The duration is below the highest buffered presentation timestamp.");
            var highestEnd = HighestBufferedEnd();
            if (highestEnd is { } end && duration < end)
                duration = end; // step 4: the buffered frames beyond the new duration stay, the duration snaps to them
            Duration = duration;
            Changed?.Invoke();
        }
    }

    /// <summary>§2.4.7 "end of stream".</summary>
    public void EndOfStream(EndOfStreamError error)
    {
        lock (Gate)
        {
            if (ReadyState != MediaSourceReadyState.Open)
                throw new MseInvalidOperationException("InvalidStateError", "The MediaSource is not open.");
            ReadyState = MediaSourceReadyState.Ended;
            EndOfStreamError = error;
            if (error == EndOfStreamError.None)
            {
                var highestEnd = HighestBufferedEnd();
                Duration = highestEnd ?? Duration ?? MediaTime.Zero;
            }

            Changed?.Invoke();
        }
    }

    /// <summary>§3.5.4 "prepare append" step 3: an append while ended puts the source back in the open state; true when it did.</summary>
    public bool Reopen()
    {
        lock (Gate)
        {
            if (ReadyState != MediaSourceReadyState.Ended)
                return false;
            ReadyState = MediaSourceReadyState.Open;
            EndOfStreamError = EndOfStreamError.None;
            Changed?.Invoke();
            return true;
        }
    }

    public void SetLiveSeekableRange(MediaTime start, MediaTime end)
    {
        lock (Gate)
        {
            LiveSeekableRange = (start, end);
            Changed?.Invoke();
        }
    }

    public void ClearLiveSeekableRange()
    {
        lock (Gate)
        {
            LiveSeekableRange = null;
            Changed?.Invoke();
        }
    }

    /// <summary>The media element's buffered attribute for a MediaSource (HTML §4.8.11.5 buffered / MSE §3.1): the intersection of the active buffers, the highest end kept when ended.</summary>
    public MediaTimeRanges Buffered
    {
        get
        {
            lock (Gate)
            {
                var active = _sourceBuffers.Where(b => b.HasTracks).ToList();
                if (active.Count == 0)
                    return MediaTimeRanges.Empty;
                MediaTimeRanges? result = null;
                MediaTime highestEnd = MediaTime.Zero;
                foreach (var buffer in active)
                {
                    var ranges = buffer.Buffered;
                    if (ranges.Count > 0 && ranges.End(ranges.Count - 1) > highestEnd)
                        highestEnd = ranges.End(ranges.Count - 1);
                }

                foreach (var buffer in active)
                {
                    var ranges = buffer.Buffered;
                    if (ReadyState == MediaSourceReadyState.Ended && ranges.Count > 0)
                        ranges = ExtendLastRange(ranges, highestEnd);

                    result = result is null ? ranges : result.Intersect(ranges);
                }

                return result ?? MediaTimeRanges.Empty;
            }
        }
    }

    /// <summary>HTML seekable for a MediaSource (MSE §8 "HTMLMediaElement.seekable").</summary>
    public MediaTimeRanges Seekable
    {
        get
        {
            lock (Gate)
            {
                if (Duration is null)
                    return MediaTimeRanges.Empty;
                if (!Duration.Value.IsInfinite)
                    return MediaTimeRanges.Single(MediaTime.Zero, Duration.Value);
                var buffered = Buffered;
                if (LiveSeekableRange is { } live)
                {
                    var union = buffered.Union(MediaTimeRanges.Single(live.Start, live.End));
                    return union.Count == 0 ? MediaTimeRanges.Empty : MediaTimeRanges.Single(union.Start(0), union.End(union.Count - 1));
                }

                return buffered.Count == 0 ? MediaTimeRanges.Empty : MediaTimeRanges.Single(MediaTime.Zero, buffered.End(buffered.Count - 1));
            }
        }
    }

    /// <summary>MSE §3.1 buffered step 5.2: the last range reaches the highest end across tracks.</summary>
    internal static MediaTimeRanges ExtendLastRange(MediaTimeRanges ranges, MediaTime highestEnd)
    {
        var extended = new List<(MediaTime Start, MediaTime End)>();
        for (int i = 0; i < ranges.Count - 1; i++)
            extended.Add((ranges.Start(i), ranges.End(i)));
        var lastStart = ranges.Start(ranges.Count - 1);
        var lastEnd = ranges.End(ranges.Count - 1);
        extended.Add((lastStart, highestEnd > lastEnd ? highestEnd : lastEnd));
        return MediaTimeRanges.From(extended);
    }

    private MediaTime? HighestBufferedEnd()
    {
        MediaTime? highest = null;
        foreach (var buffer in _sourceBuffers)
        {
            if (buffer.HighestBufferedEnd is { } end && (highest is null || end > highest))
                highest = end;
        }

        return highest;
    }

    private MediaTime? HighestBufferedStart()
    {
        MediaTime? highest = null;
        foreach (var buffer in _sourceBuffers)
        {
            foreach (var track in buffer.TrackBuffers)
            {
                foreach (var frame in track.Frames)
                {
                    if (highest is null || frame.Pts > highest)
                        highest = frame.Pts;
                }
            }
        }

        return highest;
    }

    // -- notifications from the source buffers (under the gate) ------------------------------

    internal void OnInitializationSegment(SourceBufferModel buffer, DemuxerInfo info, bool first)
    {
        // §3.5.8 step 5-6: the first initialization segment sets a NaN duration from the
        // segment, or to positive infinity when the segment carries none.
        if (Duration is null)
            Duration = info.Duration.IsInfinite || info.Duration == MediaTime.Zero ? MediaTime.PositiveInfinity : info.Duration;
        if (first)
            InitializationSegmentReceived?.Invoke(buffer, info);
        if (_sourceBuffers.All(b => b.HasTracks))
            _firstInitialization.TrySetResult(true);
        Changed?.Invoke();
    }

    /// <summary>A source buffer received its first initialization segment (the DOM side adds tracks and fires loadedmetadata through the element).</summary>
    public event Action<SourceBufferModel, DemuxerInfo>? InitializationSegmentReceived;

    internal void OnFrameAppended(MediaTime frameEnd)
    {
        // §3.5.11 step 20: the duration grows to cover the frame.
        if (Duration is { } duration && !duration.IsInfinite && frameEnd > duration)
            Duration = frameEnd;
    }

    internal void BufferedChanged() => Changed?.Invoke();

    /// <summary>Called after an append (under the gate) so listeners see the new ranges once per append.</summary>
    public void NotifyChanged()
    {
        lock (Gate)
            Changed?.Invoke();
    }
}
