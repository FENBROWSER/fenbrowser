using System.Numerics;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

// ECMA-262 23.2 — TypedArray objects.
// Abstract base for the 11 concrete typed array constructors. Provides
// typed element access via GetElement/SetElement with per-ElementType
// conversion per the spec's RawBytesToNumeric / NumericToRawBytes tables.
public abstract class TypedArrayObject : TypedArrayView
{
    public abstract TypedArrayElementType ElementType { get; }
    public int Length => ByteLength / ElementSize;

    protected TypedArrayObject(ArrayBufferObject buffer, int byteOffset, int byteLength)
        : base(buffer, byteOffset, byteLength)
    {
    }

    public JsValue GetElement(int index)
    {
        if (index < 0 || index >= Length)
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
        if (index < 0 || index >= Length)
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
    public Int8Array(ArrayBufferObject buffer, int byteOffset, int byteLength) : base(buffer, byteOffset, byteLength) { }
}

public sealed class Uint8Array : TypedArrayObject
{
    public override TypedArrayElementType ElementType => TypedArrayElementType.Uint8;
    public override int ElementSize => 1;
    public Uint8Array(ArrayBufferObject buffer, int byteOffset, int byteLength) : base(buffer, byteOffset, byteLength) { }
}

public sealed class Uint8ClampedArray : TypedArrayObject
{
    public override TypedArrayElementType ElementType => TypedArrayElementType.Uint8Clamped;
    public override int ElementSize => 1;
    public Uint8ClampedArray(ArrayBufferObject buffer, int byteOffset, int byteLength) : base(buffer, byteOffset, byteLength) { }
}

public sealed class Int16Array : TypedArrayObject
{
    public override TypedArrayElementType ElementType => TypedArrayElementType.Int16;
    public override int ElementSize => 2;
    public Int16Array(ArrayBufferObject buffer, int byteOffset, int byteLength) : base(buffer, byteOffset, byteLength) { }
}

public sealed class Uint16Array : TypedArrayObject
{
    public override TypedArrayElementType ElementType => TypedArrayElementType.Uint16;
    public override int ElementSize => 2;
    public Uint16Array(ArrayBufferObject buffer, int byteOffset, int byteLength) : base(buffer, byteOffset, byteLength) { }
}

public sealed class Int32Array : TypedArrayObject
{
    public override TypedArrayElementType ElementType => TypedArrayElementType.Int32;
    public override int ElementSize => 4;
    public Int32Array(ArrayBufferObject buffer, int byteOffset, int byteLength) : base(buffer, byteOffset, byteLength) { }
}

public sealed class Uint32Array : TypedArrayObject
{
    public override TypedArrayElementType ElementType => TypedArrayElementType.Uint32;
    public override int ElementSize => 4;
    public Uint32Array(ArrayBufferObject buffer, int byteOffset, int byteLength) : base(buffer, byteOffset, byteLength) { }
}

public sealed class Float32Array : TypedArrayObject
{
    public override TypedArrayElementType ElementType => TypedArrayElementType.Float32;
    public override int ElementSize => 4;
    public Float32Array(ArrayBufferObject buffer, int byteOffset, int byteLength) : base(buffer, byteOffset, byteLength) { }
}

public sealed class Float64Array : TypedArrayObject
{
    public override TypedArrayElementType ElementType => TypedArrayElementType.Float64;
    public override int ElementSize => 8;
    public Float64Array(ArrayBufferObject buffer, int byteOffset, int byteLength) : base(buffer, byteOffset, byteLength) { }
}

public sealed class BigInt64Array : TypedArrayObject
{
    public override TypedArrayElementType ElementType => TypedArrayElementType.BigInt64;
    public override int ElementSize => 8;
    public BigInt64Array(ArrayBufferObject buffer, int byteOffset, int byteLength) : base(buffer, byteOffset, byteLength) { }
}

public sealed class BigUint64Array : TypedArrayObject
{
    public override TypedArrayElementType ElementType => TypedArrayElementType.BigUint64;
    public override int ElementSize => 8;
    public BigUint64Array(ArrayBufferObject buffer, int byteOffset, int byteLength) : base(buffer, byteOffset, byteLength) { }
}
