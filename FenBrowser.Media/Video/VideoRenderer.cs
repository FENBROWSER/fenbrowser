using FenBrowser.Media.Buffers;

namespace FenBrowser.Media.Video;

/// <summary>
/// The video renderer node of design §2.3 and §2.4: a bounded queue of decoded pictures
/// in presentation order, and for each vsync the choice of the picture whose
/// <c>[pts, pts + duration)</c> contains the clock time. Pictures the clock has passed
/// without ever being shown are dropped and counted for <c>getVideoPlaybackQuality()</c>.
/// </summary>
/// <remarks>
/// Only the media task calls in; the counters are readable from any thread. The frame
/// returned by <see cref="Select"/> stays owned by the renderer until the next selection
/// replaces it, so a caller must copy or convert it before returning to the task loop.
/// </remarks>
public sealed class VideoRenderer : IDisposable
{
    private readonly Queue<VideoFrame> _queue = new();
    private readonly int _capacity;
    private VideoFrame? _current;
    private bool _endOfStream;
    private long _presented;
    private long _dropped;
    private long _decoded;

    public VideoRenderer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public int Capacity => _capacity;

    public int QueuedCount => _queue.Count;

    public bool IsFull => _queue.Count >= _capacity;

    /// <summary>The picture the clock last selected, until another replaces it.</summary>
    public VideoFrame? Current => _current;

    /// <summary>Pictures handed to the compositor (<c>totalVideoFrames - droppedVideoFrames</c>).</summary>
    public long PresentedFrames => Interlocked.Read(ref _presented);

    /// <summary>Pictures the clock passed before they could be shown.</summary>
    public long DroppedFrames => Interlocked.Read(ref _dropped);

    /// <summary>Every picture the decoder produced (<c>totalVideoFrames</c>).</summary>
    public long DecodedFrames => Interlocked.Read(ref _decoded);

    /// <summary>Presentation time of the last queued picture's end, or the current picture's, or null.</summary>
    public MediaTime? QueuedEnd
    {
        get
        {
            VideoFrame? last = null;
            foreach (var frame in _queue)
                last = frame;
            last ??= _current;
            return last is null ? null : last.Timestamp + last.Duration;
        }
    }

    /// <summary>Decoded pictures ahead of <paramref name="now"/> as a duration (HAVE_FUTURE_DATA accounting).</summary>
    public MediaTime BufferedAhead(MediaTime now)
    {
        var end = QueuedEnd;
        return end is { } e && e > now ? e - now : MediaTime.Zero;
    }

    public bool EndOfStream => _endOfStream;

    /// <summary>No more pictures are queued or coming, and the clock is past the last one.</summary>
    public bool IsDrained(MediaTime now) =>
        _endOfStream && _queue.Count == 0 && (_current is null || now >= _current.Timestamp + _current.Duration);

    public void Enqueue(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_queue.Count >= _capacity)
            throw new InvalidOperationException("The video queue is full; check IsFull before enqueueing.");
        _queue.Enqueue(frame);
        Interlocked.Increment(ref _decoded);
    }

    public void MarkEndOfStream() => _endOfStream = true;

    /// <summary>
    /// The picture to show at <paramref name="now"/>: the last queued one whose start is at
    /// or before <paramref name="now"/>. Returns true when that differs from the current
    /// picture. Pictures skipped over on the way are dropped; the current one stays when
    /// nothing newer is due yet, or when the queue is empty.
    /// </summary>
    public bool Select(MediaTime now, out VideoFrame? frame)
    {
        VideoFrame? chosen = null;
        while (_queue.Count > 0 && _queue.Peek().Timestamp <= now)
        {
            if (chosen is not null)
            {
                chosen.Dispose();
                Interlocked.Increment(ref _dropped);
            }

            chosen = _queue.Dequeue();
        }

        if (chosen is null)
        {
            frame = _current;
            return false;
        }

        _current?.Dispose();
        _current = chosen;
        Interlocked.Increment(ref _presented);
        frame = chosen;
        return true;
    }

    /// <summary>
    /// Shows the first queued picture regardless of the clock (a paused element after a
    /// seek, or the first frame standing in for a missing poster).
    /// </summary>
    public bool SelectFirst(out VideoFrame? frame)
    {
        if (_queue.Count == 0)
        {
            frame = _current;
            return false;
        }

        _current?.Dispose();
        _current = _queue.Dequeue();
        Interlocked.Increment(ref _presented);
        frame = _current;
        return true;
    }

    /// <summary>Drops every queued picture and the current one (a seek).</summary>
    public void Flush()
    {
        while (_queue.TryDequeue(out var frame))
            frame.Dispose();
        _current?.Dispose();
        _current = null;
        _endOfStream = false;
    }

    public void Dispose() => Flush();
}
