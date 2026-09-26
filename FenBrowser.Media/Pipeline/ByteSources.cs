namespace FenBrowser.Media.Pipeline;

/// <summary>
/// Random-access bytes of one media resource. The network implementation issues
/// range requests through the broker; the media process never opens sockets or files
/// itself (ADR-0004).
/// </summary>
public interface IByteSource : IAsyncDisposable
{
    /// <summary>Total length in bytes, or null while unknown (live or unsized responses).</summary>
    long? Length { get; }

    /// <summary>
    /// Reads up to <paramref name="destination"/>.Length bytes starting at <paramref name="position"/>.
    /// Returns 0 only at the end of the resource; a short read is not an end.
    /// </summary>
    ValueTask<int> ReadAsync(long position, Memory<byte> destination, CancellationToken cancellationToken);
}

/// <summary>Bytes already in memory: blobs, tests and <c>fenplay</c>.</summary>
public sealed class MemoryByteSource : IByteSource
{
    private readonly ReadOnlyMemory<byte> _data;

    public MemoryByteSource(ReadOnlyMemory<byte> data)
    {
        _data = data;
    }

    public long? Length => _data.Length;

    public ValueTask<int> ReadAsync(long position, Memory<byte> destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        if (position >= _data.Length)
            return ValueTask.FromResult(0);

        int count = (int)Math.Min(destination.Length, _data.Length - position);
        _data.Span.Slice((int)position, count).CopyTo(destination.Span);
        return ValueTask.FromResult(count);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public static class ByteSourceExtensions
{
    /// <summary>
    /// Fills as much of <paramref name="destination"/> as the resource allows, looping over
    /// short reads. Returns the number of bytes read, which is less than requested only at the end.
    /// </summary>
    public static async ValueTask<int> ReadAtLeastAsync(
        this IByteSource source,
        long position,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        int total = 0;
        while (total < destination.Length)
        {
            int read = await source.ReadAsync(position + total, destination[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
        }

        return total;
    }
}
