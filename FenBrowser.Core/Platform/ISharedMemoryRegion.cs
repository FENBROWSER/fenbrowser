using System;

namespace FenBrowser.Core.Platform;

/// <summary>
/// Represents a named, OS-backed memory-mapped region that can be shared between
/// two or more processes on the same machine.
/// </summary>
/// <remarks>
/// The process that creates the region (i.e. <see cref="IsOwner"/> is <c>true</c>) is
/// responsible for the lifetime of the underlying OS primitive.  All participants
/// must call <see cref="IDisposable.Dispose"/> when finished to release OS handles and
/// unmap the view.
/// </remarks>
public interface ISharedMemoryRegion : IDisposable
{
    /// <summary>Gets the OS-level name used to identify this region across processes.</summary>
    string Name { get; }

    /// <summary>Gets the total size of the mapped region in bytes.</summary>
    int SizeBytes { get; }

    /// <summary>
    /// Gets a value indicating whether this instance created the underlying OS region.
    /// The creating process owns the lifecycle; opening processes are secondary participants.
    /// </summary>
    bool IsOwner { get; }

    /// <summary>
    /// Returns an unsafe pointer to the first byte of the mapped view.
    /// </summary>
    /// <remarks>
    /// The pointer is valid until <see cref="IDisposable.Dispose"/> is called.
    /// The caller must never access memory beyond <see cref="SizeBytes"/> bytes from
    /// the returned address.
    /// </remarks>
    unsafe byte* GetPointer();

    /// <summary>
    /// Copies <paramref name="count"/> bytes from the mapped region starting at
    /// <paramref name="regionOffset"/> into <paramref name="dest"/> at
    /// <paramref name="destOffset"/>.
    /// </summary>
    /// <param name="regionOffset">Zero-based byte offset within the mapped region to begin reading.</param>
    /// <param name="dest">Destination byte array.</param>
    /// <param name="destOffset">Zero-based offset within <paramref name="dest"/> to begin writing.</param>
    /// <param name="count">Number of bytes to copy.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="regionOffset"/> + <paramref name="count"/> exceeds <see cref="SizeBytes"/>.
    /// </exception>
    void Read(int regionOffset, byte[] dest, int destOffset, int count);

    /// <summary>
    /// Copies <paramref name="count"/> bytes from <paramref name="src"/> starting at
    /// <paramref name="srcOffset"/> into the mapped region starting at
    /// <paramref name="regionOffset"/>.
    /// </summary>
    /// <param name="regionOffset">Zero-based byte offset within the mapped region to begin writing.</param>
    /// <param name="src">Source byte array.</param>
    /// <param name="srcOffset">Zero-based offset within <paramref name="src"/> to begin reading.</param>
    /// <param name="count">Number of bytes to copy.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="regionOffset"/> + <paramref name="count"/> exceeds <see cref="SizeBytes"/>.
    /// </exception>
    void Write(int regionOffset, byte[] src, int srcOffset, int count);

    /// <summary>
    /// Copies bytes from <paramref name="src"/> into the mapped region starting at
    /// <paramref name="regionOffset"/>.
    /// </summary>
    /// <param name="regionOffset">Zero-based byte offset within the mapped region to begin writing.</param>
    /// <param name="src">Source span of bytes to copy.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="regionOffset"/> + the length of <paramref name="src"/>
    /// exceeds <see cref="SizeBytes"/>.
    /// </exception>
    void Write(int regionOffset, ReadOnlySpan<byte> src);
}
