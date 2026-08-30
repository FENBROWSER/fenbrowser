using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.Host.ProcessIsolation
{
    /// <summary>
    /// Manages a cross-process shared memory region for transferring rendered frame pixels.
    /// Each tab's region is sized to its actual window dimensions (capped at 4K UHD).
    /// </summary>
    public sealed class FrameSharedMemory : IDisposable, IRendererFrameSource
    {
        public const int MaxWidth = 3840;
        public const int MaxHeight = 2160;
        public const int BytesPerPixel = 4; // BGRA32

        // Header layout at start of MMF (32 bytes):
        // [0..3]   int  FrameWidth
        // [4..7]   int  FrameHeight
        // [8..11]  uint SequenceNumber — odd while writer is publishing, even when stable
        // [12..15] int  RegionCapacity — pixel-byte capacity excluding header
        // [16..31] padding
        private const int HeaderSize = 32;
        private const int OffsetWidth = 0;
        private const int OffsetHeight = 4;
        private const int OffsetSeq = 8;
        private const int OffsetCapacity = 12;
        private const int MaxRegionCapacity = MaxWidth * MaxHeight * BytesPerPixel;

        private readonly string _mmfName;
        private readonly string _readyEventName;
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _accessor;
        private EventWaitHandle _readyEvent;
        private readonly bool _isWriter;
        private readonly int _regionCapacity;
        private bool _disposed;

        private FrameSharedMemory(
            string mmfName,
            string readyEventName,
            bool isWriter,
            MemoryMappedFile mmf,
            MemoryMappedViewAccessor accessor,
            EventWaitHandle readyEvent,
            int regionCapacity)
        {
            _mmfName = mmfName;
            _readyEventName = readyEventName;
            _isWriter = isWriter;
            _mmf = mmf;
            _accessor = accessor;
            _readyEvent = readyEvent;
            _regionCapacity = regionCapacity;
        }

        public static int ComputeRegionCapacity(int windowWidth, int windowHeight)
        {
            int w = Math.Clamp(windowWidth, 1, MaxWidth);
            int h = Math.Clamp(windowHeight, 1, MaxHeight);
            long bytes = (long)w * h * BytesPerPixel;
            return checked((int)bytes);
        }

        public static string MakeMmfName(int tabId, int parentPid) =>
            $"fen_frame_{tabId}_{parentPid}";

        public static string MakeEventName(int tabId, int parentPid) =>
            $"fen_frame_rdy_{tabId}_{parentPid}";

        public static string MakeMmfName(int tabId, int parentPid, string capabilityToken) =>
            $"fen_frame_{tabId}_{parentPid}_{MakeCapabilitySuffix(capabilityToken)}";

        public static string MakeEventName(int tabId, int parentPid, string capabilityToken) =>
            $"fen_frame_rdy_{tabId}_{parentPid}_{MakeCapabilitySuffix(capabilityToken)}";

        private static string MakeCapabilitySuffix(string capabilityToken)
        {
            if (string.IsNullOrWhiteSpace(capabilityToken))
                throw new ArgumentException("Frame shared-memory capability token is required.", nameof(capabilityToken));

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(capabilityToken));
            return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
        }

        public static FrameSharedMemory CreateForWriter(
            int tabId,
            int parentPid,
            int windowWidth = MaxWidth,
            int windowHeight = MaxHeight,
            string capabilityToken = null)
        {
            int regionCapacity = ComputeRegionCapacity(windowWidth, windowHeight);
            long totalSize = HeaderSize + (long)regionCapacity;

            // Renderer and broker are launched into the same interactive session. Using
            // Global\ unnecessarily exposes the object namespace across Windows sessions
            // and can also require SeCreateGlobalPrivilege. Keep frame transport local to
            // the browser session; a future protocol revision should additionally derive
            // these names from the authenticated renderer capability token.
            var mmfName = string.IsNullOrWhiteSpace(capabilityToken)
                ? MakeMmfName(tabId, parentPid)
                : MakeMmfName(tabId, parentPid, capabilityToken);
            var eventName = string.IsNullOrWhiteSpace(capabilityToken)
                ? MakeEventName(tabId, parentPid)
                : MakeEventName(tabId, parentPid, capabilityToken);

            if (string.IsNullOrWhiteSpace(capabilityToken))
            {
                EngineLogBridge.Warn(
                    "[FrameSharedMemory] Creating a compatibility mapping without a capability-bound name.",
                    LogCategory.General);
            }

            MemoryMappedFile mmf;
            try
            {
                mmf = MemoryMappedFile.CreateNew(mmfName, totalSize, MemoryMappedFileAccess.ReadWrite);
            }
            catch (Exception ex)
            {
                EngineLogBridge.Warn($"[FrameSharedMemory] CreateNew failed for session-local mapping: {ex.Message}", LogCategory.General);
                return null;
            }

            EventWaitHandle readyEvent;
            try
            {
                readyEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            }
            catch (Exception ex)
            {
                EngineLogBridge.Warn($"[FrameSharedMemory] Could not create session-local frame-ready event: {ex.Message}", LogCategory.General);
                TryDispose(mmf, "writer-memory-mapped-file");
                return null;
            }

            MemoryMappedViewAccessor accessor = null;
            try
            {
                accessor = mmf.CreateViewAccessor(0, totalSize, MemoryMappedFileAccess.ReadWrite);
                accessor.Write(OffsetWidth, 0);
                accessor.Write(OffsetHeight, 0);
                accessor.Write(OffsetSeq, 0u);
                accessor.Write(OffsetCapacity, regionCapacity);
            }
            catch
            {
                TryDispose(accessor, "writer-accessor");
                TryDispose(readyEvent, "writer-ready-event");
                TryDispose(mmf, "writer-memory-mapped-file");
                throw;
            }

            EngineLogBridge.Info(
                $"[FrameSharedMemory] Writer created for tab={tabId}, window={windowWidth}×{windowHeight}, " +
                $"regionBytes={regionCapacity} ({regionCapacity / 1024 / 1024} MB).",
                LogCategory.General);

            return new FrameSharedMemory(
                mmfName,
                eventName,
                isWriter: true,
                mmf,
                accessor,
                readyEvent,
                regionCapacity);
        }

        public static FrameSharedMemory OpenForReader(int tabId, int parentPid, string capabilityToken = null)
        {
            if (!OperatingSystem.IsWindows())
                return null;

            var mmfName = string.IsNullOrWhiteSpace(capabilityToken)
                ? MakeMmfName(tabId, parentPid)
                : MakeMmfName(tabId, parentPid, capabilityToken);
            var eventName = string.IsNullOrWhiteSpace(capabilityToken)
                ? MakeEventName(tabId, parentPid)
                : MakeEventName(tabId, parentPid, capabilityToken);

            MemoryMappedFile mmf;
            try
            {
                // The compositor only consumes published frame bytes. Do not give
                // the reader write access to renderer-owned shared memory.
                mmf = MemoryMappedFile.OpenExisting(mmfName, MemoryMappedFileRights.Read);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (Exception ex)
            {
                EngineLogBridge.Warn($"[FrameSharedMemory] Could not open session-local mapping: {ex.Message}", LogCategory.General);
                return null;
            }

            EventWaitHandle readyEvent;
            try
            {
                if (!EventWaitHandle.TryOpenExisting(eventName, out readyEvent))
                {
                    EngineLogBridge.Warn("[FrameSharedMemory] Could not open session-local frame-ready event; rejecting mapping.", LogCategory.General);
                    TryDispose(mmf, "reader-memory-mapped-file");
                    return null;
                }
            }
            catch (Exception ex)
            {
                EngineLogBridge.Warn($"[FrameSharedMemory] Could not open session-local frame-ready event: {ex.Message}", LogCategory.General);
                TryDispose(mmf, "reader-memory-mapped-file");
                return null;
            }

            MemoryMappedViewAccessor accessor = null;
            try
            {
                accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                if (accessor.Capacity < HeaderSize)
                    throw new InvalidDataException("Shared frame mapping is smaller than its header.");

                int regionCapacity = accessor.ReadInt32(OffsetCapacity);
                if (regionCapacity <= 0 || regionCapacity > MaxRegionCapacity)
                {
                    throw new InvalidDataException($"Invalid shared frame region capacity {regionCapacity}.");
                }

                long requiredMappingBytes = HeaderSize + (long)regionCapacity;
                if (accessor.Capacity < requiredMappingBytes)
                {
                    throw new InvalidDataException(
                        $"Shared frame mapping is truncated: capacity={accessor.Capacity}, required={requiredMappingBytes}.");
                }

                EngineLogBridge.Info(
                    $"[FrameSharedMemory] Reader opened for tab={tabId}, regionBytes={regionCapacity} " +
                    $"({regionCapacity / 1024 / 1024} MB).",
                    LogCategory.General);

                return new FrameSharedMemory(
                    mmfName,
                    eventName,
                    isWriter: false,
                    mmf,
                    accessor,
                    readyEvent,
                    regionCapacity);
            }
            catch (Exception ex)
            {
                EngineLogBridge.Warn($"[FrameSharedMemory] Rejected shared memory mapping: {ex.Message}", LogCategory.General);
                TryDispose(accessor, "reader-accessor");
                TryDispose(readyEvent, "reader-ready-event");
                TryDispose(mmf, "reader-memory-mapped-file");
                return null;
            }
        }

        public void WriteFrame(int width, int height, ReadOnlySpan<byte> bgraPixels)
        {
            if (!_isWriter)
                throw new InvalidOperationException("A shared-frame reader cannot publish frames.");
            if (_accessor == null || _disposed)
                return;

            if (!TryComputePixelBytes(width, height, out var pixelBytes) || pixelBytes > _regionCapacity)
            {
                EngineLogBridge.Warn(
                    $"[FrameSharedMemory] Rejected invalid/oversized frame dimensions: {width}x{height}.",
                    LogCategory.Rendering);
                return;
            }

            if (bgraPixels.Length < pixelBytes)
            {
                EngineLogBridge.Warn(
                    $"[FrameSharedMemory] Rejected short frame buffer: have={bgraPixels.Length}, need={pixelBytes}.",
                    LogCategory.Rendering);
                return;
            }

            uint previous = _accessor.ReadUInt32(OffsetSeq);
            uint writingSequence = (previous & 1u) == 0 ? previous + 1u : previous + 2u;
            uint publishedSequence = writingSequence + 1u;
            _accessor.Write(OffsetSeq, writingSequence);
            Thread.MemoryBarrier();

            unsafe
            {
                byte* ptr = null;
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                try
                {
                    var dest = new Span<byte>(ptr + HeaderSize, pixelBytes);
                    bgraPixels[..pixelBytes].CopyTo(dest);
                }
                finally
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }

            _accessor.Write(OffsetWidth, width);
            _accessor.Write(OffsetHeight, height);
            Thread.MemoryBarrier();
            _accessor.Write(OffsetSeq, publishedSequence);
        }

        public unsafe void WriteFrame(int width, int height, IntPtr bgraPixels, int bufferLength)
        {
            if (!_isWriter)
                throw new InvalidOperationException("A shared-frame reader cannot publish frames.");
            if (_accessor == null || _disposed)
                return;

            if (!TryComputePixelBytes(width, height, out var pixelBytes) || pixelBytes > _regionCapacity)
            {
                EngineLogBridge.Warn(
                    $"[FrameSharedMemory] Rejected invalid/oversized frame dimensions: {width}x{height}.",
                    LogCategory.Rendering);
                return;
            }

            if (bgraPixels == IntPtr.Zero || bufferLength < pixelBytes)
            {
                EngineLogBridge.Warn(
                    $"[FrameSharedMemory] Rejected invalid native frame buffer: have={bufferLength}, need={pixelBytes}.",
                    LogCategory.Rendering);
                return;
            }

            uint previous = _accessor.ReadUInt32(OffsetSeq);
            uint writingSequence = (previous & 1u) == 0 ? previous + 1u : previous + 2u;
            uint publishedSequence = writingSequence + 1u;
            _accessor.Write(OffsetSeq, writingSequence);
            Thread.MemoryBarrier();

            byte* destination = null;
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref destination);
            try
            {
                Buffer.MemoryCopy(
                    bgraPixels.ToPointer(),
                    destination + HeaderSize,
                    pixelBytes,
                    pixelBytes);
            }
            finally
            {
                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }

            _accessor.Write(OffsetWidth, width);
            _accessor.Write(OffsetHeight, height);
            Thread.MemoryBarrier();
            _accessor.Write(OffsetSeq, publishedSequence);
        }

        public unsafe bool TryCopyFrame(
            IntPtr destination,
            int destinationCapacity,
            out int width,
            out int height,
            out uint sequence)
        {
            width = 0;
            height = 0;
            sequence = 0;

            if (_isWriter)
                throw new InvalidOperationException("A shared-frame writer cannot consume compositor frames.");
            if (_accessor == null || _disposed || destination == IntPtr.Zero || destinationCapacity <= 0)
                return false;

            uint sequenceBefore = _accessor.ReadUInt32(OffsetSeq);
            if ((sequenceBefore & 1u) != 0)
                return false;

            width = _accessor.ReadInt32(OffsetWidth);
            height = _accessor.ReadInt32(OffsetHeight);
            if (!TryComputePixelBytes(width, height, out var pixelBytes) || pixelBytes > _regionCapacity)
            {
                if (width != 0 || height != 0)
                {
                    EngineLogBridge.Warn(
                        $"[FrameSharedMemory] Read rejected invalid/oversized dimensions: {width}x{height}.",
                        LogCategory.Rendering);
                }
                return false;
            }

            if (pixelBytes > destinationCapacity)
            {
                return false;
            }

            byte* ptr = null;
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            try
            {
                Buffer.MemoryCopy(ptr + HeaderSize, destination.ToPointer(), destinationCapacity, pixelBytes);
            }
            finally
            {
                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }

            Thread.MemoryBarrier();
            uint sequenceAfter = _accessor.ReadUInt32(OffsetSeq);
            if (sequenceBefore != sequenceAfter || (sequenceAfter & 1u) != 0)
                return false;

            sequence = sequenceAfter;
            return true;
        }

        private static bool TryComputePixelBytes(int width, int height, out int pixelBytes)
        {
            pixelBytes = 0;
            if (width <= 0 || height <= 0 || width > MaxWidth || height > MaxHeight)
                return false;

            long bytes = (long)width * height * BytesPerPixel;
            if (bytes <= 0 || bytes > MaxRegionCapacity || bytes > int.MaxValue)
                return false;

            pixelBytes = (int)bytes;
            return true;
        }

        public void SignalReady()
        {
            if (!_isWriter)
                throw new InvalidOperationException("A shared-frame reader cannot signal frame publication.");
            _readyEvent?.Set();
        }

        public bool WaitForReady(TimeSpan timeout)
        {
            if (_isWriter)
                throw new InvalidOperationException("A shared-frame writer cannot wait as a compositor reader.");
            return _readyEvent?.WaitOne(timeout) ?? false;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            TryDispose(_accessor, "accessor");
            TryDispose(_mmf, "memory-mapped-file");
            TryDispose(_readyEvent, "ready-event");

            _accessor = null;
            _mmf = null;
            _readyEvent = null;
        }

        private static void TryDispose(IDisposable disposable, string resourceName)
        {
            if (disposable == null)
                return;

            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                EngineLog.Write(
                    LogSubsystem.ProcessIsolation,
                    LogSeverity.Debug,
                    $"[FrameSharedMemory] Dispose failed for {resourceName}: {ex.Message}");
            }
        }
    }
}
