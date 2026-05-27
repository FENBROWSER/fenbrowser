namespace FenBrowser.Js.Runtime;

public readonly struct JsValue
{
    private static long _nextStringId;
    private static readonly Dictionary<long, string> StringPool = new();
    // Reverse map for interning short strings. Identifiers and property names
    // dominate FromString calls — deduplicating them cuts pool growth from
    // O(allocations) to O(distinct strings). Capped at 256 chars to keep the
    // intern table from absorbing arbitrarily large user strings.
    private const int StringInternMaxLength = 256;
    private static readonly Dictionary<string, long> StringInternTable = new(StringComparer.Ordinal);
    private static readonly Lock StringPoolLock = new();

    // Symbol pool. A Symbol's identity is its monotonically-assigned 64-bit id; the
    // optional description is looked up alongside. Two Symbol values are === iff
    // their ids match - the description is a debug aid only and never affects
    // identity (matching ECMA-262 7.4.4 'Symbol description', which is optional and
    // does not participate in equality).
    private static long _nextSymbolId;
    private static readonly Dictionary<long, string?> SymbolPool = new();
    private static readonly Lock SymbolPoolLock = new();

    // BigInt pool. BigInts are interned by id (same pattern as String and Symbol).
    private static long _nextBigIntId;
    private static readonly Dictionary<long, System.Numerics.BigInteger> BigIntPool = new();
    private static readonly Lock BigIntPoolLock = new();

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
        lock (StringPoolLock)
        {
            if (value.Length <= StringInternMaxLength &&
                StringInternTable.TryGetValue(value, out var existingId))
            {
                return new JsValue(JsValueTag.String, existingId, 0);
            }

            var id = ++_nextStringId;
            StringPool[id] = value;
            if (value.Length <= StringInternMaxLength)
            {
                StringInternTable[value] = id;
            }
            return new JsValue(JsValueTag.String, id, 0);
        }
    }

    public bool AsBoolean() => Tag == JsValueTag.Boolean && _payload != 0;

    public int AsInt32() => checked((int)_payload);

    public double AsNumber() => _number;

    public ObjectHandle AsObjectHandle() => ObjectHandle.FromInt64(_payload);

    public HostObjectHandle AsHostObjectHandle() => HostObjectHandle.FromInt64(_payload);

    public string AsString()
    {
        if (Tag != JsValueTag.String)
        {
            throw new InvalidOperationException($"Value is not a string (tag={Tag}).");
        }

        lock (StringPoolLock)
        {
            return StringPool.TryGetValue(_payload, out var value) ? value : string.Empty;
        }
    }

    // ECMA-262 7.4 Symbol primitive. Allocates a fresh unique id; description is
    // optional ("Symbol()" with no argument). Repeated calls produce distinct
    // symbols even with identical descriptions, matching the spec's intent that
    // Symbol() is the only public way to mint identity tokens.
    public static JsValue FromSymbol(string? description = null)
    {
        lock (SymbolPoolLock)
        {
            var id = ++_nextSymbolId;
            SymbolPool[id] = description;
            return new JsValue(JsValueTag.Symbol, id, 0);
        }
    }

    // Construct a Symbol value from an existing id; used to expose well-known
    // symbols (Symbol.iterator etc.) that the runtime mints exactly once at
    // startup and then hands the same id out repeatedly.
    public static JsValue SymbolFromId(long id)
    {
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

        lock (SymbolPoolLock)
        {
            return SymbolPool.TryGetValue(_payload, out var d) ? d : null;
        }
    }

    public static JsValue FromBigInt(System.Numerics.BigInteger value)
    {
        lock (BigIntPoolLock)
        {
            var id = ++_nextBigIntId;
            BigIntPool[id] = value;
            return new JsValue(JsValueTag.BigInt, id, 0);
        }
    }

    public System.Numerics.BigInteger AsBigInt()
    {
        if (Tag != JsValueTag.BigInt)
        {
            throw new InvalidOperationException($"Value is not a BigInt (tag={Tag}).");
        }

        lock (BigIntPoolLock)
        {
            return BigIntPool.TryGetValue(_payload, out var value) ? value : System.Numerics.BigInteger.Zero;
        }
    }
}
