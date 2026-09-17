using System.Buffers;

namespace FenBrowser.Media.Buffers;

/// <summary>
/// Private pools for media buffers.
/// </summary>
/// <remarks>
/// <see cref="ArrayPool{T}.Shared"/> is also used by the network stack and the runtime,
/// which return arrays without clearing them, so a buffer rented from it can still hold
/// another page's bytes. Media buffers come only from these pools, and every return
/// clears the array, so any buffer handed to a decoder is either fresh or zeroed.
/// </remarks>
internal static class MediaBufferPool
{
    // 1 GiB is the largest array a configurable pool will keep. It covers an 8K BGRA frame.
    private const int MaxPooledLength = 1024 * 1024 * 1024;
    private const int MaxArraysPerBucket = 16;

    public static readonly ArrayPool<byte> Bytes = ArrayPool<byte>.Create(MaxPooledLength, MaxArraysPerBucket);
    public static readonly ArrayPool<float> Floats = ArrayPool<float>.Create(MaxPooledLength, MaxArraysPerBucket);

    public static void Return(byte[] buffer) => Bytes.Return(buffer, clearArray: true);

    public static void Return(float[] buffer) => Floats.Return(buffer, clearArray: true);
}
