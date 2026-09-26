using FenBrowser.Media.Buffers;

namespace FenBrowser.Media.Video;

/// <summary>
/// One picture ready to composite, still in the decoder's planes. The presenter and every
/// painter using it share it by reference count; the frame goes back to the pool when the
/// last holder releases it. A painter converts it to BGRA with <see cref="WriteBgra"/>
/// once per new picture, on its own thread, so the media task only ever swaps pointers.
/// </summary>
public sealed class PresentedPicture
{
    private VideoFrame? _frame;
    private int _references = 1;

    internal PresentedPicture(VideoFrame frame, long sequence)
    {
        _frame = frame;
        Width = frame.Width;
        Height = frame.Height;
        Sequence = sequence;
        Timestamp = frame.Timestamp;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Increases with every picture the presenter publishes, so a painter can tell a new one from the last it drew.</summary>
    public long Sequence { get; }

    /// <summary>The media time this picture represents.</summary>
    public MediaTime Timestamp { get; }

    /// <summary>
    /// Writes the picture as BGRA rows of <paramref name="stride"/> bytes (at least
    /// <c>Width * 4</c>) into <paramref name="destination"/>. Valid only between
    /// acquisition (or <see cref="AddRef"/>) and <see cref="Release"/>.
    /// </summary>
    public void WriteBgra(Span<byte> destination, int stride)
    {
        var frame = _frame;
        ObjectDisposedException.ThrowIf(frame is null, this);
        PixelConverter.ToBgra(frame, destination, stride);
    }

    public void AddRef()
    {
        if (Interlocked.Increment(ref _references) <= 1)
            throw new ObjectDisposedException(nameof(PresentedPicture));
    }

    public void Release()
    {
        int remaining = Interlocked.Decrement(ref _references);
        if (remaining < 0)
            throw new InvalidOperationException("The picture was released more often than acquired.");
        if (remaining == 0)
            Interlocked.Exchange(ref _frame, null)?.Dispose();
    }
}

/// <summary>
/// The seam between the media task and the compositor (design §2.5): the latest picture,
/// published by the player and acquired by whoever paints the element, on any thread.
/// Publishing is a pointer swap; the painter never waits on decoding and the player
/// never waits on painting.
/// </summary>
public sealed class VideoPresenter
{
    private readonly Lock _gate = new();
    private PresentedPicture? _latest;
    private long _sequence;

    /// <summary>Raised on the media task after each publish; the host turns it into a compositor-only invalidation.</summary>
    public event Action? FrameAvailable;

    /// <summary>The sequence number of the latest picture, or 0 before the first.</summary>
    public long Sequence => Interlocked.Read(ref _sequence);

    /// <summary>Makes <paramref name="frame"/> the latest picture, taking ownership of it.</summary>
    public void Publish(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        long sequence = Interlocked.Increment(ref _sequence);
        var picture = new PresentedPicture(frame, sequence);

        PresentedPicture? previous;
        lock (_gate)
        {
            previous = _latest;
            _latest = picture;
        }

        previous?.Release();
        FrameAvailable?.Invoke();
    }

    /// <summary>
    /// The latest picture with one reference held for the caller, or null before the first
    /// frame. The caller must <see cref="PresentedPicture.Release"/> it.
    /// </summary>
    public PresentedPicture? Acquire()
    {
        lock (_gate)
        {
            var latest = _latest;
            latest?.AddRef();
            return latest;
        }
    }

    /// <summary>Drops the latest picture (the element was reset or its resource abandoned).</summary>
    public void Clear()
    {
        PresentedPicture? previous;
        lock (_gate)
        {
            previous = _latest;
            _latest = null;
        }

        previous?.Release();
    }
}
