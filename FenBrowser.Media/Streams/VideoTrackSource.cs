using FenBrowser.Media.Buffers;

namespace FenBrowser.Media.Streams;

/// <summary>
/// The pictures a video MediaStreamTrack carries - a canvas capture's frames - as the latest
/// BGRA picture. The producer publishes on its own thread; each consumer copies the picture
/// into a frame of its own when told a new one arrived.
/// </summary>
public sealed class VideoTrackSource
{
    private readonly object _gate = new();
    private byte[] _pixels = [];
    private int _width;
    private int _height;
    private long _sequence;

    /// <summary>Raised on the producer's thread after each new picture.</summary>
    public event Action<VideoTrackSource>? FrameProduced;

    public int Width
    {
        get { lock (_gate) return _width; }
    }

    public int Height
    {
        get { lock (_gate) return _height; }
    }

    /// <summary>The number of pictures published so far; 0 before the first.</summary>
    public long Sequence => Interlocked.Read(ref _sequence);

    /// <summary>Publishes a BGRA picture of <paramref name="width"/> x <paramref name="height"/>.</summary>
    public void Publish(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || bgra.Length < stride * (height - 1) + width * 4)
            throw new ArgumentException("The picture does not fit its dimensions.");

        lock (_gate)
        {
            int size = width * height * 4;
            if (_pixels.Length != size)
                _pixels = new byte[size];
            for (int row = 0; row < height; row++)
                bgra.Slice(row * stride, width * 4).CopyTo(_pixels.AsSpan(row * width * 4, width * 4));
            _width = width;
            _height = height;
        }

        Interlocked.Increment(ref _sequence);
        FrameProduced?.Invoke(this);
    }

    /// <summary>A frame holding the latest picture, or null before the first; the caller owns it.</summary>
    public VideoFrame? CopyLatest(MediaLimits limits, MediaTime timestamp)
    {
        lock (_gate)
        {
            if (_width == 0 || _height == 0)
                return null;

            var frame = VideoFrame.Allocate(limits, VideoPixelFormat.Bgra32, _width, _height, timestamp, MediaTime.Zero);
            var plane = frame.GetPlane(0);
            int stride = frame.GetStride(0);
            for (int row = 0; row < _height; row++)
                _pixels.AsSpan(row * _width * 4, _width * 4).CopyTo(plane.Slice(row * stride, _width * 4));
            return frame;
        }
    }
}
