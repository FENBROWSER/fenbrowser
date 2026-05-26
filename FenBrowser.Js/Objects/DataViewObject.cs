using System.Buffers.Binary;
using System.Numerics;

namespace FenBrowser.Js.Objects;

// ECMA-262 25.3 — DataView Objects.
// Provides flexible mixed-type read/write access to an ArrayBuffer with
// explicit endianness control. Each get/set method validates the offset
// against the view's byte length.
public sealed class DataViewObject : TypedArrayView
{
    public override int ElementSize => 1;

    public DataViewObject(ArrayBufferObject buffer, int byteOffset, int byteLength)
        : base(buffer, byteOffset, byteLength)
    {
    }

    public double GetFloat64(int byteOffset, bool littleEndian)
    {
        ValidateOffset(byteOffset, 8);
        var raw = Buffer.Data;
        var off = ByteOffset + byteOffset;
        if (littleEndian == BitConverter.IsLittleEndian)
            return BitConverter.ToDouble(raw, off);
        var v = BitConverter.ToInt64(raw, off);
        v = (long)BinaryPrimitives.ReverseEndianness((ulong)v);
        return BitConverter.Int64BitsToDouble(v);
    }

    public double GetFloat32(int byteOffset, bool littleEndian)
    {
        ValidateOffset(byteOffset, 4);
        var raw = Buffer.Data;
        var off = ByteOffset + byteOffset;
        if (littleEndian == BitConverter.IsLittleEndian)
            return BitConverter.ToSingle(raw, off);
        var v = BitConverter.ToInt32(raw, off);
        v = (int)BinaryPrimitives.ReverseEndianness((uint)v);
        return BitConverter.Int32BitsToSingle(v);
    }

    public int GetInt32(int byteOffset, bool littleEndian)
    {
        ValidateOffset(byteOffset, 4);
        var raw = Buffer.Data;
        var off = ByteOffset + byteOffset;
        var v = BitConverter.ToInt32(raw, off);
        if (littleEndian != BitConverter.IsLittleEndian)
            v = (int)BinaryPrimitives.ReverseEndianness((uint)v);
        return v;
    }

    public uint GetUint32(int byteOffset, bool littleEndian)
    {
        ValidateOffset(byteOffset, 4);
        var raw = Buffer.Data;
        var off = ByteOffset + byteOffset;
        var v = BitConverter.ToUInt32(raw, off);
        if (littleEndian != BitConverter.IsLittleEndian)
            v = BinaryPrimitives.ReverseEndianness(v);
        return v;
    }

    public short GetInt16(int byteOffset, bool littleEndian)
    {
        ValidateOffset(byteOffset, 2);
        var raw = Buffer.Data;
        var off = ByteOffset + byteOffset;
        var v = BitConverter.ToInt16(raw, off);
        if (littleEndian != BitConverter.IsLittleEndian)
            v = BinaryPrimitives.ReverseEndianness(v);
        return v;
    }

    public ushort GetUint16(int byteOffset, bool littleEndian)
    {
        ValidateOffset(byteOffset, 2);
        var raw = Buffer.Data;
        var off = ByteOffset + byteOffset;
        var v = BitConverter.ToUInt16(raw, off);
        if (littleEndian != BitConverter.IsLittleEndian)
            v = BinaryPrimitives.ReverseEndianness(v);
        return v;
    }

    public sbyte GetInt8(int byteOffset)
    {
        ValidateOffset(byteOffset, 1);
        return (sbyte)Buffer.Data[ByteOffset + byteOffset];
    }

    public byte GetUint8(int byteOffset)
    {
        ValidateOffset(byteOffset, 1);
        return Buffer.Data[ByteOffset + byteOffset];
    }

    public BigInteger GetBigInt64(int byteOffset, bool littleEndian)
    {
        ValidateOffset(byteOffset, 8);
        var v = BitConverter.ToInt64(Buffer.Data, ByteOffset + byteOffset);
        if (littleEndian != BitConverter.IsLittleEndian)
            v = (long)BinaryPrimitives.ReverseEndianness((ulong)v);
        return new BigInteger(v);
    }

    public BigInteger GetBigUint64(int byteOffset, bool littleEndian)
    {
        ValidateOffset(byteOffset, 8);
        var v = BitConverter.ToUInt64(Buffer.Data, ByteOffset + byteOffset);
        if (littleEndian != BitConverter.IsLittleEndian)
            v = BinaryPrimitives.ReverseEndianness(v);
        return new BigInteger(v);
    }

    // SetViewValue

    public void SetFloat64(int byteOffset, double value, bool littleEndian)
    {
        ValidateOffset(byteOffset, 8);
        var raw = Buffer.Data;
        var off = ByteOffset + byteOffset;
        if (littleEndian == BitConverter.IsLittleEndian)
            BitConverter.TryWriteBytes(raw.AsSpan(off, 8), value);
        else
        {
            var bits = BitConverter.DoubleToInt64Bits(value);
            bits = (long)BinaryPrimitives.ReverseEndianness((ulong)bits);
            BitConverter.TryWriteBytes(raw.AsSpan(off, 8), bits);
        }
    }

    public void SetFloat32(int byteOffset, float value, bool littleEndian)
    {
        ValidateOffset(byteOffset, 4);
        var raw = Buffer.Data;
        var off = ByteOffset + byteOffset;
        if (littleEndian == BitConverter.IsLittleEndian)
            BitConverter.TryWriteBytes(raw.AsSpan(off, 4), value);
        else
        {
            var bits = BitConverter.SingleToInt32Bits(value);
            bits = (int)BinaryPrimitives.ReverseEndianness((uint)bits);
            BitConverter.TryWriteBytes(raw.AsSpan(off, 4), bits);
        }
    }

    public void SetInt32(int byteOffset, int value, bool littleEndian)
    {
        ValidateOffset(byteOffset, 4);
        var v = littleEndian == BitConverter.IsLittleEndian ? value : (int)BinaryPrimitives.ReverseEndianness((uint)value);
        BitConverter.TryWriteBytes(Buffer.Data.AsSpan(ByteOffset + byteOffset, 4), v);
    }

    public void SetUint32(int byteOffset, uint value, bool littleEndian)
    {
        ValidateOffset(byteOffset, 4);
        var v = littleEndian == BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);
        BitConverter.TryWriteBytes(Buffer.Data.AsSpan(ByteOffset + byteOffset, 4), v);
    }

    public void SetInt16(int byteOffset, short value, bool littleEndian)
    {
        ValidateOffset(byteOffset, 2);
        var v = littleEndian == BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);
        BitConverter.TryWriteBytes(Buffer.Data.AsSpan(ByteOffset + byteOffset, 2), v);
    }

    public void SetUint16(int byteOffset, ushort value, bool littleEndian)
    {
        ValidateOffset(byteOffset, 2);
        var v = littleEndian == BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);
        BitConverter.TryWriteBytes(Buffer.Data.AsSpan(ByteOffset + byteOffset, 2), v);
    }

    public void SetInt8(int byteOffset, sbyte value)
    {
        ValidateOffset(byteOffset, 1);
        Buffer.Data[ByteOffset + byteOffset] = (byte)value;
    }

    public void SetUint8(int byteOffset, byte value)
    {
        ValidateOffset(byteOffset, 1);
        Buffer.Data[ByteOffset + byteOffset] = value;
    }

    public void SetBigInt64(int byteOffset, BigInteger value, bool littleEndian)
    {
        ValidateOffset(byteOffset, 8);
        var v = (long)value;
        if (littleEndian != BitConverter.IsLittleEndian)
            v = (long)BinaryPrimitives.ReverseEndianness((ulong)v);
        BitConverter.TryWriteBytes(Buffer.Data.AsSpan(ByteOffset + byteOffset, 8), v);
    }

    public void SetBigUint64(int byteOffset, BigInteger value, bool littleEndian)
    {
        ValidateOffset(byteOffset, 8);
        var v = (ulong)value;
        if (littleEndian != BitConverter.IsLittleEndian)
            v = BinaryPrimitives.ReverseEndianness(v);
        BitConverter.TryWriteBytes(Buffer.Data.AsSpan(ByteOffset + byteOffset, 8), v);
    }
}
