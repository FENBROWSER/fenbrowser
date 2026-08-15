using System.Collections.Concurrent;

namespace FenBrowser.Js.Runtime;

public readonly struct JsValue
{
    private static long _nextStringId;
    private static readonly ConcurrentDictionary<long, string> StringPool = new();
    // Reverse map for interning short strings. Identifiers and property names
    // dominate FromString calls — deduplicating them cuts pool growth from
    // O(allocations) to O(distinct strings). Capped at 256 chars to keep the
    // intern table from absorbing arbitrarily large user strings.
    private const int StringInternMaxLength = 256;
    private static readonly ConcurrentDictionary<string, long> StringInternTable = new(StringComparer.Ordinal);
    // Only short-string misses need serialization so two threads cannot allocate
    // different ids for the same interned primitive. Hits and all reads remain
    // lock-free; long strings never touch this gate.
    private static readonly Lock StringInternMissLock = new();

    // Symbol pool. A Symbol's identity is its monotonically-assigned 64-bit id; the
    // optional description is looked up alongside. Use a non-nullable value wrapper
    // so ConcurrentDictionary can represent Symbol() with no description safely.
    private readonly record struct SymbolRecord(string? Description);
    private static long _nextSymbolId;
    private static readonly ConcurrentDictionary<long, SymbolRecord> SymbolPool = new();

    // BigInt pool. IDs remain stable for the lifetime of a JsValue, but creation and
    // reads do not need to serialize unrelated interpreters behind a global lock.
    private static long _nextBigIntId;
    private static readonly ConcurrentDictionary<long, System.Numerics.BigInteger> BigIntPool = new();

    public readonly JsValueTag Tag;
    private readonly long _payload;
    private readonly double _number;

    private JsValue(JsValueTag tag, long payload, double number)
    {
        Tag = tag;
        _payload = payload;
        _number = number;
    }

    public static JsValue Undefined => new(JsValueTag.Undefined, 0, 0);

    public static JsValue Null => new(JsValueTag.Null, 0, 0);

    public static JsValue FromBoolean(bool value) => new(JsValueTag.Boolean, value ? 1 : 0, 0);

    public static JsValue FromInt32(int value) => new(JsValueTag.Int32, value, 0);

    public static JsValue FromNumber(double value) => new(JsValueTag.Number, 0, value);

    public static JsValue FromObject(ObjectHandle handle) => new(JsValueTag.Object, handle.ToInt64(), 0);

    public static JsValue FromHostObject(HostObjectHandle handle) => new(JsValueTag.HostObject, handle.ToInt64(), 0);

    public static JsValue FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length <= StringInternMaxLength)
        {
            if (StringInternTable.TryGetValue(value, out var existingId))
            {
                return new JsValue(JsValueTag.String, existingId, 0);
            }

            lock (StringInternMissLock)
            {
                // Double-check after acquiring the miss gate. Only the first thread
                // publishes an id for this interned string.
                if (StringInternTable.TryGetValue(value, out existingId))
                {
                    return new JsValue(JsValueTag.String, existingId, 0);
                }

                var id = Interlocked.Increment(ref _nextStringId);
                StringPool[id] = value;
                StringInternTable[value] = id;
                return new JsValue(JsValueTag.String, id, 0);
            }
        }

        var longStringId = Interlocked.Increment(ref _nextStringId);
        StringPool[longStringId] = value;
        return new JsValue(JsValueTag.String, longStringId, 0);
    }

    public bool AsBoolean() => Tag == JsValueTag.Boolean && _payload != 0;

    public int AsInt32()
    {
        if (Tag != JsValueTag.Int32)
        {
            throw new InvalidOperationException($"Value is not an Int32 (tag={Tag}).");
        }

        return checked((int)_payload);
    }

    public double AsNumber()
    {
        return Tag switch
        {
            JsValueTag.Int32 => (double)checked((int)_payload),
            JsValueTag.Number => _number,
            _ => throw new InvalidOperationException($"Value is not numeric (tag={Tag}).")
        };
    }

    public ObjectHandle AsObjectHandle()
    {
        if (Tag != JsValueTag.Object)
        {
            throw new InvalidOperationException($"Value is not an object (tag={Tag}).");
        }

        return ObjectHandle.FromInt64(_payload);
    }

    public HostObjectHandle AsHostObjectHandle()
    {
        if (Tag != JsValueTag.HostObject)
        {
            throw new InvalidOperationException($"Value is not a host object (tag={Tag}).");
        }

        return HostObjectHandle.FromInt64(_payload);
    }

    public string AsString()
    {
        if (Tag != JsValueTag.String)
        {
            throw new InvalidOperationException($"Value is not a string (tag={Tag}).");
        }

        if (StringPool.TryGetValue(_payload, out var value))
            return value;

        throw new InvalidOperationException($"Unknown or stale string pool id {_payload}.");
    }

    // ECMA-262 7.4 Symbol primitive. Allocates a fresh unique id; description is
    // optional ("Symbol()" with no argument). Repeated calls produce distinct
    // symbols even with identical descriptions, matching the spec's intent that
    // Symbol() is the only public way to mint identity tokens.
    public static JsValue FromSymbol(string? description = null)
    {
        var id = Interlocked.Increment(ref _nextSymbolId);
        SymbolPool[id] = new SymbolRecord(description);
        return new JsValue(JsValueTag.Symbol, id, 0);
    }

    // Construct a Symbol value from an existing id; used to expose well-known
    // symbols and boxed Symbol primitives. Rejecting unknown ids prevents a stale
    // or fabricated SymbolObject payload from becoming a primitive that only fails
    // much later during description/registry operations.
    public static JsValue SymbolFromId(long id)
    {
        if (id <= 0 || !SymbolPool.ContainsKey(id))
        {
            throw new InvalidOperationException($"Unknown or stale symbol pool id {id}.");
        }

        return new JsValue(JsValueTag.Symbol, id, 0);
    }

    public long AsSymbolId()
    {
        if (Tag != JsValueTag.Symbol)
        {
            throw new InvalidOperationException($"Value is not a symbol (tag={Tag}).");
        }

        return _payload;
    }

    public string? AsSymbolDescription()
    {
        if (Tag != JsValueTag.Symbol)
        {
            throw new InvalidOperationException($"Value is not a symbol (tag={Tag}).");
        }

        if (SymbolPool.TryGetValue(_payload, out var record))
            return record.Description;

        throw new InvalidOperationException($"Unknown or stale symbol pool id {_payload}.");
    }

    public static JsValue FromBigInt(System.Numerics.BigInteger value)
    {
        var id = Interlocked.Increment(ref _nextBigIntId);
        BigIntPool[id] = value;
        return new JsValue(JsValueTag.BigInt, id, 0);
    }

    public System.Numerics.BigInteger AsBigInt()
    {
        if (Tag != JsValueTag.BigInt)
        {
            throw new InvalidOperationException($"Value is not a BigInt (tag={Tag}).");
        }

        if (BigIntPool.TryGetValue(_payload, out var value))
            return value;

        throw new InvalidOperationException($"Unknown or stale BigInt pool id {_payload}.");
    }
}