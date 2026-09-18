using System.Numerics;

namespace FenBrowser.Media.Buffers;

public enum VideoPixelFormat
{
    /// <summary>8-bit planar Y, U, V with 2×2 chroma subsampling.</summary>
    I420,

    /// <summary>8-bit Y plane followed by one interleaved U/V plane, 2×2 subsampled.</summary>
    Nv12,

    /// <summary>8-bit packed B, G, R, A.</summary>
    Bgra32,
}

/// <summary>
/// One decoded picture in a single pooled buffer, laid out plane after plane.
/// </summary>
/// <remarks>
/// Strides are rounded up to <see cref="Alignment"/> so SIMD colour conversion can
/// read whole blocks. The buffer is zeroed on return (see <see cref="EncodedPacket"/>).
/// </remarks>
public sealed class VideoFrame : IDisposable
{
    private byte[]? _buffer;
    private readonly PlaneLayout[] _planes;

    private VideoFrame(byte[] buffer, PlaneLayout[] planes, VideoPixelFormat format, int width, int height, int alignment, MediaTime timestamp, MediaTime duration)
    {
        _buffer = buffer;
        _planes = planes;
        Format = format;
        Width = width;
        Height = height;
        Alignment = alignment;
        Timestamp = timestamp;
        Duration = duration;
        int total = 0;
        foreach (var plane in planes)
            total += plane.Stride * plane.Height;
        TotalBytes = total;
    }

    public VideoPixelFormat Format { get; }
    public int Width { get; }
    public int Height { get; }
    public int Alignment { get; }
    public MediaTime Timestamp { get; }
    public MediaTime Duration { get; }

    public int PlaneCount => _planes.Length;

    /// <summary>
    /// Rents a frame after checking the dimensions against <paramref name="limits"/>.
    /// </summary>
    /// <param name="alignment">Stride alignment in bytes; a power of two from 1 to 64.</param>
    public static VideoFrame Allocate(
        MediaLimits limits,
        VideoPixelFormat format,
        int width,
        int height,
        MediaTime timestamp,
        MediaTime duration,
        int alignment = 16)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.CheckVideoDimensions(width, height);
        var planes = Layout(format, width, height, alignment, out int totalBytes);
        byte[] buffer = MediaBufferPool.Bytes.Rent(totalBytes);
        return new VideoFrame(buffer, planes, format, width, height, alignment, timestamp, duration);
    }

    /// <summary>
    /// The bytes a frame of this shape occupies with its planes laid end to end: what a
    /// region carrying one such frame between processes must hold. The layout is a pure
    /// function of the arguments, so two processes computing it agree.
    /// </summary>
    public static int LayoutSize(VideoPixelFormat format, int width, int height, int alignment = 16)
    {
        _ = Layout(format, width, height, alignment, out int totalBytes);
        return totalBytes;
    }

    /// <summary>The bytes of every plane, laid end to end as the layout describes.</summary>
    public int TotalBytes { get; private set; }

    private static PlaneLayout[] Layout(VideoPixelFormat format, int width, int height, int alignment, out int totalBytes)
    {
        if (alignment is < 1 or > 64 || !BitOperations.IsPow2(alignment))
            throw new ArgumentOutOfRangeException(nameof(alignment), alignment, "Alignment must be a power of two from 1 to 64.");
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), $"{width}x{height}", "Dimensions must be positive.");

        int chromaWidth = (width + 1) / 2;
        int chromaHeight = (height + 1) / 2;
        (int Width, int Height, int BytesPerPixel)[] shapes = format switch
        {
            VideoPixelFormat.I420 => [(width, height, 1), (chromaWidth, chromaHeight, 1), (chromaWidth, chromaHeight, 1)],
            VideoPixelFormat.Nv12 => [(width, height, 1), (chromaWidth, chromaHeight, 2)],
            VideoPixelFormat.Bgra32 => [(width, height, 4)],
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };

        var planes = new PlaneLayout[shapes.Length];
        long offset = 0;
        for (int i = 0; i < shapes.Length; i++)
        {
            var (w, h, bpp) = shapes[i];
            long stride = AlignUp((long)w * bpp, alignment);
            planes[i] = new PlaneLayout(checked((int)offset), checked((int)stride), w, h);
            offset += stride * h;
        }

        totalBytes = checked((int)offset);
        return planes;
    }

    /// <summary>The bytes of plane <paramref name="index"/>: <see cref="GetPlaneHeight"/> rows of <see cref="GetStride"/> bytes.</summary>
    public Span<byte> GetPlane(int index)
    {
        var buffer = _buffer;
        ObjectDisposedException.ThrowIf(buffer is null, this);
        var plane = _planes[index];
        return buffer.AsSpan(plane.Offset, plane.Stride * plane.Height);
    }

    public int GetStride(int index) => _planes[index].Stride;

    public int GetPlaneWidth(int index) => _planes[index].Width;

    public int GetPlaneHeight(int index) => _planes[index].Height;

    public void Dispose()
    {
        byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
            MediaBufferPool.Return(buffer);
    }

    public override string ToString() => $"frame({Format} {Width}x{Height}, ts {Timestamp})";

    private static long AlignUp(long value, int alignment) => (value + alignment - 1) & ~(long)(alignment - 1);

    private readonly record struct PlaneLayout(int Offset, int Stride, int Width, int Height);
}
