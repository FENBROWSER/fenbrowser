namespace FenBrowser.Js.Bytecode;

// Process-global bytecode template cache for repeated script compilation.
//
// IMPORTANT: cached BytecodeFunction instances are immutable templates only. A
// BytecodeFunction also owns interpreter feedback (ICs/JIT counters), including call
// IC entries that contain heap-local ObjectHandle values. Cache hits therefore return
// a fresh execution copy rather than the stored template itself.
//
// The cache is bounded by both entry count and retained source size. Entry-count-only
// eviction allowed a few hundred multi-megabyte bundles to pin a very large amount of
// source/bytecode memory. Recency is tracked with an O(1) linked-list LRU.
public static class BytecodeCache
{
    private const int MaxEntries = 256;
    private const int MaxSingleSourceChars = 4 * 1024 * 1024;
    private const int MaxCachedSourceChars = 16 * 1024 * 1024;

    private sealed class CacheEntry
    {
        public required BytecodeFunction Template { get; init; }
        public required LinkedListNode<CacheKey> RecencyNode { get; init; }
    }

    private static readonly Dictionary<CacheKey, CacheEntry> Entries = new();
    private static readonly LinkedList<CacheKey> Recency = new();
    private static readonly Lock Sync = new();

    private static long _hitCount;
    private static long _missCount;
    private static int _cachedSourceChars;

    public static bool Enabled { get; set; } = true;

    public static long HitCount
    {
        get { lock (Sync) return _hitCount; }
    }

    public static long MissCount
    {
        get { lock (Sync) return _missCount; }
    }

    public static int CachedSourceChars
    {
        get { lock (Sync) return _cachedSourceChars; }
    }

    // Isolated realms bypass shared compiled templates for their full compile/execute
    // scope. The current realm API is synchronous; keep this thread-local so unrelated
    // interpreter work on another thread is not accidentally suppressed.
    [ThreadStatic] private static int _bypassDepth;

    public static bool IsBypassed => _bypassDepth > 0;

    public static IDisposable BypassScope() => new BypassToken();

    private sealed class BypassToken : IDisposable
    {
        private bool _disposed;

        public BypassToken() => _bypassDepth++;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_bypassDepth <= 0)
            {
                _bypassDepth = 0;
                return;
            }

            _bypassDepth--;
        }
    }

    public static bool TryGet(string sourceText, bool strictMode, out BytecodeFunction function)
    {
        if (!Enabled || IsBypassed || sourceText is null || sourceText.Length > MaxSingleSourceChars)
        {
            function = null!;
            return false;
        }

        var key = new CacheKey(sourceText, strictMode);
        BytecodeFunction template;
        lock (Sync)
        {
            if (!Entries.TryGetValue(key, out var entry))
            {
                _missCount++;
                function = null!;
                return false;
            }

            Recency.Remove(entry.RecencyNode);
            Recency.AddFirst(entry.RecencyNode);
            _hitCount++;
            template = entry.Template;
        }

        // Cloning a large nested function tree can be non-trivial. Do not hold the
        // process-global cache lock while doing it or one large cache hit will stall
        // unrelated script compilations in every tab/isolate.
        function = template.CreateExecutionCopy();
        return true;
    }

    public static void Put(string sourceText, bool strictMode, BytecodeFunction function)
    {
        if (!Enabled || IsBypassed || sourceText is null || function is null)
            return;

        var sourceChars = sourceText.Length;
        if (sourceChars > MaxSingleSourceChars || sourceChars > MaxCachedSourceChars)
            return;

        // Private brand tokens are evaluation identities, not reusable compiler
        // constants. Heap object/host-object constants are likewise process-local.
        // Do not cache such function trees until the compiler represents those values
        // as rematerializable descriptors rather than runtime identities.
        if (!function.CanUseProcessGlobalTemplate())
            return;

        // Snapshot the compiler output BEFORE the caller executes it. Storing the
        // original object would let the first execution populate IC/JIT feedback in
        // the supposedly immutable template later returned to other interpreters.
        // Build the copy outside the cache lock for the same reason as cache-hit copies.
        var template = function.CreateExecutionCopy();
        var key = new CacheKey(sourceText, strictMode);

        lock (Sync)
        {
            if (Entries.TryGetValue(key, out var existing))
            {
                // A racing compilation may arrive after another thread populated the
                // same key. Keep the first immutable template and refresh recency.
                Recency.Remove(existing.RecencyNode);
                Recency.AddFirst(existing.RecencyNode);
                return;
            }

            while (Entries.Count >= MaxEntries ||
                   _cachedSourceChars + sourceChars > MaxCachedSourceChars)
            {
                if (!EvictLeastRecentlyUsed())
                {
                    break;
                }
            }

            if (Entries.Count >= MaxEntries ||
                _cachedSourceChars + sourceChars > MaxCachedSourceChars)
            {
                return;
            }

            var node = Recency.AddFirst(key);
            Entries.Add(key, new CacheEntry
            {
                Template = template,
                RecencyNode = node
            });
            _cachedSourceChars += sourceChars;
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Entries.Clear();
            Recency.Clear();
            _cachedSourceChars = 0;
            _hitCount = 0;
            _missCount = 0;
        }
    }

    private static bool EvictLeastRecentlyUsed()
    {
        var node = Recency.Last;
        if (node == null)
        {
            return false;
        }

        var key = node.Value;
        Recency.RemoveLast();
        if (Entries.Remove(key))
        {
            _cachedSourceChars -= key.SourceText.Length;
            if (_cachedSourceChars < 0)
            {
                _cachedSourceChars = 0;
            }
        }

        return true;
    }

    private readonly record struct CacheKey(string SourceText, bool StrictMode);
}
