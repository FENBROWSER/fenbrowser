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
    private readonly string _path;
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

        _path = BuildPath(name);
        IpcPaths.EnsureSharedMemoryDirectory();

        if (isOwner)
        {
            _stream = new FileStream(
                _path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
            _stream.SetLength(sizeBytes);
            _stream.Flush(flushToDisk: true);
            RestrictBackingFilePermissions(_path);
        }
        else
        {
            if (!File.Exists(_path))
                throw new FileNotFoundException($"Shared memory region '{name}' was not found.", _path);

            _stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);

            if (_stream.Length < sizeBytes)
            {
                _stream.Dispose();
                throw new IOException(
                    $"Shared memory region '{name}' is smaller than expected. Expected >= {sizeBytes} bytes, actual {_stream.Length} bytes.");
            }
        }

        try
        {
            _mmf = MemoryMappedFile.CreateFromFile(
                _stream,
                mapName: null,
                capacity: sizeBytes,
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None,
                leaveOpen: true);

            _accessor = _mmf.CreateViewAccessor(0, sizeBytes, MemoryMappedFileAccess.ReadWrite);
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
        }
        catch
        {
            _stream.Dispose();
            if (isOwner)
            {
                TryDeleteBackingFile(_path);
            }
            throw;
        }
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
        if (destOffset < 0 || (long)destOffset + count > dest.Length)
            throw new ArgumentOutOfRangeException(nameof(destOffset));

        _accessor.ReadArray(regionOffset, dest, destOffset, count);
    }

    public void Write(int regionOffset, byte[] src, int srcOffset, int count)
    {
        ThrowIfDisposed();
        ValidateBounds(regionOffset, count);

        if (src == null) throw new ArgumentNullException(nameof(src));
        if (srcOffset < 0 || (long)srcOffset + count > src.Length)
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

        // The file is only a rendezvous mechanism for cross-process mappings. Once
        // the owner is gone it must not remain as a stale, reopenable shared-memory
        // capability. FileShare.Delete lets existing peer mappings survive until they
        // close while removing the pathname for new opens.
        if (IsOwner)
        {
            TryDeleteBackingFile(_path);
        }
    }

    private static string BuildPath(string name)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        string fileName = $"fenbrowser-shm-{Convert.ToHexString(hash)}.bin";
        return Path.Combine(IpcPaths.SharedMemoryDirectory, fileName);
    }

    private static void RestrictBackingFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (PlatformNotSupportedException)
        {
        }
        catch (UnauthorizedAccessException)
        {
            // The file was created by this process; inability to tighten permissions
            // should not corrupt the mapping. OS sandboxing remains the outer boundary.
        }
        catch (IOException)
        {
        }
    }

    private static void TryDeleteBackingFile(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void ValidateBounds(int regionOffset, int count)
    {
        if (regionOffset < 0 || count < 0 || (long)regionOffset + count > SizeBytes)
        {
            var end = (long)regionOffset + count;
            throw new ArgumentOutOfRangeException(
                nameof(regionOffset),
                $"Access [{regionOffset}..{end}) is outside the region bounds [0..{SizeBytes}).");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(CrossPlatformSharedMemoryRegion));
    }
}
