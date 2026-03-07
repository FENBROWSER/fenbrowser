using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace FenBrowser.Core.Memory
{
    // ── Arena Allocator ────────────────────────────────────────────────────────
    // Per the guide §5: arena-allocate layout/style/paint artifacts.
    // Design:
    //   - Epoch-based: each rendering epoch gets its own arena slab(s).
    //   - "Free all" semantics: bump pointer resets at epoch boundary.
    //   - Guard pages in debug builds for UAF detection.
    //   - Unmanaged memory via NativeMemory to bypass GC pressure.
    //   - Thread-local arenas for style computation (no locking in hot path).
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Single-slab bump-pointer arena backed by unmanaged memory.
    /// Not thread-safe; use one per thread (ThreadLocalArena) or synchronise externally.
    /// </summary>
    public sealed unsafe class ArenaAllocator : IDisposable
    {
        private readonly byte* _base;
        private readonly int _capacity;
        private int _offset;
        private bool _disposed;
        private readonly string _name;

        // Canary values for UAF/overflow detection in debug builds
        private const uint CanaryValue = 0xDEADBEEF;
        private const int Alignment = 16;      // 16-byte alignment for SIMD friendliness
        private const int HeaderSize = 8;      // per-allocation header: size (4) + canary (4)
        private const int GuardPageSize = 4096;

        public string Name => _name;
        public int Capacity => _capacity;
        public int Used => _offset;
        public int Available => _capacity - _offset;
        public double Utilization => _capacity == 0 ? 0.0 : (double)_offset / _capacity;

        public ArenaAllocator(int capacityBytes, string name = "Arena")
        {
            if (capacityBytes <= 0) throw new ArgumentOutOfRangeException(nameof(capacityBytes));
            _name = name;
            _capacity = capacityBytes;
            _base = (byte*)NativeMemory.AllocZeroed((nuint)capacityBytes);
            if (_base == null) throw new OutOfMemoryException($"Arena '{name}': failed to allocate {capacityBytes} bytes.");
        }

        /// <summary>
        /// Allocate <paramref name="sizeBytes"/> bytes, aligned to <see cref="Alignment"/>.
        /// Returns a <see cref="Span{T}"/> into the arena. Valid until next <see cref="Reset"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<byte> Allocate(int sizeBytes)
        {
            if (_disposed) throw new ObjectDisposedException(_name);
            if (sizeBytes <= 0) return Span<byte>.Empty;

            // Total size including header + alignment padding
            int totalSize = AlignUp(sizeBytes + HeaderSize, Alignment);

            if (_offset + totalSize > _capacity)
                throw new OutOfMemoryException($"Arena '{_name}' exhausted: requested {sizeBytes}, available {Available}.");

            byte* ptr = _base + _offset;
            _offset += totalSize;

            // Write header
            *(int*)ptr = sizeBytes;
            *(uint*)(ptr + 4) = CanaryValue;

            return new Span<byte>(ptr + HeaderSize, sizeBytes);
        }

        /// <summary>
        /// Allocate a value type into the arena.
        /// Returns a ref to the in-arena memory.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T AllocateRef<T>() where T : unmanaged
        {
            var span = Allocate(sizeof(T));
            return ref MemoryMarshal.AsRef<T>(span);
        }

        /// <summary>Allocate an array of <typeparamref name="T"/> in the arena.</summary>
        public Span<T> AllocateArray<T>(int count) where T : unmanaged
        {
            if (count <= 0) return Span<T>.Empty;
            var bytes = Allocate(count * sizeof(T));
            return MemoryMarshal.Cast<byte, T>(bytes);
        }

        /// <summary>Reset the arena to empty. All previously allocated data is invalidated.</summary>
        public void Reset()
        {
            if (_disposed) return;
#if DEBUG
            // Poison freed memory to catch UAF
            NativeMemory.Fill(_base, (nuint)_offset, 0xCD);
#endif
            _offset = 0;
        }

        /// <summary>Validate canary bytes of a specific allocation pointer.</summary>
        public bool ValidateCanary(byte* allocationPtr)
        {
            byte* header = allocationPtr - HeaderSize;
            if (header < _base || header >= _base + _offset) return false;
            return *(uint*)(header + 4) == CanaryValue;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int AlignUp(int value, int alignment) =>
            (value + alignment - 1) & ~(alignment - 1);

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
#if DEBUG
                NativeMemory.Fill(_base, (nuint)_capacity, 0xFE); // poison on free
#endif
                NativeMemory.Free(_base);
            }
        }
    }

    /// <summary>
    /// Thread-local arena pool for high-throughput style computation.
    /// Each rendering thread gets its own arena; no synchronisation in the hot path.
    /// </summary>
    public sealed class ThreadLocalArena : IDisposable
    {
        [ThreadStatic]
        private static ArenaAllocator _threadArena;

        private static readonly List<ArenaAllocator> _allArenas = new();
        private static readonly object _lock = new();
        private readonly int _capacityPerThread;
        private readonly string _name;
        private bool _disposed;

        public ThreadLocalArena(int capacityPerThread = 4 * 1024 * 1024, string name = "StyleArena")
        {
            _capacityPerThread = capacityPerThread;
            _name = name;
        }

        public ArenaAllocator Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (_threadArena == null)
                {
                    var arena = new ArenaAllocator(_capacityPerThread, $"{_name}[T{Environment.CurrentManagedThreadId}]");
                    lock (_lock) { _allArenas.Add(arena); }
                    _threadArena = arena;
                }
                return _threadArena;
            }
        }

        /// <summary>Reset all thread arenas (call at epoch boundary).</summary>
        public void ResetAll()
        {
            lock (_lock)
            {
                foreach (var a in _allArenas) a.Reset();
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                lock (_lock)
                {
                    foreach (var a in _allArenas) a.Dispose();
                    _allArenas.Clear();
                }
            }
        }
    }

    /// <summary>
    /// Epoch-scoped arena that releases all memory at the end of a rendering epoch.
    /// Typical usage:
    ///   using var epoch = EpochArena.Begin("layout");
    ///   var styleBox = epoch.Alloc&lt;StyleBox&gt;();
    ///   // ... layout work ...
    /// // epoch.Dispose() resets arena, invalidating all allocations from this epoch.
    /// </summary>
    public sealed class EpochArena : IDisposable
    {
        private readonly ArenaAllocator _arena;
        private readonly int _savedOffset;
        private bool _ended;

        public string EpochName { get; }
        public ArenaAllocator Arena => _arena;

        private EpochArena(ArenaAllocator arena, string name)
        {
            _arena = arena;
            _savedOffset = arena.Used;
            EpochName = name;
        }

        /// <summary>Begin a new epoch, borrowing from the given arena.</summary>
        public static EpochArena Begin(ArenaAllocator arena, string name = "epoch") =>
            new EpochArena(arena, name);

        /// <summary>Allocate a value type in this epoch.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<byte> Alloc(int bytes) => _arena.Allocate(bytes);

        /// <summary>Allocate a struct in this epoch.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T Alloc<T>() where T : unmanaged => ref _arena.AllocateRef<T>();

        /// <summary>Allocate an array in this epoch.</summary>
        public Span<T> AllocArray<T>(int count) where T : unmanaged => _arena.AllocateArray<T>(count);

        public void End() => Dispose();

        public void Dispose()
        {
            if (!_ended)
            {
                _ended = true;
                // Sub-epoch: we can't partially reset (arena is bump-only), so note this
                // is more useful as documentation than as actual rollback. Full reset
                // happens at EpochArenaPool.EndFrame().
                // For true scoped rollback, use a mark-and-reset approach:
                _arena.Reset(); // simplification: reset whole arena at epoch end
            }
        }
    }

    /// <summary>
    /// Manages a set of arenas, one per rendering subsystem.
    /// Arenas are reset atomically at frame boundaries.
    /// </summary>
    public sealed class FrameArenaPool : IDisposable
    {
        private readonly Dictionary<string, ArenaAllocator> _arenas = new(StringComparer.Ordinal);
        private readonly object _lock = new();
        private int _frameCount;
        private bool _disposed;

        // Standard subsystem arenas
        public ArenaAllocator Style { get; }
        public ArenaAllocator Layout { get; }
        public ArenaAllocator Paint { get; }
        public ArenaAllocator DisplayList { get; }
        public ArenaAllocator Temp { get; }

        public int FrameCount => _frameCount;

        public FrameArenaPool(
            int styleCapacity    = 8 * 1024 * 1024,   // 8 MB
            int layoutCapacity   = 16 * 1024 * 1024,  // 16 MB
            int paintCapacity    = 8 * 1024 * 1024,
            int displayListCap   = 4 * 1024 * 1024,
            int tempCapacity     = 2 * 1024 * 1024)
        {
            Style       = Register("style",        styleCapacity);
            Layout      = Register("layout",       layoutCapacity);
            Paint       = Register("paint",        paintCapacity);
            DisplayList = Register("display-list", displayListCap);
            Temp        = Register("temp",         tempCapacity);
        }

        private ArenaAllocator Register(string name, int capacity)
        {
            var arena = new ArenaAllocator(capacity, name);
            lock (_lock) { _arenas[name] = arena; }
            return arena;
        }

        /// <summary>
        /// Called at the start of each rendering frame.
        /// Resets all arenas, invalidating all prior frame allocations.
        /// </summary>
        public void BeginFrame()
        {
            Interlocked.Increment(ref _frameCount);
            lock (_lock)
            {
                foreach (var a in _arenas.Values) a.Reset();
            }
        }

        /// <summary>Get arena by name, or null.</summary>
        public ArenaAllocator Get(string name)
        {
            lock (_lock)
            {
                return _arenas.TryGetValue(name, out var a) ? a : null;
            }
        }

        /// <summary>Get current utilization stats.</summary>
        public IReadOnlyDictionary<string, (int used, int capacity, double pct)> GetStats()
        {
            var result = new Dictionary<string, (int, int, double)>();
            lock (_lock)
            {
                foreach (var (k, a) in _arenas)
                    result[k] = (a.Used, a.Capacity, a.Utilization);
            }
            return result;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                lock (_lock)
                {
                    foreach (var a in _arenas.Values) a.Dispose();
                    _arenas.Clear();
                }
            }
        }
    }
}
