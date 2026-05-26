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
    public int ByteLength { get; }
    public abstract int ElementSize { get; }

    protected TypedArrayView(ArrayBufferObject buffer, int byteOffset, int byteLength)
    {
        Buffer = buffer;
        ByteOffset = byteOffset;
        ByteLength = byteLength;
    }

    public bool IsViewDetached => Buffer.IsDetached;

    protected void ValidateOffset(int byteOffset, int size)
    {
        if (Buffer.IsDetached)
            throw new InvalidOperationException("Underlying ArrayBuffer is detached.");
        if (byteOffset < 0 || byteOffset + size > ByteLength)
            throw new ArgumentOutOfRangeException(nameof(byteOffset));
    }

    public override void Trace(Heap.IHeapTracer tracer)
    {
        base.Trace(tracer);
    }
}
