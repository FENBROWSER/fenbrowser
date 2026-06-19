using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

// ECMA-262 23.2 — TypedArray element types.
public enum TypedArrayElementType
{
    Int8, Uint8, Uint8Clamped,
    Int16, Uint16,
    Int32, Uint32,
    Float32, Float64,
    BigInt64, BigUint64
}

// ECMA-262 23.2 — base for DataView and TypedArray objects.
// Carries [[ViewedArrayBuffer]], [[ByteOffset]], [[ByteLength]].
public abstract class TypedArrayView : JsObject
{
    public ArrayBufferObject Buffer { get; }
    public int ByteOffset { get; }
    protected int RequestedByteLength { get; }
    protected bool IsLengthTracking { get; }
    public abstract int ElementSize { get; }

    protected TypedArrayView(ArrayBufferObject buffer, int byteOffset, int byteLength, bool isLengthTracking = false)
    {
        Buffer = buffer;
        ByteOffset = byteOffset;
        RequestedByteLength = byteLength;
        IsLengthTracking = isLengthTracking;
    }

    public int ByteLength
    {
        get
        {
            if (Buffer.IsDetached || IsOutOfBounds())
            {
                return 0;
            }

            if (!IsLengthTracking)
            {
                return RequestedByteLength;
            }

            var available = Buffer.ByteLength - ByteOffset;
            return available - (available % ElementSize);
        }
    }

    // ES2024: Returns true when the view is out of bounds (buffer detached or
    // the view extends beyond the buffer). Callers should throw TypeError when
    // this is true for byteLength/byteOffset getters.
    public bool IsViewOutOfBounds()
    {
        return Buffer.IsDetached || IsOutOfBounds();
    }

    public bool IsViewDetached => Buffer.IsDetached;

    public bool IsOutOfBounds()
    {
        if (Buffer.IsDetached)
        {
            return true;
        }

        if (IsLengthTracking)
        {
            return ByteOffset > Buffer.ByteLength;
        }

        return (long)ByteOffset + RequestedByteLength > Buffer.ByteLength;
    }

    protected void ValidateOffset(int byteOffset, int size)
    {
        if (Buffer.IsDetached)
            throw new InvalidOperationException("Underlying ArrayBuffer is detached.");
        if (byteOffset < 0 || size < 0)
            throw new ArgumentOutOfRangeException(nameof(byteOffset));

        // Use widened arithmetic so large offsets cannot wrap the signed
        // 32-bit add and sneak past bounds checks before indexing.
        if ((long)byteOffset + size > ByteLength)
            throw new ArgumentOutOfRangeException(nameof(byteOffset));
    }

    public override void Trace(Heap.IHeapTracer tracer)
    {
        base.Trace(tracer);
        if (Buffer.OwnerHandle is { } bufferHandle)
            tracer.Trace(bufferHandle);
    }
}
