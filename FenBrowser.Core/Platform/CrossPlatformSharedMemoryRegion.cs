using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;

namespace FenBrowser.Core.Platform;

/// <summary>
/// Cross-platform <see cref="ISharedMemoryRegion"/> backed by a deterministic
/// temp-directory file and mapped through <see cref="MemoryMappedFile"/>.
/// </summary>
public sealed unsafe class CrossPlatformSharedMemoryRegion : ISharedMemoryRegion
{
    private readonly FileStream _stream;
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private byte* _pointer;
    private bool _disposed;

    public CrossPlatformSharedMemoryRegion(string name, int sizeBytes)
        : this(name, sizeBytes, isOwner: true)
    {
    }

    private CrossPlatformSharedMemoryRegion(string name, int sizeBytes, bool isOwner)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name must not be null or whitespace.", nameof(name));
        if (sizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), "Size must be positive.");

        Name = name;
        SizeBytes = sizeBytes;
        IsOwner = isOwner;

        string path = BuildPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (isOwner)
        {
            _stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.ReadWrite);
            _stream.SetLength(sizeBytes);
            _stream.Flush(flushToDisk: true);
        }
        else
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Shared memory region '{name}' was not found.", path);

            _stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite);

            if (_stream.Length < sizeBytes)
            {
                throw new IOException(
                    $"Shared memory region '{name}' is smaller than expected. Expected >= {sizeBytes} bytes, actual {_stream.Length} bytes.");
            }
        }

        _mmf = MemoryMappedFile.CreateFromFile(
            _stream,
            mapName: null,
            capacity: sizeBytes,
            MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.Inheritable,
            leaveOpen: true);

        _accessor = _mmf.CreateViewAccessor(0, sizeBytes, MemoryMappedFileAccess.ReadWrite);
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
    }

    public static CrossPlatformSharedMemoryRegion Open(string name, int sizeBytes)
        => new CrossPlatformSharedMemoryRegion(name, sizeBytes, isOwner: false);

    public string Name { get; }

    public int SizeBytes { get; }

    public bool IsOwner { get; }

    public byte* GetPointer()
    {
        ThrowIfDisposed();
        return _pointer;
    }

    public void Read(int regionOffset, byte[] dest, int destOffset, int count)
    {
        ThrowIfDisposed();
        ValidateBounds(regionOffset, count);

        if (dest == null) throw new ArgumentNullException(nameof(dest));
        if (destOffset < 0 || destOffset + count > dest.Length)
            throw new ArgumentOutOfRangeException(nameof(destOffset));

        _accessor.ReadArray(regionOffset, dest, destOffset, count);
    }

    public void Write(int regionOffset, byte[] src, int srcOffset, int count)
    {
        ThrowIfDisposed();
        ValidateBounds(regionOffset, count);

        if (src == null) throw new ArgumentNullException(nameof(src));
        if (srcOffset < 0 || srcOffset + count > src.Length)
            throw new ArgumentOutOfRangeException(nameof(srcOffset));

        _accessor.WriteArray(regionOffset, src, srcOffset, count);
    }

    public void Write(int regionOffset, ReadOnlySpan<byte> src)
    {
        ThrowIfDisposed();
        ValidateBounds(regionOffset, src.Length);

        byte* dest = _pointer + regionOffset;
        src.CopyTo(new Span<byte>(dest, src.Length));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_pointer != null)
        {
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _pointer = null;
        }

        _accessor.Dispose();
        _mmf.Dispose();
        _stream.Dispose();
    }

    private static string BuildPath(string name)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        string fileName = $"fenbrowser-shm-{Convert.ToHexString(hash)}.bin";
        return Path.Combine(Path.GetTempPath(), "FenBrowser", "SharedMemory", fileName);
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
            throw new ObjectDisposedException(nameof(CrossPlatformSharedMemoryRegion));
    }
}
