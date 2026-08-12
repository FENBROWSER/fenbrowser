using System.Numerics;
using FenBrowser.Js.Builtins;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

// ECMA-262 23.2 — TypedArray objects.
// Abstract base for the 11 concrete typed array constructors. Provides
// typed element access via GetElement/SetElement with per-ElementType
// conversion per the spec's RawBytesToNumeric / NumericToRawBytes tables.
// Also overrides JsObject virtuals to implement 10.4.5 TypedArray Exotic
// Object semantics ([[DefineOwnProperty]], [[Set]], [[Delete]],
// [[GetOwnProperty]], [[HasProperty]], [[OwnPropertyKeys]]).
public abstract class TypedArrayObject : TypedArrayView
{
    public abstract TypedArrayElementType ElementType { get; }
    public int Length => ByteLength / ElementSize;

    protected TypedArrayObject(ArrayBufferObject buffer, int byteOffset, int byteLength, bool isLengthTracking = false)
        : base(buffer, byteOffset, byteLength, isLengthTracking)
    {
    }

    // ECMA-262 7.1.21 CanonicalNumericIndexString.
    // A string is canonical when it is "-0" or exactly equals ToString(ToNumber(key)).
    // Keep this as the single numeric-property classifier used by the TypedArray exotic
    // methods; canonical numeric strings that are not valid integer indices must still
    // be intercepted rather than falling through to ordinary properties/prototypes.
    public static bool TryCanonicalNumericIndexString(string key, out double numericValue)
    {
        numericValue = 0;
        if (string.IsNullOrEmpty(key)) return false;

        if (string.Equals(key, "-0", StringComparison.Ordinal))
        {
            numericValue = -0.0;
            return true;
        }

        // ToNumber has canonical string forms for these non-finite values as well.
        if (string.Equals(key, "NaN", StringComparison.Ordinal))
        {
            numericValue = double.NaN;
            return true;
        }
        if (string.Equals(key, "Infinity", StringComparison.Ordinal))
        {
            numericValue = double.PositiveInfinity;
            return true;
        }
        if (string.Equals(key, "-Infinity", StringComparison.Ordinal))
        {
            numericValue = double.NegativeInfinity;
            return true;
        }

        if (!double.TryParse(
                key,
                System.Globalization.NumberStyles.AllowLeadingSign |
                System.Globalization.NumberStyles.AllowDecimalPoint |
                System.Globalization.NumberStyles.AllowExponent,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed))
        {
            return false;
        }

        if (!string.Equals(MathHelpers.FormatNumberForString(parsed), key, StringComparison.Ordinal))
        {
            return false;
        }

        numericValue = parsed;
        return true;
    }

    // ECMA-262 10.4.5.16 IsValidIntegerIndex(O, index).
    private bool IsValidIntegerIndex(double index)
    {
        if (IsViewDetached) return false;
        if (double.IsNaN(index) || double.IsInfinity(index)) return false;
        if (Math.Truncate(index) != index) return false;
        if (index == 0d && BitConverter.DoubleToInt64Bits(index) < 0) return false;
        if (index < 0d || IsOutOfBounds()) return false;
        return index < Length;
    }

    // ECMA-262 10.4.5.4 [[DefineOwnProperty]] (P, Desc).
    public override bool DefineOwnProperty(string key, JsPropertyDescriptor descriptor)
    {
        if (TryCanonicalNumericIndexString(key, out var numericIndex))
        {
            if (!IsValidIntegerIndex(numericIndex)) return false;
            if (descriptor.HasConfigurable && !descriptor.Configurable) return false;
            if (descriptor.HasEnumerable && !descriptor.Enumerable) return false;
            if (descriptor.IsAccessor) return false;
            if (descriptor.HasWritable && !descriptor.Writable) return false;

            if (descriptor.HasValue)
            {
                SetElement((int)numericIndex, descriptor.Value);
            }

            return true;
        }

        return base.DefineOwnProperty(key, descriptor);
    }

    // ECMA-262 10.4.5.6 [[Set]] (P, V, Receiver) for the common SameValue(O, Receiver)
    // path represented by this virtual. TypedArraySetElement converts V before checking
    // whether the canonical numeric index is valid, so use an invalid sentinel to retain
    // conversion/throw side effects without creating an ordinary property.
    public override bool SetProperty(string key, JsValue value)
    {
        if (TryCanonicalNumericIndexString(key, out var numericIndex))
        {
            SetElement(IsValidIntegerIndex(numericIndex) ? (int)numericIndex : -1, value);
            return true;
        }

        return base.SetProperty(key, value);
    }

    // ECMA-262 10.4.5.7 [[Delete]] (P).
    public override bool DeleteProperty(string key)
    {
        if (TryCanonicalNumericIndexString(key, out var numericIndex))
        {
            return !IsValidIntegerIndex(numericIndex);
        }

        return base.DeleteProperty(key);
    }

    // ECMA-262 10.4.5.2 [[GetOwnProperty]] (P). Canonical numeric strings are
    // intercepted before OrdinaryGetOwnProperty, including invalid indices such as
    // "-0", negatives, fractions, NaN/Infinity, and out-of-range integers.
    public override bool TryGetOwnProperty(string key, out JsPropertyDescriptor descriptor)
    {
        if (TryCanonicalNumericIndexString(key, out var numericIndex))
        {
            if (!IsValidIntegerIndex(numericIndex))
            {
                descriptor = default;
                return false;
            }

            descriptor = new JsPropertyDescriptor(
                GetElement((int)numericIndex),
                Writable: true,
                Enumerable: true,
                Configurable: true);
            return true;
        }

        return base.TryGetOwnProperty(key, out descriptor);
    }

    // 10.4.5.8 [[OwnPropertyKeys]] — TypedArray Exotic Object.
    // Yields the integer index keys "0", "1", … "length-1" (in ascending order) before
    // any ordinary own properties. Subarray views also enumerate their local indices.
    public override IEnumerable<KeyValuePair<string, JsPropertyDescriptor>> EnumerateOwnProperties()
    {
        // YIELD integer indices first (10.4.5.8 step 3).
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
        // Resizable buffers: use per-element bounds so in-bounds indices remain
        // accessible after a shrink. Non-resizable: original view-level OOB check.
        if (index < 0 || index >= Length)
            return JsValue.Undefined;
        if (IsViewDetached)
            return JsValue.Undefined;
        if (Buffer.IsResizable)
        {
            var elemEnd = (long)ByteOffset + (index + 1) * (long)ElementSize;
            if (elemEnd > Buffer.ByteLength)
                return JsValue.Undefined;
        }
        else if (IsOutOfBounds())
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
        // ECMA-262 10.4.5.18 TypedArraySetElement: convert the value first,
        // THEN check bounds. Value conversion may trigger user code that detaches
        // the buffer, so the bounds check must happen after conversion.
        switch (ElementType)
        {
            case TypedArrayElementType.Int8:
            {
                var converted = (byte)(sbyte)ConvertToInt32(value);
                if (IsOutOfBounds() || index < 0 || index >= Length) return;
                var offset = ByteOffset + index * ElementSize;
                Buffer.Data[offset] = converted;
                return;
            }
            case TypedArrayElementType.Uint8:
            {
                var converted = (byte)ConvertToUint32(value);
                if (IsOutOfBounds() || index < 0 || index >= Length) return;
                var offset = ByteOffset + index * ElementSize;
                Buffer.Data[offset] = converted;
                return;
            }
            case TypedArrayElementType.Uint8Clamped:
            {
                var converted = ClampToUint8(value);
                if (IsOutOfBounds() || index < 0 || index >= Length) return;
                var offset = ByteOffset + index * ElementSize;
                Buffer.Data[offset] = converted;
                return;
            }
            case TypedArrayElementType.Int16:
            {
                var converted = (short)ConvertToInt32(value);
                if (IsOutOfBounds() || index < 0 || index >= Length) return;
                var offset = ByteOffset + index * ElementSize;
                BitConverter.TryWriteBytes(Buffer.Data.AsSpan(offset, 2), converted);
                return;
            }
            case TypedArrayElementType.Uint16:
            {
                var converted = (ushort)ConvertToUint32(value);
                if (IsOutOfBounds() || index < 0 || index >= Length) return;
                var offset = ByteOffset + index * ElementSize;
                BitConverter.TryWriteBytes(Buffer.Data.AsSpan(offset, 2), converted);
                return;
            }
            case TypedArrayElementType.Int32:
            {
                var converted = ConvertToInt32(value);
                if (IsOutOfBounds() || index < 0 || index >= Length) return;
                var offset = ByteOffset + index * ElementSize;
                BitConverter.TryWriteBytes(Buffer.Data.AsSpan(offset, 4), converted);
                return;
            }
            case TypedArrayElementType.Uint32:
            {
                var converted = ConvertToUint32(value);
                if (IsOutOfBounds() || index < 0 || index >= Length) return;
                var offset = ByteOffset + index * ElementSize;
                BitConverter.TryWriteBytes(Buffer.Data.AsSpan(offset, 4), converted);
                return;
            }
            case TypedArrayElementType.Float32:
            {
                var converted = (float)value.AsNumber();
                if (IsOutOfBounds() || index < 0 || index >= Length) return;
                var offset = ByteOffset + index * ElementSize;
                BitConverter.TryWriteBytes(Buffer.Data.AsSpan(offset, 4), converted);
                return;
            }
            case TypedArrayElementType.Float64:
            {
                var converted = value.AsNumber();
                if (IsOutOfBounds() || index < 0 || index >= Length) return;
                var offset = ByteOffset + index * ElementSize;
                BitConverter.TryWriteBytes(Buffer.Data.AsSpan(offset, 8), converted);
                return;
            }
            case TypedArrayElementType.BigInt64:
            {
                var big = value.Tag == JsValueTag.BigInt
                    ? value.AsBigInt()
                    : new BigInteger((long)value.AsNumber());
                if (IsOutOfBounds() || index < 0 || index >= Length) return;
                var offset = ByteOffset + index * ElementSize;
                var two64 = BigInteger.One << 64;
                var wrapped = ((big % two64) + two64) % two64;
                var signed = wrapped >= (BigInteger.One << 63) ? wrapped - two64 : wrapped;
                BitConverter.TryWriteBytes(Buffer.Data.AsSpan(offset, 8), (long)signed);
                return;
            }
            case TypedArrayElementType.BigUint64:
            {
                var big = value.Tag == JsValueTag.BigInt
                    ? value.AsBigInt()
                    : new BigInteger((ulong)Math.Max(0, value.AsNumber()));
                if (IsOutOfBounds() || index < 0 || index >= Length) return;
                var offset = ByteOffset + index * ElementSize;
                var two64 = BigInteger.One << 64;
                var wrapped = ((big % two64) + two64) % two64;
                BitConverter.TryWriteBytes(Buffer.Data.AsSpan(offset, 8), (ulong)wrapped);
                return;
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
