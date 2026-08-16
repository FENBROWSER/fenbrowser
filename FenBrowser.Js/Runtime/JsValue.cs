using System.Numerics;
using System.Threading;

namespace FenBrowser.Js.Runtime;

public readonly struct JsValue : IEquatable<JsValue>
{
    private sealed class SymbolRecord
    {
        public SymbolRecord(long id, string? description)
        {
            Id = id;
            Description = description;
        }

        public long Id { get; }
        public string? Description { get; }
    }

    private static long _nextSymbolId;

    public readonly JsValueTag Tag;
    private readonly long _payload;
    private readonly double _number;
    private readonly object? _reference;

    private JsValue(JsValueTag tag, long payload, double number, object? reference = null)
    {
        Tag = tag;
        _payload = payload;
        _number = number;
        _reference = reference;
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
        return new JsValue(JsValueTag.String, 0, 0, value);
    }

    public bool AsBoolean() => Tag == JsValueTag.Boolean && _payload != 0;

    public int AsInt32()
    {
        if (Tag != JsValueTag.Int32)
            throw new InvalidOperationException($"Value is not an Int32 (tag={Tag}).");
        return checked((int)_payload);
    }

    public double AsNumber() => Tag switch
    {
        JsValueTag.Int32 => (double)checked((int)_payload),
        JsValueTag.Number => _number,
        _ => throw new InvalidOperationException($"Value is not numeric (tag={Tag}).")
    };

    public ObjectHandle AsObjectHandle()
    {
        if (Tag != JsValueTag.Object)
            throw new InvalidOperationException($"Value is not an object (tag={Tag}).");
        return ObjectHandle.FromInt64(_payload);
    }

    public HostObjectHandle AsHostObjectHandle()
    {
        if (Tag != JsValueTag.HostObject)
            throw new InvalidOperationException($"Value is not a host object (tag={Tag}).");
        return HostObjectHandle.FromInt64(_payload);
    }

    public string AsString()
    {
        if (Tag != JsValueTag.String)
            throw new InvalidOperationException($"Value is not a string (tag={Tag}).");
        return _reference as string
            ?? throw new InvalidOperationException("String value has no backing payload.");
    }

    // Symbol identity remains a unique scalar. The optional description is carried by
    // this JsValue and is reclaimed when the Symbol value graph becomes unreachable.
    public static JsValue FromSymbol(string? description = null)
    {
        var id = Interlocked.Increment(ref _nextSymbolId);
        return new JsValue(JsValueTag.Symbol, id, 0, new SymbolRecord(id, description));
    }

    public long AsSymbolId()
    {
        if (Tag != JsValueTag.Symbol)
            throw new InvalidOperationException($"Value is not a symbol (tag={Tag}).");
        return _payload;
    }

    public string? AsSymbolDescription()
    {
        if (Tag != JsValueTag.Symbol)
            throw new InvalidOperationException($"Value is not a symbol (tag={Tag}).");

        return _reference is SymbolRecord symbol && symbol.Id == _payload
            ? symbol.Description
            : throw new InvalidOperationException("Symbol value has no matching metadata payload.");
    }

    public static JsValue FromBigInt(BigInteger value) =>
        new(JsValueTag.BigInt, 0, 0, value);

    public BigInteger AsBigInt()
    {
        if (Tag != JsValueTag.BigInt)
            throw new InvalidOperationException($"Value is not a BigInt (tag={Tag}).");

        return _reference is BigInteger value
            ? value
            : throw new InvalidOperationException("BigInt value has no backing payload.");
    }

    public bool Equals(JsValue other)
    {
        if (Tag != other.Tag) return false;

        return Tag switch
        {
            JsValueTag.Undefined or JsValueTag.Null => true,
            JsValueTag.Boolean or JsValueTag.Int32 or JsValueTag.Object or
            JsValueTag.HostObject or JsValueTag.Symbol => _payload == other._payload,
            JsValueTag.Number => _number.Equals(other._number),
            JsValueTag.String => string.Equals(
                _reference as string,
                other._reference as string,
                StringComparison.Ordinal),
            JsValueTag.BigInt => AsBigInt().Equals(other.AsBigInt()),
            _ => false
        };
    }

    public override bool Equals(object? obj) => obj is JsValue other && Equals(other);

    public override int GetHashCode() => Tag switch
    {
        JsValueTag.Undefined or JsValueTag.Null => HashCode.Combine(Tag),
        JsValueTag.Boolean or JsValueTag.Int32 or JsValueTag.Object or
        JsValueTag.HostObject or JsValueTag.Symbol => HashCode.Combine(Tag, _payload),
        JsValueTag.Number => HashCode.Combine(Tag, _number),
        JsValueTag.String => HashCode.Combine(Tag, _reference as string),
        JsValueTag.BigInt => HashCode.Combine(Tag, AsBigInt()),
        _ => HashCode.Combine(Tag)
    };

    public static bool operator ==(JsValue left, JsValue right) => left.Equals(right);
    public static bool operator !=(JsValue left, JsValue right) => !left.Equals(right);
}
