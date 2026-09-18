using FenBrowser.Media.Buffers;

namespace FenBrowser.Media.Video;

/// <summary>
/// One picture ready to composite: packed 8-bit B, G, R, A rows in a pooled buffer. The
/// presenter and every painter using it share it by reference count; the buffer goes
/// back to the pool when the last holder releases it.
/// </summary>
public sealed class PresentedPicture
{
    private byte[]? _buffer;
    private int _references = 1;

    internal PresentedPicture(byte[] buffer, int width, int height, int stride, long sequence, MediaTime timestamp)
    {
        _buffer = buffer;
        Width = width;
        Height = height;
        Stride = stride;
        Sequence = sequence;
        Timestamp = timestamp;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Bytes per row; at least <c>Width * 4</c>.</summary>
    public int Stride { get; }

    /// <summary>Increases with every picture the presenter publishes, so a painter can tell a new one from the last it drew.</summary>
    public long Sequence { get; }

    /// <summary>The media time this picture represents.</summary>
    public MediaTime Timestamp { get; }

    /// <summary>The pixel rows. Valid only between <see cref="AddRef"/> (or acquisition) and <see cref="Release"/>.</summary>
    public ReadOnlySpan<byte> Pixels
    {
        get
        {
            var buffer = _buffer;
            ObjectDisposedException.ThrowIf(buffer is null, this);
            return buffer.AsSpan(0, Stride * Height);
        }
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
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null)
                MediaBufferPool.Return(buffer);
        }
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

    /// <summary>Converts <paramref name="frame"/> to BGRA and makes it the latest picture.</summary>
    public void Publish(VideoFrame frame, MediaLimits limits)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(limits);
        limits.CheckVideoDimensions(frame.Width, frame.Height);

        int stride = frame.Width * 4;
        byte[] buffer = MediaBufferPool.Bytes.Rent(checked(stride * frame.Height));
        PixelConverter.ToBgra(frame, buffer.AsSpan(0, stride * frame.Height), stride);
        long sequence = Interlocked.Increment(ref _sequence);
        var picture = new PresentedPicture(buffer, frame.Width, frame.Height, stride, sequence, frame.Timestamp);

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
