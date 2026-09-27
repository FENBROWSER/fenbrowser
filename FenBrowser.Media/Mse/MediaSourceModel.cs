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

    internal MediaPipelineContext Context => _context;

    public MediaSourceReadyState ReadyState { get; private set; } = MediaSourceReadyState.Closed;

    /// <summary>The duration attribute: null while NaN.</summary>
    public MediaTime? Duration { get; private set; }

    /// <summary>
    /// The exact double script assigned to <c>duration</c>, while it is still in force:
    /// <see cref="MediaTime"/> rounds to microseconds and saturates, but the attribute
    /// must read back what was set (5e-324, Number.MAX_VALUE). Null once the engine set
    /// the duration itself.
    /// </summary>
    public double? ExactDurationSeconds { get; private set; }

    /// <summary>The duration as script sees it: the exact value assigned, else the media time, NaN while unset.</summary>
    public double DurationSeconds =>
        ExactDurationSeconds ?? (Duration is { } value ? (value.IsInfinite ? double.PositiveInfinity : value.TotalSeconds) : double.NaN);

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
            ExactDurationSeconds = null;
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
            ExactDurationSeconds = null;
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

    // -- duration (§2.4.6 "duration change") -------------------------------------------------

    public void SetDuration(MediaTime duration, double? exactSeconds = null)
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
            {
                duration = end; // step 4: the buffered frames beyond the new duration stay, the duration snaps to them
                exactSeconds = null;
            }

            // §2.4.6 step 1: the same duration again is not a change.
            var seconds = exactSeconds ?? (duration.IsInfinite ? double.PositiveInfinity : duration.TotalSeconds);
            if (Duration == duration && DurationSeconds.Equals(seconds))
                return;

            Duration = duration;
            ExactDurationSeconds = exactSeconds;
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
                // Step 3.1: the largest track buffer range end across every SourceBuffer,
                // which is 0 when nothing is buffered, whatever script set before.
                var ended = HighestBufferedEnd() ?? MediaTime.Zero;
                if (ended != Duration)
                    ExactDurationSeconds = null;
                Duration = ended;
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

    /// <summary>
    /// Whether a track's frame for <paramref name="position"/> is encrypted with a key that
    /// <paramref name="keys"/> (the element's MediaKeys, or null) does not hold. Such a frame
    /// is buffered but cannot be decoded, so it is not "data for the current playback
    /// position" (HTML §4.8.11.7) and the element must not leave HAVE_METADATA over it (EME
    /// §7.4 "Wait for Key").
    /// </summary>
    public bool NeedsMissingKey(MediaTime position, Eme.IMediaKeySource? keys)
    {
        lock (Gate)
        {
            foreach (var buffer in _sourceBuffers)
            {
                foreach (var track in buffer.TrackBuffers)
                {
                    CodedFrame? next = null;
                    foreach (var frame in track.Frames)
                    {
                        if (frame.End <= position)
                            continue;
                        if (next is null || frame.Pts < next.Pts)
                            next = frame;
                        if (frame.Pts <= position)
                            break;
                    }

                    if (next?.Packet.Encryption is { } encryption && (keys is null || !keys.TryGetKey(encryption.KeyId, out _)))
                        return true;
                }
            }

            return false;
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
        {
            Duration = info.Duration.IsInfinite || info.Duration == MediaTime.Zero ? MediaTime.PositiveInfinity : info.Duration;
            ExactDurationSeconds = null;
        }

        // EME §7.1: every initialization segment may announce initialization data, not
        // just the first one - a stream can change its protection system mid-flight.
        foreach (var announced in info.InitializationData)
        {
            _pendingInitializationData.Add(announced);
            // The element hears about it inside the append, so the encrypted event
            // reaches the page before any of the appended media can start playing.
            InitializationDataAnnounced?.Invoke(announced.InitDataType, announced.InitData);
        }

        if (first)
            InitializationSegmentReceived?.Invoke(buffer, info);
        if (_sourceBuffers.All(b => b.HasTracks))
            _firstInitialization.TrySetResult(true);
        Changed?.Invoke();
    }

    /// <summary>A source buffer received its first initialization segment (the DOM side adds tracks and fires loadedmetadata through the element).</summary>
    public event Action<SourceBufferModel, DemuxerInfo>? InitializationSegmentReceived;

    /// <summary>
    /// EME §7.1: an appended initialization segment announced initialization data. Raised
    /// on the thread that appended, which is the element's own.
    /// </summary>
    public event Action<string, byte[]>? InitializationDataAnnounced;

    private readonly List<(string InitDataType, byte[] InitData)> _pendingInitializationData = [];

    /// <summary>
    /// The Encrypted Media Extensions initialization data appended segments have announced
    /// since this was last called. The caller reads it under <see cref="Gate"/> and turns
    /// each entry into one <c>encrypted</c> event on the element.
    /// </summary>
    public IReadOnlyList<(string InitDataType, byte[] InitData)> TakePendingInitializationData()
    {
        if (_pendingInitializationData.Count == 0)
            return [];

        var taken = _pendingInitializationData.ToArray();
        _pendingInitializationData.Clear();
        return taken;
    }

    internal void OnFrameAppended(MediaTime frameEnd)
    {
        // §3.5.11 step 20: the duration grows to cover the frame.
        if (Duration is { } duration && !duration.IsInfinite && frameEnd > duration)
        {
            Duration = frameEnd;
            ExactDurationSeconds = null;
        }
    }

    internal void BufferedChanged() => Changed?.Invoke();

    /// <summary>Called after an append (under the gate) so listeners see the new ranges once per append.</summary>
    public void NotifyChanged()
    {
        lock (Gate)
            Changed?.Invoke();
    }
}
