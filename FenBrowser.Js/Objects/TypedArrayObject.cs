using System.Numerics;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

// ECMA-262 23.2 — TypedArray objects.
// Abstract base for the 11 concrete typed array constructors. Provides
// typed element access via GetElement/SetElement with per-ElementType
// conversion per the spec's RawBytesToNumeric / NumericToRawBytes tables.
// Also overrides JsObject virtuals to implement 10.4.5 Integer-Indexed
// Exotic Object semantics ([[DefineOwnProperty]], [[Set]], [[Delete]],
// [[GetOwnProperty]], [[HasProperty]], [[OwnPropertyKeys]]).
public abstract class TypedArrayObject : TypedArrayView
{
    public abstract TypedArrayElementType ElementType { get; }
    public int Length => ByteLength / ElementSize;

    protected TypedArrayObject(ArrayBufferObject buffer, int byteOffset, int byteLength, bool isLengthTracking = false)
        : base(buffer, byteOffset, byteLength, isLengthTracking)
    {
    }

    // ECMA-262 7.1.21 CanonicalNumericIndexString — returns the parsed non-negative
    // integer if `key` is the canonical String representation of an integer index,
    // otherwise -1. Leading zeros, overflow, and non-digit chars all disable the fast
    // integer-index path (10.4.5.x exotic-object algorithms). "-0" maps to index 0.
    public static bool IsCanonicalNumericIndex(string key, out int index)
    {
        index = -1;
        if (string.IsNullOrEmpty(key)) return false;
        // "-0" is the canonical representation of negative zero (index 0).
        if (key == "-0") { index = 0; return true; }
        // Leading-zero strings like "00", "01" are not canonical.
        if (key.Length > 1 && key[0] == '0') return false;
        var result = 0;
        foreach (var c in key)
        {
            if (c < '0' || c > '9') return false;
            if (result > (int.MaxValue - (c - '0')) / 10) return false;
            result = result * 10 + (c - '0');
        }
        index = result;
        return true;
    }

    // 10.4.5.7 IsValidIntegerIndex(O, index) — true when the buffer is not detached
    // and the index is within [0, [[ArrayLength]]).
    private bool IsValidIntegerIndex(int index)
        => !IsViewDetached && !IsOutOfBounds() && index >= 0 && index < Length;

    // 10.4.5.3 [[DefineOwnProperty]] (P, Desc) — Integer-Indexed Exotic Object.
    // When P is a canonical numeric index:
    //   - Valid (not-detached, in-range): reject accessor descriptors and
    //     configurable/enumerable/writable: false; set the element value; return true.
    //   - Detached buffer: return false (cannot define on detached TypedArray).
    //   - Out of bounds (index >= Length or index < 0): return false.
    //   - Non-canonical key: fall through to OrdinaryDefineOwnProperty.
    // Returns false when the descriptor is rejected; the caller (ObjectDefineProperty)
    // converts the false return to a TypeError throw for Object.defineProperty.
    public override bool DefineOwnProperty(string key, JsPropertyDescriptor descriptor)
    {
        // ECMA-262 7.1.21: CanonicalNumericIndexString("-0") returns -0.
        // IntegerIndexedElementSet with -0 is a no-op per 10.4.5.11 step 4,
        // and [[DefineOwnProperty]] for -0 rejects the descriptor (returns false).
        if (key == "-0")
            return false;

        if (IsCanonicalNumericIndex(key, out var numericIndex))
        {
            // 10.4.5.3 step 3.b.i: valid integer index — validate descriptor constraints.
            if (IsValidIntegerIndex(numericIndex))
            {
                // Reject accessor descriptors (step 3.b.i.1).
                if (descriptor.IsAccessor) return false;
                // Reject configurable: false, enumerable: false, writable: false
                // (steps 3.b.i.2-4 — align-detached-buffer-semantics-with-web-reality).
                if (descriptor.HasConfigurable && !descriptor.Configurable) return false;
                if (descriptor.HasEnumerable && !descriptor.Enumerable) return false;
                if (descriptor.HasWritable && !descriptor.Writable) return false;
                // Step 3.b.i.5: if the descriptor carries a [[Value]] field, write it
                // through IntegerIndexedElementSet (10.4.5.11).
                if (descriptor.HasValue)
                    SetElement(numericIndex, descriptor.Value);
                return true;
            }

            // numericIndex is canonical but the index is invalid.
            // If the buffer is detached, return false (cannot define properties on
            // a detached Integer-Indexed Exotic Object).
            if (IsViewDetached) return false;
            // If the index is out of bounds (>= Length or < 0), return false.
            if (numericIndex < 0 || numericIndex >= Length) return false;

            // Should not reach here (IsValidIntegerIndex covers all non-detached cases).
            // If we do, perform a no-op IntegerIndexedElementSet for any Value field
            // and return true.
            if (descriptor.HasValue)
                SetElement(numericIndex, descriptor.Value);
            return true;
        }

        return base.DefineOwnProperty(key, descriptor);
    }

    // 10.4.5.5 [[Set]] (P, V, Receiver) — Integer-Indexed Exotic Object.
    // Canonical integer-index keys route through IntegerIndexedElementSet rather than
    // the ordinary property store. Non-canonical keys fall through to OrdinarySet.
    // "-0" routes through IntegerIndexedElementSet which is a no-op for -0
    // (10.4.5.11 step 4: "If index = -0, return NormalCompletion(undefined)").
    public override bool SetProperty(string key, JsValue value)
    {
        // "-0" is a canonical numeric index but IntegerIndexedElementSet with -0
        // is a no-op per 10.4.5.11 step 4.
        if (key == "-0")
            return true;

        if (IsCanonicalNumericIndex(key, out var numericIndex))
        {
            SetElement(numericIndex, value);
            return true;
        }

        return base.SetProperty(key, value);
    }

    // 10.4.5.2 [[Delete]] (P) — Integer-Indexed Exotic Object.
    // Canonical integer indices cannot be deleted from a live (non-detached) TypedArray;
    // detached and out-of-bounds indices return true so the operation appears to succeed.
    // "-0" maps to canonical numeric index -0; IsValidIntegerIndex returns false for -0
    // per aligned spec, so deletion succeeds (return true).
    // Non-canonical keys (including non-integer canonical indices like "1.1") are not
    // TypedArray elements, so [[Delete]] always returns true for them (there is nothing
    // to delete — per the spec, OrdinaryDelete returns true for undefined descriptors).
    public override bool DeleteProperty(string key)
    {
        // Aligned spec: IsValidIntegerIndex returns false for -0, so deletion succeeds.
        if (key == "-0")
            return true;

        if (IsCanonicalNumericIndex(key, out var numericIndex))
        {
            if (IsViewDetached) return true;
            if (!IsValidIntegerIndex(numericIndex)) return true;
            // Valid integer index in a live buffer: cannot be deleted.
            return false;
        }

        // Non-canonical key: delete the ordinary property if present.
        // If the property doesn't exist (no own slot), return true per spec
        // (OrdinaryDelete: if desc is undefined, return true).
        if (base.DeleteProperty(key))
            return true;
        // Property didn't exist as an own property → delete succeeds by default.
        return true;
    }

    // 10.4.5.4 [[GetOwnProperty]] (P) — Integer-Indexed Exotic Object.
    // For a valid canonical integer index, synthesize a data-property descriptor with
    // the element value, writable/enumerable/configurable = true. If an ordinary own
    // property already shadows the index, return that instead (spec step ordering).
    // "-0" maps to canonical numeric index -0; IntegerIndexedElementGet returns undefined
    // for -0 (10.4.5.8 step 6), so [[GetOwnProperty]] returns undefined (step 6).
    public override bool TryGetOwnProperty(string key, out JsPropertyDescriptor descriptor)
    {
        // OrdinaryGetOwnProperty has priority: a user-defined own property at this key
        // shadows the synthetic integer-indexed descriptor.
        if (base.TryGetOwnProperty(key, out descriptor))
            return true;

        // "-0" is a canonical numeric index but IntegerIndexedElementGet returns undefined
        // for -0, so [[GetOwnProperty]] returns undefined per 10.4.5.4 step 6.
        if (key == "-0")
        {
            descriptor = default;
            return false;
        }

        if (IsCanonicalNumericIndex(key, out var numericIndex) && IsValidIntegerIndex(numericIndex))
        {
            descriptor = new JsPropertyDescriptor(
                GetElement(numericIndex),
                Writable: true,
                Enumerable: true,
                Configurable: true);
            return true;
        }

        descriptor = default;
        return false;
    }

    // 10.4.5.6 [[OwnPropertyKeys]] — Integer-Indexed Exotic Object.
    // Yields the integer index keys "0", "1", … "length-1" (in ascending order) before
    // any ordinary own properties. Subarray views also enumerate their local indices.
    public override IEnumerable<KeyValuePair<string, JsPropertyDescriptor>> EnumerateOwnProperties()
    {
        // YIELD integer indices first (10.4.5.6 step 4).
        if (!IsViewDetached && !IsOutOfBounds())
        {
            for (var i = 0; i < Length; i++)
            {
                yield return new KeyValuePair<string, JsPropertyDescriptor>(
                    i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    new JsPropertyDescriptor(GetElement(i), Writable: true, Enumerable: true, Configurable: true));
            }
        }

        // Then yield ordinary properties (if any).
        foreach (var pair in base.EnumerateOwnProperties())
            yield return pair;
    }

    public JsValue GetElement(int index)
    {
        if (IsOutOfBounds() || index < 0 || index >= Length)
            return JsValue.Undefined;
        var offset = ByteOffset + index * ElementSize;
        var raw = Buffer.Data;
        return ElementType switch
        {
            TypedArrayElementType.Int8 => JsValue.FromNumber((sbyte)raw[offset]),
            TypedArrayElementType.Uint8 => JsValue.FromNumber(raw[offset]),
            TypedArrayElementType.Uint8Clamped => JsValue.FromNumber(raw[offset]),
            TypedArrayElementType.Int16 => JsValue.FromNumber(BitConverter.ToInt16(raw, offset)),
            TypedArrayElementType.Uint16 => JsValue.FromNumber(BitConverter.ToUInt16(raw, offset)),
            TypedArrayElementType.Int32 => JsValue.FromNumber(BitConverter.ToInt32(raw, offset)),
            TypedArrayElementType.Uint32 => JsValue.FromNumber(BitConverter.ToUInt32(raw, offset)),
            TypedArrayElementType.Float32 => JsValue.FromNumber(BitConverter.ToSingle(raw, offset)),
            TypedArrayElementType.Float64 => JsValue.FromNumber(BitConverter.ToDouble(raw, offset)),
            TypedArrayElementType.BigInt64 => JsValue.FromBigInt(new BigInteger(BitConverter.ToInt64(raw, offset))),
            TypedArrayElementType.BigUint64 => JsValue.FromBigInt(new BigInteger(BitConverter.ToUInt64(raw, offset))),
            _ => JsValue.Undefined
        };
    }

    public void SetElement(int index, JsValue value)
    {
        if (IsOutOfBounds() || index < 0 || index >= Length)
            return;
        var offset = ByteOffset + index * ElementSize;
        var raw = Buffer.Data;
        switch (ElementType)
        {
            case TypedArrayElementType.Int8:
                raw[offset] = (byte)(sbyte)ConvertToInt32(value);
                break;
            case TypedArrayElementType.Uint8:
                raw[offset] = (byte)ConvertToUint32(value);
                break;
            case TypedArrayElementType.Uint8Clamped:
                raw[offset] = ClampToUint8(value);
                break;
            case TypedArrayElementType.Int16:
                BitConverter.TryWriteBytes(raw.AsSpan(offset, 2), (short)ConvertToInt32(value));
                break;
            case TypedArrayElementType.Uint16:
                BitConverter.TryWriteBytes(raw.AsSpan(offset, 2), (ushort)ConvertToUint32(value));
                break;
            case TypedArrayElementType.Int32:
                BitConverter.TryWriteBytes(raw.AsSpan(offset, 4), ConvertToInt32(value));
                break;
            case TypedArrayElementType.Uint32:
                BitConverter.TryWriteBytes(raw.AsSpan(offset, 4), ConvertToUint32(value));
                break;
            case TypedArrayElementType.Float32:
                BitConverter.TryWriteBytes(raw.AsSpan(offset, 4), (float)value.AsNumber());
                break;
            case TypedArrayElementType.Float64:
                BitConverter.TryWriteBytes(raw.AsSpan(offset, 8), value.AsNumber());
                break;
            case TypedArrayElementType.BigInt64:
            {
                var big = value.Tag == JsValueTag.BigInt ? value.AsBigInt() : new BigInteger((long)value.AsNumber());
                var two64 = BigInteger.One << 64;
                var wrapped = ((big % two64) + two64) % two64;
                var signed = wrapped >= (BigInteger.One << 63) ? wrapped - two64 : wrapped;
                BitConverter.TryWriteBytes(raw.AsSpan(offset, 8), (long)signed);
                break;
            }
            case TypedArrayElementType.BigUint64:
            {
                var big = value.Tag == JsValueTag.BigInt ? value.AsBigInt() : new BigInteger((ulong)Math.Max(0, value.AsNumber()));
                var two64 = BigInteger.One << 64;
                var wrapped = ((big % two64) + two64) % two64;
                BitConverter.TryWriteBytes(raw.AsSpan(offset, 8), (ulong)wrapped);
                break;
            }
        }
    }

    // ECMA-262 7.1.6 ToInt32
    private static int ConvertToInt32(JsValue v)
    {
        var d = v.AsNumber();
        if (double.IsNaN(d) || double.IsInfinity(d)) return 0;
        return (int)(long)d;
    }

    // ECMA-262 7.1.12 ToUint8 (used for Uint8 and Uint8Clamped via ClampToUint8)
    private static uint ConvertToUint32(JsValue v)
    {
        var d = v.AsNumber();
        if (double.IsNaN(d) || double.IsInfinity(d)) return 0;
        return (uint)(long)d;
    }

    // ECMA-262 7.1.10 ToUint8Clamp
    private static byte ClampToUint8(JsValue v)
    {
        var d = v.AsNumber();
        if (double.IsNaN(d)) return 0;
        if (d <= 0) return 0;
        if (d >= 255) return 255;
        var f = Math.Floor(d);
        if (f + 0.5 < d) return (byte)(f + 1);
        if (d < f + 0.5) return (byte)f;
        return (byte)(((int)f % 2 == 0) ? f : f + 1);
    }
}

// ECMA-262 23.2 — the 11 concrete TypedArray constructors.
// Each is a thin shell fixing ElementType and ElementSize.

public sealed class Int8Array : TypedArrayObject
{
    public override TypedArrayElementType ElementType => TypedArrayElementType.Int8;
    public override int ElementSize => 1;
    public Int8Array(ArrayBufferObject buffer, int byteOffset, int byteLength, bool isLengthTracking = false) : base(buffer, byteOffset, byteLength, isLengthTracking) { }
}

public sealed class Uint8Array : TypedArrayObject
{
    public override TypedArrayElementType ElementType => TypedArrayElementType.Uint8;
    public override int ElementSize => 1;
    public Uint8Array(ArrayBufferObject buffer, int byteOffset, int byteLength, bool isLengthTracking = false) : base(buffer, byteOffset, byteLength, isLengthTracking) { }
}

public sealed class Uint8ClampedArray : TypedArrayObject
{
    public override TypedArrayElementType ElementType => TypedArrayElementType.Uint8Clamped;
    public override int ElementSize => 1;
    public Uint8ClampedArray(ArrayBufferObject buffer, int byteOffset, int byteLength, bool isLengthTracking = false) : base(buffer, byteOffset, byteLength, isLengthTracking) { }
}

public sealed class Int16Array : TypedArrayObject
{
    public override TypedArrayElementType ElementType => TypedArrayElementType.Int16;
    public override int ElementSize => 2;
    public Int16Array(ArrayBufferObject buffer, int byteOffset, int byteLength, bool isLengthTracking = false) : base(buffer, byteOffset, byteLength, isLengthTracking) { }
}

public sealed class Uint16Array : TypedArrayObject
{
    public override TypedArrayElementType ElementType => TypedArrayElementType.Uint16;
    public override int ElementSize => 2;
    public Uint16Array(ArrayBufferObject buffer, int byteOffset, int byteLength, bool isLengthTracking = false) : base(buffer, byteOffset, byteLength, isLengthTracking) { }
}

public sealed class Int32Array : TypedArrayObject
{
    public override TypedArrayElementType ElementType => TypedArrayElementType.Int32;
    public override int ElementSize => 4;
    public Int32Array(ArrayBufferObject buffer, int byteOffset, int byteLength, bool isLengthTracking = false) : base(buffer, byteOffset, byteLength, isLengthTracking) { }
}

public sealed class Uint32Array : TypedArrayObject
{
    public override TypedArrayElementType ElementType => TypedArrayElementType.Uint32;
    public override int ElementSize => 4;
    public Uint32Array(ArrayBufferObject buffer, int byteOffset, int byteLength, bool isLengthTracking = false) : base(buffer, byteOffset, byteLength, isLengthTracking) { }
}

public sealed class Float32Array : TypedArrayObject
{
    public override TypedArrayElementType ElementType => TypedArrayElementType.Float32;
    public override int ElementSize => 4;
    public Float32Array(ArrayBufferObject buffer, int byteOffset, int byteLength, bool isLengthTracking = false) : base(buffer, byteOffset, byteLength, isLengthTracking) { }
}

public sealed class Float64Array : TypedArrayObject
{
    public override TypedArrayElementType ElementType => TypedArrayElementType.Float64;
    public override int ElementSize => 8;
    public Float64Array(ArrayBufferObject buffer, int byteOffset, int byteLength, bool isLengthTracking = false) : base(buffer, byteOffset, byteLength, isLengthTracking) { }
}

public sealed class BigInt64Array : TypedArrayObject
{
    public override TypedArrayElementType ElementType => TypedArrayElementType.BigInt64;
    public override int ElementSize => 8;
    public BigInt64Array(ArrayBufferObject buffer, int byteOffset, int byteLength, bool isLengthTracking = false) : base(buffer, byteOffset, byteLength, isLengthTracking) { }
}

public sealed class BigUint64Array : TypedArrayObject
{
    public override TypedArrayElementType ElementType => TypedArrayElementType.BigUint64;
    public override int ElementSize => 8;
    public BigUint64Array(ArrayBufferObject buffer, int byteOffset, int byteLength, bool isLengthTracking = false) : base(buffer, byteOffset, byteLength, isLengthTracking) { }
}
