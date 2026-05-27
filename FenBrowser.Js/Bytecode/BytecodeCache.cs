namespace FenBrowser.Js.Bytecode;

// Plan §31 / Tier 4 #23: process-global bytecode cache. Maps source text +
// strict-mode flag to a previously compiled BytecodeFunction so that
// repeated CompileScript / CompileProgram of the same source skips parsing
// and lowering.
//
// Shapes are process-global (Shape.Root is a static singleton), so cached
// inline caches remain valid across calls. Cache is bounded to ~256 entries
// with FIFO eviction to keep memory predictable for shells that compile
// many distinct scripts.
public static class BytecodeCache
{
    private const int MaxEntries = 256;
    private static readonly Dictionary<CacheKey, BytecodeFunction> Entries = new();
    private static readonly Queue<CacheKey> InsertionOrder = new();
    private static readonly Lock Sync = new();

    public static bool Enabled { get; set; } = true;

    public static long HitCount { get; private set; }
    public static long MissCount { get; private set; }

    // Tier 5 #28: per-thread bypass. Isolated realms (JsRealm.Isolated=true)
    // push a BypassScope across compile/execute so their bytecode is never
    // mixed with another realm's cached entries. Implemented as ThreadStatic
    // rather than AsyncLocal because the compiler runs synchronously on the
    // caller thread.
    [ThreadStatic] private static int _bypassDepth;

    public static bool IsBypassed => _bypassDepth > 0;

    public static IDisposable BypassScope() => new BypassToken();

    private sealed class BypassToken : IDisposable
    {
        public BypassToken() => _bypassDepth++;
        public void Dispose() => _bypassDepth--;
    }

    public static bool TryGet(string sourceText, bool strictMode, out BytecodeFunction function)
    {
        if (!Enabled || IsBypassed || sourceText is null)
        {
            function = null!;
            return false;
        }

        var key = new CacheKey(sourceText, strictMode);
        lock (Sync)
        {
            if (Entries.TryGetValue(key, out function!))
            {
                HitCount++;
                return true;
            }

            MissCount++;
            return false;
        }
    }

    public static void Put(string sourceText, bool strictMode, BytecodeFunction function)
    {
        if (!Enabled || IsBypassed || sourceText is null || function is null) return;

        var key = new CacheKey(sourceText, strictMode);
        lock (Sync)
        {
            if (Entries.ContainsKey(key)) return;
            if (Entries.Count >= MaxEntries && InsertionOrder.TryDequeue(out var evicted))
            {
                Entries.Remove(evicted);
            }
            Entries[key] = function;
            InsertionOrder.Enqueue(key);
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Entries.Clear();
            InsertionOrder.Clear();
            HitCount = 0;
            MissCount = 0;
        }
    }

    private readonly record struct CacheKey(string SourceText, bool StrictMode);
}
