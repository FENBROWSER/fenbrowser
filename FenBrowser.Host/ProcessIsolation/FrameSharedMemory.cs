using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.Host.ProcessIsolation
{
    /// <summary>
    /// Manages a cross-process shared memory region for transferring rendered frame pixels.
    /// Each tab's region is sized to its actual window dimensions (capped at 4K UHD),
    /// so a 1280×720 tab uses ~3.5MB instead of the former fixed 33MB.
    /// The renderer child creates it; the host opens it by mapping the entire file (size=0).
    /// </summary>
    public sealed class FrameSharedMemory : IDisposable
    {
        // Absolute upper bound — no single tab can exceed 4K UHD.
        public const int MaxWidth = 3840;
        public const int MaxHeight = 2160;
        public const int BytesPerPixel = 4; // BGRA32

        // Header layout at start of MMF (32 bytes):
        // [0..3]   int  FrameWidth        — width of the most-recently-written frame
        // [4..7]   int  FrameHeight       — height of the most-recently-written frame
        // [8..11]  uint SequenceNumber    — incremented on every WriteFrame
        // [12..15] int  RegionCapacity    — pixel-byte capacity of this MMF (excluding header)
        // [16..31] padding
        // Pixel data starts at offset 32.
        private const int HeaderSize = 32;
        private const int OffsetWidth    = 0;
        private const int OffsetHeight   = 4;
        private const int OffsetSeq      = 8;
        private const int OffsetCapacity = 12;

        private readonly string _mmfName;
        private readonly string _readyEventName;
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _accessor;
        private EventWaitHandle _readyEvent;
        private readonly bool _isWriter;
        private readonly int _regionCapacity; // pixel bytes, excluding header
        private bool _disposed;

        private FrameSharedMemory(string mmfName, string readyEventName, bool isWriter,
            MemoryMappedFile mmf, MemoryMappedViewAccessor accessor, EventWaitHandle readyEvent,
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

        /// <summary>Compute the pixel-byte capacity for a given window size (capped at 4K).</summary>
        public static int ComputeRegionCapacity(int windowWidth, int windowHeight)
        {
            int w = Math.Max(1, Math.Min(windowWidth,  MaxWidth));
            int h = Math.Max(1, Math.Min(windowHeight, MaxHeight));
            return w * h * BytesPerPixel;
        }

        public static string MakeMmfName(int tabId, int parentPid) =>
            $"fen_frame_{tabId}_{parentPid}";

        public static string MakeEventName(int tabId, int parentPid) =>
            $"fen_frame_rdy_{tabId}_{parentPid}";

        /// <summary>
        /// Writer constructor (renderer child): creates the MMF and ready event.
        /// The MMF is sized to the actual window dimensions (capped at 4K) rather than
        /// a fixed 33MB ceiling, so memory usage scales with the real viewport.
        /// Falls back from Global\ to session-local naming if access is denied.
        /// </summary>
        public static FrameSharedMemory CreateForWriter(int tabId, int parentPid,
            int windowWidth = MaxWidth, int windowHeight = MaxHeight)
        {
            int regionCapacity = ComputeRegionCapacity(windowWidth, windowHeight);
            long totalSize = HeaderSize + regionCapacity;

            var baseMmfName = MakeMmfName(tabId, parentPid);
            var baseEventName = MakeEventName(tabId, parentPid);

            // Try Global\ first, fall back to session-local on access denied.
            MemoryMappedFile mmf = null;
            string usedMmfName = null;
            foreach (var prefix in new[] { "Global\\", "" })
            {
                var candidate = prefix + baseMmfName;
                try
                {
                    mmf = MemoryMappedFile.CreateNew(candidate, totalSize, MemoryMappedFileAccess.ReadWrite);
                    usedMmfName = candidate;
                    break;
                }
                catch (UnauthorizedAccessException)
                {
                    EngineLogBridge.Warn($"[FrameSharedMemory] CreateNew denied for '{candidate}'; falling back.", LogCategory.General);
                }
                catch (Exception ex)
                {
                    EngineLogBridge.Warn($"[FrameSharedMemory] CreateNew failed for '{candidate}': {ex.Message}", LogCategory.General);
                }
            }

            if (mmf == null)
            {
                EngineLogBridge.Warn("[FrameSharedMemory] Could not create shared memory region.", LogCategory.General);
                return null;
            }

            EventWaitHandle readyEvent = null;
            foreach (var prefix in new[] { "Global\\", "" })
            {
                var candidate = prefix + baseEventName;
                try
                {
                    readyEvent = new EventWaitHandle(false, EventResetMode.AutoReset, candidate);
                    break;
                }
                catch (UnauthorizedAccessException)
                {
                    EngineLogBridge.Warn($"[FrameSharedMemory] EventWaitHandle denied for '{candidate}'; falling back.", LogCategory.General);
                }
                catch (Exception ex)
                {
                    EngineLogBridge.Warn($"[FrameSharedMemory] EventWaitHandle failed for '{candidate}': {ex.Message}", LogCategory.General);
                }
            }

            // Map the full region and record the capacity in the header so the reader
            // can validate frames without needing the size out-of-band.
            var accessor = mmf.CreateViewAccessor(0, totalSize, MemoryMappedFileAccess.ReadWrite);
            accessor.Write(OffsetCapacity, regionCapacity);

            EngineLogBridge.Info($"[FrameSharedMemory] Writer created: mmf='{usedMmfName}', " +
                $"window={windowWidth}×{windowHeight}, regionBytes={regionCapacity} ({regionCapacity / 1024 / 1024} MB).", LogCategory.General);
            return new FrameSharedMemory(usedMmfName, baseEventName, isWriter: true, mmf, accessor, readyEvent, regionCapacity);
        }

        /// <summary>
        /// Reader constructor (host): opens the existing MMF and ready event.
        /// Mirrors the fallback logic of CreateForWriter.
        /// </summary>
        public static FrameSharedMemory OpenForReader(int tabId, int parentPid)
        {
            var baseMmfName = MakeMmfName(tabId, parentPid);
            var baseEventName = MakeEventName(tabId, parentPid);

            MemoryMappedFile mmf = null;
            string usedMmfName = null;
            foreach (var prefix in new[] { "Global\\", "" })
            {
                var candidate = prefix + baseMmfName;
                try
                {
                    mmf = MemoryMappedFile.OpenExisting(candidate, MemoryMappedFileRights.ReadWrite);
                    usedMmfName = candidate;
                    break;
                }
                catch (FileNotFoundException)
                {
                    // Not created yet or different prefix — try next.
                }
                catch (UnauthorizedAccessException)
                {
                    EngineLogBridge.Warn($"[FrameSharedMemory] OpenExisting denied for '{candidate}'; falling back.", LogCategory.General);
                }
                catch (Exception ex)
                {
                    EngineLogBridge.Warn($"[FrameSharedMemory] OpenExisting failed for '{candidate}': {ex.Message}", LogCategory.General);
                }
            }

            if (mmf == null)
            {
                EngineLogBridge.Warn("[FrameSharedMemory] Could not open shared memory region.", LogCategory.General);
                return null;
            }

            EventWaitHandle readyEvent = null;
            foreach (var prefix in new[] { "Global\\", "" })
            {
                var candidate = prefix + baseEventName;
                try
                {
                    if (EventWaitHandle.TryOpenExisting(candidate, out readyEvent))
                    {
                        break;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    EngineLogBridge.Warn($"[FrameSharedMemory] EventWaitHandle open denied for '{candidate}'; falling back.", LogCategory.General);
                }
                catch (Exception ex)
                {
                    EngineLogBridge.Warn($"[FrameSharedMemory] EventWaitHandle open failed for '{candidate}': {ex.Message}", LogCategory.General);
                }
            }

            // size=0 maps the entire file — no need to know the region size upfront.
            var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

            // Read the capacity the writer stamped into the header.
            int regionCapacity = accessor.ReadInt32(OffsetCapacity);
            if (regionCapacity <= 0 || regionCapacity > MaxWidth * MaxHeight * BytesPerPixel)
            {
                EngineLogBridge.Warn($"[FrameSharedMemory] Invalid region capacity {regionCapacity} in header; clamping to max.", LogCategory.General);
                regionCapacity = MaxWidth * MaxHeight * BytesPerPixel;
            }

            EngineLogBridge.Info($"[FrameSharedMemory] Reader opened: mmf='{usedMmfName}', regionBytes={regionCapacity} ({regionCapacity / 1024 / 1024} MB).", LogCategory.General);
            return new FrameSharedMemory(usedMmfName, baseEventName, isWriter: false, mmf, accessor, readyEvent, regionCapacity);
        }

        /// <summary>
        /// Writer: write BGRA pixels from a raw byte array into shared memory.
        /// Writes the header fields first, then the pixel data after HeaderSize offset.
        /// </summary>
        public void WriteFrame(int width, int height, ReadOnlySpan<byte> bgraPixels)
        {
            if (_accessor == null || _disposed) return;

            int pixelBytes = width * height * BytesPerPixel;
            if (pixelBytes > _regionCapacity)
            {
                EngineLogBridge.Warn($"[FrameSharedMemory] Frame too large: {width}x{height} ({pixelBytes} bytes > capacity {_regionCapacity}).", LogCategory.Rendering);
                return;
            }

            // Read current sequence and increment
            uint seq = _accessor.ReadUInt32(OffsetSeq);
            seq++;

            // Write header
            _accessor.Write(OffsetWidth, width);
            _accessor.Write(OffsetHeight, height);
            _accessor.Write(OffsetSeq, seq);

            // Write pixel data after header
            unsafe
            {
                byte* ptr = null;
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                try
                {
                    var dest = new Span<byte>(ptr + HeaderSize, pixelBytes);
                    bgraPixels.Slice(0, pixelBytes).CopyTo(dest);
                }
                finally
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }

        /// <summary>
        /// Reader: copy pixels from shared memory.
        /// Returns (width, height, sequenceNumber, pixelBytes) or null if not initialized.
        /// </summary>
        public (int width, int height, uint seq, byte[] pixels)? TryReadFrame()
        {
            if (_accessor == null || _disposed) return null;

            int width = _accessor.ReadInt32(OffsetWidth);
            int height = _accessor.ReadInt32(OffsetHeight);
            uint seq = _accessor.ReadUInt32(OffsetSeq);

            if (width <= 0 || height <= 0) return null;

            int pixelBytes = width * height * BytesPerPixel;
            if (pixelBytes > _regionCapacity)
            {
                EngineLogBridge.Warn($"[FrameSharedMemory] Read: reported frame dimensions too large: {width}x{height} ({pixelBytes} bytes > capacity {_regionCapacity}).", LogCategory.Rendering);
                return null;
            }

            var pixels = new byte[pixelBytes];
            unsafe
            {
                byte* ptr = null;
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                try
                {
                    var src = new ReadOnlySpan<byte>(ptr + HeaderSize, pixelBytes);
                    src.CopyTo(pixels);
                }
                finally
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }

            return (width, height, seq, pixels);
        }

        /// <summary>
        /// Signal the host that a frame is ready (called by writer after WriteFrame).
        /// </summary>
        public void SignalReady() => _readyEvent?.Set();

        /// <summary>
        /// Wait for a frame to be ready (called by host, returns false on timeout).
        /// </summary>
        public bool WaitForReady(TimeSpan timeout) => _readyEvent?.WaitOne(timeout) ?? false;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _accessor?.Dispose(); } catch { }
            try { _mmf?.Dispose(); } catch { }
            try { _readyEvent?.Dispose(); } catch { }

            _accessor = null;
            _mmf = null;
            _readyEvent = null;
        }
    }
}

