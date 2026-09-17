namespace FenBrowser.Media.Buffers;

/// <summary>
/// One compressed access unit from a demuxer, backed by a pooled buffer.
/// </summary>
/// <remarks>
/// Ownership moves with the packet: whoever holds it last disposes it. Buffers come
/// from <see cref="MediaBufferPool"/> and are zeroed on return, because one media
/// process decodes for many origins and a decoder that over-reads must never see
/// another page's bytes.
/// </remarks>
public sealed class EncodedPacket : IDisposable
{
    private byte[]? _buffer;
    private readonly int _length;

    private EncodedPacket(byte[] buffer, int length, int trackId, MediaTime pts, MediaTime dts, MediaTime duration, bool isKeyframe)
    {
        _buffer = buffer;
        _length = length;
        TrackId = trackId;
        Pts = pts;
        Dts = dts;
        Duration = duration;
        IsKeyframe = isKeyframe;
    }

    public int TrackId { get; }

    /// <summary>Presentation timestamp.</summary>
    public MediaTime Pts { get; }

    /// <summary>Decode timestamp. Equal to <see cref="Pts"/> for codecs without reordering.</summary>
    public MediaTime Dts { get; }

    public MediaTime Duration { get; }

    public bool IsKeyframe { get; }

    public int Length => _length;

    /// <summary>The packet bytes. Producers fill this before handing the packet on; consumers only read it.</summary>
    public Memory<byte> Memory
    {
        get
        {
            var buffer = _buffer;
            ObjectDisposedException.ThrowIf(buffer is null, this);
            return buffer.AsMemory(0, _length);
        }
    }

    public ReadOnlySpan<byte> Span => Memory.Span;

    /// <summary>
    /// Rents an uninitialized packet of <paramref name="length"/> bytes after checking it
    /// against the per-kind packet ceiling.
    /// </summary>
    public static EncodedPacket Rent(
        MediaLimits limits,
        MediaTrackKind kind,
        int trackId,
        int length,
        MediaTime pts,
        MediaTime dts,
        MediaTime duration,
        bool isKeyframe)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.CheckPacketSize(kind, length);
        byte[] buffer = MediaBufferPool.Bytes.Rent(length);
        return new EncodedPacket(buffer, length, trackId, pts, dts, duration, isKeyframe);
    }

    public static EncodedPacket Copy(
        MediaLimits limits,
        MediaTrackKind kind,
        int trackId,
        ReadOnlySpan<byte> data,
        MediaTime pts,
        MediaTime dts,
        MediaTime duration,
        bool isKeyframe)
    {
        var packet = Rent(limits, kind, trackId, data.Length, pts, dts, duration, isKeyframe);
        data.CopyTo(packet.Memory.Span);
        return packet;
    }

    public void Dispose()
    {
        byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
            MediaBufferPool.Return(buffer);
    }

    public override string ToString() =>
        $"packet(track {TrackId}, pts {Pts}, dts {Dts}, {_length} bytes{(IsKeyframe ? ", key" : "")})";
}
