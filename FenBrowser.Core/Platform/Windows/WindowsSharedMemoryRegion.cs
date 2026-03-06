using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FenBrowser.Core.Platform.Windows;

/// <summary>
/// <see cref="ISharedMemoryRegion"/> implementation backed by a
/// <see cref="MemoryMappedFile"/> on Windows.
/// </summary>
[SupportedOSPlatform("windows")]
/// <remarks>
/// The creating process calls <see cref="MemoryMappedFile.CreateNew"/> with
/// <c>Global\</c>-prefixed names so that the region is visible across session
/// boundaries.  Opening processes call <see cref="MemoryMappedFile.OpenExisting"/>.
/// Both parties must hold the <see cref="WindowsSharedMemoryRegion"/> alive for the
/// duration of the IPC session, and must dispose it when done.
/// </remarks>
public sealed unsafe class WindowsSharedMemoryRegion : ISharedMemoryRegion
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private byte* _pointer;
    private bool _disposed;

    /// <summary>
    /// Creates a new named shared memory region (owner side).
    /// </summary>
    /// <param name="name">Plain identifier; <c>Global\</c> will be prepended automatically.</param>
    /// <param name="sizeBytes">Size of the region in bytes.  Must be positive.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="sizeBytes"/> is less than or equal to zero.
    /// </exception>
    /// <exception cref="System.IO.IOException">
    /// Thrown when the OS is unable to create the mapping (e.g. name already in use).
    /// </exception>
    public WindowsSharedMemoryRegion(string name, int sizeBytes)
        : this(name, sizeBytes, isOwner: true)
    {
    }

    /// <summary>
    /// Internal constructor used by <see cref="Open"/> to wrap an existing region.
    /// </summary>
    private WindowsSharedMemoryRegion(string name, int sizeBytes, bool isOwner)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name must not be null or whitespace.", nameof(name));
        if (sizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), "Size must be positive.");

        Name = name;
        SizeBytes = sizeBytes;
        IsOwner = isOwner;

        string fullName = BuildFullName(name);

        if (isOwner)
        {
            _mmf = MemoryMappedFile.CreateNew(
                fullName,
                sizeBytes,
                MemoryMappedFileAccess.ReadWrite);
        }
        else
        {
            _mmf = MemoryMappedFile.OpenExisting(
                fullName,
                MemoryMappedFileRights.ReadWrite);
        }

        _accessor = _mmf.CreateViewAccessor(0, sizeBytes, MemoryMappedFileAccess.ReadWrite);
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
    }

    /// <summary>
    /// Opens an existing named shared memory region created by another process.
    /// </summary>
    /// <param name="name">The same plain name used at creation time.</param>
    /// <param name="sizeBytes">Expected size of the region in bytes.</param>
    /// <returns>A new <see cref="WindowsSharedMemoryRegion"/> with <see cref="IsOwner"/> = <c>false</c>.</returns>
    public static WindowsSharedMemoryRegion Open(string name, int sizeBytes)
        => new WindowsSharedMemoryRegion(name, sizeBytes, isOwner: false);

    // -------------------------------------------------------------------------
    // ISharedMemoryRegion
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public int SizeBytes { get; }

    /// <inheritdoc/>
    public bool IsOwner { get; }

    /// <inheritdoc/>
    public byte* GetPointer()
    {
        ThrowIfDisposed();
        return _pointer;
    }

    /// <inheritdoc/>
    public void Read(int regionOffset, byte[] dest, int destOffset, int count)
    {
        ThrowIfDisposed();
        ValidateBounds(regionOffset, count);

        if (dest == null) throw new ArgumentNullException(nameof(dest));
        if (destOffset < 0 || destOffset + count > dest.Length)
            throw new ArgumentOutOfRangeException(nameof(destOffset));

        _accessor.ReadArray(regionOffset, dest, destOffset, count);
    }

    /// <inheritdoc/>
    public void Write(int regionOffset, byte[] src, int srcOffset, int count)
    {
        ThrowIfDisposed();
        ValidateBounds(regionOffset, count);

        if (src == null) throw new ArgumentNullException(nameof(src));
        if (srcOffset < 0 || srcOffset + count > src.Length)
            throw new ArgumentOutOfRangeException(nameof(srcOffset));

        _accessor.WriteArray(regionOffset, src, srcOffset, count);
    }

    /// <inheritdoc/>
    public void Write(int regionOffset, ReadOnlySpan<byte> src)
    {
        ThrowIfDisposed();
        ValidateBounds(regionOffset, src.Length);

        // Copy from the span directly into the mapped view via unsafe pointer arithmetic.
        byte* dest = _pointer + regionOffset;
        src.CopyTo(new Span<byte>(dest, src.Length));
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Release the pinned pointer before closing the accessor.
        if (_pointer != null)
        {
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _pointer = null;
        }

        _accessor.Dispose();
        _mmf.Dispose();
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static string BuildFullName(string name)
    {
        // On Windows, use the Global\ namespace so the mapping is visible
        // across all sessions (required for broker <-> renderer communication).
        // The Global\ prefix requires SE_CREATE_GLOBAL_PRIVILEGE; if that
        // privilege is absent (e.g. some UWP sandboxes) fall back to Local\.
        if (name.StartsWith("Global\\", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Local\\", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }
        return "Global\\" + name;
    }

    private void ValidateBounds(int regionOffset, int count)
    {
        if (regionOffset < 0 || count < 0 || (long)regionOffset + count > SizeBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(regionOffset),
                $"Access [{regionOffset}..{regionOffset + count}) is outside the region bounds [0..{SizeBytes}).");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WindowsSharedMemoryRegion));
    }
}
