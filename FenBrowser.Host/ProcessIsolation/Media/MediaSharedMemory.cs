using System;
using System.IO.MemoryMappedFiles;
using FenBrowser.Core.Platform;

namespace FenBrowser.Host.ProcessIsolation.Media
{
    /// <summary>
    /// One shared region between the renderer and the media process: the resource bytes
    /// going in, or the decoded block coming out. The renderer creates it and the media
    /// process opens it by name; neither ever sees the other's memory beyond the region.
    /// </summary>
    /// <remarks>
    /// On Windows this is a named mapping in the session's object namespace, as
    /// <see cref="FrameSharedMemory"/> does for frames: the renderer child has no writable
    /// temp directory, so a file-backed region is not an option there. Elsewhere it is a
    /// file under <see cref="IpcPaths.SharedMemoryDirectory"/>, which the sandbox binds
    /// read-write into both children.
    /// </remarks>
    internal sealed unsafe class MediaSharedMemory : IDisposable
    {
        private readonly MemoryMappedFile _mapping;
        private readonly MemoryMappedViewAccessor _accessor;
        private readonly CrossPlatformSharedMemoryRegion _fileRegion;
        private byte* _pointer;
        private bool _disposed;

        private MediaSharedMemory(string name, int sizeBytes, MemoryMappedFile mapping, MemoryMappedViewAccessor accessor)
        {
            Name = name;
            SizeBytes = sizeBytes;
            _mapping = mapping;
            _accessor = accessor;
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
        }

        private MediaSharedMemory(CrossPlatformSharedMemoryRegion region)
        {
            Name = region.Name;
            SizeBytes = region.SizeBytes;
            _fileRegion = region;
            _pointer = region.GetPointer();
        }

        public string Name { get; }

        public int SizeBytes { get; }

        public static MediaSharedMemory Create(string name, int sizeBytes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
            if (!OperatingSystem.IsWindows())
            {
                return new MediaSharedMemory(new CrossPlatformSharedMemoryRegion(name, sizeBytes));
            }

            var mapping = MemoryMappedFile.CreateNew(name, sizeBytes, MemoryMappedFileAccess.ReadWrite);
            return WithView(name, sizeBytes, mapping);
        }

        public static MediaSharedMemory Open(string name, int sizeBytes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
            if (!OperatingSystem.IsWindows())
            {
                return new MediaSharedMemory(CrossPlatformSharedMemoryRegion.Open(name, sizeBytes));
            }

            var mapping = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
            return WithView(name, sizeBytes, mapping);
        }

        private static MediaSharedMemory WithView(string name, int sizeBytes, MemoryMappedFile mapping)
        {
            try
            {
                var accessor = mapping.CreateViewAccessor(0, sizeBytes, MemoryMappedFileAccess.ReadWrite);
                return new MediaSharedMemory(name, sizeBytes, mapping, accessor);
            }
            catch
            {
                mapping.Dispose();
                throw;
            }
        }

        public void Write(int offset, ReadOnlySpan<byte> source)
        {
            Bounds(offset, source.Length);
            source.CopyTo(new Span<byte>(_pointer + offset, source.Length));
        }

        public void Read(int offset, Span<byte> destination)
        {
            Bounds(offset, destination.Length);
            new ReadOnlySpan<byte>(_pointer + offset, destination.Length).CopyTo(destination);
        }

        private void Bounds(int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (offset < 0 || count < 0 || (long)offset + count > SizeBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), $"[{offset}..{(long)offset + count}) is outside the region of {SizeBytes} bytes.");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_fileRegion != null)
            {
                _pointer = null;
                _fileRegion.Dispose();
                return;
            }

            if (_pointer != null)
            {
                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                _pointer = null;
            }

            _accessor.Dispose();
            _mapping.Dispose();
        }
    }
}
